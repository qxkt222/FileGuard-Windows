using System.Runtime.InteropServices;
using FileGuard.Service;
using Xunit;

namespace FileGuard.Service.Tests;

public class AppProcessProbeTests
{
    [Fact]
    public void MatchesExpectedPath_NullExpected_ReturnsTrue()
    {
        // 没配置期望路径：不限制，保持旧的按进程名匹配行为。
        Assert.True(AppProcessProbe.MatchesExpectedPath(null, @"C:\Apps\FileGuard.exe"));
    }

    [Fact]
    public void MatchesExpectedPath_WhitespaceExpected_ReturnsTrue()
    {
        Assert.True(AppProcessProbe.MatchesExpectedPath("   ", @"C:\Apps\FileGuard.exe"));
    }

    [Fact]
    public void MatchesExpectedPath_IgnoresCase()
    {
        Assert.True(AppProcessProbe.MatchesExpectedPath(
            @"C:\Apps\FileGuard.exe",
            @"c:\apps\FILEGUARD.EXE"));
    }

    [Fact]
    public void MatchesExpectedPath_QuotedOrPaddedPath_ReturnsTrue()
    {
        Assert.True(AppProcessProbe.MatchesExpectedPath(
            "  \"C:\\Apps\\FileGuard.exe\"  ",
            @"C:\Apps\FileGuard.exe"));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(
        string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [Fact]
    public void MatchesExpectedPath_HardLinkToSameFile_ReturnsTrue()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fg-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var original = Path.Combine(dir, "FileGuard.exe");
            var alias = Path.Combine(dir, "alias.exe");
            File.WriteAllText(original, "stub");

            // 路径文本完全不同，但文件身份一致（8.3 短名/junction 同理）。
            Assert.True(CreateHardLink(alias, original, IntPtr.Zero), "创建硬链接失败");
            Assert.True(AppProcessProbe.MatchesExpectedPath(original, alias));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void MatchesExpectedPath_DifferentDirectory_ReturnsFalse()
    {
        // 改名成 FileGuard.exe 的假进程通常在不同目录。
        Assert.False(AppProcessProbe.MatchesExpectedPath(
            @"C:\Apps\FileGuard.exe",
            @"D:\Fake\FileGuard.exe"));
    }

    [Fact]
    public void MatchesExpectedPath_DifferentFileName_ReturnsFalse()
    {
        Assert.False(AppProcessProbe.MatchesExpectedPath(
            @"C:\Apps\FileGuard.exe",
            @"C:\Apps\FileGuardOld.exe"));
    }

    [Fact]
    public void MatchesExpectedPath_NullActual_ReturnsFalse()
    {
        // 配了期望路径、却读不到实际映像路径：无法证明它是真主程序，不认领。
        // 否则一个已退出的同名进程会被当成「在跑」，服务永远不拉起真程序。
        Assert.False(AppProcessProbe.MatchesExpectedPath(@"C:\Apps\FileGuard.exe", null));
    }
}
