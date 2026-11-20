using System.Diagnostics;
using System.Text;

namespace FileGuard.Core.Logging;

/// <summary>
/// 程序运行日志：记录挂载失败、恢复残留、卸载异常等不属于「拦截记录」的事件。
/// 写文件失败一律吞掉，不能因为日志影响主流程。
/// </summary>
public static class AppLog
{
    private const long MaxFileBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null)
    {
        if (ex is not null)
        {
            message = $"{message} | {ex.GetType().Name}: {ex.Message}";
        }
        Write("ERROR", message);
    }

    private static void Write(string level, string message)
    {
        Debug.WriteLine($"[FileGuard] [{level}] {message}");

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Storage.GuardPaths.LogRoot);
                var path = Storage.GuardPaths.AppLogFile;

                if (File.Exists(path) && new FileInfo(path).Length > MaxFileBytes)
                {
                    var backup = path + ".1";
                    try
                    {
                        if (File.Exists(backup)) File.Delete(backup);
                        File.Move(path, backup);
                    }
                    catch (Exception) { /* 滚不动就继续追加 */ }
                }

                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(path, line, Encoding.UTF8);
            }
            catch (Exception)
            {
                // 日志失败不影响主流程。
            }
        }
    }
}
