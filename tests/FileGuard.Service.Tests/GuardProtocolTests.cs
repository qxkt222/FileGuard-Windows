using System.Text.Json;
using FileGuard.Core.Control;
using Xunit;

namespace FileGuard.Service.Tests;

public class GuardProtocolTests
{
    [Fact]
    public void Hello_RoundTrips_WithCamelCaseJson()
    {
        var json = GuardProtocol.Serialize(GuardMessage.Hello(4321));

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("hello", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(4321, doc.RootElement.GetProperty("pid").GetInt32());

        var parsed = GuardProtocol.Deserialize(json);
        Assert.NotNull(parsed);
        Assert.Equal(GuardMessageTypes.Hello, parsed!.Type);
        Assert.Equal(4321, parsed.Pid);
    }

    [Fact]
    public void HelloAck_RoundTrips()
    {
        var parsed = RoundTrip(GuardMessage.HelloAck());

        Assert.Equal(GuardMessageTypes.HelloAck, parsed.Type);
        Assert.True(parsed.Guarding);
        Assert.Null(parsed.Pid);
    }

    [Fact]
    public void ExitRequest_RoundTrips()
    {
        var parsed = RoundTrip(GuardMessage.ExitRequest());

        Assert.Equal(GuardMessageTypes.ExitRequest, parsed.Type);
        Assert.Null(parsed.Reason);
    }

    [Fact]
    public void ExitApproved_RoundTrips()
    {
        var json = GuardProtocol.Serialize(GuardMessage.ExitApproved());

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("exit-approved", doc.RootElement.GetProperty("type").GetString());
        Assert.True(doc.RootElement.GetProperty("approved").GetBoolean());
        // null 字段不序列化，消息体保持精简。
        Assert.False(doc.RootElement.TryGetProperty("reason", out _));

        var parsed = GuardProtocol.Deserialize(json);
        Assert.Equal(GuardMessageTypes.ExitApproved, parsed!.Type);
        Assert.True(parsed.Approved);
    }

    [Fact]
    public void ExitDenied_RoundTrips_WithChineseReason()
    {
        var json = GuardProtocol.Serialize(GuardMessage.ExitDenied("需要管理员权限"));
        Assert.Contains("需要管理员权限", json); // 线上可读，不转义成 \uXXXX

        var parsed = GuardProtocol.Deserialize(json);

        Assert.Equal(GuardMessageTypes.ExitDenied, parsed!.Type);
        Assert.Equal("需要管理员权限", parsed.Reason);
    }

    [Fact]
    public void StatusAck_RoundTrips_WithAppPid()
    {
        var parsed = RoundTrip(GuardMessage.StatusAck(guarding: true, appPid: 7788));

        Assert.Equal(GuardMessageTypes.StatusAck, parsed.Type);
        Assert.True(parsed.Guarding);
        Assert.Equal(7788, parsed.AppPid);
    }

    [Fact]
    public void StatusAck_NullAppPid_IsExplicitNull()
    {
        var json = GuardProtocol.Serialize(GuardMessage.StatusAck(guarding: false, appPid: null));

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("status-ack", doc.RootElement.GetProperty("type").GetString());
        Assert.False(doc.RootElement.GetProperty("guarding").GetBoolean());
        // 协议里 appPid 始终存在，没有主程序时是 null（不是省略字段）。
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("appPid").ValueKind);

        var parsed = GuardProtocol.Deserialize(json);
        Assert.Null(parsed!.AppPid);
    }

    [Fact]
    public void Status_RoundTrips()
    {
        Assert.Equal(GuardMessageTypes.Status, RoundTrip(GuardMessage.Status()).Type);
    }

    [Theory]
    [InlineData("""{"type":"hello","pid":1}""", GuardMessageTypes.Hello)]
    [InlineData("""{"type":"exit-request"}""", GuardMessageTypes.ExitRequest)]
    [InlineData("""{"type":"exit-denied","reason":"x"}""", GuardMessageTypes.ExitDenied)]
    [InlineData("""{"type":"status"}""", GuardMessageTypes.Status)]
    public void Deserialize_AcceptsWireFormat(string json, string expectedType)
    {
        var parsed = GuardProtocol.Deserialize(json);

        Assert.NotNull(parsed);
        Assert.Equal(expectedType, parsed!.Type);
    }

    [Fact]
    public void Deserialize_IsCaseInsensitive()
    {
        var parsed = GuardProtocol.Deserialize("""{"Type":"hello","Pid":42}""");

        Assert.NotNull(parsed);
        Assert.Equal(GuardMessageTypes.Hello, parsed!.Type);
        Assert.Equal(42, parsed.Pid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("""{"type":123}""")]
    public void Deserialize_InvalidInput_ReturnsNull(string? json)
    {
        Assert.Null(GuardProtocol.Deserialize(json));
    }

    [Fact]
    public void ProtocolConstants_AreStable()
    {
        Assert.Equal("FileGuard.Control", GuardProtocol.PipeName);
        Assert.Equal(@"\\.\pipe\FileGuard.Control", GuardProtocol.PipePath);
        Assert.Equal("FileGuardGuard", GuardProtocol.ServiceName);
        Assert.Equal("文件卫士守护", GuardProtocol.ServiceDisplayName);
        Assert.Equal(TimeSpan.FromSeconds(3), GuardProtocol.DefaultTimeout);
    }

    private static GuardMessage RoundTrip(GuardMessage message)
    {
        var json = GuardProtocol.Serialize(message);
        var parsed = GuardProtocol.Deserialize(json);
        Assert.NotNull(parsed);
        return parsed!;
    }
}
