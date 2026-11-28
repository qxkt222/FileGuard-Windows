using Microsoft.Win32;

namespace FileGuard.Core.Storage;

/// <summary>
/// 机器级的「非管理员限制」开关，落在 <c>HKLM\SOFTWARE\FileGuard\NonAdminLock</c>。
/// 用户配置（config.json）在 ProgramData 下，普通用户可写；只把锁存在那里等于没锁。
/// 守护服务安装时把当前设置写进 HKLM，之后普通用户改配置文件也解不开。
/// </summary>
public static class LockSettings
{
    public const string RegistryKeyPath = @"SOFTWARE\FileGuard";
    public const string ValueName = "NonAdminLock";

    /// <summary>HKLM 是否强制开启非管理员限制。读不到一律按未锁定处理。</summary>
    public static bool IsLockedByMachine()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(RegistryKeyPath, writable: false);
            return key?.GetValue(ValueName) is int value && value != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 写入/清除机器级开关。需要管理员权限；失败返回 false，由调用方决定怎么提示。
    /// </summary>
    public static bool TrySetMachineLock(bool locked)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.CreateSubKey(RegistryKeyPath, writable: true);
            if (key is null) return false;

            if (locked)
            {
                key.SetValue(ValueName, 1, RegistryValueKind.DWord);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
