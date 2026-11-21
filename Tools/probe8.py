"""在服务器上：从本地 Android 清单里查出指定资源名的 hash，并探测官方 CDN。

用法: python probe8.py
"""
import base64
import hashlib
import os
import sys
import urllib.request
import urllib.error

import lz4.frame

BASE = r"F:\Kitaworks\cdnmirror"
MANIFEST_DIR = os.path.join(BASE, "Manifest")
UA = "UnityPlayer/2022.3.62f1 (UnityWebRequest/1.0, libcurl/7.52.0-DEV)"
CDN = "https://prd-storage-game-umamusume.akamaized.net/dl/resources"

NAMES = [
    "outgame/announceevent/loguiasset/ast_announce_event_log_ui_asset_00058",
    "outgame/banner/img_bnr_home_link_901035",
    "sound/v/snd_voi_event_090058.acb",
    "storyevent/logo/1058/tex_storyevent_logo_1058",
    "outgame/announceevent/bg/announce_event_00058",
    "sound/v/snd_voi_event_090058.awb",
    "outgame/banner/img_bnr_home_500058",
    "outgame/banner/img_bnr_home_link_901036",
]

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


def parse_rows(raw):
    d = lz4.frame.decompress(raw)
    off = 4
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


def head(url):
    req = urllib.request.Request(url, headers={"User-Agent": UA}, method="HEAD")
    try:
        with urllib.request.urlopen(req, timeout=20) as r:
            return r.status
    except urllib.error.HTTPError as e:
        return e.code
    except Exception as e:
        return type(e).__name__


def main():
    want = set(NAMES)
    found = {}
    files = []
    for d1 in sorted(os.listdir(MANIFEST_DIR)):
        sub = os.path.join(MANIFEST_DIR, d1)
        if not os.path.isdir(sub):
            continue
        for f in sorted(os.listdir(sub)):
            files.append(os.path.join(sub, f))

    for p in files:
        try:
            raw = open(p, "rb").read()
            if raw[:4] != b"\x04\x22\x4d\x18":
                continue
            rows = parse_rows(raw)
        except Exception:
            continue
        # 只关心含目标名字的清单
        hit = [(n, h, s) for n, h, s in rows if n in want]
        if hit:
            for n, h, s in hit:
                found.setdefault(n, (h, s))

    print(f"查到 {len(found)}/{len(NAMES)} 条", flush=True)
    for n in NAMES:
        if n not in found:
            print(f"  {n}  未在清单中找到", flush=True)
            continue
        h, s = found[n]
        is_ext = bool(os.path.splitext(n)[1])
        url = (f"{CDN}/Generic/{h[:2]}/{h}" if is_ext
               else f"{CDN}/Android/assetbundles/{h[:2]}/{h}")
        local = (os.path.join(BASE, "Generic", h[:2], h) if is_ext
                 else os.path.join(BASE, "Android", "assetbundles", h[:2], h))
        print(f"  {n}", flush=True)
        print(f"     hash={h} size={s} 本地={'有' if os.path.exists(local) else '缺'}",
              flush=True)
        print(f"     CDN HEAD -> {head(url)}", flush=True)


if __name__ == "__main__":
    main()