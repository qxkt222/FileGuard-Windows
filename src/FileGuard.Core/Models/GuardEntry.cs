namespace FileGuard.Core.Models;

/// <summary>一条监视条目：一个监视文件夹 + 一套规则。</summary>
public sealed class GuardEntry
{
    public const long DefaultMaxBytes = 20 * 1024;

    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>监视文件夹（绝对路径）。</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>停用后整条规则不工作。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 拦截方式。默认 <see cref="GuardMode.Watch"/>；旧配置文件没有该字段时也按监视模式读。
    /// </summary>
    public GuardMode Mode { get; set; } = GuardMode.Watch;

    /// <summary>不勾选时只记录不删除（观察模式）。</summary>
    public bool LimitEnabled { get; set; } = true;

    /// <summary>单个文件允许的最大字节数，默认 20 KB。</summary>
    public long MaxBytes { get; set; } = DefaultMaxBytes;

    public bool WatchWrite { get; set; } = true;
    public bool WatchCreate { get; set; }
    public bool WatchDelete { get; set; }
    public bool WatchRename { get; set; }

    /// <summary>
    /// 默认只处理条目生效之后新建的文件；为 true 时，生效前已存在的文件超限也处理。
    /// </summary>
    public bool CheckExistingFiles { get; set; }

    /// <summary>豁免的子目录（绝对路径，不区分大小写）。</summary>
    public List<string> ExemptPaths { get; set; } = new();

    public GuardEntry Clone() => new()
    {
        Id = Id,
        RootPath = RootPath,
        Enabled = Enabled,
        Mode = Mode,
        LimitEnabled = LimitEnabled,
        MaxBytes = MaxBytes,
        WatchWrite = WatchWrite,
        WatchCreate = WatchCreate,
        WatchDelete = WatchDelete,
        WatchRename = WatchRename,
        CheckExistingFiles = CheckExistingFiles,
        ExemptPaths = new List<string>(ExemptPaths),
    };
}
