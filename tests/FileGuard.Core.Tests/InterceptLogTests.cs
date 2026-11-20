using FileGuard.Core.Logging;
using FileGuard.Core.Models;
using Xunit;

namespace FileGuard.Core.Tests;

public class InterceptLogTests
{
    [Fact]
    public void KeepsOnlyMostRecentRecordsInMemory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fg-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var log = new InterceptLog(Path.Combine(dir, "intercept.log"), memoryCapacity: 5);

            for (var i = 1; i <= 8; i++)
            {
                log.Append(new InterceptRecord(
                    DateTimeOffset.Now, Guid.Empty, $@"D:\p\f{i}.cs", i * 1024, 20480, InterceptAction.Deleted));
            }

            var recent = log.Recent;
            Assert.Equal(5, recent.Count);
            Assert.Equal(@"D:\p\f4.cs", recent[0].Path);
            Assert.Equal(@"D:\p\f8.cs", recent[^1].Path);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WritesEveryRecordToFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fg-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "intercept.log");
            var log = new InterceptLog(file, memoryCapacity: 5);

            for (var i = 1; i <= 8; i++)
            {
                log.Append(new InterceptRecord(
                    DateTimeOffset.Now, Guid.Empty, $@"D:\p\f{i}.cs", i * 1024, 20480, InterceptAction.Deleted));
            }

            var lines = File.ReadAllLines(file).Where(l => l.Length > 0).ToArray();
            Assert.Equal(8, lines.Length);
            Assert.Contains("f8.cs", lines[^1]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
