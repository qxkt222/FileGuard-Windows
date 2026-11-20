#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;

using Fsp;
using VolumeInfo = Fsp.Interop.VolumeInfo;
using FileInfo = Fsp.Interop.FileInfo;

namespace FileGuard.WinFsp
{
    /// <summary>
    /// Passthrough file system backed by the directory <c>backingPath</c>.
    /// Any <c>Write</c>/<c>SetFileSize</c> that would grow a file beyond
    /// <paramref name="maxFileSize"/> is refused with STATUS_DISK_FULL and the
    /// file is discarded in full when its last handle is closed.
    /// Ported from the validated POC (poc/WinfspPoc/LimitedPassthrough.cs).
    /// </summary>
    internal sealed class LimitedPassthrough : FileSystemBase
    {
        private const int ALLOCATION_UNIT = 4096;

        private readonly Int64 _maxFileSize;
        private readonly Action<String, Int64> _onRejected;
        private readonly String _Path;

        /* Backing files currently open, so unmount can release leaked handles. */
        private readonly Object _openLock = new Object();
        private readonly HashSet<FileDesc> _openFiles = new HashSet<FileDesc>();

        internal LimitedPassthrough(String backingPath, Int64 maxFileSize,
            Action<String, Int64> onRejected)
        {
            if (maxFileSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxFileSize));

            _Path = Path.GetFullPath(backingPath);
            if (_Path.EndsWith(Path.DirectorySeparatorChar.ToString()))
                _Path = _Path.Substring(0, _Path.Length - 1);
            _maxFileSize = maxFileSize;
            _onRejected = onRejected;
        }

        private static void ThrowIoExceptionWithHResult(Int32 HResult)
        {
            throw new IOException(null, HResult);
        }
        private static void ThrowIoExceptionWithWin32(Int32 Error)
        {
            ThrowIoExceptionWithHResult(unchecked((Int32)(0x80070000 | Error)));
        }
        private static void ThrowIoExceptionWithNtStatus(Int32 Status)
        {
            ThrowIoExceptionWithWin32((Int32)Win32FromNtStatus(Status));
        }

        private class FileDesc
        {
            public String Path;
            public FileStream Stream;
            public DirectoryInfo DirInfo;
            public DictionaryEntry[] FileSystemInfos;
            public bool Rejected;

            public FileDesc(FileStream Stream, String Path)
            {
                this.Stream = Stream;
                this.Path = Path;
            }
            public FileDesc(DirectoryInfo DirInfo)
            {
                this.DirInfo = DirInfo;
            }
            public static void GetFileInfoFromFileSystemInfo(
                FileSystemInfo Info,
                out FileInfo FileInfo)
            {
                FileInfo.FileAttributes = (UInt32)Info.Attributes;
                FileInfo.ReparseTag = 0;
                FileInfo.FileSize = Info is System.IO.FileInfo ?
                    (UInt64)((System.IO.FileInfo)Info).Length : 0;
                FileInfo.AllocationSize = (FileInfo.FileSize + ALLOCATION_UNIT - 1)
                    / ALLOCATION_UNIT * ALLOCATION_UNIT;
                FileInfo.CreationTime = (UInt64)Info.CreationTimeUtc.ToFileTimeUtc();
                FileInfo.LastAccessTime = (UInt64)Info.LastAccessTimeUtc.ToFileTimeUtc();
                FileInfo.LastWriteTime = (UInt64)Info.LastWriteTimeUtc.ToFileTimeUtc();
                FileInfo.ChangeTime = FileInfo.LastWriteTime;
                FileInfo.IndexNumber = 0;
                FileInfo.HardLinks = 0;
            }
            public Int32 GetFileInfo(out FileInfo FileInfo)
            {
                if (null != Stream)
                {
                    BY_HANDLE_FILE_INFORMATION Info;
                    if (!GetFileInformationByHandle(Stream.SafeFileHandle.DangerousGetHandle(),
                        out Info))
                        ThrowIoExceptionWithWin32(Marshal.GetLastWin32Error());
                    FileInfo.FileAttributes = Info.dwFileAttributes;
                    FileInfo.ReparseTag = 0;
                    FileInfo.FileSize = (UInt64)Stream.Length;
                    FileInfo.AllocationSize = (FileInfo.FileSize + ALLOCATION_UNIT - 1)
                        / ALLOCATION_UNIT * ALLOCATION_UNIT;
                    FileInfo.CreationTime = Info.ftCreationTime;
                    FileInfo.LastAccessTime = Info.ftLastAccessTime;
                    FileInfo.LastWriteTime = Info.ftLastWriteTime;
                    FileInfo.ChangeTime = FileInfo.LastWriteTime;
                    FileInfo.IndexNumber = 0;
                    FileInfo.HardLinks = 0;
                }
                else
                    GetFileInfoFromFileSystemInfo(DirInfo, out FileInfo);
                return STATUS_SUCCESS;
            }
            public void SetBasicInfo(
                UInt32 FileAttributes,
                UInt64 CreationTime,
                UInt64 LastAccessTime,
                UInt64 LastWriteTime)
            {
                if (0 == FileAttributes)
                    FileAttributes = (UInt32)System.IO.FileAttributes.Normal;
                if (null != Stream)
                {
                    FILE_BASIC_INFO Info = default(FILE_BASIC_INFO);
                    if (unchecked((UInt32)(-1)) != FileAttributes)
                        Info.FileAttributes = FileAttributes;
                    if (0 != CreationTime)
                        Info.CreationTime = CreationTime;
                    if (0 != LastAccessTime)
                        Info.LastAccessTime = LastAccessTime;
                    if (0 != LastWriteTime)
                        Info.LastWriteTime = LastWriteTime;
                    if (!SetFileInformationByHandle(Stream.SafeFileHandle.DangerousGetHandle(),
                        0/*FileBasicInfo*/, ref Info, (UInt32)Marshal.SizeOf(Info)))
                        ThrowIoExceptionWithWin32(Marshal.GetLastWin32Error());
                }
                else
                {
                    if (unchecked((UInt32)(-1)) != FileAttributes)
                        DirInfo.Attributes = (System.IO.FileAttributes)FileAttributes;
                    if (0 != CreationTime)
                        DirInfo.CreationTimeUtc = DateTime.FromFileTimeUtc((Int64)CreationTime);
                    if (0 != LastAccessTime)
                        DirInfo.LastAccessTimeUtc = DateTime.FromFileTimeUtc((Int64)LastAccessTime);
                    if (0 != LastWriteTime)
                        DirInfo.LastWriteTimeUtc = DateTime.FromFileTimeUtc((Int64)LastWriteTime);
                }
            }
            public UInt32 GetFileAttributes()
            {
                FileInfo FileInfo;
                GetFileInfo(out FileInfo);
                return FileInfo.FileAttributes;
            }
            public void SetFileAttributes(UInt32 FileAttributes)
            {
                SetBasicInfo(FileAttributes, 0, 0, 0);
            }
            public Byte[] GetSecurityDescriptor()
            {
                if (null != Stream)
                    return Stream.GetAccessControl().GetSecurityDescriptorBinaryForm();
                else
                    return DirInfo.GetAccessControl().GetSecurityDescriptorBinaryForm();
            }
            public void SetSecurityDescriptor(AccessControlSections Sections, Byte[] SecurityDescriptor)
            {
                Int32 SecurityInformation = 0;
                if (0 != (Sections & AccessControlSections.Owner))
                    SecurityInformation |= 1/*OWNER_SECURITY_INFORMATION*/;
                if (0 != (Sections & AccessControlSections.Group))
                    SecurityInformation |= 2/*GROUP_SECURITY_INFORMATION*/;
                if (0 != (Sections & AccessControlSections.Access))
                    SecurityInformation |= 4/*DACL_SECURITY_INFORMATION*/;
                if (0 != (Sections & AccessControlSections.Audit))
                    SecurityInformation |= 8/*SACL_SECURITY_INFORMATION*/;
                if (null != Stream)
                {
                    if (!SetKernelObjectSecurity(Stream.SafeFileHandle.DangerousGetHandle(),
                        SecurityInformation, SecurityDescriptor))
                        ThrowIoExceptionWithWin32(Marshal.GetLastWin32Error());
                }
                else
                {
                    if (!SetFileSecurityW(DirInfo.FullName,
                        SecurityInformation, SecurityDescriptor))
                        ThrowIoExceptionWithWin32(Marshal.GetLastWin32Error());
                }
            }
            public void SetDisposition(Boolean Safe)
            {
                if (null != Stream)
                {
                    FILE_DISPOSITION_INFO Info;
                    Info.DeleteFile = true;
                    if (!SetFileInformationByHandle(Stream.SafeFileHandle.DangerousGetHandle(),
                        4/*FileDispositionInfo*/, ref Info, (UInt32)Marshal.SizeOf(Info)))
                        if (!Safe)
                            ThrowIoExceptionWithWin32(Marshal.GetLastWin32Error());
                }
                else
                    try
                    {
                        DirInfo.Delete();
                    }
                    catch (Exception ex)
                    {
                        if (!Safe)
                            ThrowIoExceptionWithHResult(ex.HResult);
                    }
            }
            public static void Rename(String FileName, String NewFileName, Boolean ReplaceIfExists)
            {
                if (!MoveFileExW(FileName, NewFileName, ReplaceIfExists ? 1U/*MOVEFILE_REPLACE_EXISTING*/ : 0))
                    ThrowIoExceptionWithWin32(Marshal.GetLastWin32Error());
            }

            /* interop */
            [StructLayout(LayoutKind.Sequential, Pack = 4)]
            private struct BY_HANDLE_FILE_INFORMATION
            {
                public UInt32 dwFileAttributes;
                public UInt64 ftCreationTime;
                public UInt64 ftLastAccessTime;
                public UInt64 ftLastWriteTime;
                public UInt32 dwVolumeSerialNumber;
                public UInt32 nFileSizeHigh;
                public UInt32 nFileSizeLow;
                public UInt32 nNumberOfLinks;
                public UInt32 nFileIndexHigh;
                public UInt32 nFileIndexLow;
            }
            [StructLayout(LayoutKind.Sequential)]
            private struct FILE_BASIC_INFO
            {
                public UInt64 CreationTime;
                public UInt64 LastAccessTime;
                public UInt64 LastWriteTime;
                public UInt64 ChangeTime;
                public UInt32 FileAttributes;
            }
            [StructLayout(LayoutKind.Sequential)]
            private struct FILE_DISPOSITION_INFO
            {
                public Boolean DeleteFile;
            }
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern Boolean GetFileInformationByHandle(
                IntPtr hFile,
                out BY_HANDLE_FILE_INFORMATION lpFileInformation);
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern Boolean SetFileInformationByHandle(
                IntPtr hFile,
                Int32 FileInformationClass,
                ref FILE_BASIC_INFO lpFileInformation,
                UInt32 dwBufferSize);
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern Boolean SetFileInformationByHandle(
                IntPtr hFile,
                Int32 FileInformationClass,
                ref FILE_DISPOSITION_INFO lpFileInformation,
                UInt32 dwBufferSize);
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern Boolean MoveFileExW(
                [MarshalAs(UnmanagedType.LPWStr)] String lpExistingFileName,
                [MarshalAs(UnmanagedType.LPWStr)] String lpNewFileName,
                UInt32 dwFlags);
            [DllImport("advapi32.dll", SetLastError = true)]
            private static extern Boolean SetFileSecurityW(
                [MarshalAs(UnmanagedType.LPWStr)] String FileName,
                Int32 SecurityInformation,
                Byte[] SecurityDescriptor);
            [DllImport("advapi32.dll", SetLastError = true)]
            private static extern Boolean SetKernelObjectSecurity(
                IntPtr Handle,
                Int32 SecurityInformation,
                Byte[] SecurityDescriptor);
        }

        private class DirectoryEntryComparer : IComparer
        {
            public int Compare(object x, object y)
            {
                return String.Compare(
                    (String)((DictionaryEntry)x).Key,
                    (String)((DictionaryEntry)y).Key);
            }
        }
        private static DirectoryEntryComparer _DirectoryEntryComparer =
            new DirectoryEntryComparer();

        public String ConcatPath(String FileName)
        {
            return _Path + FileName;
        }

        /// <summary>
        /// Releases every backing file handle that is still open. Called after
        /// WinFsp has unmounted, before the backing directory is renamed back.
        /// A leaked handle would otherwise block the directory move.
        /// </summary>
        internal void CloseAllHandles()
        {
            FileDesc[] Open;
            lock (_openLock)
            {
                Open = new FileDesc[_openFiles.Count];
                _openFiles.CopyTo(Open);
                _openFiles.Clear();
            }
            foreach (FileDesc FileDesc in Open)
            {
                bool Rejected = FileDesc.Rejected;
                try
                {
                    if (null != FileDesc.Stream)
                    {
                        FileDesc.Stream.Dispose();
                        FileDesc.Stream = null;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[FileGuard.WinFsp] close-on-unmount failed: " + ex.Message);
                }
                if (Rejected)
                    DeleteFileWithRetry(FileDesc.Path);
            }
        }

        private void TrackOpen(FileDesc FileDesc)
        {
            if (null == FileDesc || null == FileDesc.Stream)
                return;
            lock (_openLock)
                _openFiles.Add(FileDesc);
        }

        private void UntrackOpen(FileDesc FileDesc)
        {
            if (null == FileDesc)
                return;
            lock (_openLock)
                _openFiles.Remove(FileDesc);
        }
        public override Int32 ExceptionHandler(Exception ex)
        {
            Debug.WriteLine("[FileGuard.WinFsp] EXCEPTION " + ex.GetType().FullName + ": " + ex.Message);
            Int32 HResult = ex.HResult;
            if (0x80070000 == (HResult & 0xFFFF0000))
                return NtStatusFromWin32((UInt32)HResult & 0xFFFF);
            return STATUS_UNEXPECTED_IO_ERROR;
        }
        public override Int32 Init(Object Host0)
        {
            FileSystemHost Host = (FileSystemHost)Host0;
            Host.SectorSize = ALLOCATION_UNIT;
            Host.SectorsPerAllocationUnit = 1;
            Host.MaxComponentLength = 255;
            Host.FileInfoTimeout = 1000;
            Host.CaseSensitiveSearch = false;
            Host.CasePreservedNames = true;
            Host.UnicodeOnDisk = true;
            Host.PersistentAcls = true;
            Host.PostCleanupWhenModifiedOnly = true;
            Host.PassQueryDirectoryPattern = true;
            Host.FlushAndPurgeOnCleanup = true;
            Host.VolumeCreationTime = (UInt64)File.GetCreationTimeUtc(_Path).ToFileTimeUtc();
            Host.VolumeSerialNumber = 0;
            return STATUS_SUCCESS;
        }
        public override Int32 GetVolumeInfo(
            out VolumeInfo VolumeInfo)
        {
            VolumeInfo = default(VolumeInfo);
            try
            {
                DriveInfo Info = new DriveInfo(_Path);
                VolumeInfo.TotalSize = (UInt64)Info.TotalSize;
                VolumeInfo.FreeSize = (UInt64)Info.TotalFreeSpace;
            }
            catch (ArgumentException)
            {
                /*
                 * DriveInfo only supports drives and does not support UNC paths.
                 * It would be better to use GetDiskFreeSpaceEx here.
                 */
            }
            return STATUS_SUCCESS;
        }
        public override Int32 GetSecurityByName(
            String FileName,
            out UInt32 FileAttributes/* or ReparsePointIndex */,
            ref Byte[] SecurityDescriptor)
        {
            FileName = ConcatPath(FileName);
            System.IO.FileInfo Info = new System.IO.FileInfo(FileName);
            FileAttributes = (UInt32)Info.Attributes;
            if (null != SecurityDescriptor)
                SecurityDescriptor = Info.GetAccessControl().GetSecurityDescriptorBinaryForm();
            return STATUS_SUCCESS;
        }
        public override Int32 Create(
            String FileName,
            UInt32 CreateOptions,
            UInt32 GrantedAccess,
            UInt32 FileAttributes,
            Byte[] SecurityDescriptor,
            UInt64 AllocationSize,
            out Object FileNode,
            out Object FileDesc0,
            out FileInfo FileInfo,
            out String NormalizedName)
        {
            FileDesc FileDesc = null;
            try
            {
                FileName = ConcatPath(FileName);
                if (0 == (CreateOptions & FILE_DIRECTORY_FILE))
                {
                    /* .NET 8: the FileSystemRights/Security FileStream ctor is gone.
                       Open read-write and apply the requested ACL afterwards. */
                    FileStream CreatedStream = new FileStream(
                        FileName,
                        FileMode.CreateNew,
                        FileAccess.ReadWrite,
                        FileShare.Read | FileShare.Write | FileShare.Delete,
                        4096,
                        FileOptions.None);
                    FileDesc = new FileDesc(CreatedStream, FileName);
                    TrackOpen(FileDesc);
                    FileDesc.SetFileAttributes(FileAttributes | (UInt32)System.IO.FileAttributes.Archive);
                }
                else
                {
                    if (Directory.Exists(FileName))
                        ThrowIoExceptionWithNtStatus(STATUS_OBJECT_NAME_COLLISION);
                    DirectoryInfo CreatedDir = Directory.CreateDirectory(FileName);
                    FileDesc = new FileDesc(CreatedDir);
                    FileDesc.SetFileAttributes(FileAttributes);
                }
                FileNode = default(Object);
                FileDesc0 = FileDesc;
                NormalizedName = default(String);
                return FileDesc.GetFileInfo(out FileInfo);
            }
            catch
            {
                UntrackOpen(FileDesc);
                if (null != FileDesc && null != FileDesc.Stream)
                    FileDesc.Stream.Dispose();
                throw;
            }
        }
        public override Int32 Open(
            String FileName,
            UInt32 CreateOptions,
            UInt32 GrantedAccess,
            out Object FileNode,
            out Object FileDesc0,
            out FileInfo FileInfo,
            out String NormalizedName)
        {
            FileDesc FileDesc = null;
            try
            {
                FileName = ConcatPath(FileName);
                if (!Directory.Exists(FileName))
                {
                    FileDesc = new FileDesc(
                        new FileStream(
                            FileName,
                            FileMode.Open,
                            FileAccess.ReadWrite,
                            FileShare.Read | FileShare.Write | FileShare.Delete),
                        FileName);
                    TrackOpen(FileDesc);
                }
                else
                {
                    FileDesc = new FileDesc(
                        new DirectoryInfo(FileName));
                }
                FileNode = default(Object);
                FileDesc0 = FileDesc;
                NormalizedName = default(String);
                return FileDesc.GetFileInfo(out FileInfo);
            }
            catch
            {
                UntrackOpen(FileDesc);
                if (null != FileDesc && null != FileDesc.Stream)
                    FileDesc.Stream.Dispose();
                throw;
            }
        }
        public override Int32 Overwrite(
            Object FileNode,
            Object FileDesc0,
            UInt32 FileAttributes,
            Boolean ReplaceFileAttributes,
            UInt64 AllocationSize,
            out FileInfo FileInfo)
        {
            FileDesc FileDesc = (FileDesc)FileDesc0;
            if (ReplaceFileAttributes)
                FileDesc.SetFileAttributes(FileAttributes |
                    (UInt32)System.IO.FileAttributes.Archive);
            else if (0 != FileAttributes)
                FileDesc.SetFileAttributes(FileDesc.GetFileAttributes() | FileAttributes |
                    (UInt32)System.IO.FileAttributes.Archive);
            FileDesc.Stream.SetLength(0);
            return FileDesc.GetFileInfo(out FileInfo);
        }
        public override void Cleanup(
            Object FileNode,
            Object FileDesc0,
            String FileName,
            UInt32 Flags)
        {
            FileDesc FileDesc = (FileDesc)FileDesc0;
            if (null == FileDesc)
                return;
            if (0 != (Flags & CleanupDelete))
            {
                FileDesc.SetDisposition(true);
                if (null != FileDesc.Stream)
                    FileDesc.Stream.Dispose();
            }
            DiscardRejected(FileDesc);
        }
        public override void Close(
            Object FileNode,
            Object FileDesc0)
        {
            FileDesc FileDesc = (FileDesc)FileDesc0;
            if (null == FileDesc)
                return;
            UntrackOpen(FileDesc);
            DiscardRejected(FileDesc);
            if (null != FileDesc.Stream)
            {
                FileDesc.Stream.Dispose();
                FileDesc.Stream = null;
            }
        }
        public override Int32 Read(
            Object FileNode,
            Object FileDesc0,
            IntPtr Buffer,
            UInt64 Offset,
            UInt32 Length,
            out UInt32 PBytesTransferred)
        {
            FileDesc FileDesc = (FileDesc)FileDesc0;
            if (Offset >= (UInt64)FileDesc.Stream.Length)
                ThrowIoExceptionWithNtStatus(STATUS_END_OF_FILE);
            Byte[] Bytes = new byte[Length];
            FileDesc.Stream.Seek((Int64)Offset, SeekOrigin.Begin);
            PBytesTransferred = (UInt32)FileDesc.Stream.Read(Bytes, 0, Bytes.Length);
            Marshal.Copy(Bytes, 0, Buffer, Bytes.Length);
            return STATUS_SUCCESS;
        }
        public override Int32 Write(
            Object FileNode,
            Object FileDesc0,
            IntPtr Buffer,
            UInt64 Offset,
            UInt32 Length,
            Boolean WriteToEndOfFile,
            Boolean ConstrainedIo,
            out UInt32 PBytesTransferred,
            out FileInfo FileInfo)
        {
            FileDesc FileDesc = (FileDesc)FileDesc0;
            if (ConstrainedIo)
            {
                if (Offset >= (UInt64)FileDesc.Stream.Length)
                {
                    PBytesTransferred = default(UInt32);
                    FileInfo = default(FileInfo);
                    return STATUS_SUCCESS;
                }
                if (Offset + Length > (UInt64)FileDesc.Stream.Length)
                    Length = (UInt32)((UInt64)FileDesc.Stream.Length - Offset);
            }
            UInt64 NewEnd = WriteToEndOfFile ?
                (UInt64)FileDesc.Stream.Length + Length :
                Offset + Length;
            if (NewEnd > (UInt64)_maxFileSize)
            {
                /* Reject the write itself; the file is discarded on close. */
                Reject(FileDesc, NewEnd);
                PBytesTransferred = 0;
                FileInfo = default(FileInfo);
                return STATUS_DISK_FULL;
            }
            Byte[] Bytes = new byte[Length];
            Marshal.Copy(Buffer, Bytes, 0, Bytes.Length);
            if (!WriteToEndOfFile)
                FileDesc.Stream.Seek((Int64)Offset, SeekOrigin.Begin);
            FileDesc.Stream.Write(Bytes, 0, Bytes.Length);
            PBytesTransferred = (UInt32)Bytes.Length;
            return FileDesc.GetFileInfo(out FileInfo);
        }
        public override Int32 Flush(
            Object FileNode,
            Object FileDesc0,
            out FileInfo FileInfo)
        {
            FileDesc FileDesc = (FileDesc)FileDesc0;
            if (null == FileDesc)
            {
                /* we do not flush the whole volume, so just return SUCCESS */
                FileInfo = default(FileInfo);
                return STATUS_SUCCESS;
            }
            FileDesc.Stream.Flush(true);
            return FileDesc.GetFileInfo(out FileInfo);
        }
        public override Int32 GetFileInfo(
            Object FileNode,
            Object FileDesc0,
            out FileInfo FileInfo)
        {
            FileDesc FileDesc = (FileDesc)FileDesc0;
            return FileDesc.GetFileInfo(out FileInfo);
        }
        public override Int32 SetBasicInfo(
            Object FileNode,
            Object FileDesc0,
            UInt32 FileAttributes,
            UInt64 CreationTime,
            UInt64 LastAccessTime,
            UInt64 LastWriteTime,
            UInt64 ChangeTime,
            out FileInfo FileInfo)
        {
            FileDesc FileDesc = (FileDesc)FileDesc0;
            FileDesc.SetBasicInfo(FileAttributes, CreationTime, LastAccessTime, LastWriteTime);
            return FileDesc.GetFileInfo(out FileInfo);
        }
        public override Int32 SetFileSize(
            Object FileNode,
            Object FileDesc0,
            UInt64 NewSize,
            Boolean SetAllocationSize,
            out FileInfo FileInfo)
        {
            FileDesc FileDesc = (FileDesc)FileDesc0;
            if (NewSize > (UInt64)_maxFileSize)
            {
                Reject(FileDesc, NewSize);
                FileInfo = default(FileInfo);
                return STATUS_DISK_FULL;
            }
            if (!SetAllocationSize || (UInt64)FileDesc.Stream.Length > NewSize)
            {
                /*
                 * "FileInfo.FileSize > NewSize" explanation:
                 * Ptfs does not support allocation size. However if the new AllocationSize
                 * is less than the current FileSize we must truncate the file.
                 */
                FileDesc.Stream.SetLength((Int64)NewSize);
            }
            return FileDesc.GetFileInfo(out FileInfo);
        }
        public override Int32 CanDelete(
            Object FileNode,
            Object FileDesc0,
            String FileName)
        {
            FileDesc FileDesc = (FileDesc)FileDesc0;
            FileDesc.SetDisposition(false);
            return STATUS_SUCCESS;
        }
        public override Int32 Rename(
            Object FileNode,
            Object FileDesc0,
            String FileName,
            String NewFileName,
            Boolean ReplaceIfExists)
        {
            FileName = ConcatPath(FileName);
            NewFileName = ConcatPath(NewFileName);
            FileDesc.Rename(FileName, NewFileName, ReplaceIfExists);
            FileDesc Renamed = FileDesc0 as FileDesc;
            if (null != Renamed && null != Renamed.Stream)
                Renamed.Path = NewFileName;
            return STATUS_SUCCESS;
        }
        public override Int32 GetSecurity(
            Object FileNode,
            Object FileDesc0,
            ref Byte[] SecurityDescriptor)
        {
            FileDesc FileDesc = (FileDesc)FileDesc0;
            SecurityDescriptor = FileDesc.GetSecurityDescriptor();
            return STATUS_SUCCESS;
        }
        public override Int32 SetSecurity(
            Object FileNode,
            Object FileDesc0,
            AccessControlSections Sections,
            Byte[] SecurityDescriptor)
        {
            FileDesc FileDesc = (FileDesc)FileDesc0;
            FileDesc.SetSecurityDescriptor(Sections, SecurityDescriptor);
            return STATUS_SUCCESS;
        }
        public override Boolean ReadDirectoryEntry(
            Object FileNode,
            Object FileDesc0,
            String Pattern,
            String Marker,
            ref Object Context,
            out String FileName,
            out FileInfo FileInfo)
        {
            FileDesc FileDesc = (FileDesc)FileDesc0;
            if (null == FileDesc.FileSystemInfos)
            {
                if (null != Pattern)
                    Pattern = Pattern.Replace('<', '*').Replace('>', '?').Replace('"', '.');
                else
                    Pattern = "*";
                IEnumerable Enum = FileDesc.DirInfo.EnumerateFileSystemInfos(Pattern);
                SortedList List = new SortedList();
                if (null != FileDesc.DirInfo && null != FileDesc.DirInfo.Parent)
                {
                    List.Add(".", FileDesc.DirInfo);
                    List.Add("..", FileDesc.DirInfo.Parent);
                }
                foreach (FileSystemInfo Info in Enum)
                    List.Add(Info.Name, Info);
                FileDesc.FileSystemInfos = new DictionaryEntry[List.Count];
                List.CopyTo(FileDesc.FileSystemInfos, 0);
            }
            int Index;
            if (null == Context)
            {
                Index = 0;
                if (null != Marker)
                {
                    Index = Array.BinarySearch(FileDesc.FileSystemInfos,
                        new DictionaryEntry(Marker, null),
                        _DirectoryEntryComparer);
                    if (0 <= Index)
                        Index++;
                    else
                        Index = ~Index;
                }
            }
            else
                Index = (int)Context;
            if (FileDesc.FileSystemInfos.Length > Index)
            {
                Context = Index + 1;
                FileName = (String)FileDesc.FileSystemInfos[Index].Key;
                FileDesc.GetFileInfoFromFileSystemInfo(
                    (FileSystemInfo)FileDesc.FileSystemInfos[Index].Value,
                    out FileInfo);
                return true;
            }
            else
            {
                FileName = default(String);
                FileInfo = default(FileInfo);
                return false;
            }
        }

        /// <summary>
        /// Marks a file as rejected and notifies the owner. Best effort: also
        /// mark the backing file delete-on-close so it disappears even if our
        /// Close callback never runs.
        /// </summary>
        private void Reject(FileDesc FileDesc, UInt64 AttemptedSize)
        {
            if (null == FileDesc)
                return;
            FileDesc.Rejected = true;
            try { FileDesc.SetDisposition(true); }
            catch (Exception ex) { Debug.WriteLine("[FileGuard.WinFsp] disposition failed: " + ex.Message); }

            Action<String, Int64> Callback = _onRejected;
            if (null != Callback)
            {
                Int64 Size = AttemptedSize > (UInt64)Int64.MaxValue
                    ? Int64.MaxValue
                    : (Int64)AttemptedSize;
                try { Callback(FileDesc.Path, Size); }
                catch (Exception ex) { Debug.WriteLine("[FileGuard.WinFsp] reject callback failed: " + ex.Message); }
            }
        }

        /// <summary>
        /// Deletes the whole backing file once it has been rejected.
        /// Idempotent; safe to call from both Cleanup and Close.
        /// </summary>
        private static void DiscardRejected(FileDesc FileDesc)
        {
            if (null == FileDesc || !FileDesc.Rejected)
                return;
            if (null != FileDesc.Stream)
            {
                try { FileDesc.Stream.Dispose(); }
                catch (Exception ex) { Debug.WriteLine("[FileGuard.WinFsp] dispose failed: " + ex.Message); }
                FileDesc.Stream = null;
            }
            DeleteFileWithRetry(FileDesc.Path);
        }

        private static void DeleteFileWithRetry(String Path)
        {
            if (String.IsNullOrEmpty(Path))
                return;
            for (Int32 Attempt = 0; Attempt < 20; Attempt++)
            {
                try
                {
                    if (!File.Exists(Path))
                        return;
                    File.SetAttributes(Path, System.IO.FileAttributes.Normal);
                    File.Delete(Path);
                    return;
                }
                catch (IOException)
                {
                    System.Threading.Thread.Sleep(50);
                }
                catch (UnauthorizedAccessException)
                {
                    System.Threading.Thread.Sleep(50);
                }
            }
            Debug.WriteLine("[FileGuard.WinFsp] could not delete rejected file: " + Path);
        }
    }
}
