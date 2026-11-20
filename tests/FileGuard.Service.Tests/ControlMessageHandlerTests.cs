using FileGuard.Core.Control;
using FileGuard.Service;
using Xunit;

namespace FileGuard.Service.Tests;

public class ControlMessageHandlerTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly GuardianState _state = new();
    private readonly FakeAdminChecker _admin = new();
    private readonly FakeServiceLog _log = new();

    private ControlMessageHandler CreateHandler() => new(_state, _admin, _log, () => FixedNow);

    [Fact]
    public void Hello_RecordsPid_ClearsApproval_AndAcks()
    {
        _state.ApproveExit(FixedNow);
        Assert.True(_state.ExitApproved);

        var response = CreateHandler().Handle(GuardMessage.Hello(9001), clientProcessId: 9001);

        Assert.Equal(GuardMessageTypes.HelloAck, response.Type);
        Assert.True(response.Guarding);
        Assert.False(_state.ExitApproved);
        Assert.Equal(DateTimeOffset.MinValue, _state.ApprovedAt);
        Assert.Equal(9001, _state.AppPid);
    }

    [Fact]
    public void Status_ReportsGuardingAndPid()
    {
        _state.MarkHello(1234);

        var response = CreateHandler().Handle(GuardMessage.Status(), clientProcessId: 1234);

        Assert.Equal(GuardMessageTypes.StatusAck, response.Type);
        Assert.True(response.Guarding);
        Assert.Equal(1234, response.AppPid);
    }

    [Fact]
    public void Status_AfterApprovedExit_ReportsNotGuarding()
    {
        _state.MarkHello(1234);
        _state.ApproveExit(FixedNow);

        var response = CreateHandler().Handle(GuardMessage.Status(), clientProcessId: 1234);

        Assert.False(response.Guarding);
    }

    [Fact]
    public void ExitRequest_FromAdmin_IsApproved()
    {
        _admin.Result = true;

        var response = CreateHandler().Handle(GuardMessage.ExitRequest(), clientProcessId: 4321);

        Assert.Equal(GuardMessageTypes.ExitApproved, response.Type);
        Assert.True(_state.ExitApproved);
        Assert.Equal(FixedNow, _state.ApprovedAt);
        Assert.Equal(1, _admin.Calls);
        Assert.Equal(4321u, _admin.LastClientPid);
    }

    [Fact]
    public void ExitRequest_FromNonAdmin_IsDenied_AndStateUnchanged()
    {
        _admin.Result = false;

        var response = CreateHandler().Handle(GuardMessage.ExitRequest(), clientProcessId: 4321);

        Assert.Equal(GuardMessageTypes.ExitDenied, response.Type);
        Assert.Equal("需要管理员权限", response.Reason);
        Assert.False(_state.ExitApproved);
        Assert.Equal(4321u, _admin.LastClientPid);
    }

    [Fact]
    public void ExitRequest_UsesServerSideClientPid_NotMessagePayload()
    {
        // 即便客户端在 JSON 里伪造 pid，服务端也必须用管道句柄拿到的真实客户端 PID。
        _admin.Result = false;
        var spoofed = new GuardMessage { Type = GuardMessageTypes.ExitRequest, Pid = 4 };

        var response = CreateHandler().Handle(spoofed, clientProcessId: 7777);

        Assert.Equal(GuardMessageTypes.ExitDenied, response.Type);
        Assert.Equal(7777u, _admin.LastClientPid);
    }

    [Fact]
    public void UnknownType_ReturnsError()
    {
        var response = CreateHandler().Handle(
            new GuardMessage { Type = "drop-table" }, clientProcessId: 1);

        Assert.Equal(GuardMessageTypes.Error, response.Type);
        Assert.Contains("drop-table", response.Reason);
    }
}
