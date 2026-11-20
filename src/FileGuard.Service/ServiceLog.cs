using System.Text;
using FileGuard.Core.Storage;

namespace FileGuard.Service;

/// <summary>服务日志抽象（方便单测里注入不写盘的假实现）。</summary>
public interface IServiceLog
{
    void Info(string message);

    void Error(string message, Exception? exception = null);
}

/// <summary>
/// 追加写 <c>%ProgramData%\FileGuard\logs\service.log</c>，超过 1 MB 滚动一次。
/// 写盘失败一律吞掉：日志坏了也不能影响守护循环。
/// </summary>
public sealed class ServiceLog : IServiceLog
{
    internal const string FileName = "service.log";
    private const long MaxFileBytes = 1024 * 1024;

    private static readonly object Gate = new();

    private readonly string _path;

    public ServiceLog(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(GuardPaths.LogRoot, FileName)
            : path;
    }

    public void Info(string message) => Write("INFO", message, null);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        if (exception is not null)
        {
            message = $"{message} | {exception.GetType().Name}: {exception.Message}";
        }

        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        System.Diagnostics.Debug.WriteLine($"[FileGuard.Service] {line.TrimEnd()}");

        lock (Gate)
        {
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (File.Exists(_path) && new FileInfo(_path).Length > MaxFileBytes)
                {
                    var backup = _path + ".1";
                    try
                    {
                        if (File.Exists(backup))
                        {
                            File.Delete(backup);
                        }
                        File.Move(_path, backup);
                    }
                    catch (Exception)
                    {
                        // 滚不动就继续追加。
                    }
                }

                File.AppendAllText(_path, line, Encoding.UTF8);
            }
            catch (Exception)
            {
                // 日志失败不影响守护。
            }
        }
    }
}
