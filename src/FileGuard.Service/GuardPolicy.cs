namespace FileGuard.Service;

/// <summary>一次巡检得出的动作。</summary>
/// <param name="ShouldLaunch">需要拉起主程序。</param>
/// <param name="ResetApproval">
/// 需要把「已批准退出」清掉。只在兜底超时生效时才是 true，正常情况下批准会一直保留到主程序发 hello。
/// </param>
public readonly record struct GuardDecision(bool ShouldLaunch, bool ResetApproval);

/// <summary>
/// 守护决策：只做纯函数判断，进程探测/拉起/状态都由外部注入，方便单测。
/// </summary>
public static class GuardPolicy
{
    /// <summary>
    /// 「主程序仍在运行 + 已批准退出」的兜底宽限期。超过这个时间还没收到 hello，
    /// 就认为界面已经死了（而不是正在弹确认框），清掉批准恢复正常守护。
    /// </summary>
    public static readonly TimeSpan ApprovalGracePeriod = TimeSpan.FromSeconds(120);

    /// <summary>
    /// 决策表：
    /// <list type="bullet">
    /// <item>存在 + 守护中：不动。</item>
    /// <item>存在 + 已批准退出 + 未超宽限期：不拉起，也<b>不清批准</b>。用户可能正盯着确认框。</item>
    /// <item>存在 + 已批准退出 + 已超宽限期：不拉起，清批准恢复正常守护（兜底）。</item>
    /// <item>不存在 + 已批准退出：不拉起，<b>永不超时</b>，一直等到主程序发 hello。</item>
    /// <item>不存在 + 守护中：拉起。</item>
    /// </list>
    /// 为什么「不存在 + 已批准」不能有超时：这是用户点「是」主动退出的正常路径。
    /// 如果这里也套用宽限期，用户退出 120 秒后主程序又会被服务拉起来，退出功能等于失效。
    /// 只有「主程序还在运行但迟迟不 hello」才需要兜底，因为那通常意味着界面已经崩溃/卡死。
    /// </summary>
    public static GuardDecision Decide(bool appRunning, bool exitApproved, TimeSpan approvalAge)
    {
        if (exitApproved && !appRunning)
        {
            // 用户主动退出的正常路径：主程序已消失，等它下次 hello 报到。
            return new GuardDecision(ShouldLaunch: false, ResetApproval: false);
        }

        if (exitApproved)
        {
            // 主程序仍在运行，可能正在弹退出确认框或还没处理完退出：
            // 宽限期内保留批准，绝不因为「还在跑」就把批准清掉。
            if (approvalAge < ApprovalGracePeriod)
            {
                return new GuardDecision(ShouldLaunch: false, ResetApproval: false);
            }

            // 兜底：超过宽限期仍未 hello，推断界面已崩溃/卡死，清批准恢复正常守护。
            return new GuardDecision(ShouldLaunch: false, ResetApproval: true);
        }

        return new GuardDecision(ShouldLaunch: !appRunning, ResetApproval: false);
    }
}

/// <summary>探测主程序是否在某个交互式会话里运行。</summary>
public interface IAppProcessProbe
{
    /// <summary>找到运行中的主程序返回 PID，否则返回 null。Session 0 的同名进程不算。</summary>
    int? FindRunningAppPid();
}

/// <summary>一次拉起尝试的结果。</summary>
public readonly record struct LaunchResult(bool Success, string? Error)
{
    public static LaunchResult Ok() => new(true, null);

    public static LaunchResult Fail(string error) => new(false, error);
}

/// <summary>把主程序拉起到当前活动用户会话。</summary>
public interface IAppLauncher
{
    LaunchResult TryLaunch();
}

/// <summary>判断管道调用方是否管理员。</summary>
public interface IClientAdminChecker
{
    bool IsClientAdmin(uint clientProcessId);
}
