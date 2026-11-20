using System.Security.Principal;
using Xunit;

namespace FileGuard.Service.Tests;

/// <summary>
/// 真实实现的身份检查。这里不能 mock：曾经因为 CheckTokenMembership 传了空句柄，
/// 服务实际检查的是 SYSTEM 自己的 token，对任何调用方都放行。
/// 用当前测试进程做对照，非管理员跑测试时会把这个 fail-open 抓出来。
/// </summary>
public sealed class ClientAdminCheckerTests
{
    [Fact]
    public void PidZero_ReturnsFalse()
    {
        Assert.False(new ClientAdminChecker().IsClientAdmin(0));
    }

    [Fact]
    public void IsClientAdmin_AgreesWithCurrentProcessIdentity()
    {
        var expected = new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);

        var actual = new ClientAdminChecker().IsClientAdmin((uint)Environment.ProcessId);

        Assert.Equal(expected, actual);
    }
}
