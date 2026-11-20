namespace FileGuard.Core.Storage;

/// <summary>程序用到的固定路径。数据目录跟随安装范围（用户级 AppData 或系统级 ProgramData）。</summary>
public static class GuardPaths
{
    /// <summary>数据根目录。服务版传 ProgramData，用户版传 LocalAppData。</summary>
    public static string DataRoot { get; private set; } = BuildDefaultRoot();

    public static string ConfigFile => Path.Combine(DataRoot, "config.json");
    public static string QuarantineRoot => Path.Combine(DataRoot, "quarantine");
    public static string LogRoot => Path.Combine(DataRoot, "logs");
    public static string LogFile => Path.Combine(LogRoot, "intercept.log");

    /// <summary>程序运行日志（挂载失败、恢复残留等不属于拦截记录的事件）。</summary>
    public static string AppLogFile => Path.Combine(LogRoot, "app.log");

    private static string BuildDefaultRoot()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrEmpty(programData))
        {
            return Path.Combine(programData, "FileGuard");
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileGuard");
    }

    public static void SetDataRoot(string root) => DataRoot = root;

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(QuarantineRoot);
        Directory.CreateDirectory(LogRoot);
    }
}
