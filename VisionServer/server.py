"""Vision backend for the drone sim's downward camera.

Unity POSTs a JPEG/PNG frame to /detect?target=key&conf=0.3 and gets back JSON:

    {"detected": true, "category": "key", "confidence": 0.71,
     "hasBox": true, "bbox": {"x": 0.42, "y": 0.38, "w": 0.06, "h": 0.05},   # normalized, top-left origin
     "engine": "yolo-world", "latencyMs": 84.0, "error": "", "candidates": [...]}

Engines:
  yolo-world (default)  local open-vocabulary detector, no API key, runs on CPU/MPS
  gemini                Google Gemini vision API; key read from GEMINI_API_KEY on this
                        machine only, never sent to or stored in the Unity client

Frame storage (optional, per request): add &session=<id>&x=&y=&z=&alt=&capturedAt=<ISO-8601>
and the frame plus its detection are saved with image_store; the response then also has
"imageId", "duplicate", "sessionId", "capturedAt", "receivedAt" and "storageError".
Without `session` the response is unchanged. Storage never blocks the detection answer.

Run:  .venv/bin/python server.py [--engine yolo-world|gemini] [--port 8765] [--store-dir DIR | --no-store]
"""

import argparse
import base64
import io
import json
import math
import os
import sys
import threading
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
if REPO not in sys.path:
    sys.path.insert(0, REPO)  # image_store lives at the repo root

# Text prompts per target. Several phrasings per category; the best-scoring one wins.
PROMPTS = {
    "key": ["key", "keys", "car key", "metal key"],
    "phone": ["cell phone", "smartphone", "mobile phone"],
}


class YoloWorldEngine:
    name = "yolo-world"

    def __init__(self, weights, imgsz):
        from ultralytics import YOLOWorld

        self.imgsz = imgsz
        self.model = YOLOWorld(weights)
        self.labels, self.label_category = [], []
        for category, prompts in PROMPTS.items():
            for p in prompts:
                self.labels.append(p)
                self.label_category.append(category)
        self.model.set_classes(self.labels)
        self.lock = threading.Lock()  # the model is not thread-safe

    def detect(self, image, target):
        """All candidate boxes for `target`, best first: [(confidence, (x, y, w, h) normalized, label)]."""
        with self.lock:
            r = self.model.predict(image, imgsz=self.imgsz, conf=0.01, iou=0.5, verbose=False)[0]
        W, H = image.size
        out = []
        for (x1, y1, x2, y2), c, k in zip(r.boxes.xyxy.tolist(), r.boxes.conf.tolist(), r.boxes.cls.tolist()):
            if self.label_category[int(k)] != target:
                continue
            out.append((c, (x1 / W, y1 / H, (x2 - x1) / W, (y2 - y1) / H), self.labels[int(k)]))
        out.sort(key=lambda d: -d[0])
        return out


class GeminiEngine:
    name = "gemini"

    def __init__(self, model):
        self.key = os.environ.get("GEMINI_API_KEY", "")
        if not self.key:
            raise SystemExit("GEMINI_API_KEY is not set (export it in this shell; never put it in Unity).")
        self.url = f"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent"

    def detect(self, image, target):
        buf = io.BytesIO()
        image.save(buf, format="JPEG", quality=90)
        prompt = (
            f"This is a top-down photo from a drone camera about 2.4 m above a parking lot. "
            f"Is there a {target} visible? Answer only with JSON: "
            '{"found": bool, "confidence": number 0-1, "box_2d": [ymin, xmin, ymax, xmax] normalized 0-1000 or []}'
        )
        body = {
            "contents": [{"parts": [
                {"inline_data": {"mime_type": "image/jpeg", "data": base64.b64encode(buf.getvalue()).decode()}},
                {"text": prompt},
            ]}],
            "generationConfig": {"responseMimeType": "application/json", "temperature": 0},
        }
        req = urllib.request.Request(self.url, json.dumps(body).encode(),
                                     {"Content-Type": "application/json", "x-goog-api-key": self.key})
        with urllib.request.urlopen(req, timeout=20) as resp:
            data = json.load(resp)
        ans = json.loads(data["candidates"][0]["content"]["parts"][0]["text"])
        if not ans.get("found"):
            return []
        conf = float(ans.get("confidence", 0.5))
        box = ans.get("box_2d") or []
        if len(box) == 4:
            y0, x0, y1, x1 = (v / 1000.0 for v in box)
            return [(conf, (x0, y0, x1 - x0, y1 - y0), target)]
        return [(conf, None, target)]


def run_detection(engine, image_bytes, target, conf):
    t0 = time.perf_counter()
    result = {"detected": False, "category": target, "confidence": 0.0, "hasBox": False,
              "bbox": {"x": 0, "y": 0, "w": 0, "h": 0}, "engine": engine.name, "latencyMs": 0.0,
              "error": "", "candidates": []}
    try:
        if target not in PROMPTS:
            raise ValueError(f"unknown target '{target}', expected one of {sorted(PROMPTS)}")
        image = Image.open(io.BytesIO(image_bytes)).convert("RGB")
        cands = engine.detect(image, target)
        result["candidates"] = [{"confidence": round(c, 4), "label": lbl,
                                 "bbox": dict(zip("xywh", (round(v, 4) for v in b))) if b else None}
                                for c, b, lbl in cands[:5]]
        if cands:
            c, b, _ = cands[0]
            result["confidence"] = round(c, 4)
            result["detected"] = c >= conf
            if b:
                result["hasBox"] = True
                result["bbox"] = dict(zip("xywh", (round(v, 4) for v in b)))
    except Exception as e:  # report, don't crash the server
        result["error"] = f"{type(e).__name__}: {e}"
    result["latencyMs"] = round((time.perf_counter() - t0) * 1000, 1)
    return result


# ---------- frame storage ----------

def log_storage_error(msg):
    print(f"[storage] ERROR {msg}", file=sys.stderr, flush=True)


def _utc_iso(dt):
    return dt.astimezone(timezone.utc).isoformat(timespec="milliseconds")


def _parse_time(text):
    """ISO-8601 with an offset or 'Z' -> UTC ISO string (ms). Raises ValueError."""
    dt = datetime.fromisoformat(text.strip().replace("Z", "+00:00"))
    if dt.tzinfo is None:
        raise ValueError(f"capturedAt '{text}' has no UTC offset")
    return _utc_iso(dt)


def _num(q, key):
    if key not in q:
        return None
    v = float(q[key][0])
    if not math.isfinite(v):
        raise ValueError(f"{key} is not finite")
    return v


def store_frame(store, data, q, result, received_at):
    """Saves the frame and appends `result` as its detection. Returns the fields to add to the
    response. Never raises: any failure is logged and reported as `storageError`.

    Policy: detections are append-only. A duplicate frame reuses the earlier image's ID and the
    new detection is appended to it (with this frame's own capture metadata in its record);
    nothing is overwritten or dropped."""
    session = q["session"][0]
    out = {"imageId": "", "duplicate": False, "sessionId": session,
           "capturedAt": "", "receivedAt": received_at, "storageError": ""}
    try:
        if store is None:
            raise RuntimeError("image store unavailable (see server start-up log)")
        x, y, z = (_num(q, k) for k in ("x", "y", "z"))
        if (x is None) != (y is None) or (y is None) != (z is None):
            raise ValueError("position needs all of x, y, z")
        position = None if x is None else (x, y, z)
        altitude = _num(q, "alt")
        captured_at = _parse_time(q["capturedAt"][0]) if "capturedAt" in q else received_at
        out["capturedAt"] = captured_at

        rec = store.save_image(data, session, target=result.get("category"),
                               position=position, altitude=altitude, captured_at=captured_at)
        out["imageId"], out["duplicate"] = rec.image_id, rec.duplicate
        frame = {"sessionId": session, "capturedAt": captured_at, "receivedAt": received_at,
                 "position": list(position) if position else None, "altitude": altitude,
                 "duplicateOf": rec.image_id if rec.duplicate else None}
        store.attach_detection(rec.image_id, dict(result, frame=frame))
    except Exception as e:
        out["storageError"] = f"{type(e).__name__}: {e}"
        log_storage_error(f"session={session} image={out['imageId'] or '-'} {out['storageError']}")
    return out


def open_store(args):
    """The ImageStore, or None. A store that fails to open is logged; detection keeps working."""
    if args.no_store:
        print("Frame storage: disabled (--no-store)", flush=True)
        return None, False
    try:
        from image_store import ImageStore
        store = ImageStore(args.store_dir)
        print(f"Frame storage: {store.root}", flush=True)
        return store, True
    except Exception as e:
        log_storage_error(f"could not open image store at {args.store_dir}: {type(e).__name__}: {e}")
        return None, True


def make_handler(engine, store=None, storage_enabled=False):
    class Handler(BaseHTTPRequestHandler):
        def _send(self, code, obj):
            body = json.dumps(obj).encode()
            self.send_response(code)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Access-Control-Allow-Origin", "*")  # WebGL builds run on another origin
            self.end_headers()
            self.wfile.write(body)

        def do_OPTIONS(self):
            self.send_response(204)
            self.send_header("Access-Control-Allow-Origin", "*")
            self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
            self.send_header("Access-Control-Allow-Headers", "Content-Type")
            self.end_headers()

        def do_GET(self):
            if urlparse(self.path).path == "/health":
                self._send(200, {"ok": True, "engine": engine.name, "targets": sorted(PROMPTS)})
            else:
                self._send(404, {"error": "not found"})

        def do_POST(self):
            url = urlparse(self.path)
            if url.path != "/detect":
                return self._send(404, {"error": "not found"})
            q = parse_qs(url.query)
            target = q.get("target", ["key"])[0].lower()
            conf = float(q.get("conf", ["0.3"])[0])
            data = self.rfile.read(int(self.headers.get("Content-Length", 0)))
            received_at = _utc_iso(datetime.now(timezone.utc))
            res = run_detection(engine, data, target, conf)
            if storage_enabled and q.get("session", [""])[0]:
                res.update(store_frame(store, data, q, res, received_at))
            print(f"[detect] {target:5s} detected={res['detected']!s:5s} conf={res['confidence']:.2f} "
                  f"{res['latencyMs']:.0f} ms {res['error']}"
                  + (f" image={res['imageId']}{' (dup)' if res['duplicate'] else ''}" if res.get("imageId") else ""),
                  flush=True)
            self._send(200, res)

        def log_message(self, *args):
            pass

    return Handler


def build_engine(args):
    if args.engine == "gemini":
        return GeminiEngine(args.gemini_model)
    return YoloWorldEngine(args.weights, args.imgsz)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--engine", choices=["yolo-world", "gemini"], default="yolo-world")
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=8765)
    ap.add_argument("--weights", default=os.path.join(HERE, "models", "yolov8s-worldv2.pt"))
    ap.add_argument("--imgsz", type=int, default=640)
    ap.add_argument("--gemini-model", default=os.environ.get("GEMINI_MODEL", "gemini-2.5-flash"))
    ap.add_argument("--store-dir", default=os.environ.get("VISION_STORE_DIR", os.path.join(REPO, "drone_images")))
    ap.add_argument("--no-store", action="store_true", default=os.environ.get("VISION_STORE", "1") == "0",
                    help="don't save frames (also VISION_STORE=0)")
    args = ap.parse_args()

    engine = build_engine(args)
    store, storage_enabled = open_store(args)
    print(f"Vision server: engine={engine.name} on http://{args.host}:{args.port}  (POST /detect, GET /health)", flush=True)
    ThreadingHTTPServer((args.host, args.port), make_handler(engine, store, storage_enabled)).serve_forever()


if __name__ == "__main__":
    main()
