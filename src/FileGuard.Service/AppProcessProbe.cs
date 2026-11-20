using System.Diagnostics;
using FileGuard.Core.Control;

namespace FileGuard.Service;

/// <summary>
/// 用 <see cref="Process.GetProcessesByName(string)"/> 找主程序。
/// 服务自己跑在 Session 0，里面也可能出现同名进程（例如手工调试），所以排除 SessionId == 0。
/// </summary>
public sealed class AppProcessProbe : IAppProcessProbe
{
    public int? FindRunningAppPid()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(GuardProtocol.ClientProcessName);
        }
        catch (Exception)
        {
            return null;
        }

        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.SessionId != 0)
                    {
                        return process.Id;
                    }
                }
                catch (Exception)
                {
                    // 进程刚好退出 / 权限不够：跳过。
                }
            }

            return null;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}
