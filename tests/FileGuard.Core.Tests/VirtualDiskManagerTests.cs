using System.Diagnostics;
using FileGuard.Core.Logging;
using FileGuard.Core.Models;
using FileGuard.Core.VirtualDisks;
using Xunit;

namespace FileGuard.Core.Tests;

/// <summary>
/// 虚拟盘条目的集成测试：真实挂载 WinFsp，验证挂载/卸载生命周期、
/// 残留恢复、失败上报和超限写入拒绝。
/// </summary>
public sealed class VirtualDiskManagerTests : IDisposable
{
    private const long SmallLimit = 4 * 1024;

    private readonly string _root;
    private readonly InterceptLog _log;
    private readonly VirtualDiskManager _manager;

    public VirtualDiskManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fg-vd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _log = new InterceptLog(Path.Combine(_root, "intercept.log"), 10);
        _manager = new VirtualDiskManager(_log);
    }

    public void Dispose()
    {
        _manager.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string NewProject(string name = "proj")
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static GuardEntry DiskEntry(string path, long maxBytes = SmallLimit, bool enabled = true) => new()
    {
        RootPath = path,
        Mode = GuardMode.VirtualDisk,
        MaxBytes = maxBytes,
        Enabled = enabled,
    };

    private static bool IsJunction(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    [Fact]
    public void Start_MountsEntry_StopRestoresDirectory()
    {
        var real = NewProject();
        File.WriteAllText(Path.Combine(real, "seed.txt"), "seed");
        var entry = DiskEntry(real);

        _manager.Start(new[] { entry });

        Assert.True(_manager.IsMounted(entry.Id));
        Assert.Null(_manager.GetError(entry.Id));
        Assert.Equal(real, _manager.GetMountPoint(entry.Id), ignoreCase: true);
        Assert.True(IsJunction(real), "挂载后原路径应当变成指向背书目录的 junction");
        Assert.True(Directory.Exists(real + ".__guard_store"));
        Assert.Equal("seed", File.ReadAllText(Path.Combine(real, "seed.txt")));

        _manager.Stop();

        Assert.False(_manager.IsMounted(entry.Id));
        Assert.False(Directory.Exists(real + ".__guard_store"));
        Assert.True(Directory.Exists(real));
        Assert.False(IsJunction(real));
        Assert.Equal("seed", File.ReadAllText(Path.Combine(real, "seed.txt")));
    }

    [Fact]
    public void WatchModeEntry_IsNotTouched()
    {
        var real = NewProject();
        var entry = DiskEntry(real);
        entry.Mode = GuardMode.Watch;

        _manager.Start(new[] { entry });

        Assert.False(_manager.IsMounted(entry.Id));
        Assert.False(IsJunction(real));
        Assert.False(Directory.Exists(real + ".__guard_store"));
    }

    [Fact]
    public void DisablingEntry_UnmountsAndRestoresContents()
    {
        var real = NewProject();
        File.WriteAllText(Path.Combine(real, "a.txt"), "alpha");
        var entry = DiskEntry(real);

        _manager.Start(new[] { entry });
        Assert.True(_manager.IsMounted(entry.Id));

        entry.Enabled = false;
        _manager.Apply(new[] { entry });

        Assert.False(_manager.IsMounted(entry.Id));
        Assert.False(Directory.Exists(real + ".__guard_store"));
        Assert.False(IsJunction(real));
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(real, "a.txt")));
    }

    [Fact]
    public void MountFailure_IsReportedNotThrown()
    {
        var missing = Path.Combine(_root, "missing");
        var entry = DiskEntry(missing);

        VirtualDiskFault? fault = null;
        _manager.Faulted += (_, e) => fault = e;

        _manager.Start(new[] { entry }); // 不能抛

        Assert.False(_manager.IsMounted(entry.Id));
        var error = _manager.GetError(entry.Id);
        Assert.NotNull(error);
        Assert.Contains("does not exist", error!, StringComparison.OrdinalIgnoreCase);

        Assert.NotNull(fault);
        Assert.Equal(entry.Id, fault!.EntryId);
        Assert.Equal(missing, fault.Path, ignoreCase: true);
    }

    [Fact]
    public void OrphanedBackingStore_IsRecoveredBeforeMount()
    {
        var real = Path.Combine(_root, "proj");
        string store = real + ".__guard_store";
        Directory.CreateDirectory(store);
        File.WriteAllText(Path.Combine(store, "back.txt"), "back");

        // 模拟进程崩溃：挂载点只剩一个空目录，文件都在背书目录里。
        Directory.CreateDirectory(real);
        Assert.True(FileGuard.WinFsp.VirtualDisk.IsOrphaned(real));

        var entry = DiskEntry(real);
        _manager.Start(new[] { entry });

        Assert.True(_manager.IsMounted(entry.Id));
        Assert.Null(_manager.GetError(entry.Id));
        Assert.Equal("back", File.ReadAllText(Path.Combine(real, "back.txt")));

        _manager.Stop();
        Assert.False(Directory.Exists(store));
        Assert.Equal("back", File.ReadAllText(Path.Combine(real, "back.txt")));
    }

    [Fact]
    public void OverLimitWrite_IsRecordedAsRejected()
    {
        var real = NewProject();
        var entry = DiskEntry(real);
        _manager.Start(new[] { entry });

        string target = Path.Combine(real, "too-big.bin");

        var error = Assert.ThrowsAny<IOException>(() =>
        {
            using var stream = new FileStream(
                target,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read | FileShare.Write | FileShare.Delete,
                bufferSize: 1,
                FileOptions.None);
            stream.Write(new byte[1024], 0, 1024);
            stream.Write(new byte[8 * 1024], 0, 8 * 1024);
            stream.Flush();
        });

        Assert.Equal(112, error.HResult & 0xFFFF); // ERROR_DISK_FULL

        var recorded = WaitUntil(
            () => _log.Recent.Any(r => r.EntryId == entry.Id && r.Action == InterceptAction.Rejected),
            5000);

        Assert.True(recorded, "超限写入应当产生一条 Rejected 记录");

        var record = _log.Recent.First(r => r.EntryId == entry.Id && r.Action == InterceptAction.Rejected);
        Assert.Equal(target, record.Path, ignoreCase: true);
        Assert.Equal(SmallLimit, record.Limit);
        Assert.True(record.Size > record.Limit);
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }
}
