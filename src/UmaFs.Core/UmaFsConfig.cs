using System.Text.Json;
using System.Text.Json.Serialization;

namespace UmaFs.Core;

/// <summary>
/// 用户配置：%LOCALAPPDATA%\UmaFs\config.json
/// 优先级：命令行 -r &gt; 环境变量 UMAFSPATH &gt; 本配置 &gt; 自动探测。
/// 自动探测成功后会把结果写回这里，下次无需再找。
/// </summary>
public sealed class UmaFsConfig
{
    [JsonPropertyName("persistentRoot")]
    public string? PersistentRoot { get; set; }

    [JsonPropertyName("lastMountPoint")]
    public string? LastMountPoint { get; set; }

    [JsonPropertyName("autoExtension")]
    public bool AutoExtension { get; set; } = true;

    [JsonPropertyName("objectIndexCache")]
    public bool ObjectIndexCache { get; set; } = true;

    [JsonPropertyName("neverPrompt")]
    public bool NeverPrompt { get; set; }

    [JsonIgnore]
    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UmaFs");

    [JsonIgnore]
    public static string DefaultPath => Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static UmaFsConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<UmaFsConfig>(File.ReadAllText(path), Options);
                if (cfg is not null) return cfg;
            }
        }
        catch { }
        return new UmaFsConfig();
    }

    public bool Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>配置里记着的目录是否仍然有效。</summary>
    [JsonIgnore]
    public bool HasValidRoot => !string.IsNullOrWhiteSpace(PersistentRoot) && AssetStore.LooksLikeDataDirectory(PersistentRoot!);
}
