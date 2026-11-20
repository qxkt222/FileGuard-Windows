using FileGuard.Core.Logging;
using FileGuard.Core.Models;
using FileGuard.Core.Storage;
using FileGuard.Core.VirtualDisks;
using FileGuard.Core.Watching;

namespace FileGuard.App.Services;

/// <summary>全局单例：配置、运行时、日志、隔离区都从这里拿。</summary>
public sealed class AppState
{
    public static AppState Current { get; } = new();

    public ConfigStore Store { get; private set; } = null!;
    public GuardConfig Config { get; private set; } = new();
    public InterceptLog Log { get; private set; } = null!;
    public QuarantineStore Quarantine { get; private set; } = null!;

    /// <summary>监视模式条目（FileSystemWatcher + 超限删除）。</summary>
    public GuardRuntime Runtime { get; private set; } = null!;

    /// <summary>虚拟盘模式条目（WinFsp 挂载）。</summary>
    public VirtualDiskManager VirtualDisks { get; private set; } = null!;

    public event EventHandler? ConfigChanged;
    public event EventHandler<InterceptRecord>? Intercepted;
    public event EventHandler<VirtualDiskFault>? VirtualDiskFaulted;

    private AppState() { }

    public void Initialize()
    {
        GuardPaths.EnsureDirectories();

        Store = new ConfigStore(GuardPaths.ConfigFile);
        Config = Store.Load();

        Log = new InterceptLog(GuardPaths.LogFile, Config.Options.RecentRecordCount);
        Quarantine = new QuarantineStore(GuardPaths.QuarantineRoot);
        Runtime = new GuardRuntime(Config.Options, Quarantine, Log);
        VirtualDisks = new VirtualDiskManager(Log);

        Runtime.Intercepted += (_, record) => Intercepted?.Invoke(this, record);
        VirtualDisks.Intercepted += (_, record) => Intercepted?.Invoke(this, record);
        VirtualDisks.Faulted += (_, fault) => VirtualDiskFaulted?.Invoke(this, fault);
        VirtualDisks.Changed += (_, _) => ConfigChanged?.Invoke(this, EventArgs.Empty);

        Runtime.Apply(Config.Entries);
        Runtime.Start();

        VirtualDisks.Start(Config.Entries);
    }

    public GuardOptions Options => Config.Options;

    public IReadOnlyList<GuardEntry> Entries => Config.Entries;

    public void SaveAndApply()
    {
        Store.Save(Config);

        // 先让监视引擎重建 watcher：切到虚拟盘的条目会先失去 watcher，
        // 再被挂载，避免 watcher 盯着一个马上被改名的目录。
        Runtime.Apply(Config.Entries);
        VirtualDisks.Apply(Config.Entries);

        ConfigChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SaveOnly()
    {
        Store.Save(Config);
        ConfigChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AddEntry(GuardEntry entry)
    {
        Config.Entries.Add(entry);
        SaveAndApply();
    }

    public void RemoveEntry(Guid id)
    {
        Config.Entries.RemoveAll(e => e.Id == id);
        SaveAndApply();
    }

    public void PauseAll()
    {
        Runtime.Stop();
        VirtualDisks.Stop();
    }

    public void ResumeAll()
    {
        Runtime.Apply(Config.Entries);
        Runtime.Start();
        VirtualDisks.Start(Config.Entries);
    }

    public bool IsRunning => Runtime.IsRunning;

    /// <summary>退出时调用：先卸载所有虚拟盘，再停掉 watcher。</summary>
    public void Shutdown()
    {
        try { VirtualDisks.Dispose(); } catch (Exception) { }
        try { Runtime.Dispose(); } catch (Exception) { }
    }
}
