using FileGuard.Core.Control;
using FileGuard.Service;
using FileGuard.Service.Native;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FileGuard.Service;

/// <summary>
/// 入口：
/// <list type="bullet">
/// <item>无参数 / <c>--service</c>：以 Windows 服务方式运行（日常由 SCM 调用）。</item>
/// <item><c>--install &lt;appExePath&gt;</c>：安装服务并写入主程序路径（需管理员）。</item>
/// <item><c>--uninstall</c>：停止并删除服务、清理注册表（需管理员）。</item>
/// </list>
/// </summary>
internal static class Program
{
    private const string ServiceSwitch = "--service";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && !IsServiceSwitch(args[0]))
        {
            switch (args[0].Trim().ToLowerInvariant())
            {
                case "--install":
                    return ServiceInstaller.Install(args.Length > 1 ? args[1] : null);

                case "--uninstall":
                    return ServiceInstaller.Uninstall();

                case "--help":
                case "-h":
                case "/?":
                    ServiceInstaller.PrintUsage();
                    return 0;

                default:
                    Console.Error.WriteLine($"未知参数：{args[0]}");
                    ServiceInstaller.PrintUsage();
                    return 2;
            }
        }

        return await RunAsServiceAsync(args).ConfigureAwait(false);
    }

    private static bool IsServiceSwitch(string arg) =>
        string.Equals(arg, ServiceSwitch, StringComparison.OrdinalIgnoreCase);

    private static async Task<int> RunAsServiceAsync(string[] args)
    {
        var log = new ServiceLog();

        try
        {
            // WTSQueryUserToken 要求调用方持有并启用 SeTcbPrivilege；以 LocalSystem 运行时通常已有，
            // 这里再尽力启用一次，失败只记日志（真缺权限时 WTSQueryUserToken 会返回明确错误码）。
            Privileges.TryEnableSeTcbPrivilege(log);

            // 过滤掉自定义开关，避免它被当成配置项塞进 IConfiguration。
            var hostArgs = args.Where(arg => !IsServiceSwitch(arg)).ToArray();

            var builder = Host.CreateDefaultBuilder(hostArgs)
                .UseWindowsService(options => options.ServiceName = GuardProtocol.ServiceName)
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IServiceLog>(log);
                    services.AddSingleton<GuardianState>();
                    services.AddSingleton<IAppPathProvider, RegistryAppPathProvider>();
                    services.AddSingleton<IAppProcessProbe, AppProcessProbe>();
                    services.AddSingleton<IAppLauncher, UserSessionLauncher>();
                    services.AddSingleton<IClientAdminChecker>(_ => new ClientAdminChecker(log));
                    services.AddSingleton<ControlMessageHandler>();
                    services.AddHostedService<GuardianWorker>();
                    services.AddHostedService<GuardPipeServer>();
                });

            var host = builder.Build();
            log.Info($"服务启动（service={GuardProtocol.ServiceName}，exe={Environment.ProcessPath}）");
            await host.RunAsync().ConfigureAwait(false);
            log.Info("服务停止");
            return 0;
        }
        catch (Exception ex)
        {
            log.Error("服务运行失败", ex);
            try
            {
                Console.Error.WriteLine($"服务运行失败：{ex.Message}");
            }
            catch (Exception)
            {
                // 无控制台时忽略。
            }

            return 1;
        }
    }
}
