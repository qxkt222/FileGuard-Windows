using FileGuard.Service;
using Xunit;

namespace FileGuard.Service.Tests;

public class GuardianWorkerTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Backoff = TimeSpan.FromSeconds(30);

    private readonly GuardianState _state = new();
    private readonly FakeProcessProbe _probe = new();
    private readonly FakeLauncher _launcher = new();
    private readonly FakeServiceLog _log = new();

    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private GuardianWorker CreateWorker() =>
        new(_state, _probe, _launcher, _log, Poll, Backoff, () => _now);

    [Fact]
    public void RunningAndGuarding_DoesNothing()
    {
        _probe.Pid = 500;

        CreateWorker().Tick();

        Assert.Equal(0, _launcher.Calls);
        Assert.True(_state.IsGuarding);
        Assert.Equal(500, _state.AppPid);
    }

    [Fact]
    public void MissingAndGuarding_Launches()
    {
        _probe.Pid = null;

        CreateWorker().Tick();

        Assert.Equal(1, _launcher.Calls);
        Assert.True(_state.IsGuarding);
        Assert.Contains(_log.Infos, m => m.Contains("拉起主程序成功"));
    }

    [Fact]
    public void MissingAndApproved_DoesNotLaunch()
    {
        _probe.Pid = null;
        _state.ApproveExit(_now);

        CreateWorker().Tick();

        Assert.Equal(0, _launcher.Calls);
        Assert.True(_state.ExitApproved);
    }

    [Fact]
    public void RunningAndApproved_WithinGrace_KeepsApprovalWithoutLaunching()
    {
        _probe.Pid = 600;
        _state.ApproveExit(_now);

        CreateWorker().Tick();

        Assert.Equal(0, _launcher.Calls);
        Assert.True(_state.ExitApproved);
        Assert.Equal(600, _state.AppPid);
    }

    [Fact]
    public void RunningAndApproved_AfterGracePeriod_ResetsApproval()
    {
        _probe.Pid = 600;
        _state.ApproveExit(_now);
        var worker = CreateWorker();

        _now += GuardPolicy.ApprovalGracePeriod;
        worker.Tick();

        Assert.Equal(0, _launcher.Calls);
        Assert.False(_state.ExitApproved);
        Assert.Equal(600, _state.AppPid);
    }

    [Fact]
    public void ApprovedExit_NotClearedWhileDialogOpen_AcrossManyTicks()
    {
        // 回归：管理员点退出 → 服务批准 → 主程序弹确认框仍然活着。
        // 即使用户盯着框超过 3 轮巡检（6 秒），批准也不能被清掉；
        // 否则用户点「是」关闭后，下一轮巡检会立刻把主程序拉起来，退出功能失效。
        _probe.Pid = 700;
        _state.ApproveExit(_now);
        var worker = CreateWorker();

        for (var i = 0; i < 5; i++)
        {
            _now += Poll;
            worker.Tick();
        }

        Assert.Equal(0, _launcher.Calls);
        Assert.True(_state.ExitApproved);
        Assert.Equal(700, _state.AppPid);
    }

    [Fact]
    public void HelloAfterApproval_ClearsApprovalAndResumesGuarding()
    {
        _probe.Pid = 700;
        _state.ApproveExit(_now);
        var worker = CreateWorker();
        worker.Tick();
        Assert.True(_state.ExitApproved);

        // 主程序确认退出前先补了 hello（例如用户在确认框点了「否」）。
        _state.MarkHello(700);

        Assert.False(_state.ExitApproved);
        Assert.Equal(DateTimeOffset.MinValue, _state.ApprovedAt);
    }

    [Fact]
    public void LaunchFailure_BacksOffBeforeRetrying()
    {
        _probe.Pid = null;
        _launcher.Enqueue(LaunchResult.Fail("boom"));
        var worker = CreateWorker();

        worker.Tick();
        Assert.Equal(1, _launcher.Calls);
        Assert.Single(_log.Errors);

        // 5 秒后仍在退避窗口内：不再尝试。
        _now = _now.AddSeconds(5);
        worker.Tick();
        Assert.Equal(1, _launcher.Calls);
        Assert.Single(_log.Errors);

        // 越过 30 秒退避后重试。
        _now = _now.AddSeconds(26);
        worker.Tick();
        Assert.Equal(2, _launcher.Calls);
    }

    [Fact]
    public void SuccessfulLaunch_ResetsBackoff()
    {
        _probe.Pid = null;
        _launcher.Enqueue(LaunchResult.Fail("boom"));
        var worker = CreateWorker();

        worker.Tick();
        Assert.Equal(1, _launcher.Calls);

        // 退避 30 秒后第二轮成功。
        _now = _now.AddSeconds(31);
        _launcher.Enqueue(LaunchResult.Ok());
        worker.Tick();
        Assert.Equal(2, _launcher.Calls);

        // 成功之后退避被清空：下一轮立刻可以再尝试（用于进程真的没起来的情况）。
        worker.Tick();
        Assert.Equal(3, _launcher.Calls);
    }

    [Fact]
    public void ConsecutiveImmediateExitFailures_DoNotRelaunchInsideBackoff()
    {
        _probe.Pid = null;

        // 模拟主程序每次拉起后立即退出：launcher 每次都返回 Fail。
        for (var i = 0; i < 5; i++)
        {
            _launcher.Enqueue(LaunchResult.Fail("主程序启动后立即退出（退出码 1）"));
        }

        var worker = CreateWorker();

        worker.Tick();
        Assert.Equal(1, _launcher.Calls);

        // 退避期内每 2 秒巡检一次，都不能再次拉起，否则就是每 2 秒弹一次窗。
        for (var i = 0; i < 14; i++) // 28 秒，仍在 30 秒退避窗口内
        {
            _now += Poll;
            worker.Tick();
        }

        Assert.Equal(1, _launcher.Calls);
        Assert.Single(_log.Errors);

        // 越过 30 秒退避窗口后才重试。
        _now += Poll;
        worker.Tick();
        Assert.Equal(2, _launcher.Calls);
    }

    [Fact]
    public void SuccessFollowedByImmediateDisappearance_RetriesOnNextTick()
    {
        // 拉起「成功」但下一轮巡检仍看不到主程序（mock launcher 模拟启动后立刻消失的旧行为）：
        // 成功不进入退避，下一 tick 立刻重试。
        _probe.Pid = null;
        _launcher.Enqueue(LaunchResult.Ok());
        var worker = CreateWorker();

        worker.Tick();
        Assert.Equal(1, _launcher.Calls);

        _now += Poll;
        worker.Tick();
        Assert.Equal(2, _launcher.Calls);
    }

    [Fact]
    public void AppDisappearing_ClearsTrackedPid()
    {
        _state.MarkHello(321);
        _probe.Pid = null;

        CreateWorker().Tick();

        Assert.Null(_state.AppPid);
    }
}
