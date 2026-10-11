# Vision server (Layer 1 perception)

**Trained detector:** when `models/drone_items.pt` exists the server uses it by default (`--engine items`):
a YOLO11n model trained on this simulation for **wallet, keys, phone, purse** (targets `wallet`, `key`, `phone`,
`purse`, or `all`). How it was made and how well it does: [train/README.md](train/README.md).
`--engine yolo-world` keeps the original open-vocabulary detector.

Unity's `TargetDetector` sends JPEG frames from the drone's downward camera (`DroneCamera`)
to this server, which runs a real image detector and answers whether the selected target
(`key` or `phone`) is in the frame.

```
Unity (DroneCamera → JPEG) ──POST /detect?target=key&conf=0.3──▶ server.py ──▶ YOLO-World (local)
                          ◀── {"detected", "category", "confidence", "hasBox", "bbox", ...} ──
```

## Setup (once, ~5 min, ~400 MB of downloads)

```bash
cd VisionServer
python3 -m venv --system-site-packages .venv
.venv/bin/pip install -r requirements.txt
.venv/bin/yolo settings weights_dir="$PWD/models" runs_dir="$PWD/models/runs" sync=False
```

On first start the server downloads `models/yolov8s-worldv2.pt` (26 MB) and the CLIP ViT-B/32
text encoder (~340 MB, into `models/clip/`). The CLIP download is slow (~0.7 MB/s here), so do it before the demo.

## Run

```bash
.venv/bin/python server.py                 # http://127.0.0.1:8765, engine yolo-world
curl http://127.0.0.1:8765/health
curl --data-binary @test_images/a244_phone04_center.jpg -H "Content-Type: image/jpeg" \
     "http://127.0.0.1:8765/detect?target=phone&conf=0.3"
```

Then press Play in Unity (scene `ParkingLot`), choose the target with **T** in Build mode, and press **Enter**.

## Response

| field | meaning |
|---|---|
| `detected` | best candidate's confidence ≥ `conf` |
| `category` | `key` or `phone` |
| `confidence` | best candidate's score, 0–1 |
| `hasBox`, `bbox` | normalized box `x, y, w, h`, origin at the image's top-left |
| `latencyMs` | server-side time for this frame |
| `error` | empty on success |
| `candidates` | top 5 boxes, for debugging |

## Image storage

Every frame Unity sends during a flight is saved, together with its detection result, using the
[`image_store`](../image_store/README.md) library. Storage is on by default.

**How it works**
1. Each time the drone is launched (Enter), Unity's `TargetDetector` creates a new session ID, e.g. `run-20261010-153012-a3f9c1`.
2. With every frame it adds `session`, the drone's Unity world position (`x`, `y`, `z`), the camera `alt`itude in meters, and `capturedAt` (UTC) to the `/detect` request.
3. The server runs detection as usual, then saves the image and appends the detection to it.
   The response gets extra fields: `imageId`, `duplicate`, `sessionId`, `capturedAt`, `receivedAt` (when the server got it) and `storageError`.
4. Duplicate frames (identical, or hovering in place) are not saved twice. The response carries the earlier image's `imageId` with `duplicate: true`,
   and the new detection is added to that image's history. Nothing is overwritten.

Requests without `session` (curl, `evaluate.py`, the editor end-to-end test) behave exactly as before and are not stored.
Storage problems never block detection: the response still comes back, `storageError` explains what went wrong,
and the server prints `[storage] ERROR ...`.

**Where files go**

```
drone_images/                       at the repo root (kept out of Git by .gitignore)
  index.sqlite3                     metadata: images, positions, times, and every detection result
  sessions/<session_id>/<image_id>.jpg
```

**Turning it off or moving it**

| How | Effect |
|---|---|
| `server.py --no-store` or `VISION_STORE=0` | no frames saved |
| `server.py --store-dir /some/dir` or `VISION_STORE_DIR=/some/dir` | save somewhere else |
| Unity: untick **Store Frames** on the Drone's `TargetDetector` (Play mode, Inspector) | Unity stops asking the server to save frames |

**Looking at saved sessions** (from the repo root)

```bash
python3 -c "from image_store import ImageStore; print(ImageStore('drone_images').list_sessions())"

python3 - <<'EOF'
from image_store import ImageStore
s = ImageStore("drone_images")
session = s.list_sessions()[-1]["session_id"]           # newest session
for img in s.list_images(session):
    d = s.get_detection(img.image_id)
    print(img.captured_at, img.position, img.altitude, s.image_path(img.image_id),
          "detected" if d and d.detected else "-", len(s.list_detections(img.image_id)), "check(s)")
print("images with a sighting:", [i.image_id for i in s.list_images(session, detected_only=True)])
EOF
```

The images are ordinary JPEG files, so you can also open the `sessions/<session_id>/` folder in Finder or Explorer.

## Gemini engine (optional, untested)

```bash
export GEMINI_API_KEY=...        # only in this shell; never in Unity, the repo or a WebGL build
.venv/bin/python server.py --engine gemini
```

The key stays on the machine running the server. Unity only ever talks to this server.

## Tests

`test_images/` holds real frames from `DroneCamera`, made by Unity menu **Drone Sim → Vision → Capture Test Images**
(or `Unity -batchmode -projectPath Unity -executeMethod VisionTestCapture.RunBatch`).
`.venv/bin/python evaluate.py` scores every frame for both targets.

Storage tests (no model needed): `python3 -m unittest test_server_storage -v` here, and
`python3 -m unittest image_store.test_image_store -v` from the repo root.
With the server running, `Unity -batchmode -projectPath Unity -executeMethod VisionStorageE2E.RunBatch`
sends a real camera frame from Unity and checks that it is stored.
