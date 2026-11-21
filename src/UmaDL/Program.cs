using System.Runtime.InteropServices;
using UmaFs.Core;

namespace UmaDL;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0) return RunCli(args);

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    // ---------- CLI 模式 ----------

    private static int RunCli(string[] args)
    {
        if (!AttachConsole(-1)) AllocConsole();
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        string dest = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UmaFs", "assets");
        string? group = "DeleteOnLogin";
        int parallel = 8, limit = 0;
        bool dryRun = false, noVerify = false;
        string? proxy = null;
        bool proxySet = false;
        var platform = GamePlatform.Windows;
        string? cdn = null, rootApi = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help" or "help":
                    ConsoleHelp();
                    return 0;
                case "-o" or "--out":
                    dest = ++i < args.Length ? args[i] : dest;
                    break;
                case "-g" or "--group":
                    group = ++i < args.Length ? args[i] : group;
                    break;
                case "--all":
                    group = null;
                    break;
                case "-j" or "--parallel":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var j)) { parallel = Math.Clamp(j, 1, 64); i++; }
                    break;
                case "-n" or "--limit":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var n)) { limit = n; i++; }
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--no-verify":
                    noVerify = true;
                    break;
                case "--proxy" or "-p":
                    proxy = ++i < args.Length ? args[i] : null;
                    proxySet = true;
                    break;
                case "--cdn":
                    cdn = ++i < args.Length ? args[i] : null;
                    break;
                case "--root-api":
                    rootApi = ++i < args.Length ? args[i] : null;
                    break;
                case "--platform" or "--plat":
                    if (i + 1 < args.Length)
                    {
                        if (!GamePlatformExtensions.TryParse(args[i + 1], out platform))
                        {
                            Console.Error.WriteLine($"未知平台: {args[i + 1]}（可用: pc / android / ios）");
                            return 2;
                        }
                        i++;
                    }
                    break;
                default:
                    if (!args[i].StartsWith('-')) group = args[i];
                    break;
            }
        }

        // 代理：显式参数优先；否则读 HTTP_PROXY / HTTPS_PROXY / ALL_PROXY
        try
        {
            string? effective = proxySet ? proxy : UmaFs.Core.Net.ProxyFromEnvironment();
            if (!string.IsNullOrWhiteSpace(effective))
            {
                UmaFs.Core.Net.SetProxy(effective);
                Console.WriteLine($"代理: {UmaFs.Core.Net.ProxyLabel}");
            }
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        if (!string.IsNullOrWhiteSpace(cdn))
        {
            UmaFs.Core.RemoteManifest.UseMirror(cdn);
            Console.WriteLine($"CDN: {UmaFs.Core.RemoteManifest.CdnBase}");
        }
        if (!string.IsNullOrWhiteSpace(rootApi))
            UmaFs.Core.RemoteManifest.RootApi = rootApi;

        try
        {
            return Cli.Run(dest, group, parallel, limit, dryRun, noVerify, platform: platform);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"失败: {ex.Message}");
            if (ex.InnerException is not null) Console.Error.WriteLine($"  {ex.InnerException.Message}");
            return 1;
        }
    }

    private static void ConsoleHelp()
    {
        Console.WriteLine("""
            umadl — 赛马娘资源下载器（GUI / CLI）

            GUI:  直接双击运行
            CLI:  umadl [分组] [-o 目录] [-j 并发] [-n 条数] [--dry-run] [--no-verify]

              -o, --out <目录>     目标目录，默认 %LOCALAPPDATA%\UmaFs\assets
              -g, --group <名>     分组过滤；省略 / --all = 全部
                                   DeleteOnLogin / DownloadLogin / DelayRelease / none …
              -j, --parallel <N>   并发数，默认 8
              -n, --limit <N>      只处理前 N 条
              -p, --proxy <url>    HTTP 代理，如 http://127.0.0.1:7890；--proxy off 强制直连
                                   不指定时读 HTTP_PROXY / HTTPS_PROXY / ALL_PROXY
                  --platform <名>  pc（默认）/ android / ios
                  --cdn <url>      CDN 根，如 http://host:8080/dl/resources
                  --root-api <url> root 接口地址
                  --dry-run        只统计不下载
                  --no-verify      跳过 xxHash64 校验

            示例:
              umadl                           下载 DeleteOnLogin（默认）
              umadl DeleteOnLogin -o D:\uma   指定分组与目录
              umadl --all --dry-run           看全部资源需要多少
              umadl -p 127.0.0.1:7890         走本地代理
              umadl --platform android        下 Android 版资源（默认 pc）
              umadl --cdn http://host:8080/dl/resources    从自建镜像取
              umadl --root-api http://host/api/MetaC/root  自建 root 接口
            """);
    }
}