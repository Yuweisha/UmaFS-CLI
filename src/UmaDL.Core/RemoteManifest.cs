using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using K4os.Compression.LZ4.Streams;

namespace UmaFs.Core;

/// <summary>资源平台。PC 指 Windows 打包，Android 是移动端，两者清单与资源包都不同。</summary>
public enum GamePlatform
{
    Windows,
    Android,
    IOS,
}

public static class GamePlatformExtensions
{
    /// <summary>平台清单里的名字（root 清单中对应条目的 name）。</summary>
    public static string ManifestName(this GamePlatform p) => p switch
    {
        GamePlatform.Windows => "Windows",
        GamePlatform.Android => "Android",
        GamePlatform.IOS => "iOS",
        _ => "Windows",
    };

    /// <summary>CDN 上 assetbundles 的目录名。</summary>
    public static string AssetFolder(this GamePlatform p) => p switch
    {
        GamePlatform.Windows => "Windows",
        GamePlatform.Android => "Android",
        GamePlatform.IOS => "iOS",
        _ => "Windows",
    };

    /// <summary>宽松解析用户输入的平台名。</summary>
    public static bool TryParse(string? s, out GamePlatform platform)
    {
        switch ((s ?? "").Trim().ToLowerInvariant())
        {
            case "pc" or "win" or "windows":
                platform = GamePlatform.Windows; return true;
            case "android" or "apk" or "mobile":
                platform = GamePlatform.Android; return true;
            case "ios" or "iphone":
                platform = GamePlatform.IOS; return true;
            default:
                platform = GamePlatform.Windows; return false;
        }
    }
}

/// <summary>清单里的一条资源。</summary>
public sealed class RemoteAsset
{
    public string Name { get; init; } = "";
    public long Size { get; init; }
    public string Checksum { get; init; } = "";   // xxHash64 大端 8 字节的 hex
    public string Hash { get; init; } = "";
    public int Group { get; init; }
    public string Manifest { get; set; } = "";
    /// <summary>解密密钥（清单第 7 字段，8 字节大端）。0 表示未加密。</summary>
    public ulong Key { get; init; }
    public bool Encrypted => Key != 0;
}

/// <summary>
/// 按 UmaViewer standalone / 游戏客户端的方式展开云端清单：
/// root API -> root 清单 -> 平台清单(Windows) -> 各分清单 -> 资源条目。
/// </summary>
public sealed class RemoteManifest
{
    public const string DefaultRootApi = "https://umapi.buenavista.fans:8443/api/MetaC/root";

    /// <summary>CDN 根地址。默认官方 akamaized；指向自建镜像时形如 http://host:port/dl/resources。</summary>
    public static string CdnBase { get; set; } = "https://prd-storage-game-umamusume.akamaized.net/dl/resources";

    /// <summary>清单目录的基址（官方在另一台域名上，自建镜像时与 CdnBase 相同）。</summary>
    public static string ManifestBase { get; set; } = "https://prd-storage-app-umamusume.akamaized.net/dl/resources/Manifest";

    public static string ManifestBaseAlt { get; set; } = "https://prd-storage-game-umamusume.akamaized.net/dl/resources/Manifest";

    public static string RootApi { get; set; } = DefaultRootApi;

    public static string GenericUrl => $"{CdnBase}/Generic";

    /// <summary>资源包 CDN 前缀（平台相关）。</summary>
    public static string AssetBase(GamePlatform p) => $"{CdnBase}/{p.AssetFolder()}/assetbundles";

    /// <summary>指向自建镜像：所有 URL 都走同一个根（<c>base/manifest</c>、<c>base/Generic</c>、<c>base/&lt;平台&gt;/assetbundles</c>）。</summary>
    public static void UseMirror(string baseUrl)
    {
        string b = baseUrl.TrimEnd('/');
        CdnBase = b;
        // 镜像上清单目录名统一用小写 manifest（Windows 大小写不敏感，Linux 需与服务器约定一致）
        ManifestBase = $"{b}/Manifest";
        ManifestBaseAlt = $"{b}/Manifest";
    }

    /// <summary>CDN 会按 UA 拦截，必须伪装成 Unity。</summary>
    public const string UserAgent = "UnityPlayer/2022.3.62f1 (UnityWebRequest/1.0, libcurl/7.52.0-DEV)";

    // 客户端按当前代理设置现建，代理变更后无需重启
    private static HttpClient NewClient(int conns = 16) => Net.CreateClient(conns);

    public string RootHash { get; private set; } = "";
    public GamePlatform Platform { get; private set; } = GamePlatform.Windows;
    /// <summary>各平台的平台清单 hash，便于诊断。</summary>
    public Dictionary<string, string> PlatformHashes { get; } = new();
    public List<RemoteAsset> Assets { get; } = new();
    public List<(string Name, string Error)> Failures { get; } = new();

    /// <summary>本次解析用到的全部清单：hash → 原始 LZ4 frame 字节（游戏原样可读）。</summary>
    public Dictionary<string, byte[]> RawManifests { get; } = new();

    /// <summary>带扩展名的走 Generic，否则走当前平台的 assetbundles（与游戏一致）。</summary>
    public static string UrlFor(RemoteAsset a, GamePlatform platform = GamePlatform.Windows)
    {
        bool hasExt = Path.HasExtension(a.Name);
        string h = a.Hash;
        return hasExt
            ? $"{GenericUrl}/{h[..2]}/{h}"
            : $"{AssetBase(platform)}/{h[..2]}/{h}";
    }

    public static async Task<RemoteManifest> FetchAsync(GamePlatform platform = GamePlatform.Windows,
        Action<string>? log = null, CancellationToken ct = default)
    {
        var rm = new RemoteManifest { Platform = platform };
        using var http = NewClient();

        string payload = await http.GetStringAsync(RootApi, ct).ConfigureAwait(false);
        var node = JsonNode.Parse(payload);
        rm.RootHash = node?["h"]?.GetValue<string>()
            ?? throw new InvalidOperationException("root API 返回里没有 h 字段");
        log?.Invoke($"root: {rm.RootHash}");

        var (rootRows, rootRaw) = await FetchManifestAsync(http, rm.RootHash, ct).ConfigureAwait(false);
        rm.RawManifests[rm.RootHash] = rootRaw;
        log?.Invoke($"root 清单: {rootRows.Count} 行（{string.Join(",", rootRows.Select(r => r.Name))}）");

        foreach (var r in rootRows) rm.PlatformHashes[r.Name] = r.Hash;
        log?.Invoke($"root 内平台: {string.Join(", ", rootRows.Select(r => $"{r.Name}={r.Hash[..8]}…"))}");

        string want = platform.ManifestName();
        var plat = rootRows.FirstOrDefault(r => r.Name.Equals(want, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"root 清单里没有 {want} 平台（现有: {string.Join(", ", rootRows.Select(r => r.Name))}）");

        var (platRows, platRaw) = await FetchManifestAsync(http, plat.Hash, ct).ConfigureAwait(false);
        rm.RawManifests[plat.Hash] = platRaw;
        log?.Invoke($"{want} 平台清单: {platRows.Count} 个分清单");

        var manifests = platRows.Where(r => !r.Name.Equals(want, StringComparison.OrdinalIgnoreCase)).ToList();
        int done = 0;
        foreach (var m in manifests)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (rows, raw) = await FetchManifestAsync(http, m.Hash, ct).ConfigureAwait(false);
                rm.RawManifests[m.Hash] = raw;
                foreach (var r in rows)
                {
                    r.Manifest = m.Name;
                    rm.Assets.Add(r);
                }
                done++;
                if (done % 10 == 0 || done == manifests.Count)
                    log?.Invoke($"  [{done}/{manifests.Count}] 累计 {rm.Assets.Count} 条");
            }
            catch (Exception ex)
            {
                rm.Failures.Add((m.Name, ex.Message));
            }
        }
        return rm;
    }

    private static async Task<(List<RemoteAsset> Rows, byte[] Raw)> FetchManifestAsync(
        HttpClient http, string hash, CancellationToken ct)
    {
        byte[]? raw = null;
        string? lastError = null;
        foreach (var b in new[] { ManifestBase, ManifestBaseAlt })
        {
            string url = $"{b}/{hash[..2]}/{hash}";
            try
            {
                raw = await http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
                break;
            }
            catch (Exception ex)
            {
                // 403 多半是代理出口被 CDN 拦；把状态码带出来才能判断是哪一层的问题
                lastError = $"{url} -> {ex.Message}";
            }
        }
        if (raw is null)
            throw new HttpRequestException($"清单下载失败 {hash}（代理: {Net.ProxyLabel ?? "直连"}）: {lastError}");

        using var dec = LZ4Stream.Decode(new MemoryStream(raw));
        using var ms = new MemoryStream();
        await dec.CopyToAsync(ms, ct).ConfigureAwait(false);
        return (ParseBsv(ms.ToArray()), raw);
    }

    // ---------- BSV 解析 ----------

    private const byte TText = 64, TUint4 = 17, TUlong8 = 18, TFixed = 33, TSkip8 = 19;

    private static List<RemoteAsset> ParseBsv(byte[] d)
    {
        int off = 2;
        off += 2;                       // headerSize 不可靠，跳过
        long rowsN = Vlq(d, ref off);
        _ = Vlq(d, ref off);            // maxRowSize
        _ = Vlq(d, ref off);            // schemaVersion
        long scount = Vlq(d, ref off);

        var schema = new List<(byte T, long Fix)>();
        for (int i = 0; i < scount; i++)
        {
            byte t = d[off++];
            long fix = 0;
            if (t is TFixed or 65 or 49) fix = Vlq(d, ref off);
            schema.Add((t, fix));
        }

        var list = new List<RemoteAsset>((int)rowsN);
        for (long r = 0; r < rowsN; r++)
        {
            if (off >= d.Length) break;
            // 按字段位置取值：group 与 priority 都是 UInt4，不能用类型区分
            var texts = new List<string>(2);
            var nums = new List<long>(3);
            byte[] checksum = Array.Empty<byte>();
            ulong key = 0;   // TSkip8 实际是解密密钥（8 字节大端）

            foreach (var (t, fix) in schema)
            {
                switch (t)
                {
                    case TText:
                        int end = Array.IndexOf(d, (byte)0, off);
                        texts.Add(Encoding.UTF8.GetString(d, off, end - off));
                        off = end + 1;
                        break;
                    case TUint4:
                    case TUlong8:
                        nums.Add(Vlq(d, ref off));
                        break;
                    case TSkip8:
                        // 清单第 7 字段：加密 key，大端 8 字节
                        if (off + 8 <= d.Length)
                            key = BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(off, 8));
                        off += 8;
                        break;
                    case TFixed:
                        int n = (int)(fix == 0 ? 8 : fix);
                        checksum = d[off..(off + n)];
                        off += n;
                        break;
                }
            }

            if (texts.Count == 0) continue;
            string name = texts[0];

            // Default 清单: Text(name) Text(deps) UInt(group) UInt(prio) ULong(size) Fixed8 Fixed8-skip
            // root/平台清单: Text(name) ULong(size) Fixed8
            long group = 0, size = 0;
            if (nums.Count >= 3) { group = nums[0]; size = nums[2]; }
            else if (nums.Count == 1) { size = nums[0]; }

            list.Add(new RemoteAsset
            {
                Name = name,
                Size = size,
                Checksum = Convert.ToHexString(checksum).ToLowerInvariant(),
                Hash = CalcHash(checksum, size, name),
                Group = (int)group,
                Key = key
            });
        }
        return list;
    }

    private static long Vlq(byte[] b, ref int i)
    {
        long v = 0;
        while (true)
        {
            byte x = b[i++];
            v = (v << 7) | (uint)(x & 0x7F);
            if ((x & 0x80) == 0) return v;
        }
    }

    /// <summary>hash = Base32(SHA1(checksum_be8 + size_be8 + name))，去掉填充。</summary>
    public static string CalcHash(byte[] checksum, long size, string name)
    {
        Span<byte> sizeBe = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(sizeBe, size);

        using var sha = System.Security.Cryptography.SHA1.Create();
        var buf = new byte[checksum.Length + 8 + name.Length];
        checksum.CopyTo(buf, 0);
        sizeBe.CopyTo(buf.AsSpan(checksum.Length));
        Encoding.UTF8.GetBytes(name).CopyTo(buf, checksum.Length + 8);

        var digest = sha.ComputeHash(buf);
        return Base32(digest);
    }

    private const string B32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private static string Base32(byte[] data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int bits = 0, value = 0;
        foreach (byte b in data)
        {
            value = (value << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(B32[(value >> (bits - 5)) & 0x1F]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(B32[(value << (5 - bits)) & 0x1F]);
        return sb.ToString();
    }

    /// <summary>分组位掩码 → 可读名，用于过滤。</summary>
    public static string[] GroupNames(int g) => g switch
    {
        0 => new[] { "none" },
        _ => new[]
        {
            0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80, 0x100,
            0x10000, 0x20000, 0x40000, 0x80000, 0x100000
        }.Where(bit => (g & bit) != 0).Select(bit => BitName(bit)).ToArray()
    };

    public static string BitName(int bit) => bit switch
    {
        0x01 => "DeleteOnLogin",
        0x02 => "DownloadLogin",
        0x04 => "Tutorial",
        0x08 => "HomeLogin",
        0x10 => "RequiredTutorialStart",
        0x20 => "DelayRelease",
        0x40 => "RealFanfare",
        0x80 => "WithRealFanfare",
        0x100 => "DataManagementTarget",
        0x10000 => "DeleteOnLoginWithConditionStory",
        0x20000 => "DeleteOnLoginWithConditionEvent",
        0x40000 => "DeleteOnLoginWithConditionLive",
        0x80000 => "DeleteOnLoginWithConditionRace",
        0x100000 => "DeleteOnLoginWithConditionOthers",
        _ => $"0x{bit:X}"
    };

    /// <summary>按分组名过滤（不区分大小写，支持 all）。</summary>
    public static bool MatchesGroup(RemoteAsset a, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || filter.Equals("all", StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var name in GroupNames(a.Group))
            if (name.Equals(filter, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

/// <summary>并发下载 + 校验。</summary>
public sealed class AssetDownloader
{
    private readonly HttpClient _http;

    public AssetDownloader() => _http = Net.CreateClient(32);

    public sealed class Result
    {
        public int Downloaded, Skipped, Failed;
        public List<(string Name, string Error)> Errors = new();
        public long Bytes;
    }

    /// <summary>
    /// 下载到 destDir/&lt;hash前2&gt;/&lt;hash&gt;。已存在且尺寸相符则跳过。
    /// </summary>
    /// <summary>实时进度：已完成数 / 总数 / 成功 / 跳过 / 失败 / 字节数。</summary>
    public sealed record Progress(int Done, int Total, int Downloaded, int Skipped, int Failed, long Bytes);

    public async Task<Result> RunAsync(
        IEnumerable<RemoteAsset> assets, string destDir, bool verify,
        int parallel, Action<string>? log, CancellationToken ct, Action<Progress>? onProgress = null,
        GamePlatform platform = GamePlatform.Windows)
    {
        var result = new Result();
        var gate = new SemaphoreSlim(parallel);
        var okLock = new object();
        int total = assets.Count(), done = 0;

        var tasks = assets.Select(async a =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                string dest = Path.Combine(destDir, a.Hash[..2], a.Hash);
                if (File.Exists(dest) && new FileInfo(dest).Length == a.Size)
                {
                    lock (okLock) result.Skipped++;
                    return;
                }

                byte[] data = await _http.GetByteArrayAsync(RemoteManifest.UrlFor(a, platform), ct).ConfigureAwait(false);
                if (data.Length != a.Size)
                    throw new InvalidDataException($"大小不符 {data.Length} != {a.Size}");

                if (verify)
                {
                    string got = Convert.ToHexString(System.IO.Hashing.XxHash64.Hash(data)).ToLowerInvariant();
                    // XxHash64.Hash 返回大端 8 字节（.NET 默认大端），与清单 checksum 对齐
                    if (got != a.Checksum)
                        throw new InvalidDataException($"校验不符 {got} != {a.Checksum}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                string tmp = dest + ".part";
                await File.WriteAllBytesAsync(tmp, data, ct).ConfigureAwait(false);
                File.Move(tmp, dest, overwrite: true);

                lock (okLock)
                {
                    result.Downloaded++;
                    result.Bytes += data.Length;
                }
            }
            catch (Exception ex)
            {
                lock (okLock)
                {
                    result.Failed++;
                    if (result.Errors.Count < 50) result.Errors.Add((a.Name, ex.Message));
                }
            }
            finally
            {
                int d = Interlocked.Increment(ref done);
                if (log is not null && (d % 200 == 0 || d == total))
                    lock (okLock)
                        log($"  {d}/{total}  下载 {result.Downloaded} 跳过 {result.Skipped} 失败 {result.Failed}");
                if (onProgress is not null)
                    lock (okLock)
                        onProgress(new Progress(d, total, result.Downloaded, result.Skipped, result.Failed, result.Bytes));
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return result;
    }
}