using System.Diagnostics;
using FileGuard.Core.Control;
using FileGuard.Service.Native;

namespace FileGuard.Service;

/// <summary>
/// 用 <see cref="Process.GetProcessesByName(string)"/> 找主程序。
/// 服务自己跑在 Session 0，里面也可能出现同名进程（例如手工调试），所以排除 SessionId == 0。
/// </summary>
/// <remarks>
/// 只按进程名认主程序会被「改名成 FileGuard.exe 的假进程」骗过：只要有人跑一个同名进程，
/// 服务就认为主程序在跑、永不拉起真程序。配置了期望路径后，逐个比对
/// <see cref="Process.MainModule"/> 的真实映像路径；比对不上的进程不算数。
/// 期望路径每次巡检现读（不缓存）：安装中途失败后补写注册表也能立刻生效。
/// </remarks>
public sealed class AppProcessProbe : IAppProcessProbe
{
    private readonly IAppPathProvider? _appPathProvider;

    public AppProcessProbe(IAppPathProvider? appPathProvider = null)
    {
        _appPathProvider = appPathProvider;
    }

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

        // 现读：服务可能比注册表写得更早（安装中途失败、手工补配置），不能缓存。
        var expectedExePath = NormalizeOptionalPath(_appPathProvider?.ReadAppPath());

        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.SessionId == 0)
                    {
                        continue;
                    }

                    if (expectedExePath is not null)
                    {
                        string? actualPath;
                        try
                        {
                            actualPath = process.MainModule?.FileName;
                        }
                        catch (Exception)
                        {
                            // 读不到 MainModule（权限/位数不匹配/进程刚好退出）：
                            // 无法证明它是真主程序，跳过。把可能已退出的进程当「在跑」
                            // 会让服务永远不拉起真程序，代价比偶发多拉一次更大。
                            actualPath = null;
                        }

                        if (!MatchesExpectedPath(expectedExePath, actualPath))
                        {
                            continue;
                        }
                    }

                    return process.Id;
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

    /// <summary>
    /// 判断实际映像路径是否与期望路径一致（忽略大小写）。
    /// 纯函数，抽出便于单测：<paramref name="expected"/> 为空表示未配置、不限制；
    /// <paramref name="actual"/> 为空表示拿不到真实路径，此时不能认领该进程。
    /// </summary>
    public static bool MatchesExpectedPath(string? expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(actual))
        {
            return false;
        }

        var left = NormalizePath(expected);
        var right = NormalizePath(actual);

        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 文本不同也可能是同一个文件：8.3 短名、junction、符号链接、大小写差异。
        // 用卷序列号 + 文件索引比对，才算真的同一个映像。
        return SameFile(left, right);
    }

    private static bool SameFile(string first, string second)
    {
        try
        {
            using var handleA = File.OpenHandle(
                first, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var handleB = File.OpenHandle(
                second, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            if (!NativeMethods.GetFileInformationByHandle(handleA, out var infoA)
                || !NativeMethods.GetFileInformationByHandle(handleB, out var infoB))
            {
                return false;
            }

            return infoA.VolumeSerialNumber == infoB.VolumeSerialNumber
                   && infoA.FileIndexHigh == infoB.FileIndexHigh
                   && infoA.FileIndexLow == infoB.FileIndexLow;
        }
        catch (Exception)
        {
            // 任一文件打不开（路径不存在、权限不足）：退化为文本比较的结果。
            return false;
        }
    }

    private static string? NormalizeOptionalPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : NormalizePath(path);

    private static string NormalizePath(string path) =>
        path.Trim().Trim('"');
}
