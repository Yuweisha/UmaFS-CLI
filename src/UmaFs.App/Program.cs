using UmaFs.Assets;
using UmaFs.Core;
using UmamusumeData;

namespace UmaFs.App;

internal static class Program
{
    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            var e = ex;
            int depth = 0;
            while (e is not null && depth++ < 5)
            {
                Console.Error.WriteLine($"错误({e.GetType().Name}): {e.Message}");
                if (e is TypeInitializationException || e is DllNotFoundException || e is BadImageFormatException)
                    Console.Error.WriteLine(e.StackTrace);
                e = e.InnerException;
            }
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0) return Help();

        string root = Environment.GetEnvironmentVariable("UMAFSPATH") ?? "";
        bool json = false;
        var positional = new List<string>();
        string? mountPoint = null, label = null;
        bool noExt = false;
        bool showMissing = false;
        int scanBudget = 0;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help" or "help":
                    return Help();
                case "-r" or "--root":
                    root = ++i < args.Length ? args[i] : root;
                    break;
                case "--json":
                    json = true;
                    break;
                case "-m" or "--mount":
                    mountPoint = ++i < args.Length ? args[i] : null;
                    break;
                case "-l" or "--label":
                    label = ++i < args.Length ? args[i] : null;
                    break;
                case "--no-ext":
                    noExt = true;
                    break;
                case "--show-missing":
                    showMissing = true;
                    break;
                case "-n" or "--limit":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var nb)) { scanBudget = nb; i++; }
                    break;
                default:
                    positional.Add(args[i]);
                    break;
            }
        }

        if (noExt) Naming.EnableExtensions = false;

        // 目录优先级：-r / UMAFSPATH（上面的 root 初值） > config.json > 自动探测
        var config = UmaFsConfig.Load();
        string rootSource = "命令行/环境变量";
        bool rootFromUser = !string.IsNullOrWhiteSpace(root);

        if (!rootFromUser && config.HasValidRoot)
        {
            root = config.PersistentRoot!;
            rootSource = $"配置文件 {UmaFsConfig.DefaultPath}";
        }

        if (string.IsNullOrWhiteSpace(root) || !AssetStore.LooksLikeDataDirectory(root))
        {
            var detected = AssetStore.AutoDetect();
            if (detected is not null)
            {
                root = detected;
                rootSource = "自动探测";
            }
        }

        if (!AssetStore.LooksLikeDataDirectory(root))
        {
            Console.Error.WriteLine("找不到游戏数据目录。用 -r <Persistent 路径> 指定，或设置环境变量 UMAFSPATH。");
            return 2;
        }

        // 探测到有效目录就记下来，下次不必再找
        if (!string.Equals(config.PersistentRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            config.PersistentRoot = root;
            if (config.Save()) Console.Error.WriteLine($"[config] 已记住游戏目录（来源: {rootSource}）→ {UmaFsConfig.DefaultPath}");
        }

        if (config.AutoExtension == false) Naming.EnableExtensions = false;
        if (noExt) Naming.EnableExtensions = false;

        var index = AssetIndex.Load(root);
        string cmd = positional.Count > 0 ? positional[0].ToLowerInvariant() : "ls";

        return cmd switch
        {
            "ls" => Ls(index, positional.ElementAtOrDefault(1), json),
            "tree" => Tree(index, positional.ElementAtOrDefault(1), 2),
            "cat" => Cat(index, positional.ElementAtOrDefault(1)),
            "info" => Info(index, positional.ElementAtOrDefault(1)),
            "export" => Export(index, positional.ElementAtOrDefault(1), positional.ElementAtOrDefault(2)),
            "find" => Find(index, positional.ElementAtOrDefault(1), json),
            "stats" => Stats(index, json),
            "check" => CheckCmd(index, json),
            "bundle" => BundleCmd(index, positional.Skip(1).ToList()),
            "locate" => LocateCmd(index, positional.ElementAtOrDefault(1), json, scanBudget),
            "scan" => ScanCmd(index, scanBudget),
            "config" => ConfigCmd(positional.Skip(1).ToList(), json),
            "winfsp" => WinFspCmd(),
            "extract" => ExtractCmd(index, positional.ElementAtOrDefault(1), positional.ElementAtOrDefault(2)),
            "unpack" => ExtractCmd(index, positional.ElementAtOrDefault(1), positional.ElementAtOrDefault(2)),
            "mount" => Mount(index, mountPoint, label ?? "UmaFs", showMissing),
            _ => Unknown(cmd)
        };
    }

    private static int Help()
    {
        Console.WriteLine("""
            umafs — 赛马娘资源浏览器 / 只读虚拟盘

            用法: umafs [命令] [参数] [-r <Persistent目录>] [--json] [--no-ext]

            浏览命令:
              ls [路径]             列出目录（默认根目录）
              tree [路径]           树状显示（深度 2）
              cat <文件>            输出解密后的内容到 stdout
              info <路径>           条目详情（hash / 大小 / 是否加密 / 魔数）
              export <路径> <目标>  导出文件或整个目录（自动解密）
              find <关键字>         按路径模糊搜索
              locate <对象名/关键字> 查包内对象属于哪个 AB 包
              bundle <包路径>       列出某个 AB 包里的对象（类型/名字/pathId）
              extract <包> <目录>   解包：纹理导出 PNG，其它对象导出原始数据
              scan [-n N]           建立包内对象名索引（分批，可续建）
              stats                 清单统计
              check                 核对清单与本地数据，列出缺失条目及其分组

            挂载命令（需要 WinFsp，未安装会用内置安装包自动装）:
              mount -m <盘符>       挂载为只读盘，例如 mount -m U:
                                    Ctrl+C 卸载

            配置与诊断:
              config show           显示当前配置
              config set-root <路径> 指定游戏 Persistent 目录并保存
              config auto           自动探测并保存
              config ext on|off     自动补扩展名开关
              winfsp                检查 WinFsp 安装状态

            全局参数:
              -r, --root <路径>     游戏 Persistent 目录（优先于配置文件与自动探测）
                  --json            JSON 输出
                  --no-ext          不自动补扩展名（保持清单原始名称）
                  --show-missing    挂载时也列出清单里有、但 dat 里缺数据的条目
              -l, --label <卷标>    挂载卷标，默认 UmaFs
              -n N                  scan/locate 本次最多处理 N 个包

            游戏目录优先级: -r / UMAFSPATH > 配置文件 > 自动探测。
            首次探测成功后会自动写入 %LOCALAPPDATA%\\UmaFs\\config.json。

            挂载后的目录即清单里的资源路径，文件内容是解密后的原文：
              U:\chara\chr1001\chr_icon_1001.unity3d   UnityFS 资源包
              U:\sound\b\snd_bgm_am001.awb            CriWare 音频
              U:\movie\m\...\*.usm                    视频
              U:\_manifests\                          顶层清单
              U:\_master\master.mdb                   明文 SQLite
            """);
        return 0;
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"未知命令: {cmd}");
        return Help();
    }

    private static int Usage(string msg)
    {
        Console.Error.WriteLine(msg);
        return 2;
    }

    // ---------- 浏览 ----------

    private static int Ls(AssetIndex index, string? path, bool json)
    {
        var node = string.IsNullOrWhiteSpace(path) ? index.Root : index.Resolve(path);
        if (node is null)
        {
            Console.Error.WriteLine($"路径不存在: {path}");
            return 1;
        }

        if (node is FileNode single)
        {
            Console.WriteLine($"{single.DisplayName}  {Human(single.Size)}");
            return 0;
        }

        var dir = (DirNode)node;
        var children = dir.GetChildren();

        if (json)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                path = dir.FullPath,
                entries = children.Select(n => n is DirNode d
                    ? (object)new { name = d.Name, type = "dir", count = d.Children.Count }
                    : new { name = ((FileNode)n).DisplayName, type = "file", size = ((FileNode)n).Size })
            }, JsonOpts));
            return 0;
        }

        // 缺失标记：并行探测，大目录（实测最大 24,431 项）串行 stat 要 2s 以上
        var missing = new bool[children.Length];
        Parallel.For(0, children.Length, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
        {
            if (children[i] is FileNode f && !index.FileExists(f)) missing[i] = true;
        });

        for (int i = 0; i < children.Length; i++)
        {
            var n = children[i];
            if (n is DirNode d) Console.WriteLine($"{d.Name}/  ({d.Children.Count})");
            else
            {
                var f = (FileNode)n;
                Console.WriteLine($"{f.DisplayName}  {Human(f.Size)}{(missing[i] ? "  [数据缺失]" : "")}");
            }
        }
        Console.WriteLine($"共 {children.Length} 项");
        return 0;
    }

    private static int Tree(AssetIndex index, string? path, int maxDepth)
    {
        var node = string.IsNullOrWhiteSpace(path) ? index.Root : index.Resolve(path);
        if (node is null)
        {
            Console.Error.WriteLine($"路径不存在: {path}");
            return 1;
        }
        PrintTree(node, "", 0, maxDepth);
        return 0;
    }

    private static void PrintTree(FsNode node, string indent, int depth, int maxDepth)
    {
        if (node is not DirNode dir) return;
        var children = dir.GetChildren();
        foreach (var child in children.Take(200))
        {
            Console.WriteLine($"{indent}{(child.IsDirectory ? "+" : "-")} {(child is FileNode cf ? cf.DisplayName : child.Name)}");
            if (child is DirNode dd && depth + 1 < maxDepth) PrintTree(dd, indent + "  ", depth + 1, maxDepth);
        }
        if (children.Length > 200) Console.WriteLine($"{indent}  … 另有 {children.Length - 200} 项");
    }

    private static int Cat(AssetIndex index, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Usage("cat 需要文件路径");
        if (index.Resolve(path) is not FileNode f)
        {
            Console.Error.WriteLine($"文件不存在: {path}");
            return 1;
        }
        using var src = OpenFile(index, f);
        using var stdout = Console.OpenStandardOutput();
        src.CopyTo(stdout, 1 << 16);
        stdout.Flush();
        return 0;
    }

    private static int Info(AssetIndex index, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Usage("info 需要路径");
        switch (index.Resolve(path))
        {
            case null:
                Console.Error.WriteLine($"路径不存在: {path}");
                return 1;

            case FileNode f:
                Console.WriteLine($"名称: {f.FullPath}");
                Console.WriteLine($"展示名: {f.DisplayName}");
                Console.WriteLine($"大小: {f.Size} ({Human(f.Size)})");
                if (f.Entry is null)
                {
                    Console.WriteLine($"数据: {f.BlobPath}（明文直读，不在清单内）");
                    return 0;
                }
                Console.WriteLine($"hash: {f.Entry.HashName}");
                Console.WriteLine($"类型: {f.Entry.Kind}  分组: {f.Entry.Group}  优先级: {f.Entry.Priority}");
                Console.WriteLine($"加密: {(f.Encrypted ? $"是 (key={f.Entry.EncryptionKey})" : "否")}");
                bool exists = index.FileExists(f);
                Console.WriteLine($"数据: {f.BlobPath}{(exists ? "" : "  [缺失]")}");
                Console.WriteLine($"依赖: {(string.IsNullOrEmpty(f.Entry.Dependencies) ? "(无)" : f.Entry.Dependencies)}");
                if (!exists) return 0;

                using (var s = OpenFile(index, f))
                {
                    var head = new byte[64];
                    int n = s.Read(head, 0, head.Length);
                    var span = head.AsSpan(0, n);
                    Console.WriteLine($"魔数: {Convert.ToHexString(span[..Math.Min(16, n)])}");
                    var sb = new System.Text.StringBuilder();
                    foreach (var b in span[..Math.Min(24, n)]) sb.Append(b is >= 32 and < 127 ? (char)b : '.');
                    Console.WriteLine($"可读头: {sb}");
                    Console.WriteLine($"推断扩展名: {AssetStore.GuessExtension(span.ToArray())}");
                }
                return 0;

            case DirNode d:
                Console.WriteLine($"目录: {d.FullPath}");
                Console.WriteLine($"子项: {d.Children.Count}");
                Console.WriteLine($"文件总数(含子目录): {index.EnumerateFiles(d, true).Count()}");
                return 0;

            default:
                return 1;
        }
    }

    private static int Export(AssetIndex index, string? path, string? dest)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(dest))
            return Usage("export 需要 <路径> <目标>");

        var node = index.Resolve(path);
        if (node is null)
        {
            Console.Error.WriteLine($"路径不存在: {path}");
            return 1;
        }

        bool single = node is FileNode;
        int ok = 0, fail = 0;
        foreach (var f in index.EnumerateFiles(node, true))
        {
            string outPath = single
                ? dest
                : Path.Combine(dest, f.DisplayPath.TrimStart('\\').Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            try
            {
                using var src = OpenFile(index, f);
                using var dst = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                src.CopyTo(dst, 1 << 16);
                ok++;
            }
            catch (Exception ex)
            {
                fail++;
                Console.Error.WriteLine($"跳过 {f.FullPath}: {ex.Message}");
            }
        }
        Console.WriteLine($"导出完成: {ok} 成功, {fail} 失败 -> {dest}");
        return fail == 0 ? 0 : 1;
    }

    private static int Find(AssetIndex index, string? keyword, bool json)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return Usage("find 需要关键字");
        var hits = index.Entries
            .Where(e => e.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .Take(1000)
            .ToList();

        if (json)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
                hits.Select(h => new { name = h.Name, size = h.Length, encrypted = h.EncryptionKey != 0 }), JsonOpts));
            return 0;
        }

        foreach (var h in hits) Console.WriteLine($"{h.Name}  {Human(h.Length)}");
        Console.WriteLine($"命中 {hits.Count}{(hits.Count == 1000 ? "+ (已截断)" : "")} 条");
        return 0;
    }

    private static int Stats(AssetIndex index, bool json)
    {
        var groups = index.Entries
            .Where(e => !e.Name.StartsWith("/"))
            .GroupBy(e => e.Name.Split('/')[0])
            .Select(g => new { name = g.Key, files = g.Count(), bytes = g.Sum(x => x.Length) })
            .OrderByDescending(g => g.bytes)
            .ToList();

        if (json)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(groups, JsonOpts));
            return 0;
        }

        Console.WriteLine($"数据目录: {index.PersistentRoot}");
        Console.WriteLine($"条目: {index.Entries.Count}（清单 {index.ManifestCount}，加密 {index.Entries.Count(e => e.EncryptionKey != 0)}）");
        Console.WriteLine($"解密后合计: {Human(index.Entries.Sum(e => e.Length))}");
        Console.WriteLine();
        Console.WriteLine($"{"顶层目录",-22}{"文件数",10}{"大小",14}");
        foreach (var g in groups.Take(40))
            Console.WriteLine($"{g.name,-22}{g.files,10}{Human(g.bytes),14}");
        return 0;
    }

    // ---------- 挂载 ----------

    private static int Mount(AssetIndex index, string? mountPoint, string label, bool showMissing)
    {
        if (string.IsNullOrWhiteSpace(mountPoint))
        {
            Console.Error.WriteLine("用法: umafs mount -m <盘符>  例如 mount -m U:");
            return 2;
        }

        // WinFsp 是内核驱动，未安装时用内嵌 MSI 自动装（需要管理员）
        if (!WinFspSetup.Ensure(out var winfspMessage))
        {
            Console.Error.WriteLine(winfspMessage);
            return 3;
        }
        Console.WriteLine(winfspMessage);

        // 必须在任何 Fsp 类型首次使用前，把实际 SxS bin 目录加进 DLL 搜索路径
        WinFspSetup.PrepareDllSearchPath();

        var fs = new UmaFileSystem(index, label, hideMissing: !showMissing);
        using var host = new Fsp.FileSystemHost(fs)
        {
            FileSystemName = "UmaFs",
            FileInfoTimeout = 1000,
            CaseSensitiveSearch = false,
            CasePreservedNames = true,
            UnicodeOnDisk = true,
            VolumeSerialNumber = 0x554D4166,
            MaxComponentLength = 255,
        };

        int status = host.Mount(mountPoint, null, false, 0);
        if (status < 0)
        {
            Console.Error.WriteLine($"挂载失败 (0x{status:X8})。确认已安装 WinFsp 且盘符未被占用。");
            return 1;
        }

        // 记住挂载点，下次可以不带 -m
        try
        {
            var cfg = UmaFsConfig.Load();
            cfg.LastMountPoint = mountPoint;
            cfg.Save();
        }
        catch { }

        Console.WriteLine($"已挂载 {host.MountPoint()}（卷标 {label}，{index.Entries.Count} 条目）");
        Console.WriteLine("按 Ctrl+C 卸载。");

        using var quit = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => quit.Set();
        quit.Wait();

        host.Unmount();
        Console.WriteLine("已卸载。");
        return 0;
    }

    // ---------- 完整性检查 ----------

    /// <summary>
    /// check — 核对清单与本地 dat 的一致性。
    /// 缺数据的条目按分组归类：DelayRelease 这类通常服务器上也没发布（CDN 返回 NoSuchKey）。
    /// </summary>
    private static int CheckCmd(AssetIndex index, bool json)
    {
        var files = index.Entries.Where(e => !e.Name.StartsWith("/")).ToList();
        var missing = new List<ManifestEntry>();
        var missingLock = new object();

        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 16 }, e =>
        {
            string p = Path.Combine(index.PersistentRoot, "dat", e.HashName[..2], e.HashName);
            if (!File.Exists(p)) lock (missingLock) missing.Add(e);
        });

        var byGroup = missing
            .GroupBy(e => e.Group.ToString())
            .Select(g => new { group = g.Key, count = g.Count() })
            .OrderByDescending(g => g.count)
            .ToList();

        var byTop = missing
            .GroupBy(e => e.Name.Split('/')[0])
            .Select(g => new { dir = g.Key, count = g.Count() })
            .OrderByDescending(g => g.count)
            .Take(20)
            .ToList();

        if (json)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                total = files.Count,
                present = files.Count - missing.Count,
                missing = missing.Count,
                byGroup,
                byTopDir = byTop
            }, JsonOpts));
            return 0;
        }

        Console.WriteLine($"清单条目: {files.Count}");
        Console.WriteLine($"有数据:   {files.Count - missing.Count}");
        Console.WriteLine($"缺数据:   {missing.Count}");

        if (missing.Count == 0) return 0;

        Console.WriteLine();
        Console.WriteLine("缺失按分组:");
        foreach (var g in byGroup) Console.WriteLine($"  {g.group,-40} {g.count}");

        Console.WriteLine();
        Console.WriteLine("缺失按顶层目录:");
        foreach (var g in byTop) Console.WriteLine($"  {g.dir,-24} {g.count}");

        var sample = missing.Take(10).Select(e => $"  {e.Name}  ({e.Group})").ToList();
        Console.WriteLine();
        Console.WriteLine("样例:");
        foreach (var s in sample) Console.WriteLine(s);

        Console.WriteLine();
        Console.WriteLine("说明: DelayRelease 分组通常是尚未发布的资源，服务器上也没有（CDN 返回 NoSuchKey）；");
        Console.WriteLine("      其它分组的缺失多为客户端未下载完整，可在游戏内让它重新校验一次。");
        return 0;
    }

    // ---------- 配置 ----------

    /// <summary>winfsp — 诊断 WinFsp 安装状态。</summary>
    private static int WinFspCmd()
    {
        var v = WinFspSetup.InstalledVersion();
        Console.WriteLine($"WinFsp: {(v is null ? "未安装" : $"{v.Value.Major}.{v.Value.Minor}  安装目录: {v.Value.InstallDir}")}");
        Console.WriteLine($"当前进程管理员: {(WinFspSetup.IsAdministrator() ? "是" : "否")}");
        if (WinFspSetup.Ensure(out var msg)) Console.WriteLine(msg);
        else Console.Error.WriteLine(msg);
        return v is null ? 1 : 0;
    }

    private static int ConfigCmd(List<string> args, bool json)
    {
        var cfg = UmaFsConfig.Load();
        string sub = args.Count > 0 ? args[0].ToLowerInvariant() : "show";

        switch (sub)
        {
            case "show":
            {
                if (json)
                {
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                    {
                        path = UmaFsConfig.DefaultPath,
                        persistentRoot = cfg.PersistentRoot,
                        valid = cfg.HasValidRoot,
                        autoExtension = cfg.AutoExtension,
                        objectIndexCache = cfg.ObjectIndexCache,
                        lastMountPoint = cfg.LastMountPoint
                    }, JsonOpts));
                    return 0;
                }
                Console.WriteLine($"配置文件: {UmaFsConfig.DefaultPath}{(File.Exists(UmaFsConfig.DefaultPath) ? "" : " (尚未创建)")}");
                Console.WriteLine($"游戏目录: {cfg.PersistentRoot ?? "(未设置)"}");
                Console.WriteLine($"  有效性: {(cfg.HasValidRoot ? "有效" : "无效或未设置")}");
                Console.WriteLine($"自动补扩展名: {cfg.AutoExtension}");
                Console.WriteLine($"对象索引缓存: {cfg.ObjectIndexCache}");
                Console.WriteLine($"上次挂载点: {cfg.LastMountPoint ?? "(无)"}");
                return 0;
            }

            case "set-root":
            {
                string? p = args.Count > 1 ? args[1] : null;
                if (string.IsNullOrWhiteSpace(p)) return Usage("config set-root 需要路径");
                p = Path.GetFullPath(p);
                if (!AssetStore.LooksLikeDataDirectory(p))
                {
                    Console.Error.WriteLine($"{p} 不像游戏数据目录（需要 dat/、meta、master/master.mdb）");
                    return 1;
                }
                cfg.PersistentRoot = p;
                Console.WriteLine(cfg.Save() ? $"已保存: {p}" : "保存失败");
                return cfg.HasValidRoot ? 0 : 1;
            }

            case "auto":
            {
                var detected = AssetStore.AutoDetect();
                if (detected is null)
                {
                    Console.WriteLine("自动探测没有找到有效的游戏目录。");
                    return 1;
                }
                cfg.PersistentRoot = detected;
                Console.WriteLine(cfg.Save() ? $"已探测并保存: {detected}" : $"探测到 {detected}，但保存失败");
                return 0;
            }

            case "unset":
                cfg.PersistentRoot = null;
                Console.WriteLine(cfg.Save() ? "已清除游戏目录" : "保存失败");
                return 0;

            case "ext":
            {
                bool on = args.Count < 2 || !args[1].Equals("off", StringComparison.OrdinalIgnoreCase);
                cfg.AutoExtension = on;
                Console.WriteLine(cfg.Save() ? $"自动补扩展名: {on}" : "保存失败");
                return 0;
            }

            case "path":
                Console.WriteLine(UmaFsConfig.DefaultPath);
                return 0;

            default:
                Console.Error.WriteLine("config 子命令: show | set-root <路径> | auto | unset | ext on|off | path");
                return 2;
        }
    }

    // ---------- AB 包解包 ----------

    /// <summary>
    /// locate &lt;对象名或关键字&gt; — 查一个包内对象属于哪个 AB 包。
    /// 首次跑会扫描并缓存索引；-n 限制本次扫描包数，便于分批建库。
    /// </summary>
    private static int LocateCmd(AssetIndex index, string? keyword, bool json, int scanBudget)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return Usage("locate 需要对象名或关键字，例如 locate chr_icon_1001");

        // 1) 清单名精确命中：直接给出包与解密数据路径，无需扫描
        var exactEntries = index.Entries
            .Where(e => !e.Name.StartsWith("/") &&
                        (string.Equals(e.BaseName, keyword, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(e.Name, keyword.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (exactEntries.Count == 1)
        {
            var e = exactEntries[0];
            Console.WriteLine($"清单名精确命中: {e.Name}");
            Console.WriteLine($"  AB 包: dat/{e.HashName[..2]}/{e.HashName}");
            Console.WriteLine($"  大小: {Human(e.Length)}  加密: {(e.EncryptionKey != 0 ? "是" : "否")}");
            if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { name = e.Name, hash = e.HashName, size = e.Length }, JsonOpts));
            return 0;
        }

        // 2) 已建好的对象名索引
        var oi = ObjectIndex.Load();
        var exact = oi.Lookup(keyword);
        if (exact.Count > 0)
        {
            Console.WriteLine($"对象名 {keyword} 出现在 {exact.Count} 个包（来自索引，已扫 {oi.Scanned} 包）:");
            foreach (var m in exact) Console.WriteLine($"  {m}");
            return 0;
        }

        // 3) 名字里含关键字的清单（包内对象名通常与清单名一致），只对这些包做验证解析
        var candidates = index.Entries
            .Where(e => e.Kind == ManifestEntryKind.Default && !e.Name.StartsWith("/") &&
                        e.BaseName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .Take(scanBudget > 0 ? scanBudget : 200)
            .ToList();

        Console.WriteLine($"清单名里含 “{keyword}” 的包: {candidates.Count} 个，逐个校验包内对象…");
        var store = new AssetStore(index.PersistentRoot);
        int hits = 0;
        foreach (var e in candidates)
        {
            var reader = new BundleReader(store);
            try
            {
                var objs = reader.ListObjects(e);
                var matched = objs.Where(o => o.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var o in matched)
                {
                    Console.WriteLine($"  {o.Type}  «{o.Name}»  →  {e.Name}");
                    hits++;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  跳过 {e.Name}: {ex.GetType().Name}");
            }
            finally { reader.Dispose(); }
        }

        if (hits == 0)
        {
            Console.WriteLine($"没有包内对象名含 “{keyword}”。可用 scan -n 20000 建对象索引后重查。");
            return 1;
        }
        Console.WriteLine($"共 {hits} 个对象命中");
        return 0;
    }

    /// <summary>scan — 建立/续建对象名索引。</summary>
    private static int ScanCmd(AssetIndex index, int budget)
    {
        var oi = ObjectIndex.Load();
        Console.WriteLine($"索引: {oi.CachePath}（已扫 {oi.Scanned} 包，{oi.Count} 对象名）");
        oi.Scan(index, budget, Console.WriteLine);
        return 0;
    }

    /// <summary>bundle &lt;路径&gt; — 列出某个 AB 包里的对象（纹理名/类型/大小）。</summary>
    private static int BundleCmd(AssetIndex index, List<string> args)
    {
        if (args.Count == 0) return Usage("bundle 需要 AB 包路径，例如 bundle chara/chr1001/chr_icon_1001");
        var node = index.Resolve(args[0]);
        if (node is not FileNode f || f.Entry is null)
        {
            Console.Error.WriteLine($"找不到资源包: {args[0]}");
            return 1;
        }

        try
        {
        using var reader = new BundleReader(new AssetStore(index.PersistentRoot));
        var objects = reader.ListObjects(f.Entry);
        Console.WriteLine($"{f.FullPath}  ({objects.Count} 个对象)");
        foreach (var o in objects)
            Console.WriteLine($"  {o.Type,-16} {o.Name,-44} pathId={o.PathId}  {Human(o.Size)}");
        return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[bundle 失败] {ex}");
            return 1;
        }
    }

    /// <summary>extract &lt;包&gt; &lt;输出目录&gt; — 导出包内纹理为 PNG 及其它对象。</summary>
    private static int ExtractCmd(AssetIndex index, string? path, string? outDir)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(outDir))
            return Usage("extract 需要 <AB 包路径> <输出目录>  [--tex-only]");

        var node = index.Resolve(path);
        if (node is not FileNode f || f.Entry is null)
        {
            Console.Error.WriteLine($"找不到资源包: {path}");
            return 1;
        }

        using var reader = new BundleReader(new AssetStore(index.PersistentRoot));
        int n = reader.Export(f.Entry, outDir, false, Console.WriteLine);
        Console.WriteLine($"已导出 {n} 个对象 -> {outDir}");
        return n > 0 ? 0 : 1;
    }

    private static Stream OpenFile(AssetIndex index, FileNode f) =>
        f.IsRealFile
            ? new FileStream(f.BlobPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16)
            : new AssetStore(index.PersistentRoot).Open(f.Entry!);

    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string Human(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:f2} {units[u]}";
    }
}
