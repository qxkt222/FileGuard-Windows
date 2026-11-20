namespace FileGuard.Core.Watching;

public enum WatchChangeKind
{
    Created,
    Changed,
    Renamed,
    Deleted,
}

/// <summary>Watcher 判定可能超限、交给运行时处理的一个文件。</summary>
public sealed record CandidateFile(
    string FullPath,
    long Length,
    bool IsNew,
    WatchChangeKind Kind);
