using System.ComponentModel;
using System.Runtime.InteropServices;
using FileGuard.Service.Native;

namespace FileGuard.Service;

/// <summary>
/// 把主程序拉起到当前活动控制台会话的用户桌面上。
/// 流程：WTSGetActiveConsoleSessionId → WTSQueryUserToken → DuplicateTokenEx(TokenPrimary)
/// → CreateProcessAsUser(lpDesktop = "winsta0\default")。
/// </summary>
/// <remarks>
/// 必须用 <b>TokenPrimary</b>：WTSQueryUserToken 返回的 token 直接丢给 CreateProcessAsUser 并不可靠，
/// 规范做法是 DuplicateTokenEx 成主令牌再用。lpEnvironment 传 null，让子进程沿用 token 的环境。
/// </remarks>
public sealed class UserSessionLauncher : IAppLauncher
{
    private const string DefaultDesktop = @"winsta0\default";

    /// <summary>拉起后等这么久，确认进程没有立即退出。会阻塞守护循环这么久，可接受。</summary>
    private const uint LaunchLivenessTimeoutMilliseconds = 1500;

    private readonly IAppPathProvider _appPathProvider;
    private readonly IServiceLog _log;

    public UserSessionLauncher(IAppPathProvider appPathProvider, IServiceLog log)
    {
        _appPathProvider = appPathProvider;
        _log = log;
    }

    public LaunchResult TryLaunch()
    {
        var exePath = _appPathProvider.ReadAppPath();
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return LaunchResult.Fail($@"未配置主程序路径（HKLM\SOFTWARE\FileGuard\AppPath）");
        }

        exePath = exePath.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(exePath))
        {
            return LaunchResult.Fail($"主程序路径不是绝对路径：{exePath}");
        }

        if (!File.Exists(exePath))
        {
            return LaunchResult.Fail($"主程序不存在：{exePath}");
        }

        var workingDirectory = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(workingDirectory))
        {
            return LaunchResult.Fail($"无法确定主程序工作目录：{exePath}");
        }

        var sessionId = NativeMethods.WTSGetActiveConsoleSessionId();
        if (sessionId == NativeMethods.InvalidSessionId)
        {
            return LaunchResult.Fail("没有活动控制台会话（可能未登录或处于锁屏的纯服务会话）");
        }

        if (!NativeMethods.WTSQueryUserToken(sessionId, out var userToken))
        {
            return LaunchResult.Fail(
                $"WTSQueryUserToken(session={sessionId}) 失败：{LastErrorText()}");
        }

        // WTSQueryUserToken 给的是 UAC 过滤后的 token。管理员账户还有一个 linked token（完整权限），
        // 用它拉起的话主程序本身就是管理员身份，退出审批能通过——
        // 否则会出现「拉起的实例永远退不出去」的死锁。标准用户没有 linked token，退回原 token。
        var effectiveToken = userToken;
        var linkedToken = IntPtr.Zero;
        if (NativeMethods.GetTokenInformation(
                userToken,
                NativeMethods.TokenLinkedToken,
                out linkedToken,
                IntPtr.Size,
                out _)
            && linkedToken != IntPtr.Zero)
        {
            effectiveToken = linkedToken;
        }

        try
        {
            if (!NativeMethods.DuplicateTokenEx(
                    effectiveToken,
                    NativeMethods.MaximumAllowed,
                    IntPtr.Zero,
                    NativeMethods.SecurityImpersonation,
                    NativeMethods.TokenPrimary,
                    out var primaryToken))
            {
                return LaunchResult.Fail($"DuplicateTokenEx(TokenPrimary) 失败：{LastErrorText()}");
            }

            try
            {
                var startupInfo = new NativeMethods.StartupInfo
                {
                    cb = Marshal.SizeOf<NativeMethods.StartupInfo>(),
                    lpDesktop = DefaultDesktop,
                };

                // 目标路径可能带空格，命令行整体加引号；lpApplicationName 传 null 走命令行。
                // --restarted 让主程序知道这次是被服务拉起来的，静默启动、不弹窗。
                var commandLine = "\"" + exePath + "\" --restarted";

                var created = NativeMethods.CreateProcessAsUser(
                    primaryToken,
                    lpApplicationName: null,
                    lpCommandLine: commandLine,
                    lpProcessAttributes: IntPtr.Zero,
                    lpThreadAttributes: IntPtr.Zero,
                    bInheritHandles: false,
                    dwCreationFlags: NativeMethods.CreateUnicodeEnvironment,
                    lpEnvironment: IntPtr.Zero, // null：沿用 token 的环境块
                    lpCurrentDirectory: workingDirectory,
                    lpStartupInfo: ref startupInfo,
                    lpProcessInformation: out var processInfo);

                if (!created)
                {
                    return LaunchResult.Fail(
                        $"CreateProcessAsUser 失败（session={sessionId}）：{LastErrorText()}");
                }

                try
                {
                    // 只凭 CreateProcessAsUser 返回 true 不够：主程序若启动即崩（缺依赖、配置损坏等），
                    // 守护循环下一 tick 又会拉起，形成每 2 秒一次的弹窗循环——
                    // GuardianWorker 的 30 秒退避只对「调用失败」生效。
                    // 等 1.5 秒确认它没有立即退出；仍在运行才报成功。
                    var wait = NativeMethods.WaitForSingleObject(
                        processInfo.hProcess,
                        LaunchLivenessTimeoutMilliseconds);

                    if (wait == NativeMethods.WaitObject0)
                    {
                        var exitCode = 0u;
                        _ = NativeMethods.GetExitCodeProcess(processInfo.hProcess, out exitCode);
                        return LaunchResult.Fail($"主程序启动后立即退出（退出码 {exitCode}）");
                    }

                    // 只认「超时=还活着」。WAIT_FAILED(0xFFFFFFFF) 等异常结果不能当成功，
                    // 否则「启动即崩」的保护会静默失效。
                    if (wait != NativeMethods.WaitTimeout)
                    {
                        return LaunchResult.Fail(
                            $"WaitForSingleObject 返回 0x{wait:X8}，无法确认主程序存活");
                    }

                    try
                    {
                        _log.Info(
                            $"已拉起主程序 pid={processInfo.dwProcessId} session={sessionId} path={exePath}");
                    }
                    catch (Exception)
                    {
                        // 日志失败不影响拉起结果。
                    }

                    return LaunchResult.Ok();
                }
                finally
                {
                    // 成功、失败、异常三条路径都走这里，句柄不会泄漏。
                    CloseQuietly(processInfo.hProcess);
                    CloseQuietly(processInfo.hThread);
                }
            }
            finally
            {
                CloseQuietly(primaryToken);
            }
        }
        finally
        {
            if (linkedToken != IntPtr.Zero)
            {
                CloseQuietly(linkedToken);
            }

            CloseQuietly(userToken);
        }
    }

    private static void CloseQuietly(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            _ = NativeMethods.CloseHandle(handle);
        }
    }

    private static string LastErrorText()
    {
        var code = Marshal.GetLastWin32Error();
        if (code == 0)
        {
            return "未知错误";
        }

        return $"[{code}] {new Win32Exception(code).Message}";
    }
}
