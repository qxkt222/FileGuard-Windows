using System.Runtime.InteropServices;
using System.Security.Principal;
using FileGuard.Service.Native;

namespace FileGuard.Service;

/// <summary>
/// 服务端自行核验管道调用方身份：GetNamedPipeClientProcessId → OpenProcess → OpenProcessToken
/// → 用调用方 token 构造 WindowsIdentity → IsInRole(BuiltinAdministratorsSid)。
/// 注意必须用 <c>new WindowsIdentity(token)</c>：无参构造拿的是服务自己的身份，不是调用方的。
/// </summary>
public sealed class ClientAdminChecker : IClientAdminChecker
{
    private readonly IServiceLog? _log;

    public ClientAdminChecker(IServiceLog? log = null)
    {
        _log = log;
    }

    public bool IsClientAdmin(uint clientProcessId)
    {
        if (clientProcessId == 0)
        {
            return false;
        }

        var processHandle = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryLimitedInformation,
            inheritHandle: false,
            clientProcessId);

        if (processHandle == IntPtr.Zero)
        {
            _log?.Info($"OpenProcess(pid={clientProcessId}) 失败（错误 {Marshal.GetLastWin32Error()}），按非管理员处理");
            return false;
        }

        try
        {
            // 必须用 MAXIMUM_ALLOWED：只请求 TOKEN_QUERY 时句柄权限太小，
            // WindowsIdentity 构造/IsInRole 会抛 SecurityException「拒绝访问」。
            if (!NativeMethods.OpenProcessToken(processHandle, NativeMethods.MaximumAllowed, out var token))
            {
                _log?.Info($"OpenProcessToken(pid={clientProcessId}) 失败（错误 {Marshal.GetLastWin32Error()}），按非管理员处理");
                return false;
            }

            // 用 WindowsIdentity/WindowsPrincipal 判定：内部按「已启用组」语义检查，
            // UAC 过滤过的管理员 token 得到 false，提权后的 token 得到 true。
            // 注意 WindowsIdentity 会接管这个句柄并在 Dispose 时关闭，外面不要再 CloseHandle。
            using var identity = new WindowsIdentity(token);
            var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            return new WindowsPrincipal(identity).IsInRole(adminSid);
        }
        catch (Exception ex)
        {
            _log?.Error($"检查调用方管理员身份异常（pid={clientProcessId}）", ex);
            return false;
        }
        finally
        {
            _ = NativeMethods.CloseHandle(processHandle);
        }
    }

}
