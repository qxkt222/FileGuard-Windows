using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FileGuard.App.Services;
using FileGuard.Core.Models;

namespace FileGuard.App.Views;

public partial class HomeView : UserControl
{
    private readonly MainWindow _host;

    private static readonly Brush CardBorder = new SolidColorBrush(Color.FromRgb(229, 231, 235));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(107, 114, 128));
    private static readonly Brush Danger = new SolidColorBrush(Color.FromRgb(185, 28, 28));

    public HomeView(MainWindow host)
    {
        _host = host;
        InitializeComponent();
        Reload();
    }

    private void Reload()
    {
        var entries = AppState.Current.Entries;

        Title.Text = entries.Count == 0 ? "还没有监视条目" : "监视条目";
        Subtitle.Text = entries.Count == 0
            ? "建立第一条规则：选一个文件夹，设定单文件上限，超了就删。"
            : $"{entries.Count} 个条目 · 点「进入」可以改模式、豁免或阈值";

        EntryHost.Children.Clear();
        foreach (var entry in entries)
        {
            EntryHost.Children.Add(BuildCard(entry));
        }
    }

    private FrameworkElement BuildCard(GuardEntry entry)
    {
        var border = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18),
            Margin = new Thickness(0, 0, 0, 12),
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        info.Children.Add(new TextBlock
        {
            Text = entry.RootPath,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        var detail = new StringBuilder();
        detail.Append($"单文件上限 {entry.MaxBytes / 1024.0:0.#} KB");

        if (entry.Mode == GuardMode.VirtualDisk)
        {
            detail.Append(" · 虚拟盘接管 · 写入超限当场失败");
            if (entry.Enabled)
            {
                detail.Append(" · " + VirtualDiskStatus(entry));
            }
        }
        else
        {
            detail.Append(" · 监视 " + DescribeEvents(entry));
            if (entry.ExemptPaths.Count > 0)
            {
                detail.Append($" · 豁免 {entry.ExemptPaths.Count} 个目录");
            }
            if (entry.CheckExistingFiles)
            {
                detail.Append(" · 已存在文件也管");
            }
        }

        info.Children.Add(new TextBlock
        {
            Text = detail.ToString(),
            Foreground = Muted,
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });

        if (entry.Mode == GuardMode.VirtualDisk && entry.Enabled)
        {
            var error = AppState.Current.VirtualDisks.GetError(entry.Id);
            if (error is not null)
            {
                info.Children.Add(new TextBlock
                {
                    Text = "挂载失败：" + error,
                    Foreground = Danger,
                    Margin = new Thickness(0, 4, 0, 0),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
        }

        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var modeChip = new Border
        {
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(Color.FromRgb(243, 244, 246)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(209, 213, 219)),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = entry.Mode == GuardMode.VirtualDisk ? "虚拟盘" : "监视",
                Foreground = new SolidColorBrush(Color.FromRgb(55, 65, 81)),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
            },
        };
        actions.Children.Add(modeChip);

        var stateChip = new Border
        {
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 14, 0),
            Background = StateBrush(entry),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = StateText(entry),
                Foreground = Brushes.White,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
            },
        };
        actions.Children.Add(stateChip);

        var enterBtn = new Button { Content = "进入" };
        enterBtn.Click += (_, _) => OnEnter(entry);
        actions.Children.Add(enterBtn);

        var toggleBtn = new Button { Content = entry.Enabled ? "停用" : "启用" };
        toggleBtn.Click += (_, _) => OnToggle(entry);
        actions.Children.Add(toggleBtn);

        var deleteBtn = new Button { Content = "删除", Style = (Style)FindResource("DangerButton") };
        deleteBtn.Click += (_, _) => OnDelete(entry);
        actions.Children.Add(deleteBtn);

        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);

        border.Child = grid;
        return border;
    }

    private static string DescribeEvents(GuardEntry e)
    {
        var parts = new List<string>();
        if (e.WatchWrite) parts.Add("写入/修改");
        if (e.WatchCreate) parts.Add("新建文件");
        if (e.WatchRename) parts.Add("重命名");
        return parts.Count == 0 ? "无" : string.Join("、", parts);
    }

    /// <summary>虚拟盘条目的挂载状态文案：已挂载 / 挂载失败 / 已暂停 / 未挂载。</summary>
    private static string VirtualDiskStatus(GuardEntry e)
    {
        var state = AppState.Current;
        if (state.VirtualDisks.GetError(e.Id) is not null) return "挂载失败";
        if (state.VirtualDisks.IsMounted(e.Id)) return "已挂载";
        if (!state.Runtime.IsRunning) return "已暂停";
        return "未挂载";
    }

    private static string StateText(GuardEntry e)
    {
        if (!e.Enabled) return "已停用";

        if (e.Mode == GuardMode.VirtualDisk)
        {
            return VirtualDiskStatus(e) switch
            {
                "已挂载" => "拦截中",
                "挂载失败" => "挂载失败",
                "已暂停" => "已暂停",
                _ => "未挂载",
            };
        }

        return e.LimitEnabled ? "限制中" : "观察模式";
    }

    private static Brush StateBrush(GuardEntry e)
    {
        if (!e.Enabled) return new SolidColorBrush(Color.FromRgb(156, 163, 175));

        if (e.Mode == GuardMode.VirtualDisk)
        {
            return VirtualDiskStatus(e) switch
            {
                "已挂载" => new SolidColorBrush(Color.FromRgb(22, 163, 74)),
                "挂载失败" => new SolidColorBrush(Color.FromRgb(220, 38, 38)),
                "已暂停" => new SolidColorBrush(Color.FromRgb(217, 119, 6)),
                _ => new SolidColorBrush(Color.FromRgb(107, 114, 128)),
            };
        }

        return e.LimitEnabled
            ? new SolidColorBrush(Color.FromRgb(22, 163, 74))
            : new SolidColorBrush(Color.FromRgb(217, 119, 6));
    }

    private void OnEnter(GuardEntry entry)
    {
        // 非管理员限制生效时，改上限/模式等于变相停掉保护。
        if (App.IsProtectionLocked)
        {
            MessageBox.Show("非管理员账户不能修改监视条目。", "文件卫士",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 虚拟盘整个目录被接管，没有豁免概念，直接进设置。
        if (entry.Mode == GuardMode.VirtualDisk)
        {
            _host.ShowEntryFlow(entry, 3);
            return;
        }

        var hasSubDirs = Directory.Exists(entry.RootPath) && HasSubDirectories(entry.RootPath);

        if (hasSubDirs)
        {
            var result = MessageBox.Show(
                "要修改豁免文件夹吗？\n\n是：进入豁免界面\n否：直接进监视设置",
                "修改豁免",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            _host.ShowEntryFlow(entry, result == MessageBoxResult.Yes ? 2 : 3);
            return;
        }

        _host.ShowEntryFlow(entry, 3);
    }

    private static bool HasSubDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).Any();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void OnToggle(GuardEntry entry)
    {
        // 停用条目等于停止这条保护；启用不受限制。
        if (entry.Enabled && App.IsProtectionLocked)
        {
            MessageBox.Show("非管理员账户不能停用监视条目。", "文件卫士",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        entry.Enabled = !entry.Enabled;
        AppState.Current.SaveAndApply();
        Reload();
    }

    private void OnDelete(GuardEntry entry)
    {
        if (App.IsProtectionLocked)
        {
            MessageBox.Show("非管理员账户不能删除监视条目。", "文件卫士",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show($"删除条目「{entry.RootPath}」？\n不会删除目录里的文件。",
            "删除条目", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        AppState.Current.RemoveEntry(entry.Id);
        Reload();
    }

    // 新建条目只会增加保护范围，不移除已有条目的保护，因此不受非管理员限制。
    private void OnCreate(object sender, RoutedEventArgs e) => _host.ShowEntryFlow();

    private void OnRecent(object sender, RoutedEventArgs e)
    {
        var recent = AppState.Current.Log.Recent;
        if (recent.Count == 0)
        {
            MessageBox.Show("还没有拦截记录。", "最近拦截", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var text = new StringBuilder();
        foreach (var r in recent.Reverse())
        {
            text.AppendLine($"{r.At:MM-dd HH:mm:ss}  {r.Action}  {r.Size / 1024.0:0.#} KB");
            text.AppendLine("  " + r.Path);
            text.AppendLine();
        }

        MessageBox.Show(text.ToString(), "最近拦截", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnQuarantine(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppState.Current.Quarantine.Root);
            Process.Start(new ProcessStartInfo
            {
                FileName = AppState.Current.Quarantine.Root,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打不开隔离区：{ex.Message}", "隔离区", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
