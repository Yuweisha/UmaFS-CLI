using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using Microsoft.Win32;

namespace UmaFs.App;

/// <summary>
/// WinFsp 检测与安装。程序内嵌官方 MSI；未安装时静默安装（需要管理员权限）。
/// WinFsp 是内核驱动，无法免安装，只能自动装。
/// </summary>
internal static class WinFspSetup
{
    private const string ProductName = "WinFsp";
    private const string RegKeyWow = @"SOFTWARE\WOW6432Node\WinFsp";
    private const string RegKeyNative = @"SOFTWARE\WinFsp";

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectoryW(string lpPathName);

    /// <summary>
    /// 新版 WinFsp 只写注册表 SxsDir、不写 InstallDir，而托管层 LoadDll 的回退逻辑只认 InstallDir，
    /// 于是 LoadLibraryW("winfsp-x64.dll") 失败。这里把实际的 SxS bin 目录加进 DLL 搜索路径。
    /// 必须在任何 Fsp 类型被首次使用前调用。
    /// </summary>
    public static void PrepareDllSearchPath()
    {
        try
        {
            var v = InstalledVersion();
            if (v is null) return;
            string bin = Path.Combine(v.Value.InstallDir, "SxS");
            string? dll = FindRuntimeDll(v.Value.InstallDir, ReadSxsDir());
            if (dll is null) return;
            SetDllDirectoryW(Path.GetDirectoryName(dll)!);
        }
        catch { }
    }

    private static string? ReadSxsDir()
    {
        foreach (var key in new[] { RegKeyWow, RegKeyNative })
        {
            using var hk = Registry.LocalMachine.OpenSubKey(key);
            if (hk?.GetValue("SxsDir") is string s && !string.IsNullOrWhiteSpace(s) && Directory.Exists(s))
                return s;
        }
        return null;
    }

    /// <summary>已安装的 WinFsp 主版本.次版本，未安装返回 null。</summary>
    public static (int Major, int Minor, string InstallDir)? InstalledVersion()
    {
        foreach (var key in new[] { RegKeyWow, RegKeyNative })
        {
            using var hk = Registry.LocalMachine.OpenSubKey(key);
            if (hk is null) continue;

            // 新版 WinFsp 只写 SxsDir（<InstallDir>\SxS\sxs.<时间戳>\），老版本写 InstallDir
            string? sxsDir = hk.GetValue("SxsDir") as string;
            string? installDir = hk.GetValue("InstallDir") as string;
            if (string.IsNullOrWhiteSpace(installDir) && !string.IsNullOrWhiteSpace(sxsDir))
                installDir = Path.GetDirectoryName(Path.GetDirectoryName(sxsDir!.TrimEnd('\\')));

            if (string.IsNullOrWhiteSpace(installDir)) continue;
            if (!Directory.Exists(installDir)) continue;

            // 版本取自 winfsp-x64.dll 的文件版本，与托管层 CheckVersion 的判据一致
            string? dll = FindRuntimeDll(installDir!, sxsDir);
            if (dll is null) continue;
            var vi = FileVersionInfo.GetVersionInfo(dll);
            if (vi.FileMajorPart <= 0) continue;
            return (vi.FileMajorPart, vi.FileMinorPart, installDir!);
        }
        return null;
    }

    private static string? FindRuntimeDll(string installDir, string? sxsDir)
    {
        // SxsDir 里记录的目录优先（多版本共存时才是实际生效的那个）
        if (!string.IsNullOrWhiteSpace(sxsDir))
        {
            string p = Path.Combine(sxsDir!.TrimEnd('\\'), "bin", "winfsp-x64.dll");
            if (File.Exists(p)) return p;
        }

        string direct = Path.Combine(installDir, "bin", "winfsp-x64.dll");
        if (File.Exists(direct)) return direct;

        // SxS 布局：<InstallDir>\SxS\sxs.<timestamp>\bin\winfsp-x64.dll
        try
        {
            var sxs = Path.Combine(installDir, "SxS");
            if (!Directory.Exists(sxs)) return null;
            return Directory.EnumerateDirectories(sxs)
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => Path.Combine(d, "bin", "winfsp-x64.dll"))
                .FirstOrDefault(File.Exists);
        }
        catch
        {
            return null;
        }
    }

    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 确保 WinFsp 可用，需要时用内嵌 MSI 静默安装。
    /// 返回 true 表示装好且版本匹配，可以继续挂载。
    /// </summary>
    public static bool Ensure(out string message)
    {
        var installed = InstalledVersion();
        var (wantMajor, wantMinor) = ExpectedVersion();

        if (installed is { } v)
        {
            if (v.Major == wantMajor && v.Minor == wantMinor)
            {
                message = $"WinFsp {v.Major}.{v.Minor} 已安装（{v.InstallDir}）";
                return true;
            }

            if (v.Major > wantMajor || (v.Major == wantMajor && v.Minor > wantMinor))
            {
                message = $"WinFsp 已安装 {v.Major}.{v.Minor}，比程序期望的 {wantMajor}.{wantMinor} 更新。" +
                          "请升级 umafs，或手动安装对应版本的 WinFsp。";
                return false;
            }

            message = $"WinFsp 版本过旧（{v.Major}.{v.Minor}），需要 {wantMajor}.{wantMinor}。";
        }
        else
        {
            message = "未检测到 WinFsp。";
        }

        // 需要安装
        if (!IsAdministrator())
        {
            message += "\n挂载需要先安装 WinFsp（内核驱动，无法免安装）。" +
                       "\n请以管理员身份重新运行本程序，或手动安装随程序附带的 winfsp-2.1.25156.msi。";
            return false;
        }

        string msiPath;
        try
        {
            msiPath = ExtractEmbeddedMsi();
        }
        catch (Exception ex)
        {
            message += $"\n解出内置安装包失败：{ex.Message}";
            return false;
        }

        message += $"\n正在静默安装 WinFsp {wantMajor}.{wantMinor}…（{msiPath}）";
        if (!RunMsi(msiPath, out var installLog))
        {
            message += $"\n安装失败：{installLog}";
            return false;
        }

        var after = InstalledVersion();
        if (after is null)
        {
            message += "\n安装程序返回成功，但仍未检测到 WinFsp。请重启系统后重试。";
            return false;
        }

        try { File.Delete(msiPath); } catch { }

        message = $"WinFsp {after.Value.Major}.{after.Value.Minor} 安装完成（{after.Value.InstallDir}）";
        return true;
    }

    /// <summary>程序编译时链接的 winfsp-msil 版本，必须与实际驱动版本一致。</summary>
    private static (int Major, int Minor) ExpectedVersion()
    {
        // 取自引用程序集 winfsp-msil 的文件版本，避免硬编码与 nuget 包版本脱节
        var asmPath = typeof(Fsp.FileSystemHost).Assembly.Location;
        if (!string.IsNullOrEmpty(asmPath) && File.Exists(asmPath))
        {
            var vi = FileVersionInfo.GetVersionInfo(asmPath);
            if (vi.FileMajorPart > 0) return (vi.FileMajorPart, vi.FileMinorPart);
        }
        return (2, 1); // 兜底：winfsp.net 2.1.x
    }

    private static string ExtractEmbeddedMsi()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("winfsp.msi")
            ?? throw new InvalidOperationException("内置安装包缺失（winfsp.msi 未嵌入）");

        string dir = Path.Combine(Path.GetTempPath(), "umafs-setup");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "winfsp.msi");
        using var fs = File.Create(path);
        stream.CopyTo(fs);
        return path;
    }

    private static bool RunMsi(string msiPath, out string log)
    {
        // ADDLOCAL=ALL 确保核心驱动与服务一并安装；/qn 静默
        var psi = new ProcessStartInfo
        {
            FileName = "msiexec.exe",
            Arguments = $"/i \"{msiPath}\" /qn /norestart ADDLOCAL=ALL",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        try
        {
            using var p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(180_000);
            // 3010 = 成功但需重启
            bool ok = p.ExitCode is 0 or 3010;
            log = ok ? "OK" : $"exit={p.ExitCode} {stderr} {stdout}".Trim();
            return ok;
        }
        catch (Exception ex)
        {
            log = ex.Message;
            return false;
        }
    }
}
