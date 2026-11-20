using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Fsp;
using UmaFs.Core;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace UmaFs.App;

/// <summary>
/// 只读虚拟盘：目录/文件直接映射清单，读到的字节是解密后的原文。
/// 展示名带扩展名（.unity3d / .awb / .usm …），--no-ext 时与清单原名一致。
/// </summary>
internal sealed class UmaFileSystem : FileSystemBase
{
    private readonly AssetIndex _index;
    private readonly AssetStore _store;
    private readonly byte[] _security;
    private readonly ulong _timestamp;
    private readonly ulong _totalBytes;
    private long _indexSeed;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<FsNode, object> _indexNumbers = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DirNode, MountNode[]> _dirCache = new();

    public UmaFileSystem(AssetIndex index, string volumeLabel, bool hideMissing = true)
    {
        _index = index;
        _store = new AssetStore(index.PersistentRoot);
        VolumeLabel = volumeLabel;
        HideMissing = hideMissing;
        _timestamp = (ulong)Directory.GetLastWriteTimeUtc(index.PersistentRoot).ToFileTimeUtc();
        _totalBytes = (ulong)Math.Max(index.Entries.Sum(e => e.Length), 1);

        var sd = new RawSecurityDescriptor("O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;WD)");
        _security = new byte[sd.BinaryLength];
        sd.GetBinaryForm(_security, 0);
    }

    public string VolumeLabel { get; }

    /// <summary>
    /// 清单里有、但 dat 里没有实际文件的条目（本机 5,906 条）。
    /// 默认不列出：AssetStudio 这类工具遍历到不存在的文件会直接弹异常。
    /// </summary>
    public bool HideMissing { get; }

    /// <summary>该条目是否应出现在磁盘上（目录恒为 true）。</summary>
    private bool IsVisible(FsNode node) =>
        node is not FileNode f || !HideMissing || f.IsRealFile || _index.FileExists(f);

    /// <summary>把底层异常打到 stderr，否则 winfsp 只会把错误变成“I/O 设备错误”。</summary>
    public override int ExceptionHandler(Exception ex)
    {
        Console.Error.WriteLine($"[异常] {ex.GetType().Name}: {ex.Message}");
        return STATUS_SUCCESS;
    }

    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        volumeInfo.TotalSize = _totalBytes;
        volumeInfo.FreeSize = 0;
        volumeInfo.SetVolumeLabel(VolumeLabel);
        return STATUS_SUCCESS;
    }

    public override int SetVolumeLabel(string volumeLabel, out VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        var node = _index.Resolve(fileName);
        if (node is null || !IsVisible(node))
        {
            fileAttributes = 0;
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }
        fileAttributes = (uint)(node.IsDirectory ? System.IO.FileAttributes.Directory : System.IO.FileAttributes.Normal);
        if (securityDescriptor is not null) securityDescriptor = _security;
        return STATUS_SUCCESS;
    }

    public override int Create(
        string fileName, uint createOptions, uint grantedAccess, uint fileAttributes,
        byte[] securityDescriptor, ulong allocationSize,
        out object fileNode, out object fileDesc, out FileInfo fileInfo, out string normalizedName)
    {
        fileNode = null!;
        fileDesc = null!;
        fileInfo = default;
        normalizedName = null!;
        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    public override int Open(
        string fileName, uint createOptions, uint grantedAccess,
        out object fileNode, out object fileDesc, out FileInfo fileInfo, out string normalizedName)
    {
        fileNode = null!;
        fileDesc = null!;
        fileInfo = default;
        normalizedName = null!;

        var node = _index.Resolve(fileName);
        if (node is null || !IsVisible(node)) return STATUS_OBJECT_NAME_NOT_FOUND;

        var wrapper = new MountNode(node, BuildFileInfo(node));
        if (node is FileNode file)
        {
            try
            {
                fileDesc = new FileHandle(OpenStream(file));
            }
            catch (FileNotFoundException)
            {
                return STATUS_OBJECT_NAME_NOT_FOUND;
            }
            catch (DirectoryNotFoundException)
            {
                return STATUS_OBJECT_NAME_NOT_FOUND;
            }
            catch (UnauthorizedAccessException)
            {
                return STATUS_ACCESS_DENIED;
            }
            catch (IOException)
            {
                return STATUS_ACCESS_DENIED;
            }
        }

        fileNode = wrapper;
        fileInfo = wrapper.FileInfo;
        // 必须回填请求路径（含自动补的扩展名），否则 winfsp 认为打开的不是同一个名字
        normalizedName = fileName;
        return STATUS_SUCCESS;
    }

    /// <summary>
    /// Cleanup 在句柄关闭前调用，此后仍可能有 I/O（如 FILE_DELETE_ON_CLOSE 场景），
    /// 所以这里**不释放**底层流；释放统一放在 Close。
    /// </summary>
    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
    {
    }

    public override void Close(object fileNode, object fileDesc)
    {
        if (fileDesc is FileHandle handle) handle.Dispose();
    }

    public override int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, out uint bytesTransferred)
    {
        bytesTransferred = 0;
        if (fileDesc is not FileHandle handle) return STATUS_INVALID_HANDLE;

        int total = handle.ReadAt(offset, buffer, length);
        if (total < 0) return STATUS_ACCESS_DENIED;
        if (total == 0) return STATUS_END_OF_FILE;

        bytesTransferred = (uint)total;
        return STATUS_SUCCESS;
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        if (fileNode is MountNode node)
        {
            fileInfo = node.FileInfo;
            return STATUS_SUCCESS;
        }
        fileInfo = default;
        return STATUS_INVALID_HANDLE;
    }

    public override bool ReadDirectoryEntry(
        object fileNode, object fileDesc, string pattern, string marker,
        ref object context, out string fileName, out FileInfo fileInfo)
    {
        fileName = null!;
        fileInfo = default;
        if (fileNode is not MountNode node || node.Node is not DirNode dir) return false;

        var enumerator = context as IEnumerator<MountNode>;
        if (enumerator is null)
        {
            var children = GetEntries(dir);

            int start = 0;
            if (marker is not null)
            {
                // marker 是上一次返回的名字，从它之后继续
                start = children.FindIndex(m => string.Compare(m.DisplayName, marker, StringComparison.OrdinalIgnoreCase) > 0);
                if (start < 0) start = children.Count;
            }
            context = enumerator = children.Skip(start).GetEnumerator();
        }

        while (enumerator.MoveNext())
        {
            var cur = enumerator.Current;
            fileName = cur.DisplayName ?? cur.Node.Name;
            fileInfo = cur.FileInfo;
            return true;
        }

        return false;
    }

    // ---------- 内部 ----------

    /// <summary>
    /// 目录项列表按目录缓存。全树遍历（37 万文件）时每个目录只构建一次，
    /// 否则每次枚举都要重算 FileInfo + 逐个 stat。
    /// </summary>
    private List<MountNode> GetEntries(DirNode dir)
    {
        if (_dirCache.TryGetValue(dir, out var cached)) return new List<MountNode>(cached);

        var children = new List<MountNode>(dir.Children.Count + 2);
        if (dir.Parent is not null)
        {
            children.Add(new MountNode(dir, BuildFileInfo(dir), "."));
            children.Add(new MountNode(dir.Parent, BuildFileInfo(dir.Parent), ".."));
        }

        // 按展示名排序：marker 恢复要求名字序稳定，否则 winfsp 会重复/漏项
        var kids = dir.GetChildren()
            .Where(IsVisible)
            .Select(c => new MountNode(c, BuildFileInfo(c), c is FileNode f ? f.DisplayName : c.Name))
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase);

        children.AddRange(kids);
        var arr = children.ToArray();
        _dirCache.AddOrUpdate(dir, arr);
        return new List<MountNode>(arr);
    }

    private Stream OpenStream(FileNode file)
    {
        if (file.IsRealFile)
            return new FileStream(file.BlobPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);

        if (!_index.FileExists(file))
            throw new FileNotFoundException($"资源数据缺失: {file.Entry?.Name}", file.BlobPath);
        return _store.Open(file.Entry!);
    }

    private FileInfo BuildFileInfo(FsNode node)
    {
        var info = new FileInfo
        {
            FileAttributes = (uint)(node.IsDirectory ? System.IO.FileAttributes.Directory : System.IO.FileAttributes.Normal),
            CreationTime = _timestamp,
            LastAccessTime = _timestamp,
            LastWriteTime = _timestamp,
            ChangeTime = _timestamp,
            HardLinks = 1,
            IndexNumber = IndexNumberOf(node)
        };

        if (node is FileNode file)
        {
            var size = (ulong)Math.Max(file.Size, 0);
            info.FileSize = size;
            info.AllocationSize = (size + 4095) / 4096 * 4096;
        }
        return info;
    }

    /// <summary>文件 ID 必须稳定（同一节点多次查询返回同一个值），否则资源管理器会认成不同文件。</summary>
    private ulong IndexNumberOf(FsNode node) =>
        (ulong)(long)_indexNumbers.GetValue(node, _ => (object)Interlocked.Increment(ref _indexSeed))!;
}

/// <summary>
/// 单次打开的读写句柄。Close 可能被调用多次（winfsp 在异常路径上会重复调用），
/// 用 _disposed 保证幂等——否则二次 Dispose 会让后续 I/O 抛 ObjectDisposedException。
/// </summary>
internal sealed class FileHandle : IDisposable
{
    private readonly Stream _stream;
    private readonly object _lock = new();
    private int _disposed;

    public FileHandle(Stream stream) => _stream = stream;

    /// <summary>返回实际读到的字节数；0 表示到结尾；-1 表示句柄已失效。</summary>
    public int ReadAt(ulong offset, IntPtr buffer, uint length)
    {
        if (Volatile.Read(ref _disposed) != 0) return -1;

        lock (_lock)
        {
            if (_disposed != 0) return -1;
            try
            {
                long pos = (long)offset;
                if (pos >= _stream.Length) return 0;

                int want = (int)Math.Min(length, (uint)(_stream.Length - pos));
                if (want <= 0) return 0;

                byte[] tmp = new byte[want];
                int total = 0;
                _stream.Seek(pos, SeekOrigin.Begin);
                while (total < want)
                {
                    int n = _stream.Read(tmp, total, want - total);
                    if (n <= 0) break;
                    total += n;
                }
                if (total <= 0) return 0;

                Marshal.Copy(tmp, 0, buffer, total);
                return total;
            }
            catch (ObjectDisposedException)
            {
                return -1;
            }
            catch (IOException)
            {
                return -1;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_lock)
        {
            try { _stream.Dispose(); } catch { }
        }
    }
}

internal sealed class MountNode
{
    public MountNode(FsNode node, FileInfo fileInfo, string? displayName = null)
    {
        Node = node;
        FileInfo = fileInfo;
        DisplayName = displayName;
    }

    public FsNode Node { get; }
    public FileInfo FileInfo { get; }
    public string? DisplayName { get; }
}