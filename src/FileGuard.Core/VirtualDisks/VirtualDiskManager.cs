using FileGuard.Core.Logging;
using FileGuard.Core.Models;
using FileGuard.Core.Watching;
using FileGuard.WinFsp;

namespace FileGuard.Core.VirtualDisks;

/// <summary>
/// 管理所有「虚拟盘」条目的生命周期：启动时恢复残留并挂载、启停时挂载/卸载、
/// 删除条目时卸载、退出时全部卸载。监视模式条目完全由 <see cref="Watching.GuardRuntime"/> 负责，
/// 这里只处理 <see cref="GuardMode.VirtualDisk"/>。
/// </summary>
public sealed class VirtualDiskManager : IDisposable
{
    private sealed class MountedDisk
    {
        public MountedDisk(Guid entryId, VirtualDisk disk, string rootPath, long maxBytes)
        {
            EntryId = entryId;
            Disk = disk;
            RootPath = rootPath;
            MaxBytes = maxBytes;
        }

        public Guid EntryId { get; }
        public VirtualDisk Disk { get; }
        public string RootPath { get; }
        public long MaxBytes { get; }

        public bool Matches(GuardEntry entry) =>
            MaxBytes == entry.MaxBytes &&
            string.Equals(RootPath, Normalize(entry.RootPath), StringComparison.OrdinalIgnoreCase);
    }

    private readonly InterceptLog _log;
    private readonly PathThrottle _throttle = new();
    private readonly object _gate = new();
    private readonly object _applyGate = new();
    private readonly Dictionary<Guid, MountedDisk> _mounted = new();
    private readonly Dictionary<Guid, string> _errors = new();

    private bool _running;
    private bool _disposed;

    public VirtualDiskManager(InterceptLog log)
    {
        _log = log;
    }

    /// <summary>产生一条拒绝记录（Action = Rejected）。已按路径节流通知。</summary>
    public event EventHandler<InterceptRecord>? Intercepted;

    /// <summary>挂载、恢复或卸载失败。调用方负责提示用户，不能静默。</summary>
    public event EventHandler<VirtualDiskFault>? Faulted;

    /// <summary>挂载状态发生变化，界面据此刷新。</summary>
    public event EventHandler? Changed;

    public bool IsMounted(Guid entryId)
    {
        lock (_gate) return _mounted.ContainsKey(entryId);
    }

    /// <summary>当前挂载失败的条目及原因；没失败返回 null。</summary>
    public string? GetError(Guid entryId)
    {
        lock (_gate) return _errors.TryGetValue(entryId, out var message) ? message : null;
    }

    /// <summary>条目对应的挂载点（即真实目录）；未挂载返回 null。</summary>
    public string? GetMountPoint(Guid entryId)
    {
        lock (_gate) return _mounted.TryGetValue(entryId, out var state) ? state.Disk.MountPoint : null;
    }

    /// <summary>启动保护：标记为运行，并按条目挂载所有启用的虚拟盘。</summary>
    public void Start(IReadOnlyList<GuardEntry> entries)
    {
        lock (_applyGate)
        {
            if (_disposed) return;
            _running = true;
            ApplyCore(entries);
        }
    }

    /// <summary>条目变更后重新对齐：卸载不该挂的，挂载新的/配置变了的。</summary>
    public void Apply(IReadOnlyList<GuardEntry> entries)
    {
        lock (_applyGate)
        {
            if (_disposed) return;
            ApplyCore(entries);
        }
    }

    /// <summary>暂停保护：卸载所有虚拟盘，目录恢复成普通目录。</summary>
    public void Stop()
    {
        lock (_applyGate)
        {
            if (_disposed) return;
            StopCore();
        }
    }

    public void Dispose()
    {
        lock (_applyGate)
        {
            if (_disposed) return;
            _disposed = true;
            StopCore();
        }
    }

    private void ApplyCore(IReadOnlyList<GuardEntry> entries)
    {
        var desired = new Dictionary<Guid, GuardEntry>();
        foreach (var entry in entries)
        {
            if (entry.Mode == GuardMode.VirtualDisk && entry.Enabled)
            {
                desired[entry.Id] = entry;
            }
        }

        var toUnmount = new List<MountedDisk>();
        var toMount = new List<GuardEntry>();
        var changed = false;

        lock (_gate)
        {
            foreach (var pair in _mounted.ToList())
            {
                if (!desired.TryGetValue(pair.Key, out var entry) || !pair.Value.Matches(entry))
                {
                    toUnmount.Add(pair.Value);
                    _mounted.Remove(pair.Key);
                    changed = true;
                }
            }

            if (_running)
            {
                foreach (var entry in desired.Values)
                {
                    if (!_mounted.ContainsKey(entry.Id)) toMount.Add(entry);
                }
            }

            // 条目被删除/停用后，残留的错误状态没有意义。
            foreach (var id in _errors.Keys.Where(id => !desired.ContainsKey(id)).ToList())
            {
                _errors.Remove(id);
                changed = true;
            }
        }

        foreach (var state in toUnmount)
        {
            UnmountCore(state);
        }

        if (_running)
        {
            foreach (var entry in toMount)
            {
                MountCore(entry);
            }
        }

        if (changed || toMount.Count > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void StopCore()
    {
        _running = false;

        List<MountedDisk> mounted;
        lock (_gate)
        {
            mounted = _mounted.Values.ToList();
            _mounted.Clear();
            _errors.Clear();
        }

        foreach (var state in mounted)
        {
            UnmountCore(state);
        }

        // Dispose 期间不要再通知界面：此时 WPF 正在退出。
        if (!_disposed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private void MountCore(GuardEntry entry)
    {
        try
        {
            if (VirtualDisk.IsOrphaned(entry.RootPath))
            {
                AppLog.Info($"检测到残留虚拟盘，先恢复目录：{entry.RootPath}");
                VirtualDisk.Recover(entry.RootPath);
            }
        }
        catch (Exception ex)
        {
            ReportFault(entry.Id, entry.RootPath, "恢复残留挂载失败：" + ex.Message, ex);
            return;
        }

        try
        {
            var disk = VirtualDisk.Mount(entry.RootPath, entry.MaxBytes);
            disk.WriteRejected += (_, e) => OnWriteRejected(entry.Id, e);

            lock (_gate)
            {
                _mounted[entry.Id] = new MountedDisk(entry.Id, disk, Normalize(entry.RootPath), entry.MaxBytes);
                _errors.Remove(entry.Id);
            }

            AppLog.Info($"已挂载虚拟盘 {entry.RootPath}（单文件上限 {entry.MaxBytes} 字节）");
        }
        catch (Exception ex)
        {
            ReportFault(entry.Id, entry.RootPath, "挂载失败：" + ex.Message, ex);
        }
    }

    private void UnmountCore(MountedDisk state)
    {
        try
        {
            state.Disk.Unmount();
            AppLog.Info($"已卸载虚拟盘 {state.RootPath}");
        }
        catch (Exception ex)
        {
            // Unmount 可能已经卸载成功但恢复目录失败；此时目录仍叫 xxx.__guard_store，
            // 下次启动会走 Recover，绝不静默吞掉。
            ReportFault(state.EntryId, state.RootPath, "卸载虚拟盘失败：" + ex.Message, ex);
        }
        finally
        {
            try { state.Disk.Dispose(); } catch (Exception ex) { AppLog.Error("释放虚拟盘失败：" + state.RootPath, ex); }
        }
    }

    private void OnWriteRejected(Guid entryId, DiskWriteRejected e)
    {
        var record = new InterceptRecord(
            e.Timestamp,
            entryId,
            e.FilePath,
            e.AttemptedSize,
            e.MaxBytes,
            InterceptAction.Rejected);

        // 这段代码跑在 WinFsp 的写入线程里：日志和通知丢到线程池，
        // 避免界面卡住时反过来把写入方也卡死。
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { _log.Append(record); } catch (Exception) { }

            if (_throttle.ShouldNotify(e.FilePath))
            {
                try { Intercepted?.Invoke(this, record); }
                catch (Exception ex) { AppLog.Error("虚拟盘拒绝通知失败", ex); }
            }
        });
    }

    private void ReportFault(Guid entryId, string path, string message, Exception ex)
    {
        AppLog.Error($"{path}: {message}", ex);

        if (entryId != Guid.Empty)
        {
            lock (_gate) _errors[entryId] = message;
        }

        // 退出流程中界面/托盘正在销毁，只落盘日志，不再弹提示。
        if (_disposed) return;

        try
        {
            Faulted?.Invoke(this, new VirtualDiskFault(entryId, path, message));
        }
        catch (Exception handlerError)
        {
            // 订阅方（托盘/界面）出错不能拖垮挂载流程。
            AppLog.Error("虚拟盘故障回调失败", handlerError);
        }
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (ArgumentException)
        {
            return path;
        }
    }
}
