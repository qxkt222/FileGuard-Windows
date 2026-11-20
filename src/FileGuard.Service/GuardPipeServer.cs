using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using FileGuard.Core.Control;
using FileGuard.Service.Native;
using Microsoft.Extensions.Hosting;

namespace FileGuard.Service;

/// <summary>
/// 控制管道服务端：<c>\\.\pipe\FileGuard.Control</c>，一行一个 JSON 对象。
/// 每个连接单独处理，单个连接里的异常（客户端断开、半行 JSON、乱写）只影响这个连接。
/// </summary>
public sealed class GuardPipeServer : BackgroundService
{
    /// <summary>同时允许排队的服务端实例数；多余的客户端会在 Connect 阶段等待。</summary>
    public const int MaxServerInstances = 8;

    private const int BufferSize = 4096;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ControlMessageHandler _handler;
    private readonly IServiceLog _log;
    private readonly string _pipeName;

    /// <param name="pipeName">管道名；默认 <see cref="GuardProtocol.PipeName"/>，测试可传唯一名字。</param>
    public GuardPipeServer(ControlMessageHandler handler, IServiceLog log, string? pipeName = null)
    {
        _handler = handler;
        _log = log;
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? GuardProtocol.PipeName : pipeName;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.Info($@"控制管道开始监听 \\.\pipe\{_pipeName}");
        var connections = new List<Task>();

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = CreatePipe();
            }
            catch (Exception ex)
            {
                _log.Error("创建控制管道失败，2 秒后重试", ex);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            try
            {
                await server.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                server.Dispose();
                break;
            }
            catch (Exception ex)
            {
                _log.Error("等待管道连接失败", ex);
                server.Dispose();
                continue;
            }

            // 已连接：交给独立任务处理，主循环立即准备下一个实例。
            connections.RemoveAll(static task => task.IsCompleted);
            connections.Add(Task.Run(() => HandleConnectionAsync(server, stoppingToken), CancellationToken.None));
        }

        try
        {
            await Task.WhenAll(connections)
                .WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 停止阶段不关心残余连接的状态。
        }

        _log.Info("控制管道已停止");
    }

    /// <summary>
    /// 显式设置管道 ACL：Authenticated Users 可读写（普通用户也能连进来问状态、
    /// 发 hello），SYSTEM 完全控制。不加这条，默认 DACL 只允许创建者/管理员，普通进程连不上。
    /// </summary>
    internal static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // 创建「下一个」管道实例时，Windows 会检查调用方对已存在实例是否拥有
        // FILE_CREATE_PIPE_INSTANCE（PipeAccessRights.CreateNewInstance）。上面的
        // Authenticated Users 规则只有 ReadWrite，不含该权限；服务以 SYSTEM 运行时靠
        // SYSTEM 的 FullControl 规则没问题，但交互调试（以普通用户跑 exe）时第二个实例
        // 会直接 UnauthorizedAccessException。这里给「服务自身账户」补 FullControl，
        // 生产环境下它就是 SYSTEM（与上一条重复，跳过），不会额外放宽给普通用户。
        var currentUser = WindowsIdentity.GetCurrent().User;
        var localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (currentUser is not null && !currentUser.Equals(localSystem))
        {
            security.AddAccessRule(new PipeAccessRule(
                currentUser,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
        }

        return security;
    }

    private NamedPipeServerStream CreatePipe() => NamedPipeServerStreamAcl.Create(
        _pipeName,
        PipeDirection.InOut,
        MaxServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous,
        BufferSize,
        BufferSize,
        CreatePipeSecurity());

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken stoppingToken)
    {
        using (server)
        {
            try
            {
                var clientProcessId = GetClientProcessId(server);

                using var reader = new StreamReader(server, Utf8NoBom, detectEncodingFromByteOrderMarks: false, BufferSize, leaveOpen: true);
                using var writer = new StreamWriter(server, Utf8NoBom, BufferSize, leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\n",
                };

                while (!stoppingToken.IsCancellationRequested)
                {
                    string? line;
                    try
                    {
                        line = await reader.ReadLineAsync()
                            .WaitAsync(stoppingToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (line is null)
                    {
                        break; // 客户端关闭。
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var message = GuardProtocol.Deserialize(line);
                    if (message is null)
                    {
                        await WriteAsync(writer, GuardMessage.Error("无法解析的 JSON"), stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    var response = _handler.Handle(message, clientProcessId);
                    await WriteAsync(writer, response, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // 服务停止。
            }
            catch (IOException)
            {
                // 客户端中断连接。
            }
            catch (Exception ex)
            {
                _log.Error("处理控制管道连接异常", ex);
            }
        }
    }

    private static async Task WriteAsync(StreamWriter writer, GuardMessage message, CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(GuardProtocol.Serialize(message).AsMemory(), cancellationToken)
            .ConfigureAwait(false);
    }

    private static uint GetClientProcessId(NamedPipeServerStream server)
    {
        try
        {
            return NativeMethods.GetNamedPipeClientProcessId(server.SafePipeHandle, out var pid)
                ? pid
                : 0u;
        }
        catch (Exception)
        {
            return 0u;
        }
    }
}
