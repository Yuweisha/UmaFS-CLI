using UmamusumeData;

namespace UmaFs.Core;

/// <summary>
/// 给资源补一个可辨识的扩展名。
/// 判据取清单条目类型（实测采样：Default 400/400 均为 UnityFS；Sound 为 AFS2/0x40555446；
/// Movie 为 CRID；Font 为 OTF；Master 为 LZ4），避免枚举目录时对每个文件多一次 I/O。
/// </summary>
public static class Naming
{
    /// <summary>关闭后展示名与清单原名一致（--no-ext）。</summary>
    public static bool EnableExtensions { get; set; } = true;

    public static string WithExtension(FileNode node)
    {
        string name = node.Name;
        if (!EnableExtensions) return name;
        if (node.IsRealFile) return name;
        var entry = node.Entry;
        if (entry is null) return name;
        if (name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase)) return name;

        string ext = entry.Kind switch
        {
            ManifestEntryKind.Default => ".unity3d",
            ManifestEntryKind.Sound => name.EndsWith(".acb", StringComparison.OrdinalIgnoreCase) ? "" : ".awb",
            ManifestEntryKind.Movie => ".usm",
            ManifestEntryKind.Font => ".otf",
            ManifestEntryKind.Master => ".lz4",
            ManifestEntryKind.AssetManifest or ManifestEntryKind.PlatformManifest or ManifestEntryKind.RootManifest => ".manifest",
            _ => ""
        };

        if (ext.Length == 0) return name;
        if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return name;
        return name + ext;
    }

    /// <summary>从展示名还原清单原始名（导回原始目录结构时用）。</summary>
    public static string StripExtension(string displayName, FileNode node)
    {
        string raw = node.Name;
        if (node.IsRealFile) return displayName;
        if (displayName.Equals(raw, StringComparison.OrdinalIgnoreCase)) return raw;
        if (displayName.Length > raw.Length &&
            displayName.StartsWith(raw, StringComparison.OrdinalIgnoreCase) &&
            displayName[raw.Length] == '.')
            return raw;
        return displayName;
    }
}
