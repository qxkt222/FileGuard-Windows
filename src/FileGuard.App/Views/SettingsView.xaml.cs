using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FileGuard.App.Services;
using FileGuard.Core.Models;
using FileGuard.Core.Storage;

namespace FileGuard.App.Views;

public partial class SettingsView : UserControl
{
    private readonly MainWindow _host;
    private bool _serviceBusy;

    public SettingsView(MainWindow host)
    {
        _host = host;
        InitializeComponent();
        LoadFromOptions();

        // 先用缓存值把界面填上，再后台查一次，避免打开设置页时卡住。
        UpdateGuardStatusUi();
        _ = RefreshGuardStatusAsync();
    }

    private void LoadFromOptions()
    {
        var o = AppState.Current.Options;

        DefaultKbBox.Text = (o.DefaultMaxBytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture);
        DefaultWriteBox.IsChecked = o.DefaultWatchWrite;
        DefaultExistingBox.IsChecked = o.DefaultCheckExistingFiles;
        DefaultModeWatchRadio.IsChecked = o.DefaultMode == GuardMode.Watch;
        DefaultModeDiskRadio.IsChecked = o.DefaultMode == GuardMode.VirtualDisk;

        AutoStartBox.IsChecked = AutoStart.IsEnabled();
        StartMinBox.IsChecked = o.StartMinimized;
        NonAdminBox.IsChecked = o.NonAdminLock;

        QuarantineBox.IsChecked = o.EnableQuarantine;
        QuarantineKeepBox.Text = o.QuarantineKeep.ToString(CultureInfo.InvariantCulture);
        RuleFileBox.IsChecked = o.WriteRuleFile;

        DataPathText.Text = $"数据目录：{GuardPaths.DataRoot}\n日志：{GuardPaths.LogFile}";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var state = AppState.Current;
        var o = state.Options;

        if (double.TryParse(DefaultKbBox.Text.Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var kb) && kb > 0)
        {
            o.DefaultMaxBytes = (long)Math.Round(kb * 1024);
        }

        o.DefaultWatchWrite = DefaultWriteBox.IsChecked == true;
        o.DefaultCheckExistingFiles = DefaultExistingBox.IsChecked == true;
        o.DefaultMode = DefaultModeDiskRadio.IsChecked == true ? GuardMode.VirtualDisk : GuardMode.Watch;
        o.StartMinimized = StartMinBox.IsChecked == true;
        o.NonAdminLock = NonAdminBox.IsChecked == true;
        o.EnableQuarantine = QuarantineBox.IsChecked == true;
        o.WriteRuleFile = RuleFileBox.IsChecked == true;

        if (int.TryParse(QuarantineKeepBox.Text.Trim(), out var keep) && keep > 0)
        {
            o.QuarantineKeep = keep;
        }

        try
        {
            AutoStart.Set(AutoStartBox.IsChecked == true);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"设置开机自启失败：{ex.Message}", "文件卫士",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        state.SaveOnly();
        _host.ShowHome();
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "把所有设置恢复成默认值？条目不受影响。",
            "恢复默认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        var state = AppState.Current;
        var entries = state.Config.Entries.ToList();

        var fresh = state.Store.Reset();
        state.Config.Options = fresh.Options;
        state.Config.Entries = entries;

        try { AutoStart.Set(state.Config.Options.StartWithWindows); }
        catch (Exception) { }

        state.SaveOnly();
        LoadFromOptions();
        MessageBox.Show("已恢复默认。", "文件卫士", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _host.ShowHome();

    // ── 进程守护 ────────────────────────────────────────────────────────────

    /// <summary>查询服务状态并同步到全局缓存 / 主界面 / 本页控件。不会抛异常。</summary>
    private async Task RefreshGuardStatusAsync()
    {
        var status = await GuardServiceClient.Current.QueryAsync();
        App.UpdateGuardServiceStatus(status);
        UpdateGuardStatusUi();
        _host.RefreshStatus();
    }

    /// <summary>把 <see cref="App.ServiceStatus"/> 映射成状态文字和按钮可用性。</summary>
    private void UpdateGuardStatusUi()
    {
        var status = App.ServiceStatus;

        GuardStatusText.Text = status switch
        {
            GuardServiceStatus.Running => "守护服务：运行中",
            GuardServiceStatus.Stopped => "守护服务：已停止",
            GuardServiceStatus.NotInstalled => "守护服务：未安装",
            _ => "守护服务：状态未知",
        };

        // 服务已存在（运行/停止）才能卸载；没装或问不出来时给安装按钮。
        var installed = status is GuardServiceStatus.Running or GuardServiceStatus.Stopped;
        UninstallServiceButton.IsEnabled = installed && !_serviceBusy;
        InstallServiceButton.IsEnabled = !installed && !_serviceBusy;
    }

    private static string ServiceExePath =>
        Path.Combine(AppContext.BaseDirectory, "service", "FileGuard.Service.exe");

    private void OnInstallService(object sender, RoutedEventArgs e)
    {
        if (_serviceBusy) return;

        var serviceExe = ServiceExePath;
        if (!File.Exists(serviceExe))
        {
            MessageBox.Show($"找不到守护服务程序：{serviceExe}，请先发布服务。", "文件卫士",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var appExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(appExe))
        {
            MessageBox.Show("无法确定主程序路径，安装已取消。", "文件卫士",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RunServiceCommand(serviceExe, $"--install \"{appExe}\"", "安装");
    }

    private void OnUninstallService(object sender, RoutedEventArgs e)
    {
        if (_serviceBusy) return;

        var serviceExe = ServiceExePath;
        if (!File.Exists(serviceExe))
        {
            MessageBox.Show($"找不到守护服务程序：{serviceExe}，请先发布服务。", "文件卫士",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RunServiceCommand(serviceExe, "--uninstall", "卸载");
    }

    private void OnRefreshServiceStatus(object sender, RoutedEventArgs e) => _ = RefreshGuardStatusAsync();

    /// <summary>
    /// 以管理员身份（UAC）运行服务程序的安装/卸载命令，完成后刷新状态。
    /// 用户取消 UAC 会抛 <see cref="Win32Exception"/>（1223），静默处理。
    /// </summary>
    private void RunServiceCommand(string serviceExe, string arguments, string actionName)
    {
        _serviceBusy = true;
        UpdateGuardStatusUi();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = serviceExe,
                Arguments = arguments,
                Verb = "runas",
                UseShellExecute = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                MessageBox.Show($"{actionName}失败：无法启动 {serviceExe}。", "文件卫士",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!process.WaitForExit(30000))
            {
                MessageBox.Show($"{actionName}超时（30 秒）。服务程序可能仍在运行，请稍后刷新状态确认。",
                    "文件卫士", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var exitCode = process.ExitCode;

            // 先异步刷新，弹框期间状态就能更新。
            _ = RefreshGuardStatusAsync();

            if (exitCode == 0)
            {
                MessageBox.Show($"守护服务{actionName}成功。", "文件卫士",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(
                    $"{actionName}失败（退出码 {exitCode}）。请确认已通过 UAC 以管理员身份运行，并检查安装路径。",
                    "文件卫士", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 用户在 UAC 弹窗点了「否」，什么都不做。
        }
        catch (Exception ex)
        {
            MessageBox.Show($"{actionName}失败：{ex.Message}", "文件卫士",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _serviceBusy = false;
            UpdateGuardStatusUi();
        }
    }
}
