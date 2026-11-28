using System.Globalization;

namespace FileGuard.Core.Storage;

/// <summary>
/// 隔离区：删除前把文件复制到 ProgramData 下，保留最近若干次，防止误删找不回。
/// </summary>
public sealed class QuarantineStore
{
    private readonly string _root;

    public QuarantineStore(string root) => _root = root;

    public string Root => _root;

    /// <summary>
    /// 备份文件，返回备份后的完整路径；复制失败返回 null（不影响后续删除）。
    /// </summary>
    public string? Backup(string filePath, Guid entryId, string rootPath)
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            var batchDir = Path.Combine(_root, $"{stamp}_{entryId:N}");
            Directory.CreateDirectory(batchDir);

            string relative;
            try { relative = Path.GetRelativePath(rootPath, filePath); }
            catch (ArgumentException) { relative = Path.GetFileName(filePath); }

            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                relative = Path.GetFileName(filePath);
            }

            var dest = Path.Combine(batchDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(filePath, dest, overwrite: true);
            return dest;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>只保留最近 keep 个批次。</summary>
    public void Prune(int keep)
    {
        try
        {
            if (!Directory.Exists(_root)) return;

            var batches = new DirectoryInfo(_root)
                .GetDirectories()
                .OrderByDescending(d => d.Name, StringComparer.Ordinal)
                .ToList();

            // 至少保留最新一批：keep=0 时也不能把刚备份出来的文件一起删掉。
            foreach (var old in batches.Skip(Math.Max(1, keep)))
            {
                try { old.Delete(recursive: true); }
                catch (Exception) { /* 被占用就下轮再删 */ }
            }
        }
        catch (Exception)
        {
            // 清理失败不影响主流程。
        }
    }

    public IReadOnlyList<string> ListBatches()
    {
        try
        {
            if (!Directory.Exists(_root)) return Array.Empty<string>();
            return new DirectoryInfo(_root)
                .GetDirectories()
                .OrderByDescending(d => d.Name, StringComparer.Ordinal)
                .Select(d => d.FullName)
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }
}
