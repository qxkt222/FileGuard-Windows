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
                    _log.Info(
                        $"已拉起主程序 pid={processInfo.dwProcessId} session={sessionId} path={exePath}");
                }
                catch (Exception)
                {
                    // 日志失败不影响拉起结果。
                }

                CloseQuietly(processInfo.hProcess);
                CloseQuietly(processInfo.hThread);
                return LaunchResult.Ok();
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
