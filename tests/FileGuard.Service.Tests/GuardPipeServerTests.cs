using FileGuard.Core.Control;
using Xunit;

namespace FileGuard.Service.Tests;

/// <summary>
/// 服务端管道循环的集成测试：多个客户端连接依次进来，服务端必须持续工作。
/// 这条用例专门覆盖「自定义 DACL 缺少 FILE_CREATE_PIPE_INSTANCE 时，
/// 处理完第一条连接就再也创建不出新实例」的坑。
/// </summary>
public class GuardPipeServerTests
{
    [Fact]
    public async Task Server_AcceptsMultipleSequentialConnections()
    {
        var pipeName = "FileGuard.Test.Server." + Guid.NewGuid().ToString("N");
        var state = new GuardianState();
        var log = new FakeServiceLog();
        var admin = new FakeAdminChecker { Result = false };
        var server = new GuardPipeServer(new ControlMessageHandler(state, admin, log), log, pipeName);

        await server.StartAsync(CancellationToken.None);
        try
        {
            var client = new GuardPipeClient(pipeName, TimeSpan.FromSeconds(3));

            var hello = await client.SendAsync(GuardMessage.Hello(11));
            Assert.NotNull(hello);
            Assert.Equal(GuardMessageTypes.HelloAck, hello!.Type);
            Assert.Equal(11, state.AppPid);

            var status = await client.SendAsync(GuardMessage.Status());
            Assert.NotNull(status);
            Assert.Equal(GuardMessageTypes.StatusAck, status!.Type);
            Assert.Equal(11, status.AppPid);

            var denied = await client.SendAsync(GuardMessage.ExitRequest());
            Assert.NotNull(denied);
            Assert.Equal(GuardMessageTypes.ExitDenied, denied!.Type);
            Assert.Equal("需要管理员权限", denied.Reason);
            Assert.False(state.ExitApproved);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.DoesNotContain(log.Errors, message => message.Contains("创建控制管道失败"));
        Assert.DoesNotContain(log.Errors, message => message.Contains("处理控制管道连接异常"));
    }
}
