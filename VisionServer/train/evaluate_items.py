"""Measures the trained item detector on data it never saw.

1. Held-out Unity scenes (datasets/drone4/raw/test: other random seeds than training), three camera setups:
     1280      1280x1280 frame, inference at 1280 (full resolution)
     640       frame downscaled to 640 (the drone camera's default), inference at 640
     640@1280  640 frame, inference upscaled to 1280
   For each confidence threshold: per-class recall (box of the right class overlapping the object), recall
   "any class" (some box on the object, possibly mislabeled), and false alarms per frame (boxes on no object).
2. VisionServer/test_images: the project's original drone-camera frames (key/phone/wallet/none, 640 px, 1.5 and 2.44 m).

    python3 VisionServer/train/evaluate_items.py [--weights VisionServer/models/drone_items.pt]
"""

import argparse
import json
import time
from pathlib import Path

from PIL import Image

HERE = Path(__file__).resolve().parent
SERVER = HERE.parent
REPO = SERVER.parent
CLASSES = ["wallet", "keys", "phone", "purse"]
THRESHOLDS = [0.05, 0.1, 0.25, 0.5]


def iou(a, b):
    ix = max(0.0, min(a[2], b[2]) - max(a[0], b[0]))
    iy = max(0.0, min(a[3], b[3]) - max(a[1], b[1]))
    inter = ix * iy
    u = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - inter
    return inter / u if u > 0 else 0.0


def hits(gt, pred):
    """Same object: boxes overlap (IoU >= 0.2), or the predicted center lies inside the true box.
    Tiny objects make IoU harsh, and for a search a box on the object is what counts."""
    if iou(gt, pred) >= 0.2:
        return True
    cx, cy = (pred[0] + pred[2]) / 2, (pred[1] + pred[3]) / 2
    return gt[0] <= cx <= gt[2] and gt[1] <= cy <= gt[3]


def load_gt(label_path):
    out = []
    for line in label_path.read_text().splitlines():
        if line.strip():
            c, x, y, w, h = line.split()
            x, y, w, h = map(float, (x, y, w, h))
            out.append((int(c), (x - w / 2, y - h / 2, x + w / 2, y + h / 2)))
    return out


def predict(model, img, imgsz, device):
    r = model.predict(img, imgsz=imgsz, conf=min(THRESHOLDS), iou=0.5, device=device, verbose=False)[0]
    W, H = img.size
    return [(int(k), c, (x1 / W, y1 / H, x2 / W, y2 / H))
            for (x1, y1, x2, y2), c, k in zip(r.boxes.xyxy.tolist(), r.boxes.conf.tolist(), r.boxes.cls.tolist())]


def score(frames, t):
    """frames: [(gt, preds)] -> per-class recall, any-class recall, false alarms per frame at threshold t."""
    found = [0] * len(CLASSES)
    found_any = [0] * len(CLASSES)
    total = [0] * len(CLASSES)
    false_alarms = 0
    for gt, preds in frames:
        preds = [p for p in preds if p[1] >= t]
        for c, box in gt:
            total[c] += 1
            on = [p for p in preds if hits(box, p[2])]
            found_any[c] += bool(on)
            found[c] += any(p[0] == c for p in on)
        false_alarms += sum(1 for p in preds if not any(hits(box, p[2]) for _, box in gt))
    return found, found_any, total, false_alarms / max(1, len(frames))


def pct(a, b):
    return f"{100 * a / b:5.1f}%" if b else "   - "


def held_out(model, device, limit, test):
    images = sorted((test / "images").glob("*.jpg"))[:limit]
    results = {}
    for mode in ("1280", "640", "640@1280"):
        frames, ms = [], 0.0
        for p in images:
            gt = load_gt(test / "labels" / f"{p.stem}.txt")
            with Image.open(p) as im:
                im = im.convert("RGB")
            if mode != "1280":
                im = im.resize((640, 640), Image.BILINEAR)
            t0 = time.perf_counter()
            preds = predict(model, im, 640 if mode == "640" else 1280, device)
            ms += (time.perf_counter() - t0) * 1000
            frames.append((gt, preds))
        print(f"\n== held-out scenes, {mode}: {len(images)} frames, {ms / max(1, len(images)):.0f} ms/frame")
        print(f"{'conf':>5} " + " ".join(f"{c:>15}" for c in CLASSES) + "   all (any class)   false alarms/frame")
        results[mode] = {}
        for t in THRESHOLDS:
            f, fa, tot, fp = score(frames, t)
            print(f"{t:5.2f} " + " ".join(f"{pct(f[i], tot[i])} ({pct(fa[i], tot[i]).strip()})" for i in range(len(CLASSES)))
                  + f"   {pct(sum(f), sum(tot))} ({pct(sum(fa), sum(tot)).strip()})   {fp:.2f}")
            results[mode][t] = {"recall": {c: f[i] / tot[i] if tot[i] else None for i, c in enumerate(CLASSES)},
                                "recallAnyClass": sum(fa) / max(1, sum(tot)), "falseAlarmsPerFrame": fp,
                                "objects": dict(zip(CLASSES, tot))}
    return results


def original_tests(model, device):
    """The project's own frames: does the model report the true item anywhere (and nothing on frames with truth 'none')?"""
    out = {}
    for folder in ("test_images", "test_images_flipped"):
        d = SERVER / folder
        if not (d / "manifest.json").exists():
            continue
        shots = json.loads((d / "manifest.json").read_text())["shots"]
        print(f"\n== {folder}: {len(shots)} frames (640 px, altitudes {sorted({round(s['altitude'], 2) for s in shots})})")
        out[folder] = {}
        for t in THRESHOLDS:
            pos_ok = pos = neg_fp = neg = 0
            for s in shots:
                with Image.open(d / s["file"]) as im:
                    im = im.convert("RGB")
                best = {}
                for k, c, _ in predict(model, im, max(im.size), device):   # native size, as the server does
                    best[CLASSES[k]] = max(best.get(CLASSES[k], 0), c)
                labels = {k for k, c in best.items() if c >= t}
                truth = {"key": "keys", "keys": "keys", "phone": "phone", "wallet": "wallet", "purse": "purse"}.get(s["truth"])
                if truth:
                    pos += 1
                    pos_ok += truth in labels
                else:
                    neg += 1
                    neg_fp += bool(labels)
            print(f"conf {t:.2f}: true item found in {pos_ok}/{pos} frames; empty frames with a false alarm {neg_fp}/{neg}")
            out[folder][t] = {"found": pos_ok, "positives": pos, "falseAlarmFrames": neg_fp, "negatives": neg}
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--weights", default=str(SERVER / "models" / "drone_items.pt"))
    ap.add_argument("--device", default="mps")
    ap.add_argument("--limit", type=int, default=100000)
    ap.add_argument("--test", default=str(REPO / "datasets" / "drone4" / "raw" / "test"), help="rendered test split")
    ap.add_argument("--out", default=str(HERE / "evaluation.json"))
    args = ap.parse_args()
    from ultralytics import YOLO

    model = YOLO(args.weights)
    report = {"weights": args.weights, "test": args.test, "heldOut": held_out(model, args.device, args.limit, Path(args.test)),
              "originalTests": original_tests(model, args.device)}
    out = Path(args.out)
    out.write_text(json.dumps(report, indent=2))
    print(f"\nwrote {out}")


if __name__ == "__main__":
    main()
