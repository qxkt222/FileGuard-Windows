using System.ComponentModel;
using System.Windows;
using FileGuard.App.Services;
using FileGuard.App.Views;
using FileGuard.Core.Models;

namespace FileGuard.App;

public partial class MainWindow : Window
{
    public event EventHandler? HideRequested;

    public MainWindow()
    {
        InitializeComponent();

        AppState.Current.ConfigChanged += OnConfigChanged;
        ShowHome();
        RefreshStatus();
    }

    public bool IsHomeVisible => PageHost.Content is HomeView;

    public void ShowHome()
    {
        PageHost.Content = new HomeView(this);
        RefreshStatus();
    }

    public void ShowSettings()
    {
        PageHost.Content = new SettingsView(this);
        RefreshStatus();
    }

    public void ShowEntryFlow(GuardEntry? existing = null, int startStep = 0)
    {
        PageHost.Content = new EntryFlowView(this, existing, startStep);
        RefreshStatus();
    }

    public void RefreshStatus()
    {
        var state = AppState.Current;
        var enabled = state.Entries.Count(e => e.Enabled);
        var virtualDisks = state.Entries.Count(e => e.Enabled && e.Mode == GuardMode.VirtualDisk);
        var suffix = virtualDisks > 0 ? $"（{virtualDisks} 个虚拟盘）" : string.Empty;

        // 只有守护服务确实在运行时才提示；停止/未安装不打扰用户。
        var guardSuffix = App.GuardServiceRunning ? " · 守护中" : string.Empty;

        StatusText.Text = (state.Runtime.IsRunning
            ? $"限制中 · {enabled} 个条目{suffix}"
            : "已暂停") + guardSuffix;
    }

    private void OnConfigChanged(object? sender, EventArgs e) => RefreshStatus();

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app) app.TogglePause();
        RefreshStatus();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => ShowSettings();

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        HideRequested?.Invoke(this, EventArgs.Empty);
    }
}
