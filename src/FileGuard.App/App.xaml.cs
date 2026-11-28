using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using FileGuard.App.Services;
using FileGuard.Core.Control;
using FileGuard.Core.Models;
using FileGuard.Core.Storage;
using FileGuard.Core.VirtualDisks;

namespace FileGuard.App;

public partial class App : Application
{
    /// <summary>守护服务拉起主程序时附加的开关，用于静默启动。</summary>
    private const string RestartedSwitch = "--restarted";

    private Mutex? _mutex;
    private TrayService? _tray;
    private MainWindow? _main;

    public static bool IsAdministrator { get; private set; }

    /// <summary>
    /// 非管理员限制生效中。开启后非管理员不能暂停保护、停用/删除/修改条目，
    /// 也不能在设置里关掉这个限制。
    /// 守护服务安装时会把开关写进 HKLM；那里开着的话，改 config.json 也解不开。
    /// </summary>
    public static bool IsProtectionLocked =>
        (AppState.Current.Options.NonAdminLock || LockSettings.IsLockedByMachine())
        && !IsAdministrator;

    /// <summary>守护服务当前是否在运行。启动时的后台查询和设置页的刷新都会更新它。</summary>
    public static bool GuardServiceRunning { get; private set; }

    /// <summary>最近一次查询到的守护服务状态；退出流程直接用这个缓存，不重复查询。</summary>
    public static GuardServiceStatus ServiceStatus { get; private set; } = GuardServiceStatus.Unknown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 被守护服务拉起来时带 --restarted：静默启动，不显示主窗口也不弹通知。
        var restarted = e.Args.Contains(RestartedSwitch, StringComparer.OrdinalIgnoreCase);

        using (var identity = WindowsIdentity.GetCurrent())
        {
            IsAdministrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        if (!TryAcquireSingleInstance(TimeSpan.Zero))
        {
            // 已经在跑。管理员可以抢占——否则「普通实例退不掉、管理员实例又起不来」会死锁。
            // 被服务拉起的实例安静退场，让旧实例继续跑。
            if (IsAdministrator && !restarted)
            {
                if (!TakeOverExistingInstance())
                {
                    Shutdown();
                    return;
                }
            }
            else
            {
                if (!restarted)
                {
                    MessageBox.Show(
                        "文件卫士已经在运行，请从托盘打开。\n\n如果要以管理员身份接管（用于退出或改设置），请结束当前实例后再试。",
                        "文件卫士", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                Shutdown();
                return;
            }
        }

        try
        {
            AppState.Current.Initialize();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"初始化失败：{ex.Message}", "文件卫士", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        _tray = new TrayService();
        _tray.OpenRequested += (_, _) => ShowMain();
        _tray.ExitRequested += (_, _) => RequestExit();
        _tray.PauseToggleRequested += (_, _) => TogglePause();

        AppState.Current.Intercepted += OnIntercepted;
        AppState.Current.VirtualDiskFaulted += OnVirtualDiskFaulted;

        // 后台问一次守护服务，不阻塞启动；结果回到 UI 线程再刷新托盘和主界面。
        _ = Task.Run(QueryGuardServiceAsync);

        // 初始化阶段（托盘还没建好）发生的挂载失败，这里补一次提示。
        // 被服务拉起时要求完全静默，不打扰用户。
        if (!restarted)
        {
            NotifyStartupFaults();
        }

        _main = new MainWindow();
        _main.HideRequested += (_, _) => HideMain();

        RefreshTrayState();

        if (!restarted && !AppState.Current.Options.StartMinimized)
        {
            ShowMain();
        }
    }

    /// <summary>
    /// 启动时的服务状态探测：查询状态 + 报到（hello），全部在后台线程完成。
    /// 两步都不抛异常；结果通过 <see cref="UpdateGuardServiceStatus"/> 回到 UI 线程。
    /// </summary>
    private async Task QueryGuardServiceAsync()
    {
        var status = await GuardServiceClient.Current.QueryAsync().ConfigureAwait(false);

        // hello 同时告诉服务「主程序已经起来了」，并清掉可能残留的「已批准退出」。
        await GuardServiceClient.Current.SendHelloAsync(Environment.ProcessId).ConfigureAwait(false);

        UpdateGuardServiceStatus(status);
    }

    /// <summary>
    /// 保存最新的服务状态并刷新界面。可以在任意线程调用，内部会切回 UI 线程。
    /// </summary>
    public static void UpdateGuardServiceStatus(GuardServiceStatus status)
    {
        var app = Application.Current as App;

        if (app is not null && !app.Dispatcher.CheckAccess())
        {
            try
            {
                app.Dispatcher.BeginInvoke(new Action(() => UpdateGuardServiceStatus(status)));
            }
            catch (Exception)
            {
                // 程序正在退出、Dispatcher 已关闭：状态已无意义，忽略。
            }

            return;
        }

        ServiceStatus = status;
        GuardServiceRunning = status == GuardServiceStatus.Running;

        if (app is null) return;

        app.RefreshTrayState();
        app._main?.RefreshStatus();
    }

    /// <summary>尝试拿单实例互斥量；超时内反复重试，等旧实例退干净。</summary>
    private bool TryAcquireSingleInstance(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                _mutex = new Mutex(true, @"Local\FileGuard.SingleInstance", out var createdNew);
                if (createdNew) return true;

                _mutex.Dispose();
                _mutex = null;
            }
            catch (AbandonedMutexException)
            {
                // 上一个持有者崩溃了：互斥量归我们。
                return true;
            }

            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(150);
        }
    }

    /// <summary>
    /// 管理员接管正在运行的实例：先请守护服务放行，再结束旧进程，最后拿互斥量。
    /// 顺序不能反——先杀的话会被服务立刻拉起来，两个实例互抢。
    /// </summary>
    private bool TakeOverExistingInstance()
    {
        var status = GuardServiceClient.Current.QueryAsync().GetAwaiter().GetResult();
        if (status == GuardServiceStatus.Running)
        {
            var reply = GuardServiceClient.Current.RequestExit();
            if (reply?.Type != GuardMessageTypes.ExitApproved)
            {
                MessageBox.Show("守护服务没有放行，无法接管正在运行的实例。", "文件卫士",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        foreach (var process in Process.GetProcessesByName(GuardProtocol.ClientProcessName))
        {
            if (process.Id == Environment.ProcessId) continue;

            try
            {
                process.Kill();
                process.WaitForExit(3000);
            }
            catch (Exception)
            {
                // 杀不掉就继续尝试拿互斥量，拿不到再报错。
            }
        }

        if (TryAcquireSingleInstance(TimeSpan.FromSeconds(5))) return true;

        MessageBox.Show("无法接管正在运行的文件卫士，请手动结束后再试。", "文件卫士",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private void OnIntercepted(object? sender, InterceptRecord record)
    {
        Dispatcher.Invoke(() =>
        {
            _tray?.NotifyIntercepted(record);
            RefreshTrayState();
        });
    }

    private void OnVirtualDiskFaulted(object? sender, VirtualDiskFault fault)
    {
        Dispatcher.Invoke(() =>
        {
            _tray?.NotifyError("虚拟盘挂载失败", $"{fault.Path}{Environment.NewLine}{fault.Message}");
            RefreshTrayState();
        });
    }

    private void NotifyStartupFaults()
    {
        var state = AppState.Current;
        var failed = state.Entries
            .Where(e => e.Mode == GuardMode.VirtualDisk && state.VirtualDisks.GetError(e.Id) is not null)
            .ToList();

        if (failed.Count == 0) return;

        var message = failed.Count == 1
            ? $"{failed[0].RootPath}{Environment.NewLine}{state.VirtualDisks.GetError(failed[0].Id)}"
            : $"有 {failed.Count} 个虚拟盘条目挂载失败，请打开主界面查看原因。";

        _tray?.NotifyError("虚拟盘挂载失败", message);
    }

    private void RefreshTrayState()
    {
        var state = AppState.Current;
        var running = state.Runtime.IsRunning;
        var count = state.Entries.Count(e => e.Enabled);
        _tray?.SetState(running, count);
    }

    public void ShowMain()
    {
        if (_main is null) return;
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public void HideMain()
    {
        _main?.Hide();
    }

    public void TogglePause()
    {
        var state = AppState.Current;

        if (state.Runtime.IsRunning)
        {
            // 暂停是一条「停止保护」的路径，要拦；恢复保护永远允许。
            if (IsProtectionLocked)
            {
                MessageBox.Show("非管理员账户不能暂停保护。", "文件卫士",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            state.PauseAll();
        }
        else
        {
            state.ResumeAll();
        }

        RefreshTrayState();
        _main?.RefreshStatus();

        // 暂停/恢复会挂载或卸载虚拟盘，首页的挂载状态要跟着刷新。
        if (_main is { IsHomeVisible: true }) _main.ShowHome();
    }

    public void RequestExit()
    {
        // 实时查一次：启动时缓存的状态可能是过期的（服务刚装好、刚重启），
        // 用错状态会让「退出后不被拉起」失效。
        var status = GuardServiceClient.Current.QueryAsync().GetAwaiter().GetResult();
        UpdateGuardServiceStatus(status);

        switch (status)
        {
            case GuardServiceStatus.Running:
                RequestGuardedExit();
                return;

            case GuardServiceStatus.Stopped:
            case GuardServiceStatus.Unknown:
                MessageBox.Show("守护服务未运行，本次退出不会被自动拉起。", "文件卫士",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                ExitWithLegacyChecks();
                return;

            default:
                // NotInstalled：服务根本没装，保持原来的退出行为，不额外打扰。
                ExitWithLegacyChecks();
                return;
        }
    }

    /// <summary>守护服务在运行：用户确认后申请批准，再退出。</summary>
    private void RequestGuardedExit()
    {
        // 先问用户、再申请批准。批准会一直保留到 hello 或超过兜底宽限期：
        // 若在确认框还开着时就申请，用户犹豫期间程序崩溃/被杀，服务会认为
        // 「已批准退出 + 主程序不在」，此后永不拉起，守护等于被永久关掉。
        var result = MessageBox.Show(
            "确定退出？退出后守护服务也会放行，不会自动重启。",
            "文件卫士", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        var reply = GuardServiceClient.Current.RequestExit();

        switch (reply?.Type)
        {
            case GuardMessageTypes.ExitApproved:
                // 批准后立即关闭，把「已批准但还活着」的窗口压到最小。
                Shutdown();
                return;

            case GuardMessageTypes.ExitDenied:
                MessageBox.Show(
                    "非管理员账户不能退出文件卫士。\n\n如果你确实是管理员，请关闭本程序，右键以管理员身份重新启动后再退出。",
                    "文件卫士", MessageBoxButton.OK, MessageBoxImage.Warning);
                // 服务可能已进入「已批准」状态，补发 hello 让守护恢复正常。
                _ = GuardServiceClient.Current.SendHelloAsync(Environment.ProcessId);
                return;

            default:
                // null（服务没响应/超时）或 error：按普通模式处理。
                MessageBox.Show("守护服务无响应，已按普通模式处理。", "文件卫士",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                ExitWithLegacyChecks();
                return;
        }
    }

    /// <summary>原来的退出流程：非管理员锁定检查 + 确认框 + 关闭。</summary>
    private void ExitWithLegacyChecks()
    {
        if (AppState.Current.Options.NonAdminLock && !IsAdministrator)
        {
            MessageBox.Show("非管理员账户不能退出文件卫士。", "文件卫士", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show("确定退出文件卫士？所有监视都会停止。", "文件卫士",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { AppState.Current.Shutdown(); } catch { }
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
