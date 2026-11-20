using Microsoft.Extensions.Hosting;

namespace FileGuard.Service;

/// <summary>
/// 守护循环：每 2 秒巡检一次。主程序不在、且没被批准退出，就拉起来。
/// 连续拉起失败时退避 30 秒，避免每 2 秒刷一次日志、也无谓地和系统较劲。
/// </summary>
public sealed class GuardianWorker : BackgroundService
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultFailureBackoff = TimeSpan.FromSeconds(30);

    private readonly GuardianState _state;
    private readonly IAppProcessProbe _probe;
    private readonly IAppLauncher _launcher;
    private readonly IServiceLog _log;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _failureBackoff;
    private readonly Func<DateTimeOffset> _clock;

    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;
    private int _consecutiveFailures;

    public GuardianWorker(GuardianState state, IAppProcessProbe probe, IAppLauncher launcher, IServiceLog log)
        : this(state, probe, launcher, log, DefaultPollInterval, DefaultFailureBackoff, () => DateTimeOffset.UtcNow)
    {
    }

    public GuardianWorker(
        GuardianState state,
        IAppProcessProbe probe,
        IAppLauncher launcher,
        IServiceLog log,
        TimeSpan pollInterval,
        TimeSpan failureBackoff,
        Func<DateTimeOffset> clock)
    {
        _state = state;
        _probe = probe;
        _launcher = launcher;
        _log = log;
        _pollInterval = pollInterval > TimeSpan.Zero ? pollInterval : DefaultPollInterval;
        _failureBackoff = failureBackoff >= TimeSpan.Zero ? failureBackoff : DefaultFailureBackoff;
        _clock = clock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.Info($"守护循环启动（间隔 {_pollInterval.TotalSeconds:0} 秒）");

        while (!stoppingToken.IsCancellationRequested)
        {
            Tick();

            try
            {
                await Task.Delay(_pollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _log.Info("守护循环停止");
    }

    /// <summary>
    /// 单次巡检。抽成 public 是为了让单测直接驱动，不必等真实计时器。
    /// </summary>
    public void Tick()
    {
        try
        {
            var runningPid = _probe.FindRunningAppPid();
            var running = runningPid.HasValue;

            // 批准一旦给出就持续有效，直到主程序 hello 或超过兜底宽限期；
            // 只有「已批准」时时间戳才有意义，否则统一按 0 计算。
            var approvalAge = _state.ExitApproved
                ? _clock() - _state.ApprovedAt
                : TimeSpan.Zero;
            var decision = GuardPolicy.Decide(running, _state.ExitApproved, approvalAge);

            if (running)
            {
                _state.SetAppPid(runningPid);

                // 主程序在跑，之前的失败退避作废。
                _consecutiveFailures = 0;
                _retryAfter = DateTimeOffset.MinValue;
            }
            else if (_state.AppPid.HasValue)
            {
                _state.SetAppPid(null);
            }

            if (decision.ResetApproval)
            {
                _state.ExitApproved = false;
                _log.Info("已批准退出超过兜底宽限期仍未收到 hello，清空「已批准退出」状态并恢复守护");
            }

            if (!decision.ShouldLaunch)
            {
                return;
            }

            var now = _clock();
            if (now < _retryAfter)
            {
                return;
            }

            var result = _launcher.TryLaunch();
            if (result.Success)
            {
                _consecutiveFailures = 0;
                _retryAfter = DateTimeOffset.MinValue;
                _log.Info("拉起主程序成功，等待 hello 报到");
            }
            else
            {
                _consecutiveFailures++;
                _retryAfter = now + _failureBackoff;
                _log.Error(
                    $"拉起主程序失败（连续 {_consecutiveFailures} 次），{_failureBackoff.TotalSeconds:0} 秒后重试：{result.Error}");
            }
        }
        catch (Exception ex)
        {
            // 巡检本身出错也不能让循环退出：下一轮继续。
            _log.Error("守护巡检异常", ex);
            _retryAfter = _clock() + _failureBackoff;
        }
    }
}
