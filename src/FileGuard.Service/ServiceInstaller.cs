using System.Diagnostics;
using System.Security.Principal;
using FileGuard.Core.Control;

namespace FileGuard.Service;

/// <summary>
/// 用 sc.exe 安装/卸载 Windows 服务。所有失败都打印明确原因并返回非 0 退出码。
/// </summary>
internal static class ServiceInstaller
{
    private const int ExitCodeUsage = 2;
    private const int ExitCodeAccessDenied = 5;
    private const int ExitCodeFailure = 1;

    // sc.exe 的常见错误码（Win32）。
    private const int ErrorServiceExists = 1073;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceNotActive = 1062;

    public static void PrintUsage()
    {
        Console.WriteLine(
            """
            文件卫士守护服务

            用法：
              FileGuard.Service.exe                          以 Windows 服务方式运行（由 SCM 调用）
              FileGuard.Service.exe --service                同上
              FileGuard.Service.exe --install <appExePath>   安装并启动服务（需管理员）
              FileGuard.Service.exe --uninstall              停止并卸载服务、清理注册表（需管理员）

            示例：
              FileGuard.Service.exe --install "D:\FileGuard\FileGuard.exe"
            """);
    }

    public static int Install(string? appExePath)
    {
        if (!IsAdministrator())
        {
            Console.Error.WriteLine("错误：安装服务需要管理员权限，请以管理员身份运行此命令。");
            return ExitCodeAccessDenied;
        }

        if (string.IsNullOrWhiteSpace(appExePath))
        {
            Console.Error.WriteLine("错误：缺少主程序路径。");
            Console.Error.WriteLine(@"  示例：FileGuard.Service.exe --install ""D:\FileGuard\FileGuard.exe""");
            return ExitCodeUsage;
        }

        string appFullPath;
        try
        {
            appFullPath = Path.GetFullPath(appExePath.Trim().Trim('"'));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误：主程序路径无效：{ex.Message}");
            return ExitCodeUsage;
        }

        if (!File.Exists(appFullPath))
        {
            Console.Error.WriteLine($"错误：主程序不存在：{appFullPath}");
            return ExitCodeUsage;
        }

        var ownExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(ownExe)
            || !string.Equals(Path.GetFileNameWithoutExtension(ownExe), "FileGuard.Service", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                "错误：无法确定服务自身 exe 路径（是否通过 dotnet 命令启动？）。" +
                "请直接运行编译产物 FileGuard.Service.exe --install <主程序路径>。");
            return ExitCodeUsage;
        }

        Console.WriteLine($"安装服务 {GuardProtocol.ServiceName}（显示名：{GuardProtocol.ServiceDisplayName}）…");
        Console.WriteLine($"  服务程序：{ownExe}");
        Console.WriteLine($"  主程序：  {appFullPath}");

        // sc.exe 的语法要求 "binPath=" 与值之间必须有空格，值本身是一个参数。
        // 服务 exe 路径可能含空格，所以内层再加一层引号。
        var binPath = $"\"{ownExe}\" --service";

        var create = RunSc(
            "create",
            GuardProtocol.ServiceName,
            "binPath=", binPath,
            "start=", "auto",
            "DisplayName=", GuardProtocol.ServiceDisplayName);

        PrintScOutput(create);

        if (create.ExitCode != 0)
        {
            if (create.ExitCode == ErrorServiceExists)
            {
                Console.Error.WriteLine(
                    $"错误：服务 {GuardProtocol.ServiceName} 已存在。请先运行 --uninstall 再安装。");
            }
            else
            {
                Console.Error.WriteLine($"错误：sc create 失败（退出码 {create.ExitCode}）。");
            }

            return create.ExitCode;
        }

        try
        {
            AppSettings.WriteAppPath(appFullPath);
            Console.WriteLine($@"已写入 HKLM\{AppSettings.RegistryKeyPath}\{AppSettings.AppPathValueName} = {appFullPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误：服务已创建，但写入注册表失败：{ex.Message}");
            Console.Error.WriteLine(
                $@"  请手动设置 HKLM\{AppSettings.RegistryKeyPath}\{AppSettings.AppPathValueName}（REG_SZ）为 {appFullPath}");
            return ExitCodeFailure;
        }

        Console.WriteLine("启动服务…");
        var start = RunSc("start", GuardProtocol.ServiceName);
        PrintScOutput(start);

        if (start.ExitCode != 0)
        {
            Console.Error.WriteLine($"错误：sc start 失败（退出码 {start.ExitCode}）。");
            return start.ExitCode;
        }

        Console.WriteLine("安装完成。服务已启动，将守护主程序。");
        return 0;
    }

    public static int Uninstall()
    {
        if (!IsAdministrator())
        {
            Console.Error.WriteLine("错误：卸载服务需要管理员权限，请以管理员身份运行此命令。");
            return ExitCodeAccessDenied;
        }

        Console.WriteLine($"停止服务 {GuardProtocol.ServiceName}…");
        var stop = RunSc("stop", GuardProtocol.ServiceName);
        PrintScOutput(stop);

        if (stop.ExitCode != 0 && stop.ExitCode != ErrorServiceDoesNotExist && stop.ExitCode != ErrorServiceNotActive)
        {
            Console.Error.WriteLine($"警告：sc stop 失败（退出码 {stop.ExitCode}），继续尝试删除。");
        }

        Console.WriteLine($"删除服务 {GuardProtocol.ServiceName}…");
        var delete = RunSc("delete", GuardProtocol.ServiceName);
        PrintScOutput(delete);

        if (delete.ExitCode != 0)
        {
            if (delete.ExitCode == ErrorServiceDoesNotExist)
            {
                Console.WriteLine("服务本来就不存在，继续清理注册表。");
            }
            else
            {
                Console.Error.WriteLine($"错误：sc delete 失败（退出码 {delete.ExitCode}）。");
                return delete.ExitCode;
            }
        }

        try
        {
            if (AppSettings.DeleteAppPath())
            {
                Console.WriteLine($@"已删除 HKLM\{AppSettings.RegistryKeyPath}\{AppSettings.AppPathValueName}");
            }
            else
            {
                Console.WriteLine("注册表中没有需要清理的主程序路径。");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误：删除注册表值失败：{ex.Message}");
            return ExitCodeFailure;
        }

        Console.WriteLine("卸载完成。");
        return 0;
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static (int ExitCode, string StdOut, string StdErr) RunSc(params string[] arguments)
    {
        var scPath = Path.Combine(Environment.SystemDirectory, "sc.exe");

        var startInfo = new ProcessStartInfo(scPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (ExitCodeFailure, string.Empty, "无法启动 sc.exe");
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (ExitCodeFailure, string.Empty, $"运行 sc.exe 失败：{ex.Message}");
        }
    }

    private static void PrintScOutput((int ExitCode, string StdOut, string StdErr) result)
    {
        if (!string.IsNullOrWhiteSpace(result.StdOut))
        {
            Console.WriteLine(result.StdOut.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(result.StdErr))
        {
            Console.Error.WriteLine(result.StdErr.TrimEnd());
        }
    }
}
