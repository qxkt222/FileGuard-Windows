using FileGuard.Core.Storage;
using Xunit;

namespace FileGuard.Core.Tests;

public sealed class QuarantineStoreTests
{
    [Fact]
    public void Prune_Zero_StillKeepsNewestBatch()
    {
        var root = Path.Combine(Path.GetTempPath(), "fg-q-" + Guid.NewGuid().ToString("N"));
        var watch = Path.Combine(root, "watch");
        Directory.CreateDirectory(watch);

        try
        {
            var source = Path.Combine(watch, "big.cs");
            File.WriteAllText(source, "payload");

            var store = new QuarantineStore(Path.Combine(root, "q"));
            for (var i = 0; i < 3; i++)
            {
                Assert.NotNull(store.Backup(source, Guid.NewGuid(), watch));
                Thread.Sleep(5); // 批次名带毫秒，隔开保证是不同目录
            }

            Assert.Equal(3, store.ListBatches().Count);

            // keep=0 也不能把刚备份出来的最新批次一起删掉。
            store.Prune(0);

            Assert.Single(store.ListBatches());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
