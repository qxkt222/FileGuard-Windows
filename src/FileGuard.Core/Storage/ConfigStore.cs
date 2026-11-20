using System.Text.Json;
using System.Text.Json.Serialization;
using FileGuard.Core.Models;

namespace FileGuard.Core.Storage;

public sealed class GuardConfig
{
    public GuardOptions Options { get; set; } = new();
    public List<GuardEntry> Entries { get; set; } = new();
}

/// <summary>配置读写。写文件走临时文件 + 替换，避免半截文件。</summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 枚举写成可读字符串（"watch" / "virtualDisk"）；allowIntegerValues 让旧的手写数字配置也能读。
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true) },
    };

    private readonly string _path;

    public ConfigStore(string path) => _path = path;

    public string FilePath => _path;

    public GuardConfig Load()
    {
        try
        {
            if (!File.Exists(_path)) return new GuardConfig();

            var json = File.ReadAllText(_path);
            var config = JsonSerializer.Deserialize<GuardConfig>(json, JsonOptions);
            if (config is null) return new GuardConfig();

            config.Options ??= new GuardOptions();
            config.Entries ??= new List<GuardEntry>();
            return config;
        }
        catch (Exception)
        {
            // 配置损坏时回退默认值，不阻塞启动。
            return new GuardConfig();
        }
    }

    public void Save(GuardConfig config)
    {
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = _path + ".tmp";
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(tmp, json);

        if (File.Exists(_path)) File.Replace(tmp, _path, null);
        else File.Move(tmp, _path);
    }

    /// <summary>一键恢复默认：删掉现有配置，重新落一份默认的。</summary>
    public GuardConfig Reset()
    {
        var config = new GuardConfig();
        Save(config);
        return config;
    }
}
