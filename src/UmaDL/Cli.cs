using UmaFs.Core;

namespace UmaDL;

/// <summary>CLI 与 GUI 共用的下载流程。</summary>
internal static class Cli
{
    public static int Run(string dest, string? group, int parallel, int limit, bool dryRun, bool noVerify,
                          Action<string>? log = null, Action<AssetDownloader.Progress>? progress = null,
                          CancellationToken ct = default, GamePlatform platform = GamePlatform.Windows,
                          OutputLayout layout = OutputLayout.Cdn, bool full = false, bool prune = false)
    {
        void Log(string s) => (log ?? Console.WriteLine)(s);

        Log("读取云端清单…");
        var rm = RemoteManifest.FetchAsync(platform, s => Log("  " + s), ct).GetAwaiter().GetResult();
        Log($"平台: {platform.ManifestName()}   清单就绪: {rm.Assets.Count} 条（失败分清单 {rm.Failures.Count}）");

        var local = full ? new DownloadState() : DownloadState.Load(dest);
        if (!full && local.Platform.Length > 0 &&
            !local.Platform.Equals(platform.ManifestName(), StringComparison.OrdinalIgnoreCase))
        {
            Log($"注意: 本地快照属于 {local.Platform}，本次是 {platform.ManifestName()}，按全量处理");
            local = new DownloadState();
        }

        var plan = UpdatePlan.Build(rm, local, dest, layout);

        var selected = plan.ToDownload;
        if (!string.IsNullOrWhiteSpace(group) && !group.Equals("all", StringComparison.OrdinalIgnoreCase))
            selected = selected.Where(a => RemoteManifest.MatchesGroup(a, group)).ToList();
        if (limit > 0) selected = selected.Take(limit).ToList();

        string gl = string.IsNullOrWhiteSpace(group) ? "all" : group;
        Log($"布局: {(layout == OutputLayout.Cdn ? "CDN 目录树" : "扁平 {xx}/{hash}")}   分组: {gl}");
        Log($"增量: 未变 {plan.Unchanged}，更新 {plan.Changed}，新增 {plan.Added}，已移除 {plan.Removed.Count}");
        if (plan.ManifestChanged) Log("  清单有变化");
        if (prune && plan.Removed.Count > 0) Log($"  将删除 {plan.Removed.Count} 个已移除资源");

        Log($"待下载 {selected.Count} 条（{Human(selected.Sum(a => a.Size))}）→ {dest}");

        // 1) 清单落盘：无论下不下资源，清单都要保证是本版本
        int mCount = WriteManifests(rm, dest, layout);
        Log($"清单: {rm.RawManifests.Count} 个（新增/更新 {mCount}）");

        // 2) 删除已移除资源（仅 --prune）
        int removed = 0;
        if (prune)
        {
            foreach (var (name, oldHash) in plan.Removed)
            {
                string p = Path.HasExtension(name)
                    ? (layout == OutputLayout.Cdn
                        ? Path.Combine(dest, "Generic", oldHash[..2], oldHash)
                        : Path.Combine(dest, oldHash[..2], oldHash))
                    : (layout == OutputLayout.Cdn
                        ? Path.Combine(dest, platform.AssetFolder(), "assetbundles", oldHash[..2], oldHash)
                        : Path.Combine(dest, oldHash[..2], oldHash));
                if (File.Exists(p))
                {
                    try { File.Delete(p); removed++; } catch { }
                }
            }
            if (removed > 0) Log($"已删除 {removed} 个旧资源");
        }

        if (dryRun || selected.Count == 0)
        {
            if (dryRun) Log("(--dry-run，未下载资源)");
            else
            {
                Log("无需下载，快照已更新");
                SaveState(dest, rm, layout);
            }
            return rm.Failures.Count == 0 ? 0 : 1;
        }

        // 3) 下载资源
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new AssetDownloader()
            .RunAsync(selected, dest, !noVerify, parallel, s => Log(s), ct, progress, platform, layout)
            .GetAwaiter().GetResult();
        sw.Stop();

        Log("");
        Log($"完成: 下载 {result.Downloaded}，跳过 {result.Skipped}，失败 {result.Failed}" +
            $"（{Human(result.Bytes)}，{sw.Elapsed.TotalSeconds:F0}s）");
        if (result.Errors.Count > 0)
            foreach (var e in result.Errors.Take(10)) Log($"  {e.Name}: {e.Error}");

        if (result.Failed == 0) SaveState(dest, rm, layout);
        else Log("  有失败项，本次不更新快照（下次会重试）");

        return result.Failed == 0 ? 0 : 1;
    }

    private static int WriteManifests(RemoteManifest rm, string dest, OutputLayout layout)
    {
        int n = 0;
        foreach (var kv in rm.RawManifests)
        {
            string p = UpdatePlan.ManifestPath(dest, layout, kv.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            if (File.Exists(p) && new FileInfo(p).Length == kv.Value.Length) continue;
            File.WriteAllBytes(p, kv.Value);
            n++;
        }
        return n;
    }

    private static void SaveState(string dest, RemoteManifest rm, OutputLayout layout)
    {
        var st = new DownloadState
        {
            RootHash = rm.RootHash,
            Platform = rm.Platform.ManifestName(),
            Manifests = new Dictionary<string, string>(rm.PlatformHashes, StringComparer.OrdinalIgnoreCase),
            Assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        };
        // 只记录磁盘上真实存在的资源：否则未下载的条目下次会被当成「更新」
        foreach (var a in rm.Assets)
        {
            if (File.Exists(UpdatePlan.LocalPath(dest, layout, a, rm.Platform)))
                st.Assets[a.Name] = a.Hash;
        }
        st.Save(dest);
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