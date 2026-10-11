"""Turns Unity's rendered frames (DatasetGenerator.cs) into a YOLO training set.

From each 1280x1280 render it makes:
  - 640x640 tiles at native resolution (objects keep their full-resolution size, e.g. keys ~25-45 px), and
  - the whole frame downscaled to 640x640 (objects half as big, as a 640 px drone camera sees them).
So one model handles both camera resolutions. Tiles without objects are kept at a reduced rate (negatives).

    python3 VisionServer/train/prepare.py            # datasets/drone4/raw -> datasets/drone4/yolo
"""

import argparse
import random
import shutil
from pathlib import Path

from PIL import Image

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
CLASSES = ["wallet", "keys", "phone", "purse"]   # must match DatasetGenerator.Classes


def read_labels(path):
    rows = []
    for line in path.read_text().splitlines():
        if line.strip():
            c, x, y, w, h = line.split()
            rows.append((int(c), float(x), float(y), float(w), float(h)))
    return rows


def crop_labels(rows, W, H, x0, y0, size, min_visible=0.6):
    """Boxes (normalized to the full image) inside the crop, re-normalized to it. Boxes less than
    `min_visible` inside are dropped, and None is returned if any is cut badly (ambiguous tile)."""
    out = []
    for c, x, y, w, h in rows:
        bx0, by0, bx1, by1 = (x - w / 2) * W, (y - h / 2) * H, (x + w / 2) * W, (y + h / 2) * H
        cx0, cy0 = max(bx0, x0), max(by0, y0)
        cx1, cy1 = min(bx1, x0 + size), min(by1, y0 + size)
        if cx1 <= cx0 or cy1 <= cy0:
            continue
        frac = (cx1 - cx0) * (cy1 - cy0) / max(1e-9, (bx1 - bx0) * (by1 - by0))
        if frac < 0.25:
            continue            # a sliver: fine to leave unlabeled
        if frac < min_visible:
            return None         # half an object: skip this tile rather than teach a wrong label
        out.append((c, ((cx0 + cx1) / 2 - x0) / size, ((cy0 + cy1) / 2 - y0) / size, (cx1 - cx0) / size, (cy1 - cy0) / size))
    return out


def write(img, rows, out_dir, name):
    img.save(out_dir / "images" / f"{name}.jpg", quality=92)
    (out_dir / "labels" / f"{name}.txt").write_text("\n".join(f"{c} {x:.6f} {y:.6f} {w:.6f} {h:.6f}" for c, x, y, w, h in rows))


def prepare(raw, out, split, tile=640, negative_rate=0.25, seed=0):
    rng = random.Random(seed)
    src = raw / split
    dst = out / split
    for sub in ("images", "labels"):
        (dst / sub).mkdir(parents=True, exist_ok=True)
    counts = {"tiles": 0, "frames": 0, "boxes": [0] * len(CLASSES)}
    for img_path in sorted((src / "images").glob("*.jpg")):
        rows = read_labels(src / "labels" / f"{img_path.stem}.txt")
        with Image.open(img_path) as im:
            im = im.convert("RGB")
        W, H = im.size

        # Whole frame at half resolution (a 640 px drone camera).
        small = im.resize((tile, tile), Image.BILINEAR) if W != tile else im
        write(small, rows, dst, f"{img_path.stem}_full")
        counts["frames"] += 1
        for r in rows:
            counts["boxes"][r[0]] += 1

        # Native-resolution tiles: the four corners plus one random offset.
        if W > tile:
            offsets = [(0, 0), (W - tile, 0), (0, H - tile), (W - tile, H - tile),
                       (rng.randint(0, W - tile), rng.randint(0, H - tile))]
            for i, (x0, y0) in enumerate(offsets):
                t_rows = crop_labels(rows, W, H, x0, y0, tile)
                if t_rows is None or (not t_rows and rng.random() > negative_rate):
                    continue
                write(im.crop((x0, y0, x0 + tile, y0 + tile)), t_rows, dst, f"{img_path.stem}_t{i}")
                counts["tiles"] += 1
                for r in t_rows:
                    counts["boxes"][r[0]] += 1
    return counts


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--raw", default=str(REPO / "datasets" / "drone4" / "raw"))
    ap.add_argument("--out", default=str(REPO / "datasets" / "drone4" / "yolo"))
    args = ap.parse_args()
    raw, out = Path(args.raw), Path(args.out)
    if out.exists():
        shutil.rmtree(out)
    for split in ("train", "val"):
        c = prepare(raw, out, split, seed=1 if split == "train" else 2)
        print(f"{split}: {c['frames']} full frames + {c['tiles']} tiles; boxes "
              + ", ".join(f"{n} {k}" for n, k in zip(CLASSES, c["boxes"])))
    (out / "data.yaml").write_text(
        f"path: {out}\ntrain: train/images\nval: val/images\nnames:\n"
        + "".join(f"  {i}: {n}\n" for i, n in enumerate(CLASSES)))
    print(f"wrote {out / 'data.yaml'}")


if __name__ == "__main__":
    main()
