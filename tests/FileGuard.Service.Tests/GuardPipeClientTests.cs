using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using FileGuard.Core.Control;
using Xunit;

namespace FileGuard.Service.Tests;

/// <summary>
/// GuardPipeClient 与真实 NamedPipeServerStream 的端到端测试：
/// 每个用例用独立的管道名，互不干扰。
/// </summary>
public class GuardPipeClientTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static string NewPipeName() => "FileGuard.Test." + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task SendAsync_ExchangesHelloForAck()
    {
        var pipeName = NewPipeName();
        var ready = new TaskCompletionSource();
        var serverSawHello = new TaskCompletionSource<int>();

        var serverTask = Task.Run(async () =>
        {
            using var server = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            ready.SetResult();
            await server.WaitForConnectionAsync();

            using var reader = new StreamReader(server, Utf8NoBom, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(server, Utf8NoBom, 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };

            var line = await reader.ReadLineAsync();
            var hello = GuardProtocol.Deserialize(line);
            Assert.NotNull(hello);
            Assert.Equal(GuardMessageTypes.Hello, hello!.Type);
            serverSawHello.SetResult(hello.Pid ?? -1);

            await writer.WriteLineAsync(GuardProtocol.Serialize(GuardMessage.HelloAck()));
        });

        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var client = new GuardPipeClient(pipeName, TimeSpan.FromSeconds(3));
        var ack = await client.SendAsync(GuardMessage.Hello(5566));

        Assert.NotNull(ack);
        Assert.Equal(GuardMessageTypes.HelloAck, ack!.Type);
        Assert.True(ack.Guarding);
        Assert.Equal(5566, await serverSawHello.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SendAsync_ReturnsNull_WhenServerNeverReplies()
    {
        var pipeName = NewPipeName();
        var ready = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var serverTask = Task.Run(async () =>
        {
            using var server = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            ready.SetResult();
            await server.WaitForConnectionAsync();
            await release.Task;
        });

        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var client = new GuardPipeClient(pipeName, TimeSpan.FromMilliseconds(300));
        var stopwatch = Stopwatch.StartNew();
        var response = await client.SendAsync(GuardMessage.Status());
        stopwatch.Stop();

        Assert.Null(response);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(3));

        release.SetResult();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SendAsync_ReturnsNull_WhenServerNotRunning()
    {
        var client = new GuardPipeClient(NewPipeName(), TimeSpan.FromMilliseconds(300));
        var stopwatch = Stopwatch.StartNew();

        var response = await client.SendAsync(GuardMessage.Status());

        stopwatch.Stop();
        Assert.Null(response);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3));
    }
}
