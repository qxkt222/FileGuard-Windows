namespace FileGuard.Core.Models;

public enum InterceptAction
{
    /// <summary>观察模式：只记录，没删。</summary>
    Observed,

    /// <summary>已隔离并删除。</summary>
    Deleted,

    /// <summary>超过重试时间仍然删不掉。</summary>
    DeleteFailed,

    /// <summary>虚拟盘模式：写入当场被拒绝（文件未落盘）。</summary>
    Rejected,
}

/// <summary>一次超限拦截的完整记录。</summary>
public sealed record InterceptRecord(
    DateTimeOffset At,
    Guid EntryId,
    string Path,
    long Size,
    long Limit,
    InterceptAction Action);
