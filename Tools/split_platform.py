"""用 umadl 导出的 win_hashes.txt / and_hashes.txt 把混放的资源包分到两个平台目录。

Windows/assetbundles 里:
  命中 win_hashes  -> 保留
  命中 and_hashes  -> 移到 Android/assetbundles
  都不命中         -> 留在原地并计数

用法: python split_platform.py [--dry-run]
"""
import os
import shutil
import sys

BASE = r"F:\Kitaworks\cdnmirror"
SRC = os.path.join(BASE, "Windows", "assetbundles")
DST = os.path.join(BASE, "Android", "assetbundles")


def load(path):
    s = set()
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line:
                s.add(line)
    return s


def main():
    dry = "--dry-run" in sys.argv
    win = load(os.path.join(BASE, "win_hashes.txt"))
    andr = load(os.path.join(BASE, "and_hashes.txt"))
    print(f"Windows hash {len(win)}，Android hash {len(andr)}", flush=True)

    stayed = moved = unknown = 0
    unknown_sample = []
    dirs = sorted(os.listdir(SRC))
    for i, d in enumerate(dirs, 1):
        sub = os.path.join(SRC, d)
        if not os.path.isdir(sub):
            continue
        for f in list(os.listdir(sub)):
            if f in win:
                stayed += 1
            elif f in andr:
                if not dry:
                    dst = os.path.join(DST, d, f)
                    os.makedirs(os.path.dirname(dst), exist_ok=True)
                    shutil.move(os.path.join(sub, f), dst)
                moved += 1
            else:
                unknown += 1
                if len(unknown_sample) < 8:
                    unknown_sample.append(f)
        if i % 100 == 0:
            print(f"  [{i}/{len(dirs)}] Windows={stayed} Android={moved} 未知={unknown}", flush=True)

    print()
    print(f"结果{'（dry-run）' if dry else ''}:", flush=True)
    print(f"  留在 Windows : {stayed}", flush=True)
    print(f"  移到 Android : {moved}", flush=True)
    print(f"  两边都不匹配 : {unknown}", flush=True)
    if unknown_sample:
        print(f"  未知样例: {unknown_sample}", flush=True)


if __name__ == "__main__":
    main()