"""Side-by-side recall of several model versions on the same unseen frames, at the drone camera's 640 px
(frames downscaled, inference at native size) and confidence 0.25.

    python3 VisionServer/train/compare_models.py models/drone_items_v1.pt models/drone_items_v2.pt models/drone_items.pt
"""

import json
import sys
from pathlib import Path

from PIL import Image

HERE = Path(__file__).resolve().parent
SERVER, REPO = HERE.parent, HERE.parent.parent
sys.path.insert(0, str(HERE))
import evaluate_items as ev  # noqa: E402

CONF = 0.25
SETS = [("new wallet, phones screen-down", REPO / "datasets/drone5/raw/fresh"),
        ("new wallet, phones both ways", REPO / "datasets/drone6/raw/fresh")]


def rendered(model, folder):
    frames = []
    for p in sorted((folder / "images").glob("*.jpg")):
        with Image.open(p) as im:
            im = im.convert("RGB").resize((640, 640), Image.BILINEAR)
        frames.append((ev.load_gt(folder / "labels" / f"{p.stem}.txt"), ev.predict(model, im, 640, "mps")))
    f, fa, tot, fp = ev.score(frames, CONF)
    return {c: f"{100 * f[i] / tot[i]:.1f}%" for i, c in enumerate(ev.CLASSES)} | {"wrong/frame": f"{fp:.2f}"}


def original(model, folder):
    shots = json.loads((SERVER / folder / "manifest.json").read_text())["shots"]
    found, tot, alarms = {}, {}, 0
    for s in shots:
        with Image.open(SERVER / folder / s["file"]) as im:
            labels = {ev.CLASSES[k] for k, c, _ in ev.predict(model, im.convert("RGB"), 640, "mps") if c >= CONF}
        t = {"key": "keys"}.get(s["truth"], s["truth"])
        if t == "none":
            alarms += bool(labels)
            continue
        tot[t] = tot.get(t, 0) + 1
        found[t] = found.get(t, 0) + (t in labels)
    return {t: f"{found[t]}/{tot[t]}" for t in sorted(tot)} | {"empty-frame alarms": f"{alarms}/24"}


def main():
    from ultralytics import YOLO

    out = {}
    for w in sys.argv[1:]:
        m = YOLO(str(SERVER / w) if not Path(w).is_absolute() else w)
        out[w] = {name: rendered(m, folder) for name, folder in SETS}
        out[w]["original test_images (phones screen-down)"] = original(m, "test_images")
        out[w]["original test_images_flipped (phones screen-up)"] = original(m, "test_images_flipped")
        for k, v in out[w].items():
            print(f"{w:28s} {k:48s} {v}")
    (HERE / "comparison.json").write_text(json.dumps(out, indent=2))


if __name__ == "__main__":
    main()
