using FileGuard.Core.Models;
using FileGuard.Core.Storage;
using Xunit;

namespace FileGuard.Core.Tests;

public sealed class ConfigStoreTests
{
    private static string NewPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fg-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "config.json");
    }

    [Fact]
    public void LegacyConfigWithoutMode_LoadsAsWatch()
    {
        var path = NewPath();
        try
        {
            // 旧版本写出的配置：没有 mode 字段。
            File.WriteAllText(path, """
            {
              "options": { "defaultMaxBytes": 20480 },
              "entries": [
                {
                  "id": "11111111-1111-1111-1111-111111111111",
                  "rootPath": "C:/work",
                  "enabled": true,
                  "limitEnabled": true,
                  "maxBytes": 20480
                }
              ]
            }
            """);

            var config = new ConfigStore(path).Load();

            Assert.Single(config.Entries);
            Assert.Equal(GuardMode.Watch, config.Entries[0].Mode);
            Assert.Equal(GuardMode.Watch, config.Options.DefaultMode);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void CorruptConfig_IsPreservedBeforeOverwrite()
    {
        var path = NewPath();
        try
        {
            // 读不动的配置：回退默认值可以，但不能让后续 Save 把原文件悄悄覆盖掉。
            File.WriteAllText(path, "{ this is not json");

            var store = new ConfigStore(path);
            var config = store.Load();

            Assert.Empty(config.Entries);
            Assert.True(File.Exists(path + ".bad"), "损坏的配置应当留一份 .bad 副本");
            Assert.Equal("{ this is not json", File.ReadAllText(path + ".bad"));

            // 即便 Load 时复制失败，Save 前还会再试一次；这里验证正常路径下副本仍在。
            store.Save(config);
            Assert.True(File.Exists(path + ".bad"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Mode_RoundTripsAsReadableString()
    {
        var path = NewPath();
        try
        {
            var config = new GuardConfig();
            config.Options.DefaultMode = GuardMode.VirtualDisk;
            config.Entries.Add(new GuardEntry
            {
                RootPath = "C:/work",
                Mode = GuardMode.VirtualDisk,
            });

            var store = new ConfigStore(path);
            store.Save(config);

            var json = File.ReadAllText(path);
            Assert.Contains("\"mode\": \"virtualDisk\"", json);

            var loaded = store.Load();
            Assert.Equal(GuardMode.VirtualDisk, loaded.Entries[0].Mode);
            Assert.Equal(GuardMode.VirtualDisk, loaded.Options.DefaultMode);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
