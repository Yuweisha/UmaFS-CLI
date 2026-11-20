using UmaFs.Core;

namespace UmaDL;

/// <summary>CLI 与 GUI 共用的下载流程。</summary>
internal static class Cli
{
    public static int Run(string dest, string? group, int parallel, int limit, bool dryRun, bool noVerify,
                          Action<string>? log = null, Action<AssetDownloader.Progress>? progress = null,
                          CancellationToken ct = default)
    {
        void Log(string s) => (log ?? Console.WriteLine)(s);

        Log("读取云端清单…");
        var rm = RemoteManifest.FetchAsync(s => Log("  " + s), ct).GetAwaiter().GetResult();
        Log($"清单就绪: {rm.Assets.Count} 条（失败分清单 {rm.Failures.Count}）");

        var selected = rm.Assets.Where(a => RemoteManifest.MatchesGroup(a, group)).ToList();
        if (limit > 0) selected = selected.Take(limit).ToList();

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in selected)
        {
            string p = Path.Combine(dest, f.Hash[..2], f.Hash);
            if (File.Exists(p)) present.Add(f.Hash);
        }

        var todo = selected.Where(a => !present.Contains(a.Hash)).ToList();
        long bytes = todo.Sum(a => a.Size);

        string label = string.IsNullOrWhiteSpace(group) ? "all" : group;
        Log($"分组 {label}: {selected.Count} 条，已有 {present.Count}，待下载 {todo.Count}（{Human(bytes)}）");
        Log($"目标目录: {dest}");

        if (dryRun || todo.Count == 0)
        {
            if (dryRun) Log("(--dry-run，未下载)");
            return 0;
        }

        Directory.CreateDirectory(dest);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new AssetDownloader()
            .RunAsync(todo, dest, !noVerify, parallel, s => Log(s), ct, progress)
            .GetAwaiter().GetResult();
        sw.Stop();

        Log("");
        Log($"完成: 下载 {result.Downloaded}，跳过 {result.Skipped}，失败 {result.Failed}" +
            $"（{Human(result.Bytes)}，{sw.Elapsed.TotalSeconds:F0}s）");
        if (result.Errors.Count > 0)
            foreach (var e in result.Errors.Take(10)) Log($"  {e.Name}: {e.Error}");
        return result.Failed == 0 ? 0 : 1;
    }

    public static string Human(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:f2} {units[u]}";
    }
}