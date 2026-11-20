using System;

namespace FileGuard.WinFsp
{
    /// <summary>
    /// Raised every time a write or size change is refused because it would
    /// push a file past the disk's per-file byte limit.
    /// </summary>
    public sealed class DiskWriteRejected : EventArgs
    {
        public DiskWriteRejected(string filePath, long attemptedSize, long maxBytes, DateTimeOffset timestamp)
        {
            FilePath = filePath;
            AttemptedSize = attemptedSize;
            MaxBytes = maxBytes;
            Timestamp = timestamp;
        }

        /// <summary>Path of the rejected file as seen through the mount point.</summary>
        public string FilePath { get; }

        /// <summary>Resulting file size the caller asked for, in bytes.</summary>
        public long AttemptedSize { get; }

        /// <summary>Configured per-file limit, in bytes.</summary>
        public long MaxBytes { get; }

        /// <summary>Local time at which the rejection happened.</summary>
        public DateTimeOffset Timestamp { get; }

        public override string ToString() =>
            $"{Timestamp:O} rejected '{FilePath}' ({AttemptedSize} > {MaxBytes} bytes)";
    }
}
