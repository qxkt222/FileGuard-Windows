using FileGuard.Core.Control;

namespace FileGuard.Service;

/// <summary>
/// 把一条协议消息变成一条响应消息。纯逻辑，不碰管道，方便单测（批准/拒绝分支）。
/// </summary>
public sealed class ControlMessageHandler
{
    private readonly GuardianState _state;
    private readonly IClientAdminChecker _adminChecker;
    private readonly IServiceLog _log;
    private readonly Func<DateTimeOffset> _clock;

    /// <param name="clock">注入时钟，测试可固定时间；默认取系统 UTC 时间。</param>
    public ControlMessageHandler(
        GuardianState state,
        IClientAdminChecker adminChecker,
        IServiceLog log,
        Func<DateTimeOffset>? clock = null)
    {
        _state = state;
        _adminChecker = adminChecker;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public GuardMessage Handle(GuardMessage message, uint clientProcessId) => message.Type switch
    {
        GuardMessageTypes.Hello => OnHello(message),
        GuardMessageTypes.ExitRequest => OnExitRequest(clientProcessId),
        GuardMessageTypes.Status => GuardMessage.StatusAck(_state.IsGuarding, _state.AppPid),
        _ => GuardMessage.Error($"未知消息类型：{message.Type}"),
    };

    private GuardMessage OnHello(GuardMessage message)
    {
        var pid = message.Pid ?? 0;
        _state.MarkHello(pid);
        _log.Info($"主程序已报到 pid={pid}，回到守护状态");
        return GuardMessage.HelloAck();
    }

    private GuardMessage OnExitRequest(uint clientProcessId)
    {
        if (!_adminChecker.IsClientAdmin(clientProcessId))
        {
            _log.Info($"拒绝退出请求：调用方 pid={clientProcessId} 不是管理员");
            return GuardMessage.ExitDenied("需要管理员权限");
        }

        _state.ApproveExit(_clock());
        _log.Info($"批准退出请求：调用方 pid={clientProcessId}");
        return GuardMessage.ExitApproved();
    }
}
