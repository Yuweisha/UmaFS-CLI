using System.Collections.Concurrent;
using UmaFs.Core;
using UmamusumeData;

namespace UmaFs.Assets;

/// <summary>
/// 反向索引：包内对象名 → 所属 AB 包（清单路径）。
/// 全量扫描 34 万个包太慢，所以支持分段扫描 + 落盘累积，中断后可从上次位置继续。
/// 缓存格式：每行 "对象名\t清单名"，首个 # 行记录已扫描到的清单序号。
/// </summary>
public sealed class ObjectIndex
{
    private readonly Dictionary<string, List<string>> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private long _scanned;

    public static string CacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UmaFs");

    public static string DefaultCachePath => Path.Combine(CacheDir, "objectindex.tsv");

    public int Count => _map.Count;
    public long Scanned => _scanned;
    public string CachePath { get; }

    private ObjectIndex(string cachePath) => CachePath = cachePath;

    public static ObjectIndex Load(string? cachePath = null)
    {
        var idx = new ObjectIndex(cachePath ?? DefaultCachePath);
        if (!File.Exists(idx.CachePath)) return idx;
        foreach (var line in File.ReadLines(idx.CachePath))
        {
            if (line.StartsWith('#'))
            {
                if (long.TryParse(line.AsSpan(1).Trim(), out var n)) idx._scanned = n;
                continue;
            }
            int tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            idx.Add(line[..tab], line[(tab + 1)..]);
        }
        return idx;
    }

    private void Add(string objectName, string manifestName)
    {
        if (!_map.TryGetValue(objectName, out var list))
            _map[objectName] = list = new List<string>(1);
        if (!list.Contains(manifestName)) list.Add(manifestName);
    }

    public IReadOnlyList<string> Lookup(string objectName) =>
        _map.TryGetValue(objectName, out var list) ? list : Array.Empty<string>();

    /// <summary>按前缀找对象名（缓存里已有的）。</summary>
    public IEnumerable<KeyValuePair<string, List<string>>> Search(string keyword, int limit)
    {
        int n = 0;
        foreach (var kv in _map)
        {
            if (!kv.Key.Contains(keyword, StringComparison.OrdinalIgnoreCase)) continue;
            yield return kv;
            if (++n >= limit) yield break;
        }
    }

    /// <summary>
    /// 继续扫描清单里尚未索引的包，最多 maxBundles 个（0 = 不限）。
    /// </summary>
    public void Scan(AssetIndex index, int maxBundles, Action<string> log, CancellationToken ct = default)
    {
        var store = new AssetStore(index.PersistentRoot);
        var bundles = index.Entries
            .Where(e => e.Kind == ManifestEntryKind.Default && !e.Name.StartsWith("/"))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .Skip((int)_scanned)
            .ToList();

        if (maxBundles > 0 && bundles.Count > maxBundles) bundles = bundles.GetRange(0, maxBundles);

        Directory.CreateDirectory(CacheDir);
        var newLines = new ConcurrentBag<string>();
        var errors = 0;
        long done = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct };
        try
        {
            Parallel.ForEach(bundles, options, entry =>
            {
                // 超大包（视频/大音频）几乎不含可查对象名，跳过以免拖慢整体
                if (entry.Length > 24L * 1024 * 1024) { Interlocked.Increment(ref done); return; }
                var reader = new BundleReader(store);
                try
                {
                    foreach (var obj in reader.ListObjects(entry))
                    {
                        if (string.IsNullOrEmpty(obj.Name)) continue;
                        newLines.Add($"{obj.Name}\t{entry.Name}");
                        lock (_sync) Add(obj.Name, entry.Name);
                    }
                }
                catch { Interlocked.Increment(ref errors); }
                finally { reader.Dispose(); }

                long n = Interlocked.Increment(ref done);
                if (n % 2000 == 0)
                {
                    lock (_sync)
                    {
                        log($"  {n}/{bundles.Count} 包，索引 {_map.Count} 个对象名，{sw.Elapsed.TotalSeconds:F0}s（失败 {errors}）");
                        AppendCache(newLines);
                    }
                }
            });
        }
        finally
        {
            lock (_sync)
            {
                AppendCache(newLines);
                _scanned += bundles.Count;
                RewriteHeader();
            }
        }
        log($"扫描完成：{bundles.Count} 个包，失败 {errors}，累计已扫 {_scanned} 个包，索引 {_map.Count} 个对象名");
    }

    private void AppendCache(ConcurrentBag<string> lines)
    {
        if (lines.IsEmpty) return;
        var buf = new List<string>();
        while (lines.TryTake(out var l)) buf.Add(l);
        if (buf.Count == 0) return;
        Directory.CreateDirectory(CacheDir);
        File.AppendAllLines(CachePath, buf);
    }

    private void RewriteHeader()
    {
        if (!File.Exists(CachePath)) return;
        try
        {
            var lines = File.ReadAllLines(CachePath).Where(l => !l.StartsWith('#')).ToArray();
            var tmp = CachePath + ".tmp";
            File.WriteAllLines(tmp, new[] { $"#{_scanned}" }.Concat(lines));
            File.Move(tmp, CachePath, overwrite: true);
        }
        catch { }
    }
}
