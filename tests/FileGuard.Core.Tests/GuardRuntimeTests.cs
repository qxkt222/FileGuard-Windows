using System.Diagnostics;
using FileGuard.Core.Logging;
using FileGuard.Core.Models;
using FileGuard.Core.Storage;
using FileGuard.Core.Watching;
using Xunit;

namespace FileGuard.Core.Tests;

public sealed class GuardRuntimeTests : IDisposable
{
    private readonly string _root;
    private readonly string _watch;
    private readonly GuardOptions _options;
    private readonly InterceptLog _log;
    private readonly QuarantineStore _quarantine;
    private readonly GuardRuntime _runtime;

    public GuardRuntimeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fg-rt-" + Guid.NewGuid().ToString("N"));
        _watch = Path.Combine(_root, "watch");
        Directory.CreateDirectory(_watch);

        _options = new GuardOptions
        {
            EnableQuarantine = true,
            QuarantineKeep = 5,
        };

        _log = new InterceptLog(Path.Combine(_root, "intercept.log"), 5);
        _quarantine = new QuarantineStore(Path.Combine(_root, "quarantine"));
        _runtime = new GuardRuntime(_options, _quarantine, _log);
    }

    public void Dispose()
    {
        _runtime.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private GuardEntry Entry(long maxBytes = 1024, bool limit = true, bool checkExisting = false) => new()
    {
        RootPath = _watch,
        MaxBytes = maxBytes,
        LimitEnabled = limit,
        CheckExistingFiles = checkExisting,
        WatchWrite = true,
        WatchCreate = true,
    };

    [Fact]
    public async Task OversizedNewFile_IsDeleted()
    {
        var entry = Entry(maxBytes: 1024);
        _runtime.Apply(new[] { entry });
        _runtime.Start();

        var path = Path.Combine(_watch, "big.cs");
        await File.WriteAllBytesAsync(path, new byte[4096]);

        var gone = await WaitUntilAsync(() => !File.Exists(path), 5000);
        Assert.True(gone, "超过阈值的文件应当被删除");
        Assert.Contains(_log.Recent, r => r.Path == path && r.Action == InterceptAction.Deleted);
    }

    [Fact]
    public async Task SmallFile_IsKept()
    {
        var entry = Entry(maxBytes: 1024);
        _runtime.Apply(new[] { entry });
        _runtime.Start();

        var path = Path.Combine(_watch, "small.cs");
        await File.WriteAllBytesAsync(path, new byte[512]);

        await Task.Delay(800);
        Assert.True(File.Exists(path), "未超过阈值的文件不应被删除");
        Assert.Empty(_log.Recent);
    }

    [Fact]
    public async Task ObserveMode_KeepsFileButRecords()
    {
        var entry = Entry(maxBytes: 1024, limit: false);
        _runtime.Apply(new[] { entry });
        _runtime.Start();

        var path = Path.Combine(_watch, "big-observe.cs");
        await File.WriteAllBytesAsync(path, new byte[4096]);

        var recorded = await WaitUntilAsync(
            () => _log.Recent.Any(r => r.Path == path), 5000);

        Assert.True(recorded, "观察模式应当留下记录");
        await Task.Delay(500);
        Assert.True(File.Exists(path), "观察模式不删文件");
        Assert.Contains(_log.Recent, r => r.Path == path && r.Action == InterceptAction.Observed);
    }

    [Fact]
    public async Task ExistingFile_NotCheckedByDefault()
    {
        var path = Path.Combine(_watch, "preexisting.cs");
        await File.WriteAllBytesAsync(path, new byte[4096]);

        var entry = Entry(maxBytes: 1024, checkExisting: false);
        _runtime.Apply(new[] { entry });
        _runtime.Start();

        // 触发一次修改事件。
        using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write))
        {
            await fs.WriteAsync(new byte[128]);
        }

        await Task.Delay(1200);
        Assert.True(File.Exists(path), "默认只拦新建文件，已存在文件超限不删");
    }

    [Fact]
    public async Task ExemptDirectory_IsIgnored()
    {
        var exempt = Path.Combine(_watch, "keep");
        Directory.CreateDirectory(exempt);

        var entry = Entry(maxBytes: 1024);
        entry.ExemptPaths.Add(exempt);

        _runtime.Apply(new[] { entry });
        _runtime.Start();

        var path = Path.Combine(exempt, "big.cs");
        await File.WriteAllBytesAsync(path, new byte[4096]);

        await Task.Delay(1200);
        Assert.True(File.Exists(path), "豁免目录里的文件不应被删除");
    }

    [Fact]
    public void VirtualDiskEntry_DoesNotStartWatcher()
    {
        var entry = Entry();
        entry.Mode = GuardMode.VirtualDisk;

        _runtime.Apply(new[] { entry });
        _runtime.Start();

        Assert.False(_runtime.IsEntryActive(entry.Id),
            "虚拟盘条目由 VirtualDiskManager 接管，监视引擎不应创建 watcher");
    }

    [Fact]
    public async Task OversizedFile_IsBackedUpToQuarantine()
    {
        var entry = Entry(maxBytes: 1024);
        _runtime.Apply(new[] { entry });
        _runtime.Start();

        var path = Path.Combine(_watch, "big-backup.cs");
        await File.WriteAllBytesAsync(path, new byte[4096]);

        var gone = await WaitUntilAsync(() => !File.Exists(path), 5000);
        Assert.True(gone);

        var hasBackup = await WaitUntilAsync(
            () => _quarantine.ListBatches().Count > 0, 3000);
        Assert.True(hasBackup, "删除前应当已备份到隔离区");
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }
}
