namespace FileGuard.Core.Models;

/// <summary>条目的拦截方式。</summary>
public enum GuardMode
{
    /// <summary>监视目录：文件写入完成后再删除超限文件（现有行为，默认）。</summary>
    Watch,

    /// <summary>用虚拟盘接管目录：写入超限时当场失败，文件根本落不了盘。</summary>
    VirtualDisk,
}
