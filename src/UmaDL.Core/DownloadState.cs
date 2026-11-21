using System.Text.Json;
using System.Text.Json.Serialization;

namespace UmaFs.Core;

/// <summary>输出目录布局。</summary>
public enum OutputLayout
{
    /// <summary>CDN 原样：Manifest/ Generic/ &lt;平台&gt;/assetbundles/</summary>
    Cdn = 0,
    /// <summary>扁平：全部按 {hash前2位}/{hash} 平铺，与游戏 Persistent/dat 一致。</summary>
    Flat = 1,
}

public static class OutputLayoutExtensions
{
    public static bool TryParse(string? s, out OutputLayout layout)
    {
        switch ((s ?? "").Trim().ToLowerInvariant())
        {
            case "cdn" or "tree":
                layout = OutputLayout.Cdn; return true;
            case "flat" or "dat":
                layout = OutputLayout.Flat; return true;
            default:
                layout = OutputLayout.Cdn; return false;
        }
    }
}

/// <summary>本地已下载内容的快照，用于增量更新。</summary>
public sealed class DownloadState
{
    private const string FileName = "_umadl_state.json";

    [JsonPropertyName("rootHash")]
    public string RootHash { get; set; } = "";

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "";

    /// <summary>分清单名 → 清单 hash。</summary>
    [JsonPropertyName("manifests")]
    public Dictionary<string, string> Manifests { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>资源名 → hash（判断「名字相同但内容变了」）。</summary>
    [JsonPropertyName("assets")]
    public Dictionary<string, string> Assets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string PathIn(string dir) => Path.Combine(dir, FileName);

    public static DownloadState Load(string dir)
    {
        try
        {
            string p = PathIn(dir);
            if (!File.Exists(p)) return new DownloadState();
            var st = JsonSerializer.Deserialize<DownloadState>(File.ReadAllText(p), Options);
            return st ?? new DownloadState();
        }
        catch
        {
            return new DownloadState();
        }
    }

    public bool Save(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            UpdatedAt = DateTime.UtcNow;
            string tmp = PathIn(dir) + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
            File.Move(tmp, PathIn(dir), overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>增量更新计划。</summary>
public sealed class UpdatePlan
{
    public List<RemoteAsset> ToDownload { get; } = new();
    public List<(string Name, string OldHash)> Removed { get; } = new();
    public int Unchanged { get; set; }
    public int Changed { get; set; }
    public int Added { get; set; }

    /// <summary>清单本身是否变了（root 或平台清单 hash 不同）。</summary>
    public bool ManifestChanged { get; set; }

    public long Bytes => ToDownload.Sum(a => a.Size);

    /// <summary>对比本地快照与云端清单，算出需要下载的集合。</summary>
    public static UpdatePlan Build(RemoteManifest rm, DownloadState local, string destDir, OutputLayout layout)
    {
        var plan = new UpdatePlan();

        plan.ManifestChanged =
            !string.Equals(local.RootHash, rm.RootHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(local.Platform, rm.Platform.ManifestName(), StringComparison.OrdinalIgnoreCase);

        var oldByName = local.Assets;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in rm.Assets)
        {
            seen.Add(a.Name);

            if (oldByName.TryGetValue(a.Name, out var oldHash))
            {
                if (string.Equals(oldHash, a.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    // 清单没变且文件还在 → 跳过
                    if (File.Exists(LocalPath(destDir, layout, a, rm.Platform)))
                    {
                        plan.Unchanged++;
                        continue;
                    }
                    // 文件被删了，重新下
                    plan.ToDownload.Add(a);
                    plan.Changed++;
                    continue;
                }

                // 名字在、hash 变了 → 内容更新
                plan.ToDownload.Add(a);
                plan.Changed++;
                continue;
            }

            // 全新条目
            if (File.Exists(LocalPath(destDir, layout, a, rm.Platform)) && !plan.ManifestChanged)
            {
                plan.Unchanged++;
                continue;
            }
            plan.ToDownload.Add(a);
            plan.Added++;
        }

        foreach (var kv in oldByName)
        {
            if (!seen.Contains(kv.Key))
                plan.Removed.Add((kv.Key, kv.Value));
        }

        return plan;
    }

    /// <summary>某条资源在当前布局下的落盘绝对路径。</summary>
    public static string LocalPath(string destDir, OutputLayout layout, RemoteAsset a, GamePlatform p)
    {
        string h = a.Hash;
        if (layout == OutputLayout.Flat) return Path.Combine(destDir, h[..2], h);
        return Path.HasExtension(a.Name)
            ? Path.Combine(destDir, "Generic", h[..2], h)
            : Path.Combine(destDir, p.AssetFolder(), "assetbundles", h[..2], h);
    }

    /// <summary>清单文件的落盘路径。</summary>
    public static string ManifestPath(string destDir, OutputLayout layout, string hash)
    {
        if (layout == OutputLayout.Flat) return Path.Combine(destDir, hash[..2], hash);
        return Path.Combine(destDir, "Manifest", hash[..2], hash);
    }
}