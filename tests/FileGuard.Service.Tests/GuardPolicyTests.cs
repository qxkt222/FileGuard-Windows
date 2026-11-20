using FileGuard.Service;
using Xunit;

namespace FileGuard.Service.Tests;

public class GuardPolicyTests
{
    [Theory]
    // 存在 + 守护中：不动。
    [InlineData(true, false, 0, false, false)]
    // 不存在 + 守护中：拉起。
    [InlineData(false, false, 0, true, false)]
    // 存在 + 已批准退出 + 宽限期内：不拉起、不清批准（用户可能正盯着确认框）。
    [InlineData(true, true, 0, false, false)]
    // 存在 + 已批准退出 + 超过宽限期：不拉起，清批准（兜底恢复守护）。
    [InlineData(true, true, 120, false, true)]
    // 不存在 + 已批准退出：永不拉起、永不清批准（用户主动退出的正常路径，没有超时）。
    [InlineData(false, true, 999_999, false, false)]
    public void Decide_CoversAllCombinations(
        bool appRunning,
        bool exitApproved,
        int approvalAgeSeconds,
        bool expectedLaunch,
        bool expectedReset)
    {
        var decision = GuardPolicy.Decide(
            appRunning,
            exitApproved,
            TimeSpan.FromSeconds(approvalAgeSeconds));

        Assert.Equal(expectedLaunch, decision.ShouldLaunch);
        Assert.Equal(expectedReset, decision.ResetApproval);
    }

    [Fact]
    public void Decide_OneSecondBeforeGracePeriod_DoesNotResetApproval()
    {
        var age = GuardPolicy.ApprovalGracePeriod - TimeSpan.FromSeconds(1);

        var decision = GuardPolicy.Decide(appRunning: true, exitApproved: true, approvalAge: age);

        Assert.False(decision.ShouldLaunch);
        Assert.False(decision.ResetApproval);
    }

    [Fact]
    public void Decide_ExactlyAtGracePeriod_ResetsApproval()
    {
        var decision = GuardPolicy.Decide(
            appRunning: true,
            exitApproved: true,
            approvalAge: GuardPolicy.ApprovalGracePeriod);

        Assert.False(decision.ShouldLaunch);
        Assert.True(decision.ResetApproval);
    }

    [Fact]
    public void Decide_MissingAppWithApproval_NeverResetsRegardlessOfAge()
    {
        // 用户点「是」退出后主程序消失；无论过多久都不能被服务重新拉起，只能等 hello。
        var decision = GuardPolicy.Decide(
            appRunning: false,
            exitApproved: true,
            approvalAge: TimeSpan.FromDays(365));

        Assert.False(decision.ShouldLaunch);
        Assert.False(decision.ResetApproval);
    }
}
