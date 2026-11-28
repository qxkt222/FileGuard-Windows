using FileGuard.Core.Models;
using FileGuard.Core.Watching;
using Xunit;

namespace FileGuard.Core.Tests;

public sealed class RuleFileWriterTests : IDisposable
{
    private readonly string _dir;

    public RuleFileWriterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fg-rules-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void NormalMode_MentionsImmediateDeletionAndByteLimit()
    {
        var entry = new GuardEntry
        {
            RootPath = _dir,
            MaxBytes = 4096,
            LimitEnabled = true,
        };

        var file = RuleFileWriter.Write(_dir, entry, quarantineKeep: 5);
        var text = File.ReadAllText(file);

        Assert.Equal(Path.Combine(_dir, RuleFileWriter.DefaultFileName), file);
        Assert.Contains("立即删除", text);
        Assert.Contains("4096", text);
    }

    [Fact]
    public void ObserveMode_DoesNotClaimImmediateDeletion()
    {
        var entry = new GuardEntry
        {
            RootPath = _dir,
            MaxBytes = 4096,
            LimitEnabled = false,
        };

        var text = File.ReadAllText(RuleFileWriter.Write(_dir, entry, quarantineKeep: 5));

        Assert.DoesNotContain("立即删除", text);
        Assert.Contains("记录", text);
        Assert.Contains("不会被删除", text);
        Assert.Contains("4096", text);
    }

    [Fact]
    public void CustomFileName_IsUsed()
    {
        var entry = new GuardEntry
        {
            RootPath = _dir,
            MaxBytes = 4096,
        };

        var file = RuleFileWriter.Write(_dir, entry, quarantineKeep: 5, fileName: "my-rules.txt");

        Assert.Equal(Path.Combine(_dir, "my-rules.txt"), file);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void FileNameWithTraversal_StaysInsideRoot()
    {
        var entry = new GuardEntry
        {
            RootPath = _dir,
            MaxBytes = 4096,
        };

        // 配置里的文件名只取末段，不能借 ..\ 写到监视目录外。
        var file = RuleFileWriter.Write(_dir, entry, quarantineKeep: 5, fileName: @"..\..\evil.md");

        Assert.Equal(Path.Combine(_dir, "evil.md"), file);
        Assert.True(File.Exists(file));
    }
}
