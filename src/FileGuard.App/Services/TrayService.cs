using System.Drawing;
using System.IO;
using System.Windows.Forms;
using FileGuard.Core.Models;

namespace FileGuard.App.Services;

/// <summary>托盘图标、右键菜单、超限气泡通知。</summary>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly Icon _runningIcon;
    private readonly Icon _pausedIcon;
    private bool _disposed;

    public event EventHandler? OpenRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? PauseToggleRequested;

    public TrayService()
    {
        _runningIcon = LoadIcon("tray-on.ico");
        _pausedIcon = LoadIcon("tray-off.ico");

        _pauseItem = new ToolStripMenuItem("暂停全部限制", null, (_, _) => PauseToggleRequested?.Invoke(this, EventArgs.Empty));

        _menu = new ContextMenuStrip();
        _menu.Items.Add(new ToolStripMenuItem("打开主界面", null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty)));
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty)));

        _icon = new NotifyIcon
        {
            Icon = _runningIcon,
            Text = "文件卫士",
            Visible = true,
            ContextMenuStrip = _menu,
        };

        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 从程序集的 WPF 资源里读托盘图标。资源缺失或损坏时退回系统图标，
    /// 不能让托盘因为一张图起不来。
    /// </summary>
    private static Icon LoadIcon(string fileName)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/Resources/{fileName}", UriKind.Absolute);
            using var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (stream is null) return SystemIcons.Shield;

            return new Icon(stream);
        }
        catch (Exception)
        {
            return SystemIcons.Shield;
        }
    }

    /// <summary>根据运行状态换图标和提示文字。</summary>
    public void SetState(bool running, int entryCount)
    {
        if (_disposed) return;

        _icon.Icon = running ? _runningIcon : _pausedIcon;
        _icon.Text = running
            ? $"文件卫士 · 限制中（{entryCount} 条目）"
            : "文件卫士 · 已暂停";

        _pauseItem.Text = running ? "暂停全部限制" : "恢复全部限制";
    }

    public void NotifyIntercepted(InterceptRecord record)
    {
        if (_disposed) return;

        try
        {
            var kb = record.Size / 1024.0;
            var limit = record.Limit / 1024.0;
            var title = record.Action switch
            {
                InterceptAction.Deleted => "文件超重，已删除",
                InterceptAction.Rejected => "文件超重，写入已拒绝",
                InterceptAction.DeleteFailed => "文件超重，删除失败",
                _ => "文件超重（观察模式）",
            };

            _icon.ShowBalloonTip(
                4000,
                title,
                $"{Path.GetFileName(record.Path)}  {kb:0.#} KB > {limit:0.#} KB",
                ToolTipIcon.Warning);
        }
        catch (Exception)
        {
            // 通知失败不影响拦截。
        }
    }

    /// <summary>虚拟盘挂载失败等错误提示。</summary>
    public void NotifyError(string title, string message)
    {
        if (_disposed) return;

        try
        {
            _icon.ShowBalloonTip(5000, title, message, ToolTipIcon.Error);
        }
        catch (Exception)
        {
            // 通知失败不影响主流程。
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();

        // 退回系统图标时不能释放：SystemIcons 是共享实例。
        if (!ReferenceEquals(_runningIcon, SystemIcons.Shield)) _runningIcon.Dispose();
        if (!ReferenceEquals(_pausedIcon, SystemIcons.Shield)) _pausedIcon.Dispose();
    }
}
