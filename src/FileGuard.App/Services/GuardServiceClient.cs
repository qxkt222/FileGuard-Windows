using System.ServiceProcess;
using FileGuard.Core.Control;

namespace FileGuard.App.Services;

/// <summary>守护服务在主程序眼里的可用状态。</summary>
public enum GuardServiceStatus
{
    /// <summary>Windows 服务没有安装。</summary>
    NotInstalled,

    /// <summary>服务已安装，但当前不在运行。</summary>
    Stopped,

    /// <summary>服务已安装且在运行（控制管道能应答，或 SCM 报告 Running）。</summary>
    Running,

    /// <summary>问不出来（拒绝访问、SCM 不可用等）；调用方按「不确定」处理。</summary>
    Unknown,
}

/// <summary>
/// 主程序访问守护服务的唯一入口：优先走控制管道，连不上再问 SCM。
/// 所有方法都不抛异常，最坏情况返回 <see cref="GuardServiceStatus.Unknown"/> 或 null，
/// 方便退出流程在 UI 线程上「尽力而为」地询问服务。
/// </summary>
public sealed class GuardServiceClient
{
    private readonly GuardPipeClient _pipe;

    public GuardServiceClient(GuardPipeClient? pipe = null)
    {
        _pipe = pipe ?? new GuardPipeClient();
    }

    /// <summary>进程内共享实例（管道客户端本身无状态，复用没有副作用）。</summary>
    public static GuardServiceClient Current { get; } = new();

    /// <summary>
    /// 查询守护服务状态：
    /// 控制管道能回 <c>status-ack</c> → Running；否则用 <see cref="ServiceController"/> 兜底。
    /// 管道只在服务运行时才存在，所以「管道没回应」不等于服务没运行（可能正忙/重启中），必须再问 SCM。
    /// </summary>
    public async Task<GuardServiceStatus> QueryAsync()
    {
        try
        {
            var ack = await _pipe.SendAsync(GuardMessage.Status()).ConfigureAwait(false);
            if (ack?.Type == GuardMessageTypes.StatusAck)
            {
                return GuardServiceStatus.Running;
            }
        }
        catch (Exception)
        {
            // GuardPipeClient 约定不抛异常；这里再兜一层，防止未来改动破坏调用方。
        }

        return QueryServiceController();
    }

    /// <summary>后台发一条 hello 让服务回到守护状态；服务不可用时返回 null。</summary>
    public async Task<GuardMessage?> SendHelloAsync(int pid)
    {
        try
        {
            return await _pipe.SendAsync(GuardMessage.Hello(pid)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>同步发退出请求，最多阻塞 3 秒（<see cref="GuardPipeClient.Send"/> 的超时）。</summary>
    public GuardMessage? RequestExit()
    {
        try
        {
            return _pipe.Send(GuardMessage.ExitRequest());
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static GuardServiceStatus QueryServiceController()
    {
        try
        {
            using var controller = new ServiceController(GuardProtocol.ServiceName);
            return controller.Status switch
            {
                ServiceControllerStatus.Running => GuardServiceStatus.Running,
                ServiceControllerStatus.Stopped => GuardServiceStatus.Stopped,
                _ => GuardServiceStatus.Unknown,
            };
        }
        catch (InvalidOperationException)
        {
            // ServiceController 找不到指定服务时抛 InvalidOperationException（错误码 1060）。
            return GuardServiceStatus.NotInstalled;
        }
        catch (Exception)
        {
            // 拒绝访问、SCM 无法连接等：宁可说「不知道」，也不要谎报「未安装」。
            return GuardServiceStatus.Unknown;
        }
    }
}
