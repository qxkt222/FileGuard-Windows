using System.Diagnostics;
using System.Text;

namespace FileGuard.WinFsp;

/// <summary>
/// WinFsp 模块自己的运行日志。这个项目不能引用 FileGuard.Core（Core 反过来依赖它），
/// 所以路径和滚动逻辑在这里单独实现一份，写进和主程序同一个数据目录。
/// 写失败一律吞掉：日志不能影响挂载/卸载。
/// </summary>
public static class DiskLog
{
    private const long MaxFileBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static string LogFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "FileGuard", "logs", "winfsp.log");

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
        Debug.WriteLine($"[FileGuard.WinFsp] [{level}] {message}");

        lock (Gate)
        {
            try
            {
                var path = LogFile;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                if (File.Exists(path) && new FileInfo(path).Length > MaxFileBytes)
                {
                    var backup = path + ".1";
                    try
                    {
                        if (File.Exists(backup)) File.Delete(backup);
                        File.Move(path, backup);
                    }
                    catch (Exception)
                    {
                        // 滚不动就继续追加。
                    }
                }

                var line = string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
                File.AppendAllText(path, line, Encoding.UTF8);
            }
            catch (Exception)
            {
                // 日志失败不影响挂载。
            }
        }
    }
}
