namespace FileGuard.Service;

/// <summary>
/// 守护状态，由守护循环和控制管道两条线程共享。
/// 所有字段都在同一把锁下读写，读多写少，锁竞争可以忽略。
/// </summary>
public sealed class GuardianState
{
    private readonly object _gate = new();

    private int? _appPid;
    private bool _exitApproved;
    private DateTimeOffset? _approvedAt;

    /// <summary>当前是否处于「已批准退出」状态：批准后不再拉起，直到主程序发 hello。</summary>
    public bool ExitApproved
    {
        get
        {
            lock (_gate)
            {
                return _exitApproved;
            }
        }
        set
        {
            lock (_gate)
            {
                _exitApproved = value;

                // 批准状态一旦被清掉，批准时间也必须一起清掉，
                // 否则下一轮巡检会拿着一个陈旧的时间戳算出巨大的 approvalAge。
                if (!value)
                {
                    _approvedAt = null;
                }
            }
        }
    }

    /// <summary>本次「已批准退出」的时间；未批准时返回 <see cref="DateTimeOffset.MinValue"/>。</summary>
    public DateTimeOffset ApprovedAt
    {
        get
        {
            lock (_gate)
            {
                return _approvedAt ?? DateTimeOffset.MinValue;
            }
        }
    }

    /// <summary>服务当前跟踪的主程序 PID；null 表示未知/未运行。</summary>
    public int? AppPid
    {
        get
        {
            lock (_gate)
            {
                return _appPid;
            }
        }
    }

    /// <summary>对外汇报的守护状态：批准退出后就是 false。</summary>
    public bool IsGuarding => !ExitApproved;

    /// <summary>主程序已启动并报到：记录 PID，并清掉「已批准退出」。</summary>
    public void MarkHello(int pid)
    {
        lock (_gate)
        {
            _appPid = pid > 0 ? pid : null;
            _exitApproved = false;
            _approvedAt = null;
        }
    }

    public void SetAppPid(int? pid)
    {
        lock (_gate)
        {
            _appPid = pid;
        }
    }

    public void ApproveExit(DateTimeOffset at)
    {
        lock (_gate)
        {
            _exitApproved = true;
            _approvedAt = at;
        }
    }
}
