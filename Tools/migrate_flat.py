"""把扁平的 dat 目录迁移成 CDN 目录树。

按文件头魔数分类:
  UnityFS        -> <平台>/assetbundles/{xx}/{hash}
  LZ4 frame      -> Manifest/{xx}/{hash}
  AFS2 / CRID    -> Generic/{xx}/{hash}
  其它           -> Generic/{xx}/{hash}

用法:
  python migrate_flat.py --src <扁平目录> --dst <镜像目录>
  python migrate_flat.py --src ... --dst ... --move
  python migrate_flat.py --src ... --dst ... --dry-run
"""
import argparse
import os
import shutil
import sys

UNITYFS = b"UnityFS"
LZ4FRAME = b"\x04\x22\x4d\x18"
AFS2 = b"AFS2"
CRID = b"CRID"


def classify(path):
    try:
        with open(path, "rb") as f:
            head = f.read(8)
    except Exception:
        return None

    if head[:7] == UNITYFS:
        return "assetbundles"
    if head[:4] == LZ4FRAME:
        return "Manifest"
    if head[:4] in (AFS2, CRID):
        return "Generic"
    return "Generic"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", required=True)
    ap.add_argument("--dst", required=True)
    ap.add_argument("--platform", default="Windows")
    ap.add_argument("--move", action="store_true")
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    src = args.src.rstrip("\\/")
    dst = args.dst.rstrip("\\/")
    stats = {"UnityFS": 0, "Manifest": 0, "Generic": 0, "skip": 0, "err": 0}
    total = 0
    done = 0

    for root, dirs, files in os.walk(src):
        total += len(files)
    print(f"源文件总数: {total}", flush=True)

    for root, dirs, files in os.walk(src):
        dirs[:] = [d for d in dirs if os.path.join(root, d) != dst]
        for name in files:
            path = os.path.join(root, name)
            done += 1
            if done % 20000 == 0:
                print(f"  {done}/{total}  " + " ".join(f"{k}={v}" for k, v in stats.items()), flush=True)

            rel = os.path.relpath(path, src)
            parts = rel.split(os.sep)
            if len(parts) != 2:
                stats["skip"] += 1
                continue

            kind = classify(path)
            if kind is None:
                stats["err"] += 1
                continue

            if kind == "assetbundles":
                target_rel = os.path.join(args.platform, "assetbundles", parts[0], parts[1])
                key = "UnityFS"
            elif kind == "Manifest":
                target_rel = os.path.join("Manifest", parts[0], parts[1])
                key = "Manifest"
            else:
                target_rel = os.path.join("Generic", parts[0], parts[1])
                key = "Generic"

            target = os.path.join(dst, target_rel)
            if args.dry_run:
                stats[key] += 1
                continue

            try:
                os.makedirs(os.path.dirname(target), exist_ok=True)
                if args.move:
                    shutil.move(path, target)
                else:
                    if not os.path.exists(target):
                        shutil.copy2(path, target)
                stats[key] += 1
            except Exception as e:
                stats["err"] += 1
                if stats["err"] <= 5:
                    print(f"  ERR {path}: {e}", file=sys.stderr, flush=True)

    print()
    print("完成:")
    for k, v in stats.items():
        print(f"  {k:<10} {v}")


if __name__ == "__main__":
    main()