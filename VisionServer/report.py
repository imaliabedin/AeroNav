"""Scan report: turns one area scan (the JSON Unity's ScanMission posts to /report) plus the frames the
vision server stored for that session into a folder you can open in a browser:

    drone_images/reports/<session_id>/
        report.html                 summary, map, object table with evidence, validation
        report.json                 the same data, machine-readable
        scan.json                   the raw scan summary from Unity (input; enough to rebuild the report)
        map.png                     top-down map: scan area, coverage, flight path, objects
        evidence/object_<id>.jpg        stored camera frame with the object's box drawn
        evidence/object_<id>_crop.jpg   close-up of the box

Called by server.py (POST /report). To rebuild a report later, for example after the server was offline
during the scan (Unity then saves scan.json itself), run from the repo root:

    python3 VisionServer/report.py <session_id> [--store-dir drone_images]
"""

import argparse
import html
import json
import os
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
SESSION_RE = re.compile(r"^[A-Za-z0-9_-]{1,64}$")

CONFIRMED = (40, 200, 230)
UNCONFIRMED = (150, 150, 150)


def _vec(v):
    """Unity's JsonUtility writes Vector3 as {"x":..,"y":..,"z":..}."""
    return (float(v.get("x", 0)), float(v.get("y", 0)), float(v.get("z", 0))) if isinstance(v, dict) else tuple(v)


def _find_frame(store, store_root, session, image_id):
    if not image_id or not re.match(r"^[0-9a-f]{32}$", image_id):
        return None
    if store is not None:
        p = store.image_path(image_id)
        if p is not None and p.exists():
            return p
    for ext in ("jpg", "png"):
        p = Path(store_root) / "sessions" / session / f"{image_id}.{ext}"
        if p.exists():
            return p
    return None


def _evidence(frame_path, obj, out_dir):
    """Full frame with the box drawn, and a close-up crop. Returns (frame_rel, crop_rel) or (None, None)."""
    b = obj.get("bbox") or {}
    with Image.open(frame_path) as im:
        im = im.convert("RGB")
    W, H = im.size
    x0, y0 = b.get("x", 0) * W, b.get("y", 0) * H
    x1, y1 = x0 + b.get("w", 0) * W, y0 + b.get("h", 0) * H
    color = CONFIRMED if obj.get("confirmed") else UNCONFIRMED

    full = im.copy()
    d = ImageDraw.Draw(full)
    if x1 > x0 and y1 > y0:
        d.rectangle([x0 - 2, y0 - 2, x1 + 2, y1 + 2], outline=color, width=3)
        label = f"#{obj['id']} {obj.get('category', '?')} {obj.get('confidence', 0):.2f}"
        tw = d.textlength(label)
        ty = y0 - 16 if y0 >= 16 else y1 + 2
        d.rectangle([x0 - 2, ty, x0 + tw + 4, ty + 14], fill=color)
        d.text((x0 + 1, ty + 1), label, fill=(0, 0, 0))
    name = f"object_{obj['id']}"
    full.save(out_dir / f"{name}.jpg", quality=90)

    # Close-up: the box with 1.5x its size of context on each side (at least 64 px), enlarged to 256 px.
    if x1 > x0 and y1 > y0:
        cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
        half = max(32.0, 2.0 * max(x1 - x0, y1 - y0))
        crop = im.crop((int(max(0, cx - half)), int(max(0, cy - half)), int(min(W, cx + half)), int(min(H, cy + half))))
        ox, oy = max(0, cx - half), max(0, cy - half)
        s = 256 / max(crop.size)
        crop = crop.resize((max(1, int(crop.width * s)), max(1, int(crop.height * s))), Image.LANCZOS)
        ImageDraw.Draw(crop).rectangle([(x0 - ox) * s, (y0 - oy) * s, (x1 - ox) * s, (y1 - oy) * s], outline=color, width=2)
        crop.save(out_dir / f"{name}_crop.jpg", quality=92)
        return f"evidence/{name}.jpg", f"evidence/{name}_crop.jpg"
    return f"evidence/{name}.jpg", None


def _map(p, out_path):
    """Top-down map, world X to the right and Z up."""
    x0, x1, z0, z1 = p["xMin"], p["xMax"], p["zMin"], p["zMax"]
    pts = [(x0, z0), (x1, z1)]
    pts += [(_vec(v)[0], _vec(v)[2]) for v in p.get("path", [])]
    pts += [(_vec(o["position"])[0], _vec(o["position"])[2]) for o in p.get("objects", [])]
    if p.get("home"):
        h = _vec(p["home"])
        pts.append((h[0], h[2]))
    minx, maxx = min(q[0] for q in pts) - 3, max(q[0] for q in pts) + 3
    minz, maxz = min(q[1] for q in pts) - 3, max(q[1] for q in pts) + 3
    scale = min(900 / max(1e-3, maxx - minx), 640 / max(1e-3, maxz - minz))
    W, H = int((maxx - minx) * scale) + 1, int((maxz - minz) * scale) + 40

    def px(x, z):
        return ((x - minx) * scale, (maxz - z) * scale)

    img = Image.new("RGBA", (W, H), (250, 250, 248, 255))
    over = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(over)

    cols, rows, cov = p.get("coverageCols", 0), p.get("coverageRows", 0), p.get("coverage", "")
    if cols and rows and len(cov) == cols * rows:
        for r in range(rows):
            for c in range(cols):
                a, b = px(x0 + c, z0 + r + 1), px(x0 + c + 1, z0 + r)
                seen = cov[r * cols + c] == "1"
                d.rectangle([a, b], fill=(60, 190, 90, 70) if seen else (230, 70, 60, 70))
    img = Image.alpha_composite(img, over)
    d = ImageDraw.Draw(img)
    d.rectangle([px(x0, z1), px(x1, z0)], outline=(20, 150, 190), width=3)

    path = [px(_vec(v)[0], _vec(v)[2]) for v in p.get("path", [])]
    if len(path) > 1:
        d.line(path, fill=(90, 90, 160), width=2)
    for v in p.get("waypoints", []):
        x, _, z = _vec(v)
        q = px(x, z)
        d.rectangle([q[0] - 2, q[1] - 2, q[0] + 2, q[1] + 2], fill=(90, 90, 160))
    if p.get("home"):
        h = _vec(p["home"])
        q = px(h[0], h[2])
        d.rectangle([q[0] - 6, q[1] - 6, q[0] + 6, q[1] + 6], outline=(0, 0, 0), width=2)
        d.text((q[0] + 8, q[1] - 6), "start", fill=(0, 0, 0))
    for g in p.get("groundTruth", []):
        x, _, z = _vec(g["position"])
        q = px(x, z)
        d.ellipse([q[0] - 7, q[1] - 7, q[0] + 7, q[1] + 7], outline=(40, 160, 60) if g.get("matched") else (200, 60, 50), width=2)
    for o in p.get("objects", []):
        x, _, z = _vec(o["position"])
        q = px(x, z)
        col = CONFIRMED if o.get("confirmed") else UNCONFIRMED
        d.ellipse([q[0] - 4, q[1] - 4, q[0] + 4, q[1] + 4], fill=col)
        d.text((q[0] + 6, q[1] - 12), f"#{o['id']} {o.get('category', '')}", fill=(0, 0, 0))

    y = H - 30
    d.text((8, y), "area (blue)  coverage: green seen / red not seen  path: purple  "
                   "objects: cyan confirmed / grey unconfirmed  rings: scene truth (green found / red missed)",
           fill=(60, 60, 60))
    d.text((8, y + 14), f"X {minx:.0f}..{maxx:.0f} m   Z {minz:.0f}..{maxz:.0f} m   1 m = {scale:.1f} px", fill=(60, 60, 60))
    img.convert("RGB").save(out_path)


def build_report(payload, store=None, report_root=None):
    """Writes the report folder for one scan. Returns {"dir", "html", "json"} (absolute paths)."""
    session = payload.get("sessionId", "")
    if not SESSION_RE.match(session or ""):
        raise ValueError("sessionId must be 1-64 characters of A-Z a-z 0-9 _ -")
    store_root = Path(store.root) if store is not None else Path(REPO) / "drone_images"
    out = Path(report_root or store_root / "reports") / session
    ev = out / "evidence"
    ev.mkdir(parents=True, exist_ok=True)
    (out / "scan.json").write_text(json.dumps(payload, indent=2))

    objects = sorted(payload.get("objects", []), key=lambda o: (not o.get("confirmed"), -o.get("confidence", 0)))
    for o in objects:
        frame = _find_frame(store, store_root, session, o.get("imageId", ""))
        o["evidence"], o["evidenceCrop"] = _evidence(frame, o, ev) if frame else (None, None)
    _map(payload, out / "map.png")

    # Validation against the simulator's ground truth (the drone never sees it; this only measures the scan).
    truth = payload.get("groundTruth", [])
    found = sum(1 for g in truth if g.get("matched"))
    confirmed = [o for o in objects if o.get("confirmed")]
    errs = [o["nearestSceneDistance"] for o in confirmed if o.get("nearestSceneDistance", -1) >= 0]
    frames_with_sightings = len(store.list_images(session, detected_only=True)) if store is not None else None
    frames_stored = len(store.list_images(session)) if store is not None else None

    summary = {
        "sessionId": session,
        "status": payload.get("status"),
        "generatedAt": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "startedAt": payload.get("startedAtUtc"),
        "finishedAt": payload.get("finishedAtUtc"),
        "durationSeconds": round(payload.get("durationSeconds", 0), 1),
        "area": {k: payload.get(k) for k in ("xMin", "xMax", "zMin", "zMax")},
        "areaSquareMeters": round((payload["xMax"] - payload["xMin"]) * (payload["zMax"] - payload["zMin"]), 1),
        "navigation": payload.get("navigation"),
        "altitudeMeters": round(payload.get("altitude", 0), 2),
        "footprintMeters": round(payload.get("footprint", 0), 2),
        "laneSpacingMeters": round(payload.get("laneSpacing", 0), 2),
        "lanes": payload.get("lanes"),
        "waypoints": {"planned": payload.get("waypointsPlanned"), "reached": payload.get("waypointsReached"),
                      "skipped": payload.get("waypointsSkipped")},
        "coveragePercent": round(payload.get("coveragePercent", 0), 1),
        "framesAnalyzed": payload.get("framesAnalyzed"),
        "framesStored": frames_stored,
        "framesWithSightings": frames_with_sightings,
        "detections": payload.get("detectionsTotal"),
        "detectionsOutsideArea": payload.get("detectionsOutsideArea"),
        "confidenceThreshold": payload.get("confidenceThreshold"),
        "objects": len(objects),
        "objectsConfirmed": len(confirmed),
        "validation": {
            "sceneObjectsInArea": len(truth),
            "found": found,
            "recallPercent": round(100 * found / len(truth), 1) if truth else None,
            "meanErrorToNearestSceneObjectMeters": round(sum(errs) / len(errs), 2) if errs else None,
        },
    }
    report = {"summary": summary, "objects": objects, "groundTruth": truth}
    (out / "report.json").write_text(json.dumps(report, indent=2))
    (out / "report.html").write_text(_html(summary, objects, truth))
    return {"dir": str(out.resolve()), "html": str((out / "report.html").resolve()), "json": str((out / "report.json").resolve())}


def _html(s, objects, truth):
    e = lambda v: html.escape("" if v is None else str(v))

    def row(k, v):
        return f"<tr><th>{e(k)}</th><td>{e(v)}</td></tr>"

    a = s["area"]
    wp = s["waypoints"]
    v = s["validation"]
    stats = "".join([
        row("Status", s["status"]),
        row("Started / finished (UTC)", f"{s['startedAt']}  /  {s['finishedAt']}"),
        row("Duration", f"{s['durationSeconds']} s"),
        row("Scan area", f"X {a['xMin']:g} to {a['xMax']:g} m, Z {a['zMin']:g} to {a['zMax']:g} m ({s['areaSquareMeters']} m²)"),
        row("Navigation", s["navigation"]),
        row("Altitude / camera footprint / lane spacing", f"{s['altitudeMeters']} m / {s['footprintMeters']} m / {s['laneSpacingMeters']} m"),
        row("Lanes / waypoints", f"{s['lanes']} lanes; {wp['reached']} of {wp['planned']} waypoints reached, {wp['skipped']} skipped"),
        row("Area coverage", f"{s['coveragePercent']} % of the area seen in at least one analyzed frame"),
        row("Frames", f"{s['framesAnalyzed']} analyzed" + (f", {s['framesStored']} stored, {s['framesWithSightings']} with sightings" if s["framesStored"] is not None else "")),
        row("Detections", f"{s['detections']} in total, {s['detectionsOutsideArea']} outside the area (ignored); confidence threshold {s['confidenceThreshold']}"),
        row("Objects", f"{s['objects']} ({s['objectsConfirmed']} confirmed)"),
    ])

    rows = []
    for o in objects:
        x, y, z = _vec(o["position"])
        img = (f'<a href="{e(o["evidence"])}"><img src="{e(o["evidenceCrop"] or o["evidence"])}" alt="evidence #{e(o["id"])}"></a>'
               if o.get("evidence") else "<span class=muted>no stored frame</span>")
        near = (f'{e(o.get("nearestSceneObject"))} ({o["nearestSceneDistance"]:.2f} m)'
                if o.get("nearestSceneDistance", -1) >= 0 else "-")
        rows.append(
            f'<tr class="{"" if o.get("confirmed") else "muted"}"><td>#{e(o["id"])}</td><td><b>{e(o.get("category"))}</b>'
            f'<br><span class=muted>"{e(o.get("label"))}"</span></td><td>{o.get("confidence", 0):.2f}</td>'
            f'<td>{e(o.get("observations"))}</td><td>{"yes" if o.get("confirmed") else "no"}</td>'
            f'<td>X {x:.2f}<br>Y {y:.2f}<br>Z {z:.2f}</td><td>{"object surface" if o.get("hitObject") else "ground plane"}</td>'
            f'<td>{near}</td><td>{img}</td></tr>')
    table = ("".join(rows) if rows else '<tr><td colspan=9 class=muted>No objects were detected in the scan area.</td></tr>')

    gt = "".join(f'<tr><td>{e(g["name"])}</td><td>{e(g["category"])}</td>'
                 f'<td>X {_vec(g["position"])[0]:.2f}, Z {_vec(g["position"])[2]:.2f}</td>'
                 f'<td>{"found" if g.get("matched") else "<b>missed</b>"}</td></tr>' for g in truth)
    recall = (f"{v['found']} of {v['sceneObjectsInArea']} scene objects in the area were found ({v['recallPercent']} %)."
              if v["sceneObjectsInArea"] else "The scan area contained no catalogued scene objects.")
    err = (f" Mean distance from a confirmed object's estimated position to the nearest scene object: "
           f"{v['meanErrorToNearestSceneObjectMeters']} m." if v["meanErrorToNearestSceneObjectMeters"] is not None else "")

    return f"""<!doctype html>
<html lang="en"><head><meta charset="utf-8"><title>Scan report {e(s['sessionId'])}</title>
<style>
body {{ font: 14px/1.45 -apple-system, system-ui, sans-serif; margin: 24px auto; max-width: 1100px; padding: 0 16px; color: #1d1d1f; }}
h1 {{ font-size: 22px; margin-bottom: 2px; }} h2 {{ font-size: 17px; margin-top: 28px; }}
table {{ border-collapse: collapse; width: 100%; }} th, td {{ text-align: left; vertical-align: top; padding: 6px 8px; border-bottom: 1px solid #e3e3e3; }}
.stats th {{ width: 300px; color: #555; font-weight: 500; }} .muted {{ color: #888; }}
img {{ max-width: 180px; border-radius: 4px; }} .map {{ max-width: 100%; border: 1px solid #ddd; }}
.note {{ background: #f6f6f3; padding: 10px 12px; border-radius: 6px; color: #444; }}
</style></head><body>
<h1>Drone area scan report</h1>
<div class=muted>Session {e(s['sessionId'])} · generated {e(s['generatedAt'])}</div>

<h2>Summary</h2>
<table class=stats>{stats}</table>

<h2>Map</h2>
<img class=map src="map.png" alt="top-down map of the scan">

<h2>Detected objects</h2>
<p class=muted>Positions are Unity world coordinates (meters, Y up), from the camera ray through each box center at the
moment the frame was captured. Repeated sightings of one category within the merge radius are averaged into one object.
An object is confirmed after several sightings or one confident sighting. Click a picture for the full camera frame.</p>
<table><tr><th>ID</th><th>Class</th><th>Best conf.</th><th>Sightings</th><th>Confirmed</th><th>Position (m)</th>
<th>Ray hit</th><th>Nearest scene object (validation)</th><th>Evidence</th></tr>{table}</table>

<h2>Validation against the simulator</h2>
<p class=note>Only possible in simulation: the scene's real object list is compared with what the drone reported.
The drone never uses this list. {e(recall)}{e(err)}</p>
<table><tr><th>Scene object</th><th>Category</th><th>True position</th><th>Result</th></tr>{gt or '<tr><td colspan=4 class=muted>none</td></tr>'}</table>

<h2>Limitations</h2>
<p class=note>Detection uses a pretrained open-vocabulary model (YOLO-World) on synthetic Unity images with no fine-tuning, so
small items (keys, phones, wallets) are often missed and some boxes are wrong. Confidence scores are not
probabilities. Positions assume the camera looks straight down (true in this simulation) and are only as good as the boxes.</p>
</body></html>
"""


def main():
    ap = argparse.ArgumentParser(description="Rebuild a scan report from drone_images/reports/<session>/scan.json")
    ap.add_argument("session")
    ap.add_argument("--store-dir", default=os.environ.get("VISION_STORE_DIR", os.path.join(REPO, "drone_images")))
    args = ap.parse_args()
    if not SESSION_RE.match(args.session):
        raise SystemExit("bad session id")
    scan = Path(args.store_dir) / "reports" / args.session / "scan.json"
    if not scan.exists():
        raise SystemExit(f"no scan data at {scan}")
    sys.path.insert(0, REPO)
    from image_store import ImageStore

    store = ImageStore(args.store_dir)
    out = build_report(json.loads(scan.read_text()), store, Path(args.store_dir) / "reports")
    print(out["html"])


if __name__ == "__main__":
    main()
