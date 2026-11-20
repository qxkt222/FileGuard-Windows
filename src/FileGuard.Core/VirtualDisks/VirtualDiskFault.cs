namespace FileGuard.Core.VirtualDisks;

/// <summary>虚拟盘挂载、恢复或卸载失败的事件参数。界面和托盘靠它提示用户。</summary>
public sealed class VirtualDiskFault : EventArgs
{
    public VirtualDiskFault(Guid entryId, string path, string message)
    {
        EntryId = entryId;
        Path = path;
        Message = message;
    }

    public Guid EntryId { get; }

    /// <summary>条目对应的真实目录。</summary>
    public string Path { get; }

    /// <summary>给用户看的原因。</summary>
    public string Message { get; }
}
