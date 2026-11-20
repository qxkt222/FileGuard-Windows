using Microsoft.Win32;

namespace FileGuard.Service;

/// <summary>主程序路径的来源（注册表；单测里可替换）。</summary>
public interface IAppPathProvider
{
    /// <summary>返回主程序绝对路径；没配置或读失败返回 null。</summary>
    string? ReadAppPath();
}

/// <summary>
/// <c>HKLM\SOFTWARE\FileGuard\AppPath</c>（REG_SZ）的读写。
/// 安装在 SYSTEM 下运行，统一用 64 位注册表视图，避免 WOW64 重定向。
/// </summary>
public static class AppSettings
{
    public const string RegistryKeyPath = @"SOFTWARE\FileGuard";
    public const string AppPathValueName = "AppPath";

    public static string? ReadAppPath()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(RegistryKeyPath, writable: false);
            var value = key?.GetValue(AppPathValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return value as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>写入主程序路径；失败抛异常（安装命令会把它变成明确的错误输出）。</summary>
    public static void WriteAppPath(string appPath)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(RegistryKeyPath, writable: true)
            ?? throw new InvalidOperationException($@"无法创建注册表键 HKLM\{RegistryKeyPath}");
        key.SetValue(AppPathValueName, appPath, RegistryValueKind.String);
    }

    /// <summary>
    /// 删除 AppPath 值；键为空时顺手删掉键。
    /// 返回是否真的找到了要删的东西（供卸载命令判断）。
    /// </summary>
    public static bool DeleteAppPath()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(RegistryKeyPath, writable: true);
        if (key is null)
        {
            return false;
        }

        var removed = key.GetValue(AppPathValueName) is not null;
        if (removed)
        {
            key.DeleteValue(AppPathValueName, throwOnMissingValue: false);
        }

        if (key.SubKeyCount == 0 && key.ValueCount == 0)
        {
            key.Dispose();
            try
            {
                baseKey.DeleteSubKeyTree(RegistryKeyPath, throwOnMissingSubKey: false);
            }
            catch (Exception)
            {
                // 删键失败不影响卸载。
            }
        }

        return removed;
    }
}

/// <summary>从注册表读主程序路径。</summary>
public sealed class RegistryAppPathProvider : IAppPathProvider
{
    public string? ReadAppPath() => AppSettings.ReadAppPath();
}
