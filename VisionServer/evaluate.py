"""Scores the detector on the Unity drone-camera frames in test_images/ (written by
Unity menu Drone Sim/Vision/Capture Test Images). Every frame is asked about both targets.

    .venv/bin/python evaluate.py [--imgsz 640] [--conf 0.3] [--engine yolo-world|gemini]
"""

import argparse
import json
import os
import statistics
from collections import defaultdict

import server

HERE = os.path.dirname(os.path.abspath(__file__))


def expected_center(kind):
    # Capture places the item at the frame centre, or 70% of the way to the bottom-left
    # ground corner, which appears top-right in the image (image top = world +Z).
    return (0.5, 0.5) if kind == "center" else (0.85, 0.15)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--engine", choices=["yolo-world", "gemini"], default="yolo-world")
    ap.add_argument("--weights", default=os.path.join(HERE, "models", "yolov8s-worldv2.pt"))
    ap.add_argument("--imgsz", type=int, default=640)
    ap.add_argument("--conf", type=float, default=0.3)
    ap.add_argument("--dir", default="test_images")
    ap.add_argument("--gemini-model", default=os.environ.get("GEMINI_MODEL", "gemini-2.5-flash"))
    args = ap.parse_args()

    engine = server.build_engine(args)
    shots = json.load(open(os.path.join(HERE, args.dir, "manifest.json")))["shots"]
    rows, latencies = [], []
    for s in shots:
        data = open(os.path.join(HERE, args.dir, s["file"]), "rb").read()
        for target in ("key", "phone"):
            r = server.run_detection(engine, data, target, args.conf)
            latencies.append(r["latencyMs"])
            positive = s["truth"] == target
            located = None
            if positive and r["detected"] and r["hasBox"]:
                b = r["bbox"]
                ex, ey = expected_center(s["kind"])
                located = abs(b["x"] + b["w"] / 2 - ex) < 0.08 and abs(b["y"] + b["h"] / 2 - ey) < 0.08
            rows.append((s, target, positive, r, located))

    print(f"engine={engine.name} imgsz={args.imgsz} threshold={args.conf}  frames={len(shots)}")
    print(f"latency per call: median {statistics.median(latencies):.0f} ms, max {max(latencies):.0f} ms "
          f"(first call includes warm-up)\n")
    for alt in sorted({s["altitude"] for s in shots}, reverse=True):
        print(f"--- altitude {alt} m ---")
        for target in ("key", "phone"):
            sub = [x for x in rows if x[0]["altitude"] == alt and x[1] == target]
            pos = [x for x in sub if x[2]]
            neg = [x for x in sub if not x[2]]
            tp = [x for x in pos if x[3]["detected"]]
            fp = [x for x in neg if x[3]["detected"]]
            loc = [x for x in tp if x[4]]
            print(f"{target:5s}: found {len(tp)}/{len(pos)} (box on the object {len(loc)}/{len(tp)})   "
                  f"false alarms {len(fp)}/{len(neg)}")
            for s, _, p, r, located in sub:
                if p or r["detected"] or r["confidence"] >= args.conf * 0.5:
                    flag = ("HIT " if r["detected"] else "MISS") if p else ("FP  " if r["detected"] else "neg ")
                    where = "" if located is None else ("  box OK" if located else "  box OFF-TARGET")
                    print(f"   {flag} {s['file']:28s} best {r['confidence']:.3f}{where}")
        print()


if __name__ == "__main__":
    main()
