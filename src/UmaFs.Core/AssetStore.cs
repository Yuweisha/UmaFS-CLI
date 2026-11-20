using UmamusumeData;

namespace UmaFs.Core;

/// <summary>赛马娘 (Windows/Steam/DMM) 资源解密与路径解析。</summary>
public sealed class AssetStore
{
    private const int HeaderSize = 256;
    private static readonly byte[] BaseKeys = { 0x53, 0x2B, 0x46, 0x31, 0xE4, 0xA7, 0xB9, 0x47, 0x3E, 0x7C, 0xFB };

    public string PersistentRoot { get; }
    public string DatRoot => Path.Combine(PersistentRoot, "dat");

    public AssetStore(string persistentRoot) => PersistentRoot = persistentRoot;

    public static bool LooksLikeDataDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return Directory.Exists(Path.Combine(path, "dat"))
            && File.Exists(Path.Combine(path, "meta"))
            && File.Exists(Path.Combine(path, "master", "master.mdb"));
    }

    /// <summary>自动探测本机游戏数据目录（LocalLow、DMM、Steam 各库）。</summary>
    public static string? AutoDetect()
    {
        var candidates = new List<string>();
        string localLow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "Cygames");
        candidates.Add(Path.Combine(localLow, "umamusume"));
        candidates.Add(Path.Combine(localLow, "UmamusumePrettyDerby_Jpn"));

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            try { candidates.Add(Path.Combine(drive.RootDirectory.FullName, "DMMGamePlayer", "Umamusume", "Umamusume")); } catch { }
        }

        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string libFile = Path.Combine(pf86, "Steam", "steamapps", "libraryfolders.vdf");
        if (File.Exists(libFile))
        {
            foreach (var line in File.ReadLines(libFile))
            {
                if (!line.Contains("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;
                var parts = line.Split('"');
                if (parts.Length < 4) continue;
                string lib = parts[3].Replace("\\\\", "\\");
                candidates.Add(Path.Combine(lib, "steamapps", "common", "UmamusumePrettyDerby_Jpn", "UmamusumePrettyDerby_Jpn_Data", "Persistent"));
            }
        }
        candidates.Add(Path.Combine(pf86, "Steam", "steamapps", "common", "UmamusumePrettyDerby_Jpn", "UmamusumePrettyDerby_Jpn_Data", "Persistent"));

        foreach (var c in candidates)
        {
            try { if (LooksLikeDataDirectory(c)) return c; } catch { }
        }
        return null;
    }

    public string ResolveBlobPath(ManifestEntry entry) =>
        Path.Combine(DatRoot, entry.HashName[..2], entry.HashName);

    /// <summary>构造该资源的异或密钥流（88 字节）；未加密返回 null。</summary>
    public static byte[]? BuildKeyStream(long key)
    {
        if (key == 0) return null;
        byte[] kb = BitConverter.GetBytes(key);
        if (!BitConverter.IsLittleEndian) Array.Reverse(kb);
        var keys = new byte[BaseKeys.Length * 8];
        for (int i = 0; i < BaseKeys.Length; i++)
            for (int j = 0; j < 8; j++)
                keys[i * 8 + j] = (byte)(BaseKeys[i] ^ kb[j]);
        return keys;
    }

    public AssetStream Open(ManifestEntry entry)
    {
        string path = ResolveBlobPath(entry);
        if (!File.Exists(path)) throw new FileNotFoundException($"资源数据缺失: {entry.Name}", path);
        return new AssetStream(path, entry.EncryptionKey);
    }

    /// <summary>一次性读出解密后的完整内容。</summary>
    public byte[] ReadAll(ManifestEntry entry)
    {
        using var s = Open(entry);
        var buf = new byte[s.Length];
        int read = 0;
        while (read < buf.Length)
        {
            int n = s.Read(buf, read, buf.Length - read);
            if (n <= 0) break;
            read += n;
        }
        if (read != buf.Length) Array.Resize(ref buf, read);
        return buf;
    }

    /// <summary>按魔数猜扩展名，便于浏览时一眼认出内容类型。</summary>
    public static string GuessExtension(byte[] head)
    {
        if (head.Length >= 7 && head[0] == 'U' && head[1] == 'n' && head[2] == 'i' && head[3] == 't' && head[4] == 'y' && head[5] == 'F' && head[6] == 'S')
            return ".unity3d";
        if (head.Length >= 4)
        {
            if (head[0] == 0x04 && head[1] == 0x22 && head[2] == 0x4D && head[3] == 0x18) return ".bsv";
            if (head[0] == 'A' && head[1] == 'F' && head[2] == 'S' && head[3] == '2') return ".awb";
            if (head[0] == 'C' && head[1] == 'R' && head[2] == 'I' && head[3] == 'D') return ".cpk";
            if (head[0] == 'S' && head[1] == 'Q' && head[2] == 'L') return ".mdb";
        }
        return "";
    }
}

/// <summary>只读、可随机访问、按需解密的资源流。前 256 字节固定明文。</summary>
public sealed class AssetStream : Stream
{
    private const int HeaderSize = 256;
    private readonly FileStream _inner;
    private readonly byte[]? _keys;
    private long _position;

    public AssetStream(string path, long encryptionKey)
    {
        _inner = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        _keys = AssetStore.BuildKeyStream(encryptionKey);
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        _inner.Position = _position;
        int n = _inner.Read(buffer);
        if (n > 0 && _keys is not null)
        {
            long start = _position;
            for (int i = 0; i < n; i++)
            {
                long pos = start + i;
                if (pos < HeaderSize) continue;
                buffer[i] ^= _keys[(int)(pos % _keys.Length)];
            }
        }
        _position += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _inner.Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
