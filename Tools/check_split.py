"""在服务器上核对：assetbundles 里哪些文件属于 PC 清单。

只做一件事：用 PC 清单算出 hash 集合，统计目录里命中多少。
命中率 ~100% => 纯 PC；~50% => PC 与 Android 混放。
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
    """返回 [(name, hash, size)]"""
    d = lz4.frame.decompress(raw)
    off = 2 + 2
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
        if name is None or cs is None or len(nums) < 1:
            continue
        size = nums[-1]
        h = base64.b32encode(
            hashlib.sha1(cs + size.to_bytes(8, "big") + name.encode()).digest()
        ).decode().rstrip("=")
        out.append((name, h, size))
    return out


def main():
    raws = {}
    for d1 in sorted(os.listdir(MANIFEST_DIR)):
        sub = os.path.join(MANIFEST_DIR, d1)
        if not os.path.isdir(sub):
            continue
        for f in sorted(os.listdir(sub)):
            p = os.path.join(sub, f)
            try:
                b = open(p, "rb").read()
                if b[:4] == b"\x04\x22\x4d\x18":
                    raws[f] = b
            except Exception:
                pass
    print(f"清单文件 {len(raws)} 个", flush=True)

    # 平台清单 = 41 行那个
    plat = None
    for h, raw in raws.items():
        try:
            rows = parse(raw)
        except Exception:
            continue
        if len(rows) == 41:
            plat = (h, rows)
            print(f"平台清单 {h}", flush=True)
            break
    if not plat:
        print("找不到平台清单", flush=True)
        return

    # 自检：已知资源名应算出已知 hash
    probe_name, probe_expect = "chara/chr0001/chr_icon_1001", "JJXNI44DUT5P5T4SYMISYE44I4HDGJ36"
    checked = False

    pc = set()
    for cname, chash, csize in plat[1]:
        if chash not in raws or chash == plat[0]:
            continue
        try:
            rows = parse(raws[chash])
        except Exception:
            continue
        for name, h, size in rows:
            if os.path.splitext(name)[1]:
                continue
            pc.add(h)
            if not checked and name == probe_name:
                print(f"自检 {name} -> {h}  期望 {probe_expect}  " +
                      ("OK" if h == probe_expect else "MISMATCH"), flush=True)
                checked = True

    print(f"PC 资源包 hash 数: {len(pc)}", flush=True)

    hit = miss = 0
    dirs = 0
    for d1 in os.listdir(ASSET_DIR):
        sub = os.path.join(ASSET_DIR, d1)
        if not os.path.isdir(sub):
            continue
        dirs += 1
        for f in os.listdir(sub):
            if f in pc:
                hit += 1
            else:
                miss += 1

    total = hit + miss
    print(f"\nassetbundles: {dirs} 个子目录，{total} 个文件", flush=True)
    print(f"  属于 PC : {hit}  ({100.0*hit/max(total,1):.1f}%)", flush=True)
    print(f"  不属于  : {miss}  ({100.0*miss/max(total,1):.1f}%)", flush=True)


if __name__ == "__main__":
    main()