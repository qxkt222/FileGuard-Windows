using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

using FileGuard.WinFsp;

using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace FileGuard.WinFsp.Tests
{
    /// <summary>
    /// Real-mount tests. The POC proved that directory mount points work
    /// without administrator rights because the WinFsp.Launcher service is
    /// running. Every test unmounts in a finally block.
    /// </summary>
    public sealed class VirtualDiskTests
    {
        private const long Limit = 20 * 1024; // 20 KiB

        private static string NewRoot()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "fg_winfsp_tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static string NewProject(string root)
        {
            string real = Path.Combine(root, "proj");
            Directory.CreateDirectory(real);
            return real;
        }

        private static void Cleanup(string root)
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[test] cleanup failed: " + ex.Message);
            }
        }

        private static bool WaitUntil(Func<bool> condition, int timeoutMs)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                if (condition())
                    return true;
                Thread.Sleep(50);
            }
            return condition();
        }

        private static FileStream OpenForWrite(string path, FileMode mode, FileAccess access) =>
            new FileStream(path, mode, access,
                FileShare.Read | FileShare.Write | FileShare.Delete,
                bufferSize: 1, FileOptions.None);

        [Fact]
        public void Mount_Write5K_ReadBackSameContents()
        {
            string root = NewRoot();
            string real = NewProject(root);
            File.WriteAllText(Path.Combine(real, "seed.txt"), "seed");
            VirtualDisk? disk = null;
            try
            {
                disk = VirtualDisk.Mount(real, Limit);

                Assert.True(disk.IsMounted);
                Assert.Equal(real, disk.MountPoint, ignoreCase: true);
                Assert.Equal(real + ".__guard_store", disk.BackingStore, ignoreCase: true);
                // While mounted, WinFsp turns the original path into a junction.
                Assert.True(File.GetAttributes(real).HasFlag(FileAttributes.ReparsePoint));

                var payload = new byte[5 * 1024];
                new Random(42).NextBytes(payload);
                string target = Path.Combine(real, "five.bin");

                using (FileStream stream = OpenForWrite(target, FileMode.Create, FileAccess.Write))
                    stream.Write(payload, 0, payload.Length);

                Assert.True(File.Exists(target));
                Assert.Equal(payload, File.ReadAllBytes(target));
                Assert.Equal("seed", File.ReadAllText(Path.Combine(real, "seed.txt")));
            }
            finally
            {
                try { disk?.Unmount(); }
                finally { Cleanup(root); }
            }
        }

        [Fact]
        public void Mount_WriteOverLimit_FailsWithDiskFull_AndFileIsRemoved()
        {
            string root = NewRoot();
            string real = NewProject(root);
            VirtualDisk? disk = null;
            try
            {
                disk = VirtualDisk.Mount(real, Limit);

                var events = new ConcurrentQueue<DiskWriteRejected>();
                disk.WriteRejected += (_, e) => events.Enqueue(e);

                string target = Path.Combine(real, "too-big.bin");
                string backing = Path.Combine(disk.BackingStore, "too-big.bin");

                IOException? error = null;
                try
                {
                    using (FileStream stream = OpenForWrite(target, FileMode.Create, FileAccess.Write))
                    {
                        stream.Write(new byte[5 * 1024], 0, 5 * 1024);
                        stream.Write(new byte[30 * 1024], 0, 30 * 1024);
                    }
                }
                catch (IOException ex)
                {
                    error = ex;
                }

                Assert.NotNull(error);
                // WinFsp maps STATUS_DISK_FULL to Win32 ERROR_DISK_FULL (112).
                Assert.Equal(112, error!.HResult & 0xFFFF);

                Assert.True(events.Count >= 1, "WriteRejected was not raised.");
                DiskWriteRejected raised = events.First();
                Assert.Equal(target, raised.FilePath, ignoreCase: true);
                Assert.Equal(Limit, raised.MaxBytes);
                Assert.True(raised.AttemptedSize > Limit,
                    $"AttemptedSize {raised.AttemptedSize} should exceed {Limit}.");
                Assert.True(Math.Abs((raised.Timestamp - DateTimeOffset.Now).TotalMinutes) < 1);

                Assert.True(
                    WaitUntil(() => !File.Exists(target) && !File.Exists(backing), 5000),
                    "Rejected file was not cleaned up.");
            }
            finally
            {
                try { disk?.Unmount(); }
                finally { Cleanup(root); }
            }
        }

        [Fact]
        public void Boundary_ExactlyLimitSucceeds_OneByteMoreFails()
        {
            string root = NewRoot();
            string real = NewProject(root);
            VirtualDisk? disk = null;
            try
            {
                disk = VirtualDisk.Mount(real, Limit);

                string exact = Path.Combine(real, "exact.bin");
                var payload = new byte[Limit];
                new Random(7).NextBytes(payload);
                using (FileStream stream = OpenForWrite(exact, FileMode.Create, FileAccess.Write))
                    stream.Write(payload, 0, payload.Length);

                Assert.Equal(Limit, new FileInfo(exact).Length);
                Assert.Equal(payload, File.ReadAllBytes(exact));

                string plusOne = Path.Combine(real, "plus-one.bin");
                IOException? writeError = null;
                try
                {
                    using (FileStream stream = OpenForWrite(plusOne, FileMode.Create, FileAccess.Write))
                    {
                        stream.Write(new byte[Limit], 0, (int)Limit);
                        stream.WriteByte(0x42);
                        stream.Flush();
                    }
                }
                catch (IOException ex)
                {
                    writeError = ex;
                }

                Assert.NotNull(writeError);
                Assert.Equal(112, writeError!.HResult & 0xFFFF);

                string viaSetLength = Path.Combine(real, "set-length.bin");
                IOException? sizeError = null;
                try
                {
                    using (FileStream stream = OpenForWrite(viaSetLength, FileMode.Create, FileAccess.ReadWrite))
                        stream.SetLength(Limit + 1);
                }
                catch (IOException ex)
                {
                    sizeError = ex;
                }

                Assert.NotNull(sizeError);
                Assert.Equal(112, sizeError!.HResult & 0xFFFF);
            }
            finally
            {
                try { disk?.Unmount(); }
                finally { Cleanup(root); }
            }
        }

        [Fact]
        public void Unmount_RestoresOriginalDirectoryAndContents()
        {
            string root = NewRoot();
            string real = NewProject(root);
            Directory.CreateDirectory(Path.Combine(real, "sub"));
            File.WriteAllText(Path.Combine(real, "a.txt"), "alpha");
            File.WriteAllText(Path.Combine(real, "sub", "b.txt"), "beta");

            VirtualDisk? disk = null;
            try
            {
                disk = VirtualDisk.Mount(real, Limit);

                Assert.True(Directory.Exists(real));
                Assert.True((File.GetAttributes(real) & FileAttributes.ReparsePoint) != 0);
                Assert.True(Directory.Exists(disk.BackingStore));
                Assert.False(File.GetAttributes(disk.BackingStore).HasFlag(FileAttributes.ReparsePoint));
                Assert.Equal("alpha", File.ReadAllText(Path.Combine(real, "a.txt")));

                string added = Path.Combine(real, "added.txt");
                File.WriteAllText(added, "written-through-mount");

                disk.Unmount();
                disk = null;

                Assert.True(Directory.Exists(real));
                Assert.False(Directory.Exists(real + ".__guard_store"));
                Assert.False(File.GetAttributes(real).HasFlag(FileAttributes.ReparsePoint));
                Assert.Equal("alpha", File.ReadAllText(Path.Combine(real, "a.txt")));
                Assert.Equal("beta", File.ReadAllText(Path.Combine(real, "sub", "b.txt")));
                Assert.Equal("written-through-mount", File.ReadAllText(Path.Combine(real, "added.txt")));
            }
            finally
            {
                try { disk?.Unmount(); }
                finally { Cleanup(root); }
            }
        }

        [Fact]
        public void Recover_RestoresStoreWhenMountPointIsEmptyDirectory()
        {
            string root = NewRoot();
            string real = Path.Combine(root, "proj");
            string store = real + ".__guard_store";
            try
            {
                Directory.CreateDirectory(store);
                File.WriteAllText(Path.Combine(store, "data.txt"), "back");
                Directory.CreateDirectory(real); // empty leftover mount point

                Assert.True(VirtualDisk.IsOrphaned(real));
                VirtualDisk.Recover(real);

                Assert.False(Directory.Exists(store));
                Assert.True(Directory.Exists(real));
                Assert.Equal("back", File.ReadAllText(Path.Combine(real, "data.txt")));
                Assert.False(VirtualDisk.IsOrphaned(real));
            }
            finally
            {
                Cleanup(root);
            }
        }

        [Fact]
        public void Recover_RestoresStoreWhenMountPointIsMissing()
        {
            string root = NewRoot();
            string real = Path.Combine(root, "proj");
            string store = real + ".__guard_store";
            try
            {
                Directory.CreateDirectory(store);
                File.WriteAllText(Path.Combine(store, "data.txt"), "back");
                // real never created: simulates a crash after the rename.

                Assert.True(VirtualDisk.IsOrphaned(real));
                VirtualDisk.Recover(real);

                Assert.False(Directory.Exists(store));
                Assert.Equal("back", File.ReadAllText(Path.Combine(real, "data.txt")));
            }
            finally
            {
                Cleanup(root);
            }
        }

        [Fact]
        public void Recover_RefusesNonEmptyMountPoint()
        {
            string root = NewRoot();
            string real = Path.Combine(root, "proj");
            string store = real + ".__guard_store";
            try
            {
                Directory.CreateDirectory(store);
                File.WriteAllText(Path.Combine(store, "data.txt"), "back");
                Directory.CreateDirectory(real);
                File.WriteAllText(Path.Combine(real, "foreign.txt"), "do-not-touch");

                Assert.False(VirtualDisk.IsOrphaned(real));
                Assert.Throws<InvalidOperationException>(() => VirtualDisk.Recover(real));

                Assert.True(Directory.Exists(store));
                Assert.Equal("do-not-touch", File.ReadAllText(Path.Combine(real, "foreign.txt")));
            }
            finally
            {
                Cleanup(root);
            }
        }

        [Fact]
        public void Mount_TwiceOnSamePath_ThrowsClearly()
        {
            string root = NewRoot();
            string real = NewProject(root);
            VirtualDisk? disk = null;
            try
            {
                disk = VirtualDisk.Mount(real, Limit);

                Assert.False(VirtualDisk.IsOrphaned(real));

                var error = Assert.Throws<InvalidOperationException>(
                    () => VirtualDisk.Mount(real, Limit));
                Assert.Contains("already mounted", error.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { disk?.Unmount(); }
                finally { Cleanup(root); }
            }
        }
    }
}
