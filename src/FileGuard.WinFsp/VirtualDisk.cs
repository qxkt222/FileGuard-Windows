using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

using Fsp;

namespace FileGuard.WinFsp
{
    /// <summary>
    /// Mounts <c>realDir</c> in place as a size-limited virtual disk.
    /// On <see cref="Mount"/> the real directory is renamed to
    /// <c>&lt;realDir&gt;.__guard_store</c>; WinFsp then creates a junction at
    /// the original path and serves the backing store through it.
    /// <see cref="Unmount"/> reverses the rename.
    /// </summary>
    /// <remarks>
    /// WinFsp creates the directory mount point itself. It refuses (NTSTATUS
    /// 0xC0000035) if the mount point path already exists, so the original
    /// directory must be moved away before mounting and must not be recreated.
    /// </remarks>
    public sealed class VirtualDisk : IDisposable
    {
        internal const string StoreSuffix = ".__guard_store";

        private static readonly object SyncRoot = new object();
        private static readonly HashSet<string> MountedPaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly string _mountPoint;
        private readonly string _backingStore;
        private readonly long _maxBytes;
        private readonly LimitedPassthrough _fileSystem;
        private readonly FileSystemHost _host;
        private volatile bool _mounted;
        private bool _disposed;

        private VirtualDisk(string mountPoint, string backingStore, long maxBytes,
            LimitedPassthrough fileSystem, FileSystemHost host)
        {
            _mountPoint = mountPoint;
            _backingStore = backingStore;
            _maxBytes = maxBytes;
            _fileSystem = fileSystem;
            _host = host;
            _mounted = true;
        }

        /// <summary>Path the disk is mounted at (e.g. <c>D:\proj</c>).</summary>
        public string MountPoint => _mountPoint;

        /// <summary>Directory that physically holds the files (e.g. <c>D:\proj.__guard_store</c>).</summary>
        public string BackingStore => _backingStore;

        /// <summary>True between a successful <see cref="Mount"/> and <see cref="Unmount"/>.</summary>
        public bool IsMounted => _mounted;

        /// <summary>Raised on every write/size change refused for exceeding the limit.</summary>
        public event EventHandler<DiskWriteRejected>? WriteRejected;

        /// <summary>
        /// Mounts <paramref name="realDir"/> in place with a per-file limit of
        /// <paramref name="maxBytes"/> bytes.
        /// </summary>
        /// <exception cref="ArgumentException">Path is empty, a drive root, or looks like a backing store.</exception>
        /// <exception cref="DirectoryNotFoundException">The directory does not exist.</exception>
        /// <exception cref="InvalidOperationException">
        /// Already mounted in this process, the path is a reparse point, or a
        /// leftover backing store needs <see cref="Recover"/> first.
        /// </exception>
        /// <exception cref="IOException">The directory could not be renamed (in use) or WinFsp refused the mount.</exception>
        public static VirtualDisk Mount(string realDir, long maxBytes)
        {
            if (maxBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes,
                    "maxBytes must be positive.");

            string full = NormalizeDirectory(realDir, nameof(realDir));
            EnsureNotDriveRoot(full, nameof(realDir));
            if (full.EndsWith(StoreSuffix, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"'{full}' looks like a FileGuard backing store; pass the original directory instead.",
                    nameof(realDir));

            string store = full + StoreSuffix;

            lock (SyncRoot)
            {
                if (MountedPaths.Contains(full))
                    throw new InvalidOperationException(
                        $"A VirtualDisk is already mounted at '{full}' in this process.");

                if (File.Exists(full))
                    throw new IOException($"Cannot mount '{full}': the path is a file.");

                // Checked before the directory-existence probe: a crashed
                // process can leave a dead junction that Directory.Exists
                // reports as missing, and telling the caller to Recover is
                // more useful than "directory does not exist".
                if (Directory.Exists(store) || File.Exists(store))
                    throw new InvalidOperationException(
                        $"A backing store already exists at '{store}'. Either another FileGuard instance is using " +
                        $"this folder, or a previous mount did not unmount cleanly. Unmount the other instance, or " +
                        $"call VirtualDisk.Recover(\"{full}\") after confirming it crashed.");

                if (!Directory.Exists(full))
                    throw new DirectoryNotFoundException(
                        $"Cannot mount '{full}': the directory does not exist.");

                FileAttributes attributes = File.GetAttributes(full);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException(
                        $"Cannot mount '{full}': it is a reparse point (junction, symlink, or an active mount point).");

                MoveDirectory(full, store);

                LimitedPassthrough? fileSystem = null;
                FileSystemHost? host = null;
                bool mountSucceeded = false;
                try
                {
                    VirtualDisk? disk = null;
                    fileSystem = new LimitedPassthrough(store, maxBytes,
                        (path, size) => disk?.OnRejected(path, size));
                    host = new FileSystemHost(fileSystem);
                    int rc = host.Mount(full, null, true, 0);
                    if (rc < 0)
                        throw new IOException(
                            $"WinFsp mount at '{full}' failed with NTSTATUS 0x{unchecked((uint)rc):X8}.");

                    mountSucceeded = true;
                    if (!Directory.Exists(full))
                        throw new IOException(
                            $"WinFsp reported success but the mount point '{full}' is missing.");
                    disk = new VirtualDisk(full, store, maxBytes, fileSystem, host);
                    MountedPaths.Add(full);
                    return disk;
                }
                catch
                {
                    if (mountSucceeded && host != null)
                    {
                        try { host.Unmount(); } catch (Exception ex) { Debug.WriteLine(ex); }
                        try { fileSystem?.CloseAllHandles(); } catch (Exception ex) { Debug.WriteLine(ex); }
                    }
                    try { host?.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex); }
                    try
                    {
                        RemoveMountPointArtifact(full);
                        MoveDirectory(store, full);
                    }
                    catch (Exception ex)
                    {
                        // 回滚失败用户会看到目录消失/停在半截，必须留下可查的记录。
                        DiskLog.Error(
                            $"挂载失败回滚未完成，backing store 保留在 '{store}'，原路径 '{full}' 未恢复", ex);
                    }
                    throw;
                }
            }
        }

        /// <summary>
        /// Unmounts the disk, removes the mount point, and renames the backing
        /// store back to its original path.
        /// </summary>
        public void Unmount()
        {
            lock (SyncRoot)
            {
                if (!_mounted)
                    return;

                bool unmounted = false;
                try
                {
                    _host.Unmount();
                    unmounted = true;
                    _mounted = false;
                    MountedPaths.Remove(_mountPoint);
                    // Release any backing handle WinFsp did not deliver a
                    // Close for; otherwise the directory rename below fails.
                    _fileSystem.CloseAllHandles();
                }
                finally
                {
                    if (unmounted)
                    {
                        try { _host.Dispose(); }
                        catch (Exception ex) { Debug.WriteLine(ex); }
                    }
                }

                try
                {
                    RemoveMountPointArtifact(_mountPoint);
                    MoveDirectory(_backingStore, _mountPoint);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw new IOException(
                        $"Unmounted, but failed to restore '{_backingStore}' to '{_mountPoint}'. " +
                        $"Close any handles inside it, then call VirtualDisk.Recover(\"{_mountPoint}\").",
                        ex);
                }
            }
        }

        /// <summary>
        /// Detects a crash leftover: <c>&lt;realDir&gt;.__guard_store</c> exists
        /// and the mount point is missing, an empty plain directory, or a dead
        /// junction.
        /// </summary>
        public static bool IsOrphaned(string realDir)
        {
            string full = NormalizeDirectory(realDir, nameof(realDir));
            string store = full + StoreSuffix;

            lock (SyncRoot)
            {
                if (!Directory.Exists(store))
                    return false;
                if (MountedPaths.Contains(full))
                    return false;
                return CanRestore(full, out _);
            }
        }

        /// <summary>
        /// Restores an orphaned backing store to its original path. Refuses to
        /// touch anything that is still in use or that holds foreign files.
        /// </summary>
        public static void Recover(string realDir)
        {
            string full = NormalizeDirectory(realDir, nameof(realDir));
            EnsureNotDriveRoot(full, nameof(realDir));
            string store = full + StoreSuffix;

            lock (SyncRoot)
            {
                if (!Directory.Exists(store) && !File.Exists(store))
                    throw new InvalidOperationException(
                        $"No backing store found at '{store}'; nothing to recover.");

                if (MountedPaths.Contains(full))
                    throw new InvalidOperationException(
                        $"A VirtualDisk is currently mounted at '{full}'; unmount it first.");

                if (!CanRestore(full, out string reason))
                    throw new InvalidOperationException(
                        $"Cannot recover '{full}': {reason}. Move the existing contents away and retry.");

                RemoveMountPointArtifact(full);

                try
                {
                    MoveDirectory(store, full);
                }
                catch (IOException ex)
                {
                    throw new IOException(
                        $"Failed to restore '{store}' to '{full}': {ex.Message}", ex);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            try
            {
                Unmount();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[FileGuard.WinFsp] Dispose failed to unmount: " + ex.Message);
            }
            _disposed = true;
            // Keep the file system instance reachable for the whole lifetime
            // of the host (WinFsp stores an unmanaged pointer to it).
            GC.KeepAlive(_fileSystem);
        }

        private void OnRejected(string backingPath, long attemptedSize)
        {
            EventHandler<DiskWriteRejected>? handler = WriteRejected;
            if (handler == null)
                return;
            try
            {
                handler(this, new DiskWriteRejected(
                    ToLogicalPath(backingPath), attemptedSize, _maxBytes, DateTimeOffset.Now));
            }
            catch (Exception ex)
            {
                // A faulty subscriber must never break the file system.
                Debug.WriteLine("[FileGuard.WinFsp] WriteRejected subscriber failed: " + ex.Message);
            }
        }

        private string ToLogicalPath(string backingPath)
        {
            try
            {
                string relative = Path.GetRelativePath(_backingStore, backingPath);
                if (relative == "." || relative == ".." ||
                    relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                    Path.IsPathRooted(relative))
                    return backingPath;
                return Path.Combine(_mountPoint, relative);
            }
            catch (ArgumentException)
            {
                return backingPath;
            }
        }

        private static bool CanRestore(string mountPoint, out string reason)
        {
            if (File.Exists(mountPoint))
            {
                reason = "the path is a file";
                return false;
            }

            FileAttributes? attributes = TryGetAttributes(mountPoint);

            if (!Directory.Exists(mountPoint))
            {
                if (attributes.HasValue && (attributes.Value & FileAttributes.ReparsePoint) != 0)
                {
                    if (IsMountPointInUse(mountPoint))
                    {
                        reason = "the mount point is in use";
                        return false;
                    }
                    reason = "stale mount point";
                    return true;
                }
                reason = "the mount point is missing";
                return true;
            }

            FileAttributes actual = attributes ?? File.GetAttributes(mountPoint);
            if ((actual & FileAttributes.ReparsePoint) != 0)
            {
                if (IsMountPointInUse(mountPoint))
                {
                    reason = "the mount point is in use";
                    return false;
                }
                reason = "stale mount point";
                return true;
            }

            try
            {
                if (IsDirectoryEmpty(mountPoint))
                {
                    reason = "empty mount point directory";
                    return true;
                }
                reason = "the mount point directory is not empty";
                return false;
            }
            catch (IOException ex)
            {
                reason = "the mount point directory is unreadable: " + ex.Message;
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                reason = "the mount point directory is inaccessible: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// A junction is considered live if its target can be enumerated (or
        /// access is denied). A dangling junction fails with an I/O error.
        /// </summary>
        private static bool IsMountPointInUse(string path)
        {
            try
            {
                using (IEnumerator<string> entries =
                    Directory.EnumerateFileSystemEntries(path).GetEnumerator())
                {
                    entries.MoveNext();
                }
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }

        private static bool IsDirectoryEmpty(string path)
        {
            using (IEnumerator<string> entries =
                Directory.EnumerateFileSystemEntries(path).GetEnumerator())
            {
                return !entries.MoveNext();
            }
        }

        /// <summary>
        /// Removes whatever occupies the mount point path after unmount: the
        /// empty directory WinFsp leaves behind, or a dangling junction.
        /// Refuses to touch a plain directory that is not empty.
        /// </summary>
        private static void RemoveMountPointArtifact(string path)
        {
            if (File.Exists(path))
                throw new IOException($"Mount point '{path}' is a file; refusing to delete it.");

            if (Directory.Exists(path))
            {
                if (!IsDirectoryEmpty(path))
                    throw new IOException($"Mount point '{path}' is not empty; refusing to delete it.");
                Directory.Delete(path, false);
                return;
            }

            // Directory.Exists is false for dangling junctions; remove the
            // leftover name if one exists.
            try
            {
                Directory.Delete(path, false);
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (IOException ex)
            {
                Debug.WriteLine($"[FileGuard.WinFsp] stale mount point '{path}' could not be removed: {ex.Message}");
            }
        }

        private static FileAttributes? TryGetAttributes(string path)
        {
            try
            {
                return File.GetAttributes(path);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static string NormalizeDirectory(string path, string parameterName)
        {
            if (path == null)
                throw new ArgumentNullException(parameterName);
            if (path.Trim().Length == 0)
                throw new ArgumentException("Path must not be empty.", parameterName);

            string full = Path.GetFullPath(path);
            if (full.Length > 3)
                full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full;
        }

        private static void EnsureNotDriveRoot(string full, string parameterName)
        {
            if (Path.GetDirectoryName(full) == null)
                throw new ArgumentException($"Cannot use a drive root ('{full}') as a mount point.", parameterName);
        }

        private static void MoveDirectory(string source, string destination)
        {
            // A just-unmounted volume, an open Explorer window, or an
            // antivirus scan can hold the directory for a moment. Retry for
            // up to ~5 seconds before giving up.
            Exception? last = null;
            for (int attempt = 0; attempt < 50; attempt++)
            {
                try
                {
                    Directory.Move(source, destination);
                    return;
                }
                catch (IOException ex)
                {
                    last = ex;
                    Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException ex)
                {
                    last = ex;
                    Thread.Sleep(100);
                }
            }
            throw new IOException(
                $"Could not move directory '{source}' to '{destination}'. " +
                "It may be open in another process (Explorer, scanner) — close it and retry.",
                last);
        }
    }
}
