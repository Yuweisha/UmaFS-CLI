using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using UmaFs.Core;
using UmamusumeData;

namespace UmaFs.Assets;

/// <summary>一个 AB 包里的对象（Texture2D / TextAsset / Mesh / AudioClip …）。</summary>
public sealed class BundleObject
{
    public long PathId { get; init; }
    public string Type { get; init; } = "";
    public string Name { get; init; } = "";
    public long Size { get; init; }
}

/// <summary>AB 包解包：列对象、导出原生资源、导出纹理为 PNG。</summary>
public sealed class BundleReader : IDisposable
{
    private readonly AssetStore _store;
    private readonly AssetsManager _manager = new();

    public BundleReader(AssetStore store) => _store = store;

    /// <summary>打开清单条目对应的包（自动解密）。</summary>
    private (BundleFileInstance bun, List<AssetsFileInstance> files) OpenBundle(ManifestEntry entry)
    {
        byte[] data = _store.ReadAll(entry);
        var bun = _manager.LoadBundleFile(new MemoryStream(data), entry.Name);
        var files = new List<AssetsFileInstance>();
        foreach (var name in bun.file.GetAllFileNames())
        {
            try
            {
                var inst = _manager.LoadAssetsFileFromBundle(bun, name, false);
                if (inst?.file?.AssetInfos is not null) files.Add(inst);
            }
            catch { }
        }
        return (bun, files);
    }

    public List<BundleObject> ListObjects(ManifestEntry entry)
    {
        var (_, files) = OpenBundle(entry);
        var result = new List<BundleObject>();
        foreach (var f in files)
        {
            foreach (var info in f.file.AssetInfos)
            {
                if (info is null) continue;
                try
                {
                    string name = "";
                    string type = TypeName(f, info);
                    var bf = _manager.GetBaseField(f, info);
                    if (bf is not null && bf["m_Name"] is not null && !bf["m_Name"].IsDummy)
                        name = bf["m_Name"].AsString;
                    result.Add(new BundleObject
                    {
                        PathId = info.PathId,
                        Type = type,
                        Name = name,
                        Size = info.ByteSize
                    });
                }
                catch (Exception ex)
                {
                    result.Add(new BundleObject
                    {
                        PathId = info.PathId,
                        Type = $"TypeId{info.TypeId}",
                        Name = $"<读取失败: {ex.GetType().Name}>",
                        Size = info.ByteSize
                    });
                }
            }
        }
        return result;
    }

    /// <summary>导出所有 Texture2D 为 PNG；其它可命名对象导出为 .bin（原始序列化数据）。</summary>
    public int Export(ManifestEntry entry, string outDir, bool texturesOnly, Action<string> log)
    {
        var (_, files) = OpenBundle(entry);
        Directory.CreateDirectory(outDir);
        int count = 0;

        foreach (var f in files)
        {
            foreach (var info in f.file.AssetInfos)
            {
              if (info is null) continue;
              try
              {
                string type = TypeName(f, info);
                AssetTypeValueField bf;
                try { bf = _manager.GetBaseField(f, info); }
                catch (Exception ex) { log($"跳过 {type} pathId={info.PathId}: 解析失败 {ex.GetType().Name}"); continue; }
                if (bf is null) continue;

                string name = bf["m_Name"] is not null && !bf["m_Name"].IsDummy ? bf["m_Name"].AsString : "";
                if (string.IsNullOrEmpty(name)) name = $"{type}_{info.PathId}";

                if (type == "Texture2D")
                {
                    try
                    {
                        var tf = TextureFile.ReadTextureFile(bf);
                        var data = tf.FillPictureData(f);
                        var raw = tf.DecodeTextureRaw(data);
                        string outPath = Path.Combine(outDir, $"{SafeName(name)}_{info.PathId}.png");
                        using var img = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Bgra32>(
                            raw, tf.m_Width, tf.m_Height);
                        // Unity 纹理是自下而上存储的，导出前翻转
                        img.ProcessPixelRows(acc =>
                        {
                            for (int y = 0; y < acc.Height / 2; y++)
                            {
                                var top = acc.GetRowSpan(y);
                                var bottom = acc.GetRowSpan(acc.Height - 1 - y);
                                for (int x = 0; x < top.Length; x++)
                                    (top[x], bottom[x]) = (bottom[x], top[x]);
                            }
                        });
                        img.Save(outPath, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
                        log($"PNG  {name} → {Path.GetFileName(outPath)}  ({tf.m_Width}x{tf.m_Height}, {tf.m_TextureFormat})");
                        count++;
                    }
                    catch (Exception ex)
                    {
                        log($"跳过纹理 {name}: {ex.Message}");
                    }
                }
                else if (!texturesOnly)
                {
                    try
                    {
                        var raw = bf.WriteToByteArray();
                        string outPath = Path.Combine(outDir, $"{SafeName(name)}_{info.PathId}.{type}.bin");
                        File.WriteAllBytes(outPath, raw);
                        log($"BIN  {type} {name} → {Path.GetFileName(outPath)} ({raw.Length} B)");
                        count++;
                    }
                    catch (Exception ex)
                    {
                        log($"跳过 {type} {name}: {ex.Message}");
                    }
                }
              }
              catch (Exception ex)
              {
                log($"跳过 pathId={info.PathId}: {ex.GetType().Name} {ex.Message}");
              }
            }
        }
        return count;
    }

    public void Dispose()
    {
        // UnloadAll 在部分 bundle 上会抛 NRE，清理失败不应影响已经导出的结果
        try { _manager.UnloadAll(); } catch (Exception ex) { Console.Error.WriteLine($"[清理警告] {ex.GetType().Name}"); }
    }

    /// <summary>TypeId → 类名（AssetsTools.NET 的 AssetClassID 枚举）。</summary>
    private static string TypeName(AssetsFileInstance inst, AssetFileInfo info)
    {
        var id = (AssetClassID)info.TypeId;
        string name = id.ToString();
        return string.IsNullOrEmpty(name) || int.TryParse(name, out _) ? $"TypeId{info.TypeId}" : name;
    }

    private static string SafeName(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        return sb.Length == 0 ? "unnamed" : sb.ToString();
    }
}
