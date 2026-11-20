using FileGuard.Core.Models;

namespace FileGuard.Core.Watching;

/// <summary>
/// 建条目时在监视目录写一份规则说明，让 AI / 编辑器在动手之前就知道限额。
/// 硬删是兜底，这份文件是第一道防线。
/// </summary>
public static class RuleFileWriter
{
    public static string Write(string rootPath, GuardEntry entry, int quarantineKeep)
    {
        var kb = Math.Max(1, entry.MaxBytes / 1024);
        var file = Path.Combine(rootPath, ".guard-rules.md");

        var text =
$"""
# 文件写入限制（FileGuard）

本目录处于监视状态，写入规则如下：

- **单个文件上限 {kb} KB**（{entry.MaxBytes} 字节）。
- 超过上限的文件会被**立即删除**，这次写入视为失败。
- 请把大文件拆成多个小文件，每个都不超过 {kb} KB。
- 被删除的内容只可能从隔离区找回最近 {quarantineKeep} 次，不要指望恢复。

如果你正在生成代码：先规划文件拆分，不要一次性输出超大单文件。
""";

        File.WriteAllText(file, text);
        return file;
    }
}
