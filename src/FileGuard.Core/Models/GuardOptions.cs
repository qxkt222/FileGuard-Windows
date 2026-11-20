namespace FileGuard.Core.Models;

/// <summary>全局设置。设置界面改这里，支持一键恢复默认。</summary>
public sealed class GuardOptions
{
    public long DefaultMaxBytes { get; set; } = GuardEntry.DefaultMaxBytes;

    /// <summary>新建条目默认使用的拦截方式。</summary>
    public GuardMode DefaultMode { get; set; } = GuardMode.Watch;

    public bool DefaultWatchWrite { get; set; } = true;
    public bool DefaultWatchCreate { get; set; }
    public bool DefaultCheckExistingFiles { get; set; }

    /// <summary>开机自启。</summary>
    public bool StartWithWindows { get; set; } = true;

    /// <summary>启动后直接最小化到托盘。</summary>
    public bool StartMinimized { get; set; } = true;

    /// <summary>非管理员限制：开启后非管理员不能退出或停止保护。</summary>
    public bool NonAdminLock { get; set; } = true;

    /// <summary>删除前备份到隔离区。</summary>
    public bool EnableQuarantine { get; set; } = true;

    /// <summary>隔离区保留最近多少次。</summary>
    public int QuarantineKeep { get; set; } = 50;

    /// <summary>建条目时是否在监视目录写规则说明文件，让 AI 事先知道限额。</summary>
    public bool WriteRuleFile { get; set; } = true;

    public string RuleFileName { get; set; } = ".guard-rules.md";

    /// <summary>托盘面板显示最近几条拦截记录。</summary>
    public int RecentRecordCount { get; set; } = 5;

    public GuardOptions Clone() => new()
    {
        DefaultMaxBytes = DefaultMaxBytes,
        DefaultMode = DefaultMode,
        DefaultWatchWrite = DefaultWatchWrite,
        DefaultWatchCreate = DefaultWatchCreate,
        DefaultCheckExistingFiles = DefaultCheckExistingFiles,
        StartWithWindows = StartWithWindows,
        StartMinimized = StartMinimized,
        NonAdminLock = NonAdminLock,
        EnableQuarantine = EnableQuarantine,
        QuarantineKeep = QuarantineKeep,
        WriteRuleFile = WriteRuleFile,
        RuleFileName = RuleFileName,
        RecentRecordCount = RecentRecordCount,
    };
}
