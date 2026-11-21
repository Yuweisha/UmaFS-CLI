"""核对 Windows/assetbundles 里的文件有多少属于 PC 清单，多少是多余的（疑似 Android）。

PC 清单 = Manifest/ 下 41 个分清单里所有 UnityFS 资源（名称无扩展名）的 hash 集合。
"""
import base64
import hashlib
import os
import sys

import lz4.frame

BASE = r"F:\Kitaworks\cdnmirror"
MANIFEST_DIR = os.path.join(BASE, "Manifest")
ASSET_DIR = os.path.join(BASE, "Windows", "assetbundles")

T_TEXT, T_UINT4, T_ULONG8, T_FIXED, T_SKIP8 = 64, 17, 18, 33, 19


def vlq(b, i):
    v = 0
    while True:
        x = b[i]
        i += 1
        v = (v << 7) | (x & 0x7F)
        if not (x & 0x80):
            break
    return v, i


def parse(raw):
    d = lz4.frame.decompress(raw)
    off = 2
    off += 2
    rows, off = vlq(d, off)
    _, off = vlq(d, off)
    _, off = vlq(d, off)
    sc, off = vlq(d, off)
    sch = []
    for _ in range(sc):
        t = d[off]
        off += 1
        fx = None
        if t in (33, 65, 49):
            fx, off = vlq(d, off)
        sch.append((t, fx))
    out = []
    for _ in range(rows):
        name = None
        nums = []
        cs = None
        for t, fx in sch:
            if t == T_TEXT:
                e = d.index(b"\0", off)
                name = d[off:e].decode("utf-8", "replace")
                off = e + 1
            elif t in (T_UINT4, T_ULONG8):
                v, off = vlq(d, off)
                nums.append(v)
            elif t == T_SKIP8:
                off += 8
            elif t == T_FIXED:
                n = fx or 8
                cs = d[off:off + n]
                off += n
        if name is None or cs is None or not nums:
            continue
        size = nums[-1]
        h = base64.b32encode(
            hashlib.sha1(cs + size.to_bytes(8, "big") + name.encode()).digest()
        ).decode().rstrip("=")
        out.append((name, h, size))
    return out


def main():
    pc_hashes = set()
    pc_bytes = 0
    manifest_files = 0

    # 先找出平台清单（3 字段、41 行），据此挑出分清单
    platform_hash = None
    all_manifest = {}
    for d1 in sorted(os.listdir(MANIFEST_DIR)):
        sub = os.path.join(MANIFEST_DIR, d1)
        if not os.path.isdir(sub):
            continue
        for f in sorted(os.listdir(sub)):
            p = os.path.join(sub, f)
            try:
                raw = open(p, "rb").read()
                if raw[:4] != b"\x04\x22\x4d\x18":
                    continue
                all_manifest[f] = raw
            except Exception:
                pass

    print(f"Manifest 目录里共 {len(all_manifest)} 个 LZ4 文件", flush=True)

    for h, raw in all_manifest.items():
        try:
            rows = parse(raw)
        except Exception:
            continue
        if len(rows) == 41:
            platform_hash = h
            print(f"  平台清单 = {h}（41 项）", flush=True)
            break

    if platform_hash is None:
        print("找不到平台清单", flush=True)
        return

    for name, h, size in parse(all_manifest[platform_hash]):
        if h in all_manifest and h != platform_hash:
            try:
                rows = parse(all_manifest[h])
            except Exception:
                continue
            for aname, ah, asize in rows:
                if os.path.splitext(aname)[1]:
                    continue      # 带扩展名走 Generic
                pc_hashes.add(ah)
                pc_bytes += asize
            manifest_files += 1

    print(f"PC 分清单 {manifest_files} 个，资源包 hash {len(pc_hashes)} 个，" +
          f"合计 {pc_bytes/1024/1024/1024:.2f} GB", flush=True)

    # 扫 assetbundles
    present, extra, extra_bytes = 0, 0, 0
    extra_sample = []
    for d1 in os.listdir(ASSET_DIR):
        sub = os.path.join(ASSET_DIR, d1)
        if not os.path.isdir(sub):
            continue
        for f in os.listdir(sub):
            if f in pc_hashes:
                present += 1
            else:
                extra += 1
                try:
                    extra_bytes += os.path.getsize(os.path.join(sub, f))
                except Exception:
                    pass
                if len(extra_sample) < 10:
                    extra_sample.append(f)

    print(f"\nassetbundles 目录:", flush=True)
    print(f"  属于 PC 清单 : {present}", flush=True)
    print(f"  清单里没有   : {extra}  ({extra_bytes/1024/1024/1024:.2f} GB)", flush=True)
    print(f"  样例: {extra_sample}", flush=True)


if __name__ == "__main__":
    main()