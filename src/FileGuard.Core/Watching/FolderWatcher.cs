using FileGuard.Core.Models;
using FileGuard.Core.Rules;

namespace FileGuard.Core.Watching;

/// <summary>
/// 一个监视文件夹对应一个 FolderWatcher。
/// 负责过滤（忽略目录/豁免目录/非文本文件/临时文件）和事件去抖，
/// 只把「值得检查大小」的文件抛给运行时，不做限制判断。
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(150);

    private readonly GuardEntry _entry;
    private readonly PathRules _rules;
    private readonly Action<CandidateFile> _onCandidate;
    private readonly Dictionary<string, long> _lastSeenTicks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _baselineFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private FileSystemWatcher? _fsw;
    private bool _disposed;

    public FolderWatcher(GuardEntry entry, PathRules rules, Action<CandidateFile> onCandidate)
    {
        _entry = entry;
        _rules = rules;
        _onCandidate = onCandidate;
    }

    public GuardEntry Entry => _entry;

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FolderWatcher));
        if (_fsw is not null) return;
        if (!Directory.Exists(_entry.RootPath)) return;

        CaptureBaseline();

        var fsw = new FileSystemWatcher(_entry.RootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                         | NotifyFilters.LastWrite
                         | NotifyFilters.Size
                         | NotifyFilters.CreationTime,
            InternalBufferSize = 64 * 1024,
        };

        fsw.Created += (_, e) => Handle(e.FullPath, WatchChangeKind.Created);
        fsw.Changed += (_, e) => Handle(e.FullPath, WatchChangeKind.Changed);
        fsw.Deleted += (_, e) => Handle(e.FullPath, WatchChangeKind.Deleted);
        fsw.Renamed += (_, e) => Handle(e.FullPath, WatchChangeKind.Renamed);
        fsw.Error += (_, _) => Restart();

        fsw.EnableRaisingEvents = true;
        _fsw = fsw;
    }

    public void Stop()
    {
        var fsw = _fsw;
        _fsw = null;
        if (fsw is null) return;

        fsw.EnableRaisingEvents = false;
        fsw.Dispose();
    }

    private void Restart()
    {
        // 缓冲区溢出后 watcher 会失效，重建一个继续。
        try
        {
            Stop();
            Thread.Sleep(200);
            Start();
        }
        catch (Exception)
        {
            // 重建失败就保持停止，下一次 Apply 会再试。
        }
    }

    private bool IsKindEnabled(WatchChangeKind kind) => kind switch
    {
        WatchChangeKind.Created => _entry.WatchCreate,
        WatchChangeKind.Changed => _entry.WatchWrite,
        WatchChangeKind.Deleted => _entry.WatchDelete,
        WatchChangeKind.Renamed => _entry.WatchRename,
        _ => false,
    };

    private void Handle(string path, WatchChangeKind kind)
    {
        if (_disposed) return;
        if (!_entry.Enabled) return;
        if (!IsKindEnabled(kind)) return;

        if (_rules.IsIgnoredPath(path, _entry.RootPath)) return;
        if (IsExempt(path)) return;
        if (kind != WatchChangeKind.Deleted && !_rules.IsTextFile(path)) return;

        if (kind == WatchChangeKind.Deleted) return;

        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) return;
        }
        catch (Exception)
        {
            return;
        }

        // 先看长度：文件刚创建时 Created 事件带的是 0 字节，不能占掉去抖窗口，
        // 否则内容写进来后的 Changed 会被吞掉。
        if (info.Length <= _entry.MaxBytes) return;

        if (IsDebounced(path)) return;

        _onCandidate(new CandidateFile(path, info.Length, IsNew(path), kind));
    }

    private bool IsExempt(string path)
    {
        foreach (var exempt in _entry.ExemptPaths)
        {
            if (PathRules.IsUnder(path, exempt)) return true;
        }
        return false;
    }

    /// <summary>启动时把已存在的文件记进基线，之后没在基线里的才算「新建」。</summary>
    private void CaptureBaseline()
    {
        _baselineFiles.Clear();
        try
        {
            foreach (var file in Directory.EnumerateFiles(_entry.RootPath, "*", SearchOption.AllDirectories))
            {
                _baselineFiles.Add(file);
            }
        }
        catch (Exception)
        {
            // 枚举失败就退化为「全部视为新建」，宁可多检查也不漏拦。
        }
    }

    private bool IsNew(string path) => !_baselineFiles.Contains(path);

    private bool IsDebounced(string path)
    {
        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (_lastSeenTicks.TryGetValue(path, out var last) &&
                now - last < DebounceWindow.TotalMilliseconds)
            {
                return true;
            }
            _lastSeenTicks[path] = now;

            // 简单清理，防止长期运行后字典无限增长。
            if (_lastSeenTicks.Count > 4096)
            {
                var cutoff = now - (long)TimeSpan.FromMinutes(5).TotalMilliseconds;
                foreach (var key in _lastSeenTicks.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
                {
                    _lastSeenTicks.Remove(key);
                }
            }
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
