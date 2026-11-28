using System.Collections.Concurrent;
using FileGuard.Core.Logging;
using FileGuard.Core.Models;
using FileGuard.Core.Rules;
using FileGuard.Core.Storage;

namespace FileGuard.Core.Watching;

/// <summary>
/// 运行时：持有所有条目的 watcher，判定超限，执行隔离 + 删除 + 记录。
/// </summary>
public sealed class GuardRuntime : IDisposable
{
    private static readonly TimeSpan DeleteRetryWindow = TimeSpan.FromSeconds(30);

    private readonly PathRules _rules;
    private readonly GuardOptions _options;
    private readonly QuarantineStore _quarantine;
    private readonly InterceptLog _log;

    private readonly Dictionary<Guid, FolderWatcher> _watchers = new();
    private readonly ConcurrentDictionary<string, byte> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly PathThrottle _throttle = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();

    private IReadOnlyList<GuardEntry> _entries = Array.Empty<GuardEntry>();
    private bool _running;
    private bool _disposed;

    public GuardRuntime(GuardOptions options, QuarantineStore quarantine, InterceptLog log, PathRules? rules = null)
    {
        _options = options;
        _quarantine = quarantine;
        _log = log;
        _rules = rules ?? PathRules.Default;
    }

    /// <summary>产生一条拦截记录（含观察模式）。已按路径节流。</summary>
    public event EventHandler<InterceptRecord>? Intercepted;

    public event EventHandler? EntriesChanged;

    public IReadOnlyList<GuardEntry> Entries
    {
        get { lock (_gate) return _entries; }
    }

    public IReadOnlyList<InterceptRecord> Recent => _log.Recent;

    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    /// <summary>某条目当前是否有活跃 watcher（用于界面显示限制状态）。</summary>
    public bool IsEntryActive(Guid entryId)
    {
        lock (_gate) return _watchers.ContainsKey(entryId);
    }

    /// <summary>替换全部条目，按当前运行状态重建 watcher。</summary>
    public void Apply(IReadOnlyList<GuardEntry> entries)
    {
        List<FolderWatcher> old;
        lock (_gate)
        {
            old = _watchers.Values.ToList();
            _watchers.Clear();
            _entries = entries.Select(e => e.Clone()).ToList();
        }

        foreach (var w in old) { try { w.Dispose(); } catch { } }

        if (!_running) return;

        lock (_gate)
        {
            foreach (var entry in _entries)
            {
                StartWatcherLocked(entry);
            }
        }

        EntriesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running || _disposed) return;
            _running = true;
            foreach (var entry in _entries)
            {
                StartWatcherLocked(entry);
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _running = false;
            foreach (var w in _watchers.Values) { try { w.Dispose(); } catch { } }
            _watchers.Clear();
        }
    }

    private void StartWatcherLocked(GuardEntry entry)
    {
        if (!entry.Enabled) return;
        // 虚拟盘条目由 VirtualDiskManager 接管，监视模式不碰它。
        if (entry.Mode != GuardMode.Watch) return;
        if (!Directory.Exists(entry.RootPath)) return;
        if (_watchers.ContainsKey(entry.Id)) return;

        var watcher = new FolderWatcher(entry, _rules, candidate => OnCandidate(entry, candidate));
        try
        {
            watcher.Start();
            _watchers[entry.Id] = watcher;
        }
        catch (Exception)
        {
            watcher.Dispose();
        }
    }

    private void OnCandidate(GuardEntry entry, CandidateFile file)
    {
        try
        {
            // 暂停后 watcher 已释放，但后台扫描/在途事件可能还在回调：停了就不再处理。
            if (!IsRunning) return;
            if (!entry.Enabled) return;
            if (entry.Mode != GuardMode.Watch) return;

            // 默认只拦生效之后新建的文件；开启 CheckExistingFiles 后已存在文件也管。
            if (!file.IsNew && !entry.CheckExistingFiles) return;

            if (file.Length <= entry.MaxBytes) return;

            if (!entry.LimitEnabled)
            {
                Publish(entry, file, InterceptAction.Observed);
                return;
            }

            _ = InterceptAsync(entry, file);
        }
        catch (Exception)
        {
            // 单个事件失败不能拖垮 watcher 线程。
        }
    }

    private async Task InterceptAsync(GuardEntry entry, CandidateFile file)
    {
        if (!_inflight.TryAdd(file.FullPath, 0)) return;

        try
        {
            var ct = _cts.Token;

            // 事件到处理之间文件可能被截断/替换：复核一次，别误删已经合规的文件。
            if (!IsStillOversized(file.FullPath, entry.MaxBytes)) return;

            if (_options.EnableQuarantine)
            {
                try
                {
                    var backup = await Task.Run(
                            () => _quarantine.Backup(file.FullPath, entry.Id, entry.RootPath), ct)
                        .ConfigureAwait(false);
                    if (backup is null)
                    {
                        // 备份是尽力而为，删除是保护本身；失败要留痕，别静默。
                        AppLog.Error($"隔离区备份失败，仍按保护规则删除：{file.FullPath}");
                    }

                    _quarantine.Prune(_options.QuarantineKeep);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception) { }
            }

            var deleted = await DeleteWithRetryAsync(file.FullPath, ct).ConfigureAwait(false);

            Publish(entry, file, deleted ? InterceptAction.Deleted : InterceptAction.DeleteFailed);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _inflight.TryRemove(file.FullPath, out _);
        }
    }

    private static bool IsStillOversized(string path, long maxBytes)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > maxBytes;
        }
        catch (Exception)
        {
            // 读不到大小就按原判断继续，保护优先。
            return true;
        }
    }

    private static async Task<bool> DeleteWithRetryAsync(string path, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + DeleteRetryWindow;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                File.Delete(path);
                return !File.Exists(path);
            }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        return !File.Exists(path);
    }

    private void Publish(GuardEntry entry, CandidateFile file, InterceptAction action)
    {
        var record = new InterceptRecord(
            DateTimeOffset.Now,
            entry.Id,
            file.FullPath,
            file.Length,
            entry.MaxBytes,
            action);

        _log.Append(record);

        // 同一路径 3 秒内只通知一次，避免 AI 反复重试时弹出一串。
        if (_throttle.ShouldNotify(file.FullPath))
        {
            Intercepted?.Invoke(this, record);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        Stop();
        _cts.Dispose();
    }
}
