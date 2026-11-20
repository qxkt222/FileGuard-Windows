using System.IO.Pipes;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileGuard.Core.Control;

/// <summary>控制管道上出现的消息类型（string 常量，直接写进 JSON 的 type 字段）。</summary>
public static class GuardMessageTypes
{
    public const string Hello = "hello";
    public const string HelloAck = "hello-ack";
    public const string ExitRequest = "exit-request";
    public const string ExitApproved = "exit-approved";
    public const string ExitDenied = "exit-denied";
    public const string Status = "status";
    public const string StatusAck = "status-ack";
    public const string Error = "error";
}

/// <summary>
/// 主程序 &lt;-&gt; 守护服务的一条控制消息。一行一个 JSON 对象，属性名 camelCase。
/// 空字段在序列化时省略，客户端按 <see cref="Type"/> 分支处理。
/// </summary>
public sealed class GuardMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>hello：主程序进程 ID。</summary>
    [JsonPropertyName("pid")]
    public int? Pid { get; set; }

    /// <summary>hello-ack / status-ack：服务当前是否处于守护状态。</summary>
    [JsonPropertyName("guarding")]
    public bool? Guarding { get; set; }

    /// <summary>exit-approved：服务是否真的批准了退出（保留字段，便于未来扩展）。</summary>
    [JsonPropertyName("approved")]
    public bool? Approved { get; set; }

    /// <summary>exit-denied / error：人话解释。</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>status-ack：服务当前跟踪的主程序 PID，没有则显式给 null（协议要求字段始终存在）。</summary>
    [JsonPropertyName("appPid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? AppPid { get; set; }

    public static GuardMessage Hello(int pid) => new() { Type = GuardMessageTypes.Hello, Pid = pid };

    public static GuardMessage HelloAck() => new() { Type = GuardMessageTypes.HelloAck, Guarding = true };

    public static GuardMessage ExitRequest() => new() { Type = GuardMessageTypes.ExitRequest };

    public static GuardMessage ExitApproved() => new() { Type = GuardMessageTypes.ExitApproved, Approved = true };

    public static GuardMessage ExitDenied(string reason) => new() { Type = GuardMessageTypes.ExitDenied, Reason = reason };

    public static GuardMessage Status() => new() { Type = GuardMessageTypes.Status };

    public static GuardMessage StatusAck(bool guarding, int? appPid) =>
        new() { Type = GuardMessageTypes.StatusAck, Guarding = guarding, AppPid = appPid };

    public static GuardMessage Error(string reason) => new() { Type = GuardMessageTypes.Error, Reason = reason };
}

/// <summary>协议用的 JSON 配置。camelCase + 读的时候不区分大小写 + 空字段不写。</summary>
public static class GuardJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // 默认编码器会把中文转成 \uXXXX，协议本身没问题，但抓包/日志里没法读。
        // 这条管道不产出 HTML/脚本，放宽转义是安全的。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>协议常量与 JSON 辅助方法。</summary>
public static class GuardProtocol
{
    /// <summary>管道名（不含 <c>\\.\pipe\</c> 前缀），与<see cref="PipePath"/>保持一致。</summary>
    public const string PipeName = "FileGuard.Control";

    /// <summary>完整的管道路径，给日志/诊断用。</summary>
    public const string PipePath = @"\\.\pipe\FileGuard.Control";

    /// <summary>主程序进程名（无扩展名）。</summary>
    public const string ClientProcessName = "FileGuard";

    /// <summary>Windows 服务名（SCM 中的键名）。</summary>
    public const string ServiceName = "FileGuardGuard";

    /// <summary>服务显示名。</summary>
    public const string ServiceDisplayName = "文件卫士守护";

    /// <summary>管道客户端默认超时。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    public static string Serialize(GuardMessage message) => JsonSerializer.Serialize(message, GuardJson.Options);

    /// <summary>解析一行 JSON；格式不对返回 null，不抛异常。</summary>
    public static GuardMessage? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<GuardMessage>(json, GuardJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>
/// 控制管道客户端：连接 → 发一行 JSON → 读一行 JSON。
/// 所有失败（服务没启动、超时、对端断开、JSON 坏了）都返回 null，不抛异常，
/// 方便主程序在退出流程里「尽力而为」地询问服务。
/// </summary>
public sealed class GuardPipeClient
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _pipeName;
    private readonly TimeSpan _timeout;

    public GuardPipeClient(string pipeName = GuardProtocol.PipeName, TimeSpan? timeout = null)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? GuardProtocol.PipeName : pipeName;
        _timeout = timeout ?? GuardProtocol.DefaultTimeout;
    }

    /// <summary>发送一条消息并等待一条响应。任何失败或超时返回 null。</summary>
    public async Task<GuardMessage?> SendAsync(GuardMessage request, CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);
            return await SendCoreAsync(request, cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 管道不存在、连不上、超时、被拒绝 —— 一律当作「服务不可用」。
            return null;
        }
    }

    /// <summary>
    /// 同步版本，方便在 UI 线程的退出流程里调用。
    /// 内部全部 <c>ConfigureAwait(false)</c>，不会因为同步上下文而死锁，但最多会阻塞 <see cref="_timeout"/>。
    /// </summary>
    public GuardMessage? Send(GuardMessage request) => SendAsync(request).GetAwaiter().GetResult();

    private async Task<GuardMessage?> SendCoreAsync(GuardMessage request, CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(
            ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        await pipe.ConnectAsync(ToMilliseconds(_timeout), cancellationToken).ConfigureAwait(false);

        using var writer = new StreamWriter(pipe, Utf8NoBom, 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        using var reader = new StreamReader(pipe, Utf8NoBom, detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true);

        await writer.WriteLineAsync(GuardProtocol.Serialize(request).AsMemory(), cancellationToken).ConfigureAwait(false);

        var lineTask = reader.ReadLineAsync();
        string? line;
        try
        {
            line = await lineTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 放弃等待时底层读还挂着，管道 Dispose 会让它失败；这里提前观察掉异常。
            _ = lineTask.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            throw;
        }

        return GuardProtocol.Deserialize(line);
    }

    private static int ToMilliseconds(TimeSpan timeout)
    {
        var ms = (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);
        return ms;
    }
}
