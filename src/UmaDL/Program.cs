using System.Runtime.InteropServices;

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
                default:
                    if (!args[i].StartsWith('-')) group = args[i];
                    break;
            }
        }

        return Cli.Run(dest, group, parallel, limit, dryRun, noVerify);
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
                  --dry-run        只统计不下载
                  --no-verify      跳过 xxHash64 校验

            示例:
              umadl                           下载 DeleteOnLogin（默认）
              umadl DeleteOnLogin -o D:\uma   指定分组与目录
              umadl --all --dry-run           看全部资源需要多少
            """);
    }
}