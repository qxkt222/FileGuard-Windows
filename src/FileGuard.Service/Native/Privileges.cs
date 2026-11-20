using System.Runtime.InteropServices;

namespace FileGuard.Service.Native;

/// <summary>
/// 服务以 LocalSystem 运行时通常已持有 SeTcbPrivilege，但「持有」不等于「已启用」。
/// 启动时尽力启用一次；失败只记日志，不影响后续 —— 真正调用 API 时若缺权限会拿到明确错误码。
/// </summary>
internal static class Privileges
{
    private const string SeTcbPrivilege = "SeTcbPrivilege";

    internal static bool TryEnableSeTcbPrivilege(IServiceLog log)
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!NativeMethods.OpenProcessToken(
                    NativeMethods.GetCurrentProcess(),
                    NativeMethods.TokenAdjustPrivileges | NativeMethods.TokenQuery,
                    out token))
            {
                log.Error($"OpenProcessToken 失败（错误 {Marshal.GetLastWin32Error()}），无法启用 {SeTcbPrivilege}");
                return false;
            }

            if (!NativeMethods.LookupPrivilegeValue(null, SeTcbPrivilege, out var luid))
            {
                log.Error($"LookupPrivilegeValue({SeTcbPrivilege}) 失败（错误 {Marshal.GetLastWin32Error()}）");
                return false;
            }

            var privileges = new NativeMethods.TokenPrivileges
            {
                PrivilegeCount = 1,
                Privileges = new NativeMethods.LuidAndAttributes
                {
                    Luid = luid,
                    Attributes = NativeMethods.SePrivilegeEnabled,
                },
            };

            if (!NativeMethods.AdjustTokenPrivileges(
                    token,
                    disableAllPrivileges: false,
                    ref privileges,
                    bufferLength: 0,
                    previousState: IntPtr.Zero,
                    returnLength: IntPtr.Zero))
            {
                log.Error($"AdjustTokenPrivileges({SeTcbPrivilege}) 失败（错误 {Marshal.GetLastWin32Error()}）");
                return false;
            }

            // ERROR_NOT_ALL_ASSIGNED = 1300：调用成功但权限没拿到。
            var lastError = Marshal.GetLastWin32Error();
            if (lastError == 1300)
            {
                log.Error($"{SeTcbPrivilege} 未分配给当前账户（错误 1300）");
                return false;
            }

            log.Info($"已启用 {SeTcbPrivilege}");
            return true;
        }
        catch (Exception ex)
        {
            log.Error($"启用 {SeTcbPrivilege} 异常", ex);
            return false;
        }
        finally
        {
            if (token != IntPtr.Zero)
            {
                _ = NativeMethods.CloseHandle(token);
            }
        }
    }
}
