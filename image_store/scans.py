"""Area-scan image storage: one folder per scan, a JSON manifest, and the read API the AI model will use.

Layout under `root` (default image_store/scans/):
    <scan_id>/manifest.json        scan record + one entry per capture (format below; see SCANS.md)
    <scan_id>/images/<seq>_<image_id>.jpg
    <scan_id>/detections.jsonl     future model results, one JSON object per line, keyed by image_id
    <scan_id>/ground_truth.json    where the object was really placed (evaluation only; never in the manifest)

Standard library only. Thread-safe within one process (one lock), and every manifest write is atomic
(temporary file + rename), so a crash never leaves a half-written manifest. Image files are created with
exclusive mode, so an existing capture is never overwritten.
"""

import base64
import hashlib
import json
import os
import re
import secrets
import threading
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Dict, Iterator, List, Optional

FORMAT_VERSION = 1
SCAN_ID_RE = re.compile(r"^scan-\d{8}-\d{6}-[0-9a-f]{6}$")
# Metadata fields the scanner may send with an image (anything else is dropped).
IMAGE_FIELDS = (
    "sequence_number", "timestamp", "sim_time", "capture_reason", "lane_index", "revisit_round",
    "drone_position", "camera_position", "camera_orientation", "planned_waypoint", "actual_speed",
    "ground_footprint", "occlusion_status", "occluded_regions", "revisit_required", "camera",
    "drone_id", "floor",                                   # Rescue Mission frames
)


def _now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")


def _write_json(path: Path, obj) -> None:
    tmp = path.with_name(path.name + f".{os.getpid()}.{threading.get_ident()}.tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(obj, f, indent=1)
        f.flush()
        os.fsync(f.fileno())
    os.replace(tmp, path)


def _image_format(data: bytes) -> Optional[str]:
    if data[:3] == b"\xff\xd8\xff":
        return "jpg"
    if data[:8] == b"\x89PNG\r\n\x1a\n":
        return "png"
    return None


class ScanError(Exception):
    """Bad input (unknown scan, invalid image, ...)."""


class ScanStore:
    def __init__(self, root="image_store/scans"):
        self.root = Path(root).resolve()
        self.root.mkdir(parents=True, exist_ok=True)
        self._lock = threading.Lock()

    # ---------- writing (used by scan_server.py) ----------

    def create_scan(self, record: Dict) -> Dict:
        """Starts a scan. `record` holds target_category, region, config, route, coordinate_frame."""
        with self._lock:
            for _ in range(10):
                scan_id = datetime.now(timezone.utc).strftime("scan-%Y%m%d-%H%M%S-") + secrets.token_hex(3)
                folder = self.root / scan_id
                try:
                    folder.mkdir()          # exclusive: never reuse an existing scan folder
                    break
                except FileExistsError:
                    continue
            else:  # pragma: no cover
                raise ScanError("could not allocate a scan id")
            (folder / "images").mkdir()
            manifest = {
                "format_version": FORMAT_VERSION,
                "scan_id": scan_id,
                "scan_region_id": f"{scan_id}-region",
                "status": "running",
                "started_at": _now(),
                "ended_at": None,
                "target_category": record.get("target_category"),
                "region": record.get("region"),
                "config": record.get("config"),
                "route": record.get("route"),
                "coordinate_frame": record.get("coordinate_frame"),
                "summary": None,
                "counts": {"saved": 0, "failed": 0, "duplicates": 0},
                "images": [],
            }
            _write_json(folder / "manifest.json", manifest)
            return manifest

    def add_image(self, scan_id: str, image: bytes, meta: Dict) -> Dict:
        """Saves one capture and appends it to the manifest. Returns the manifest entry.

        A byte-identical repeat of an earlier image in the scan is not saved again: the earlier entry is returned
        with "duplicate": true. A failed write is recorded in the manifest with capture_status "failed" and re-raised.
        """
        with self._lock:
            folder = self._folder(scan_id)
            manifest = self._read(folder)
            if manifest["status"] != "running":
                raise ScanError(f"scan {scan_id} is {manifest['status']}")
            fmt = _image_format(image or b"")
            if fmt is None:
                raise ScanError("image is not a JPEG or PNG")
            sha = hashlib.sha256(image).hexdigest()
            for e in manifest["images"]:
                if e.get("sha256") == sha and e["capture_status"] == "saved":
                    manifest["counts"]["duplicates"] += 1
                    _write_json(folder / "manifest.json", manifest)
                    return dict(e, duplicate=True)

            image_id = uuid.uuid4().hex
            seq = meta.get("sequence_number")
            seq = seq if isinstance(seq, int) and seq >= 0 else len(manifest["images"]) + 1
            name = f"{seq:05d}_{image_id}.{fmt}"
            entry = {"scan_id": scan_id, "image_id": image_id, "scan_region_id": manifest["scan_region_id"]}
            entry.update({k: meta[k] for k in IMAGE_FIELDS if k in meta})
            entry["sequence_number"] = seq
            entry.setdefault("timestamp", _now())
            entry["received_at"] = _now()
            try:
                with open(folder / "images" / name, "xb") as f:   # exclusive: never overwrite
                    f.write(image)
                    f.flush()
                    os.fsync(f.fileno())
            except OSError as e:
                entry.update(capture_status="failed", error=str(e), image_path=None)
                manifest["images"].append(entry)
                manifest["counts"]["failed"] += 1
                _write_json(folder / "manifest.json", manifest)
                raise
            entry.update(capture_status="saved", image_path=f"images/{name}", format=fmt,
                         size_bytes=len(image), sha256=sha)
            manifest["images"].append(entry)
            manifest["counts"]["saved"] += 1
            _write_json(folder / "manifest.json", manifest)
            return entry

    def finish_scan(self, scan_id: str, summary: Dict) -> Dict:
        """Ends a scan. summary["status"]: complete | incomplete | stopped."""
        with self._lock:
            folder = self._folder(scan_id)
            manifest = self._read(folder)
            manifest["status"] = summary.get("status", "incomplete")
            manifest["ended_at"] = _now()
            manifest["summary"] = summary
            _write_json(folder / "manifest.json", manifest)
            return manifest

    def set_ground_truth(self, scan_id: str, truth: Dict) -> None:
        """Evaluation only. Stored apart from the manifest; the read API below never returns it."""
        with self._lock:
            _write_json(self._folder(scan_id) / "ground_truth.json", dict(truth, recorded_at=_now()))

    # ---------- reading (for the AI model and the website) ----------

    def list_scans(self) -> List[Dict]:
        """Newest first: scan_id, status, target_category, started_at, ended_at, counts."""
        out = []
        for folder in sorted(self.root.iterdir(), reverse=True):
            if not (SCAN_ID_RE.match(folder.name) and (folder / "manifest.json").is_file()):
                continue
            m = self._read(folder)
            out.append({k: m.get(k) for k in ("scan_id", "status", "target_category", "started_at", "ended_at", "counts")})
        return out

    def manifest(self, scan_id: str) -> Dict:
        return self._read(self._folder(scan_id))

    def images(self, scan_id: str, include_failed: bool = False) -> List[Dict]:
        """Entries whose image file exists, in capture order, each with "path": the absolute file path.
        include_failed=True: every entry (failed saves and discarded negatives have path None)."""
        folder = self._folder(scan_id)
        out = []
        for e in sorted(self._read(folder)["images"], key=lambda e: e["sequence_number"]):
            if (e["capture_status"] != "saved" or not e.get("image_path")) and not include_failed:
                continue                  # failed saves, and negatives whose file was discarded
            out.append(dict(e, path=str(folder / e["image_path"]) if e.get("image_path") else None))
        return out

    def iter_batches(self, scan_id: str, batch_size: int = 16) -> Iterator[List[Dict]]:
        """Saved images in capture order, `batch_size` at a time (batch_size=1 for one by one)."""
        items = self.images(scan_id)
        for i in range(0, len(items), max(1, batch_size)):
            yield items[i:i + batch_size]

    def read_image(self, scan_id: str, image_id: str) -> bytes:
        for e in self.images(scan_id):
            if e["image_id"] == image_id:
                return Path(e["path"]).read_bytes()
        raise ScanError(f"no saved image {image_id} in {scan_id}")

    def detections(self, scan_id: str) -> Dict:
        """Model results for a scan: {"detections": [...], "inference": counts by status}. See SCANS.md."""
        folder = self._folder(scan_id)
        m = self._read(folder)
        path = folder / "detections" / "detections.json"
        dets = json.loads(path.read_text(encoding="utf-8")) if path.exists() else []
        return {"scan_id": scan_id, "status": m["status"], "target_category": m.get("target_category"),
                "inference": _inference_counts(m),
                # Rescue frames (forward camera, ray localization) are less precise than straight-down scan photos.
                "sightings": sightings(dets, m, radius=1.5 if any(d.get("drone_id") for d in dets) else 0.75),
                "detections": dets}

    def set_world_positions(self, scan_id: str, image_id: str, positions: List[Dict]) -> int:
        """Rescue Mission: the scene-based 3-D position of each detection in an image, computed by Unity (a ray from
        the frame's camera pose through the box center). Replaces `world_position` of the matching records."""
        with self._lock:
            folder = self._folder(scan_id)
            path = folder / "detections" / "detections.json"
            if not path.exists():
                return 0
            dets = json.loads(path.read_text(encoding="utf-8"))
            by_id = {p.get("detection_id"): p for p in positions if isinstance(p, dict)}
            n = 0
            for d in dets:
                p = by_id.get(d["detection_id"])
                if d["image_id"] != image_id or p is None:
                    continue
                w = {k: p[k] for k in ("available", "x", "y", "z", "floor", "distance_m", "ray_hit", "method", "reason",
                                       "size_m", "plausible") if k in p}
                if w.get("available"):
                    w["approximate"] = True
                    w["frame"] = "Unity world meters (x east, y up, z north)"
                d["world_position"] = w
                n += 1
            _write_json(path, dets)
            return n

    # ---------- AI inference results (written by scan_server.py's worker) ----------

    def mark_inference(self, scan_id: str, image_id: str, status: str, **info) -> Optional[Dict]:
        """Sets an image's inference status without touching its file (queued, running, failed).
        Returns None if the image already has a final result (positive/negative): never processed twice."""
        with self._lock:
            folder = self._folder(scan_id)
            m = self._read(folder)
            e = _entry(m, image_id)
            if (e.get("inference") or {}).get("status") in ("positive", "negative"):
                return None
            e["inference"] = dict(info, status=status, updated_at=_now())
            _write_json(folder / "manifest.json", m)
            return e

    def apply_inference(self, scan_id: str, image_id: str, result: Dict, relevant: List[Dict],
                        keep_negatives: bool = False) -> Dict:
        """Stores the model's answer for one saved image.

        relevant non-empty → positive: the image moves (atomic rename) to detections/, and one record per relevant
        object is appended to detections/detections.json, only after the move succeeded.
        relevant empty → negative: the image file is deleted (unless keep_negatives); its metadata stays.
        `result` is the model's full response (kept in the manifest entry, minus bulky debug fields).
        """
        with self._lock:
            folder = self._folder(scan_id)
            m = self._read(folder)
            e = _entry(m, image_id)
            if (e.get("inference") or {}).get("status") in ("positive", "negative"):
                return e                                   # already done: no duplicate results
            if not e.get("image_path"):
                raise ScanError(f"image {image_id} has no file")
            src = folder / e["image_path"]
            info = {"engine": result.get("engine"), "latency_ms": result.get("latencyMs"),
                    "objects": result.get("objects", []), "updated_at": _now()}
            if relevant:
                (folder / "detections").mkdir(exist_ok=True)
                dst = folder / "detections" / src.name
                os.replace(src, dst)                       # same filesystem: atomic, no second copy
                e["image_path"] = f"detections/{src.name}"
                records = []
                for k, obj in enumerate(relevant):
                    records.append({
                        "detection_id": f"{image_id}-{k}",
                        "scan_id": scan_id, "image_id": image_id, "sequence_number": e["sequence_number"],
                        "timestamp": e.get("timestamp"), "image_path": e["image_path"],
                        "category": obj.get("category"), "label": obj.get("label"),
                        "confidence": obj.get("confidence"),
                        "bbox": obj.get("bbox"),           # normalized x, y, w, h; origin top-left
                        "bbox_pixels": _bbox_pixels(obj.get("bbox"), e.get("camera")),
                        "world_position": ground_position(e, obj.get("bbox")),
                        "drone_position": e.get("drone_position"), "camera_position": e.get("camera_position"),
                        "camera_orientation": e.get("camera_orientation"), "camera": e.get("camera"),
                        "ground_footprint": e.get("ground_footprint"), "engine": result.get("engine"),
                        "drone_id": e.get("drone_id"), "floor": e.get("floor"),     # Rescue Mission frames
                    })
                path = folder / "detections" / "detections.json"
                existing = json.loads(path.read_text(encoding="utf-8")) if path.exists() else []
                existing.extend(records)
                existing.sort(key=lambda r: (r["sequence_number"], r["detection_id"]))
                _write_json(path, existing)
                e["inference"] = dict(info, status="positive", detections=len(records))
            else:
                if not keep_negatives:
                    try:
                        src.unlink()
                    except FileNotFoundError:
                        pass
                    e["image_path"] = None
                    e["image_discarded"] = True
                e["inference"] = dict(info, status="negative")
            _write_json(folder / "manifest.json", m)
            return e

    # ---------- helpers ----------

    def _folder(self, scan_id: str) -> Path:
        if not isinstance(scan_id, str) or not SCAN_ID_RE.match(scan_id):
            raise ScanError(f"bad scan id: {scan_id!r}")
        folder = self.root / scan_id
        if not (folder / "manifest.json").is_file():
            raise ScanError(f"unknown scan: {scan_id}")
        return folder

    @staticmethod
    def _read(folder: Path) -> Dict:
        with open(folder / "manifest.json", encoding="utf-8") as f:
            return json.load(f)


def _entry(manifest: Dict, image_id: str) -> Dict:
    for e in manifest["images"]:
        if e["image_id"] == image_id:
            return e
    raise ScanError(f"no image {image_id}")


def _inference_counts(manifest: Dict) -> Dict:
    counts = {"queued": 0, "running": 0, "positive": 0, "negative": 0, "failed": 0, "not_sent": 0, "skipped_altitude": 0}
    for e in manifest["images"]:
        if e["capture_status"] != "saved":
            continue
        st = (e.get("inference") or {}).get("status", "not_sent")
        counts[st] = counts.get(st, 0) + 1
    counts["analyzed"] = counts["positive"] + counts["negative"]
    return counts


def sightings(dets: List[Dict], manifest: Dict, radius: float = 0.75) -> List[Dict]:
    """Groups detections whose estimated ground positions are within `radius` m: one group per object on the ground.
    For each, `photos_covering` counts the analyzed photos whose footprint contained that spot, so a real object
    (found in most photos that saw it) stands out from a one-off false alarm. Best first."""
    groups = []
    for d in sorted(dets, key=lambda r: -(r.get("confidence") or 0)):
        w = d.get("world_position") or {}
        if not w.get("available"):
            groups.append({"members": [d], "x": None, "z": None})
            continue
        g = next((g for g in groups if g["x"] is not None and (g["x"] - w["x"]) ** 2 + (g["z"] - w["z"]) ** 2 <= radius ** 2
                  and g.get("floor") == w.get("floor")), None)            # floor: Rescue Mission (None for scans)
        if g is None:
            groups.append({"members": [d], "x": w["x"], "z": w["z"], "floor": w.get("floor")})
        else:
            g["members"].append(d)
    analyzed = [e for e in manifest["images"] if (e.get("inference") or {}).get("status") in ("positive", "negative")]
    out = []
    for g in groups:
        ms = g["members"]
        if g["x"] is not None:
            xs = [m["world_position"]["x"] for m in ms]; zs = [m["world_position"]["z"] for m in ms]
            x, z = sum(xs) / len(xs), sum(zs) / len(zs)
            # A photo that detected it covered it, even if the averaged position falls just outside its footprint.
            detected_in = {m["image_id"] for m in ms}
            covering = len(detected_in | {e["image_id"] for e in analyzed if _covers(e, x, z)})
        else:
            x = z = None
            covering = None
        best = max(ms, key=lambda m: m.get("confidence") or 0)
        out.append({
            "category": best.get("category"), "best_confidence": best.get("confidence"),
            "world_position": {"x": round(x, 3), "z": round(z, 3), "approximate": True} if x is not None else None,
            "floor": g.get("floor"),
            "photos_detected": len({m["image_id"] for m in ms}), "photos_covering": covering,
            "best_image_id": best["image_id"], "best_detection_id": best["detection_id"],
            "detection_ids": [m["detection_id"] for m in ms],
        })
    out.sort(key=lambda s: (-(s["photos_detected"] / max(1, s["photos_covering"] or 1)), -(s["best_confidence"] or 0)))
    return out


def _covers(entry: Dict, x: float, z: float) -> bool:
    fp = entry.get("ground_footprint") or {}
    if not fp.get("center") or not fp.get("size"):
        return False
    return abs(fp["center"][0] - x) <= fp["size"][0] / 2 and abs(fp["center"][1] - z) <= fp["size"][1] / 2


def _bbox_pixels(bbox, camera):
    if not bbox or not camera:
        return None
    W, H = camera.get("width"), camera.get("height")
    if not W or not H:
        return None
    return {"x": round(bbox["x"] * W, 1), "y": round(bbox["y"] * H, 1), "w": round(bbox["w"] * W, 1), "h": round(bbox["h"] * H, 1)}


def ground_position(entry: Dict, bbox: Optional[Dict]) -> Dict:
    """Estimated world position of a box's center on the ground.

    The scan camera looks straight down (gimbal), image top = world +Z, image right = world +X, and the image covers
    exactly `ground_footprint` (computed by Unity from the camera's height above the ground plane and its FOV). So a
    normalized pixel (u, v) (origin top-left) lies at
        x = cx + (u - 0.5) * size_x,   z = cz - (v - 0.5) * size_z,   y = ground_y.
    Exact for an object on flat ground at ground_y; errors come from the box center not being the object's center,
    ground height differences (sidewalks are 8 cm higher), and camera tilt (none: gimbal). Never the drone position.
    """
    fp = (entry or {}).get("ground_footprint") or {}
    cam = (entry or {}).get("camera_position")
    if not bbox or not fp.get("center") or not fp.get("size") or not cam:
        return {"available": False, "reason": "missing box or footprint"}
    u = bbox["x"] + bbox["w"] / 2.0
    v = bbox["y"] + bbox["h"] / 2.0
    cx, cz = fp["center"]
    sx, sz = fp["size"]
    altitude = fp.get("altitude")
    ground_y = cam[1] - altitude if altitude is not None else None
    W = ((entry.get("camera") or {}).get("width")) or 640
    pixel_m = sx / W
    return {
        "available": True, "approximate": True,
        "x": round(cx + (u - 0.5) * sx, 3), "y": round(ground_y, 3) if ground_y is not None else None,
        "z": round(cz - (v - 0.5) * sz, 3),
        "frame": "Unity world meters (x east, y up, z north)",
        "method": "box center projected onto the flat ground plane through the straight-down camera",
        "pixel_size_m": round(pixel_m, 4),
        "error_estimate_m": round(max(0.1, 0.5 * max(bbox["w"] * sx, bbox["h"] * sz)), 2),
    }


def decode_image_payload(payload: Dict) -> bytes:
    """The scanner sends {"image_b64": "...", <metadata>}; returns the image bytes."""
    try:
        return base64.b64decode(payload["image_b64"], validate=True)
    except (KeyError, ValueError, TypeError) as e:
        raise ScanError(f"missing or invalid image_b64: {e}")
