using System.Globalization;
using System.Text;
using FileGuard.Core.Models;

namespace FileGuard.Core.Logging;

/// <summary>
/// 拦截记录：内存里保留最近 N 条给托盘面板显示，文件按大小滚动（防止日志爆炸）。
/// </summary>
public sealed class InterceptLog
{
    private const long MaxFileBytes = 1024 * 1024;

    private readonly string _filePath;
    private readonly int _memoryCapacity;
    private readonly Queue<InterceptRecord> _recent = new();
    private readonly object _gate = new();

    public InterceptLog(string filePath, int memoryCapacity = 5)
    {
        _filePath = filePath;
        _memoryCapacity = Math.Max(1, memoryCapacity);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<InterceptRecord> Recent
    {
        get
        {
            lock (_gate) return _recent.ToArray();
        }
    }

    public void Append(InterceptRecord record)
    {
        // 文件写入也放在锁里：多个文件同时被拦时并发追加会丢记录。
        lock (_gate)
        {
            _recent.Enqueue(record);
            while (_recent.Count > _memoryCapacity) _recent.Dequeue();

            try { AppendToFile(record); }
            catch (Exception) { /* 日志写失败不影响拦截 */ }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void AppendToFile(InterceptRecord record)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (File.Exists(_filePath) && new FileInfo(_filePath).Length > MaxFileBytes)
        {
            var backup = _filePath + ".1";
            try
            {
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(_filePath, backup);
            }
            catch (Exception) { /* 滚不动就继续追加 */ }
        }

        var line = string.Create(CultureInfo.InvariantCulture,
            $"{record.At:yyyy-MM-dd HH:mm:ss} [{record.Action}] {record.Size}/{record.Limit} {record.Path}{Environment.NewLine}");

        File.AppendAllText(_filePath, line, Encoding.UTF8);
    }
}
