using System.Collections.Concurrent;
using UmamusumeData;

namespace UmaFs.Core;

/// <summary>把 meta 清单组织成目录树，供 CLI 与虚拟盘共享。</summary>
public sealed class AssetIndex
{
    private readonly ConcurrentDictionary<string, bool> _existsCache = new(StringComparer.OrdinalIgnoreCase);

    public DirNode Root { get; }
    public string PersistentRoot { get; }
    public IReadOnlyList<ManifestEntry> Entries { get; }
    public int ManifestCount { get; }
    public string MasterMdbPath => Path.Combine(PersistentRoot, "master", "master.mdb");

    /// <summary>数据文件是否真的在磁盘上（带缓存，避免枚举目录时反复 stat）。</summary>
    public bool FileExists(FileNode node)
    {
        if (node.IsRealFile) return File.Exists(node.BlobPath);
        return _existsCache.GetOrAdd(node.BlobPath, static p => File.Exists(p));
    }

    private AssetIndex(string persistentRoot, List<ManifestEntry> entries)
    {
        PersistentRoot = persistentRoot;
        Entries = entries;
        Root = new DirNode("\\", parent: null);

        int manifests = 0;
        foreach (var e in entries)
        {
            string name = e.Name.Replace('\\', '/');
            string blob = Path.Combine(persistentRoot, "dat", e.HashName[..2], e.HashName);

            if (name.StartsWith('/'))
            {
                // 顶层清单（//root, //Windows, //chara …）归到 _manifests
                string fileName = name.TrimStart('/');
                if (fileName.Length == 0) continue;
                AddFile(EnsureDirectory("_manifests"), new FileNode(fileName, e, blob));
                manifests++;
                continue;
            }

            var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            DirNode cur = Root;
            for (int i = 0; i < parts.Length - 1; i++) cur = EnsureDirectory(cur, parts[i]);
            AddFile(cur, new FileNode(parts[^1], e, blob));
        }
        ManifestCount = manifests;

        // 明文 SQLite 库，旁挂出来方便直接用工具打开
        string master = Path.Combine(persistentRoot, "master", "master.mdb");
        if (File.Exists(master)) AddFile(EnsureDirectory("_master"), new FileNode("master.mdb", master));
    }

    public static AssetIndex Load(string persistentRoot)
    {
        UmaDataHelper.UmamusumeDirectory = persistentRoot;
        UmaDataHelper.Initialize();
        var entries = UmaDataHelper.GetManifestEntries();
        return new AssetIndex(persistentRoot, entries);
    }

    private DirNode EnsureDirectory(string name) => EnsureDirectory(Root, name);

    private static DirNode EnsureDirectory(DirNode parent, string name)
    {
        if (parent.Children.TryGetValue(name, out var existing) && existing is DirNode d) return d;
        var dir = new DirNode(name, parent);
        parent.Children[name] = dir;
        parent.Invalidate();
        return dir;
    }

    private static void AddFile(DirNode parent, FileNode file)
    {
        file.Parent = parent;
        if (parent.Children.TryGetValue(file.Name, out var existing))
        {
            // 同名文件/目录冲突：保留目录，文件退让加后缀
            if (existing is DirNode) file.Name = file.Name + "~file";
            else return;
        }
        parent.Children[file.Name] = file;
        parent.Invalidate();
    }

    /// <summary>
    /// 解析 \a\b\c 形式的路径（大小写不敏感）。允许用户带上自动补的扩展名，
    /// 例如 chara\chr1001\chr_icon_1001.unity3d 也能解析到清单里的 chr_icon_1001。
    /// </summary>
    public FsNode? Resolve(string path)
    {
        var parts = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        FsNode cur = Root;
        foreach (var part in parts)
        {
            if (cur is not DirNode dir) return null;
            if (dir.Children.TryGetValue(part, out var next))
            {
                cur = next;
                continue;
            }
            int dot = part.LastIndexOf('.');
            if (dot > 0 && dir.Children.TryGetValue(part[..dot], out var stripped))
            {
                cur = stripped;
                continue;
            }
            return null;
        }
        return cur;
    }

    public IEnumerable<FileNode> EnumerateFiles(FsNode node, bool recursive)
    {
        switch (node)
        {
            case FileNode f:
                yield return f;
                break;
            case DirNode d:
                foreach (var child in d.GetChildren())
                {
                    if (child is FileNode cf) yield return cf;
                    else if (recursive)
                        foreach (var nf in EnumerateFiles(child, true)) yield return nf;
                }
                break;
        }
    }
}

public abstract class FsNode
{
    protected FsNode(string name, DirNode? parent)
    {
        Name = name;
        Parent = parent;
    }

    /// <summary>清单里的原始名称（挂载时去掉扩展名后能还原成它）。</summary>
    public string Name { get; set; }

    public DirNode? Parent { get; internal set; }

    /// <summary>展示名：文件会按类型补扩展名（.unity3d / .awb / .usm …）。</summary>
    public virtual string DisplayName => Name;

    /// <summary>带扩展名的完整路径（导出、显示用）。</summary>
    public string DisplayPath
    {
        get
        {
            if (Parent is null) return "\\";
            var stack = new Stack<string>();
            stack.Push(DisplayName);
            FsNode? n = Parent;
            while (n is not null && n.Parent is not null)
            {
                stack.Push(n.Name);
                n = n.Parent;
            }
            return "\\" + string.Join("\\", stack);
        }
    }

    public string FullPath
    {
        get
        {
            if (Parent is null) return "\\";
            var stack = new Stack<string>();
            FsNode? n = this;
            while (n is not null && n.Parent is not null)
            {
                stack.Push(n.Name);
                n = n.Parent;
            }
            return "\\" + string.Join("\\", stack);
        }
    }

    public abstract bool IsDirectory { get; }
}

public sealed class DirNode : FsNode
{
    private volatile FsNode[]? _ordered;

    public DirNode(string name, DirNode? parent) : base(name, parent) { }

    public Dictionary<string, FsNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public override bool IsDirectory => true;

    internal void Invalidate() => _ordered = null;

    /// <summary>目录项缓存：目录在前、按名称排序，枚举大目录时不必每次重排。</summary>
    public FsNode[] GetChildren()
    {
        var cached = _ordered;
        if (cached is not null) return cached;
        lock (Children)
        {
            cached = _ordered;
            if (cached is not null) return cached;
            var list = Children.Values.ToArray();
            Array.Sort(list, static (a, b) =>
            {
                if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            _ordered = cached = list;
            return cached;
        }
    }
}

public sealed class FileNode : FsNode
{
    private string? _display;

    public FileNode(string name, ManifestEntry entry, string blobPath) : base(name, null)
    {
        Entry = entry;
        IsRealFile = false;
        BlobPath = blobPath;
    }

    /// <summary>旁挂的真实文件（如 master.mdb），直接透传，不经过解密。</summary>
    public FileNode(string name, string realPath) : base(name, null)
    {
        Entry = null;
        IsRealFile = true;
        BlobPath = realPath;
    }

    public ManifestEntry? Entry { get; }
    public bool IsRealFile { get; }
    public string BlobPath { get; }

    public override bool IsDirectory => false;

    public override string DisplayName => _display ??= Naming.WithExtension(this);

    /// <summary>解密后的大小（取自清单，不访问磁盘）。</summary>
    public long Size => IsRealFile ? new FileInfo(BlobPath).Length : Entry!.Length;

    public bool Encrypted => Entry is { EncryptionKey: not 0 };
}
