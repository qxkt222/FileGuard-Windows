namespace FileGuard.Core.Watching;

/// <summary>
/// 同一路径 3 秒内只通知一次，避免写入方反复重试时弹出一串气泡。
/// 监视模式与虚拟盘模式共用这一份逻辑。
/// </summary>
public sealed class PathThrottle
{
    private static readonly TimeSpan NotifyThrottle = TimeSpan.FromSeconds(3);

    private readonly Dictionary<string, long> _lastNotifyTicks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public bool ShouldNotify(string path)
    {
        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (_lastNotifyTicks.TryGetValue(path, out var last) &&
                now - last < NotifyThrottle.TotalMilliseconds)
            {
                return false;
            }
            _lastNotifyTicks[path] = now;

            if (_lastNotifyTicks.Count > 2048)
            {
                var cutoff = now - (long)TimeSpan.FromMinutes(10).TotalMilliseconds;
                foreach (var key in _lastNotifyTicks.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
                {
                    _lastNotifyTicks.Remove(key);
                }
            }
        }
        return true;
    }
}
