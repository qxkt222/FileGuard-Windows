using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FileGuard.App.Services;
using FileGuard.Core.Models;
using FileGuard.Core.Rules;
using FileGuard.Core.Watching;

namespace FileGuard.App.Views;

public partial class EntryFlowView : UserControl
{
    private readonly MainWindow _host;
    private readonly GuardEntry? _existing;
    private readonly GuardEntry _draft;
    private readonly List<CheckBox> _exemptChecks = new();
    private int _step;
    private bool _hasSubDirs;
    private bool _loading;

    public EntryFlowView(MainWindow host, GuardEntry? existing = null, int startStep = 0)
    {
        _host = host;
        _existing = existing;

        if (existing is null)
        {
            var options = AppState.Current.Options;
            _draft = new GuardEntry
            {
                MaxBytes = options.DefaultMaxBytes,
                Mode = options.DefaultMode,
                WatchWrite = options.DefaultWatchWrite,
                WatchCreate = options.DefaultWatchCreate,
                CheckExistingFiles = options.DefaultCheckExistingFiles,
            };
        }
        else
        {
            _draft = existing.Clone();
            _hasSubDirs = HasSubDirectories(_draft.RootPath);
        }

        InitializeComponent();

        if (_existing is null)
        {
            ShowStep1();
        }
        else if (startStep == 2 && _hasSubDirs && _draft.Mode == GuardMode.Watch)
        {
            PathBox.Text = _draft.RootPath;
            ShowStep2();
        }
        else
        {
            PathBox.Text = _draft.RootPath;
            ShowStep3();
        }
    }

    // ---------- 步骤切换 ----------

    private void ShowStep1()
    {
        _step = 1;
        Step1Panel.Visibility = Visibility.Visible;
        Step2Panel.Visibility = Visibility.Collapsed;
        Step3Panel.Visibility = Visibility.Collapsed;

        StepTitle.Text = "第一步 · 选择监视文件夹";
        StepHint.Text = "一个条目监视一个文件夹。选好后会检查里面有没有子文件夹。";
        BackBtn.Content = "取消";
        PrimaryBtn.Content = "下一步";
        PrimaryBtn.IsEnabled = !string.IsNullOrWhiteSpace(_draft.RootPath);

        PathBox.Text = _draft.RootPath;
    }

    private void ShowStep2()
    {
        _step = 2;
        Step1Panel.Visibility = Visibility.Collapsed;
        Step2Panel.Visibility = Visibility.Visible;
        Step3Panel.Visibility = Visibility.Collapsed;

        StepTitle.Text = "第二步 · 选择豁免文件夹";
        StepHint.Text = "这个目录里有子文件夹。勾选不想监视的，其余的都会被监视。";
        BackBtn.Content = "返回";
        PrimaryBtn.Content = "确定";
        PrimaryBtn.IsEnabled = true;

        BuildExemptList(ExpandAllBox.IsChecked == true);
    }

    private void ShowStep3()
    {
        _step = 3;
        Step1Panel.Visibility = Visibility.Collapsed;
        Step2Panel.Visibility = Visibility.Collapsed;
        Step3Panel.Visibility = Visibility.Visible;

        StepTitle.Text = "第三步 · 监视设置";
        StepHint.Text = _draft.Mode == GuardMode.VirtualDisk
            ? "确认后目录会由虚拟盘接管，超过上限的写入当场失败。"
            : "确认后条目立即生效。超过上限的文件会被删除。";
        BackBtn.Content = "返回";
        PrimaryBtn.Content = "确认";
        PrimaryBtn.IsEnabled = true;

        LoadDraftIntoControls();
    }

    // ---------- 第一步 ----------

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择要监视的文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        _draft.RootPath = dialog.SelectedPath;
        PathBox.Text = dialog.SelectedPath;
        PrimaryBtn.IsEnabled = true;

        _hasSubDirs = HasSubDirectories(dialog.SelectedPath);
        PathHint.Text = _draft.Mode == GuardMode.VirtualDisk
            ? "虚拟盘模式会接管整个目录，不需要选择豁免。"
            : _hasSubDirs
                ? "发现子文件夹：下一步选择豁免目录。"
                : "没有子文件夹：直接进入监视设置。";
    }

    // ---------- 第二步 ----------

    private void OnExpandToggle(object sender, RoutedEventArgs e)
    {
        if (_step == 2) BuildExemptList(ExpandAllBox.IsChecked == true);
    }

    private void BuildExemptList(bool recursive)
    {
        ExemptHost.Children.Clear();
        _exemptChecks.Clear();

        if (!Directory.Exists(_draft.RootPath)) return;

        IEnumerable<string> dirs;
        try
        {
            dirs = recursive
                ? Directory.EnumerateDirectories(_draft.RootPath, "*", SearchOption.AllDirectories)
                : Directory.EnumerateDirectories(_draft.RootPath);
        }
        catch (Exception)
        {
            return;
        }

        var root = Path.GetFullPath(_draft.RootPath);
        var rules = PathRules.Default;

        foreach (var dir in dirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            // 默认忽略目录（node_modules、.git 等）不列出来，它们本来就不监视。
            if (rules.IsIgnoredPath(Path.Combine(dir, "_probe_"), root)) continue;

            string rel;
            try { rel = Path.GetRelativePath(root, dir); }
            catch (ArgumentException) { rel = dir; }

            var depth = rel.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar);

            var check = new CheckBox
            {
                Content = rel,
                Tag = dir,
                Margin = new Thickness(depth * 18, 2, 0, 2),
                IsChecked = _draft.ExemptPaths.Any(p => PathRules.IsUnder(dir, p) || PathRules.IsUnder(p, dir)),
            };

            _exemptChecks.Add(check);
            ExemptHost.Children.Add(check);
        }

        if (_exemptChecks.Count == 0)
        {
            ExemptHost.Children.Add(new TextBlock
            {
                Text = "没有可选的子文件夹。",
                Foreground = System.Windows.Media.Brushes.Gray,
                Margin = new Thickness(0, 8, 0, 0),
            });
        }
    }

    private void CollectExempt()
    {
        _draft.ExemptPaths = _exemptChecks
            .Where(c => c.IsChecked == true)
            .Select(c => (string)c.Tag)
            .ToList();
    }

    // ---------- 第三步 ----------

    private void LoadDraftIntoControls()
    {
        _loading = true;
        try
        {
            WatchModeRadio.IsChecked = _draft.Mode == GuardMode.Watch;
            DiskModeRadio.IsChecked = _draft.Mode == GuardMode.VirtualDisk;

            LimitBox.IsChecked = _draft.LimitEnabled;
            MaxKbBox.Text = (_draft.MaxBytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture);
            WriteBox.IsChecked = _draft.WatchWrite;
            CreateBox.IsChecked = _draft.WatchCreate;
            RenameBox.IsChecked = _draft.WatchRename;
            ExistingBox.IsChecked = _draft.CheckExistingFiles;
            RuleFileBox.IsChecked = AppState.Current.Options.WriteRuleFile;
        }
        finally
        {
            _loading = false;
        }

        UpdateModePanels();
    }

    private void CollectStep3()
    {
        _draft.Mode = DiskModeRadio.IsChecked == true ? GuardMode.VirtualDisk : GuardMode.Watch;
        _draft.MaxBytes = ParseMaxKb();

        if (_draft.Mode == GuardMode.VirtualDisk)
        {
            // 虚拟盘没有观察模式：上限始终生效。事件勾选和豁免在提交时丢弃，
            // 这样只是临时切回监视模式时不会把原来的豁免弄丢。
            _draft.LimitEnabled = true;
            return;
        }

        _draft.LimitEnabled = LimitBox.IsChecked == true;
        _draft.WatchWrite = WriteBox.IsChecked == true;
        _draft.WatchCreate = CreateBox.IsChecked == true;
        _draft.WatchRename = RenameBox.IsChecked == true;
        _draft.CheckExistingFiles = ExistingBox.IsChecked == true;
    }

    // ---------- 模式切换 ----------

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var newMode = DiskModeRadio.IsChecked == true ? GuardMode.VirtualDisk : GuardMode.Watch;

        // 已存在的条目改模式意味着重建：监视要拆 watcher，虚拟盘要卸了再挂。
        if (_existing is not null && newMode != _draft.Mode)
        {
            var ok = MessageBox.Show(
                "切换模式会重建条目：\n" +
                "· 切到虚拟盘：会挂载目录（短暂改名后挂回）\n" +
                "· 切到监视：会卸载虚拟盘并还原目录\n\n确定切换吗？",
                "切换拦截模式",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (ok != MessageBoxResult.Yes)
            {
                _loading = true;
                try
                {
                    WatchModeRadio.IsChecked = _draft.Mode == GuardMode.Watch;
                    DiskModeRadio.IsChecked = _draft.Mode == GuardMode.VirtualDisk;
                }
                finally
                {
                    _loading = false;
                }
                return;
            }
        }

        _draft.Mode = newMode;
        UpdateModePanels();
    }

    private void UpdateModePanels()
    {
        var isDisk = _draft.Mode == GuardMode.VirtualDisk;

        WatchOptionsPanel.Visibility = isDisk ? Visibility.Collapsed : Visibility.Visible;
        DiskOptionsPanel.Visibility = isDisk ? Visibility.Visible : Visibility.Collapsed;

        ModeHint.Text = isDisk
            ? "目录由虚拟盘接管，只保留单文件上限；事件勾选与豁免目录不适用。"
            : "文件先写入、超限后删除；可设置监视事件和豁免目录。";

        if (_step == 3)
        {
            StepHint.Text = isDisk
                ? "确认后目录会由虚拟盘接管，超过上限的写入当场失败。"
                : "确认后条目立即生效。超过上限的文件会被删除。";
        }
    }

    private long ParseMaxKb()
    {
        var text = MaxKbBox.Text.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var kb) && kb > 0)
        {
            return (long)Math.Round(kb * 1024);
        }
        return GuardEntry.DefaultMaxBytes;
    }

    // ---------- 按钮 ----------

    private void OnPrimary(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case 1:
                if (!Directory.Exists(_draft.RootPath))
                {
                    MessageBox.Show("请先选一个存在的文件夹。", "文件卫士");
                    return;
                }
                _hasSubDirs = HasSubDirectories(_draft.RootPath);
                // 虚拟盘接管整个目录，没有豁免这一步。
                if (_hasSubDirs && _draft.Mode == GuardMode.Watch) ShowStep2();
                else ShowStep3();
                break;

            case 2:
                CollectExempt();
                ShowStep3();
                break;

            case 3:
                CollectStep3();
                Commit();
                break;
        }
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case 1:
                _host.ShowHome();
                break;

            case 2:
                if (_existing is null) ShowStep1();
                else _host.ShowHome();
                break;

            case 3:
                CollectStep3();
                if (_hasSubDirs && _draft.Mode == GuardMode.Watch) ShowStep2();
                else if (_existing is null) ShowStep1();
                else _host.ShowHome();
                break;
        }
    }

    private void Commit()
    {
        var state = AppState.Current;

        // 虚拟盘接管整个目录，豁免目录没有意义，别留在配置里误导人。
        if (_draft.Mode == GuardMode.VirtualDisk)
        {
            _draft.ExemptPaths.Clear();
        }

        if (_existing is null)
        {
            state.AddEntry(_draft);
        }
        else
        {
            var list = state.Config.Entries;
            var index = list.FindIndex(x => x.Id == _draft.Id);
            if (index >= 0) list[index] = _draft;
            state.SaveAndApply();
        }

        if (_draft.Mode == GuardMode.Watch &&
            RuleFileBox.IsChecked == true &&
            Directory.Exists(_draft.RootPath))
        {
            try
            {
                RuleFileWriter.Write(
                    _draft.RootPath, _draft, state.Options.QuarantineKeep, state.Options.RuleFileName);
            }
            catch (Exception) { /* 写规则文件失败不影响条目 */ }
        }

        _host.ShowHome();
    }

    private static bool HasSubDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) && Directory.EnumerateDirectories(path).Any();
        }
        catch (Exception)
        {
            return false;
        }
    }
}
