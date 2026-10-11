# image_store

Saves drone camera frames per search session and indexes them, with their detection results, in SQLite.
It's a plain Python library with no server and no model inference, meant to be imported by the main backend.
It uses the standard library only. Pillow is optional and enables near-duplicate skipping.

## Use

```python
from image_store import ImageStore

store = ImageStore("drone_images")            # creates drone_images/index.sqlite3 and drone_images/sessions/

rec = store.save_image(jpeg_or_png_bytes, session_id="search-001", target="phone",
                       position=(x, y, z), altitude=2.44)    # position/altitude optional
store.attach_detection(rec.image_id, result)   # e.g. the JSON from VisionServer /detect
# A duplicate returns the earlier image's record; attaching still adds a new detection (nothing is overwritten).

store.get_image(image_id)          # ImageRecord, or None
store.image_path(image_id)         # absolute Path of the file
store.get_detection(image_id)      # latest DetectionRecord, or None
store.list_detections(image_id)    # every DetectionRecord for the image, oldest first
store.list_images("search-001", detected_only=False)   # True: images with any positive detection
store.list_sessions()              # [{session_id, images, started, last}]
store.export_session("search-001", url_prefix="/images/")   # JSON-ready list for the website
```

`ImageStore` is safe to share across threads in a single process.

## Stored data

```
drone_images/
  index.sqlite3
  sessions/<session_id>/<image_id>.jpg|png
```

| Table | Fields |
|---|---|
| `images` | `image_id` (UUID hex), `session_id`, `filename`, `rel_path`, `captured_at` (UTC ISO-8601), `target`, `pos_x/y/z`, `altitude`, `format`, `width`, `height`, `size_bytes`, `sha256`, `dhash` |
| `detections` | `image_id`, `created_at`, `detected`, `category`, `confidence`, `bbox_x/y/w/h` (normalized, origin top-left), `engine`, `raw_json` (the full result) |

An image can have several detections, for example when a duplicate frame is checked again.
`get_detection` returns the newest one and `list_detections` returns all of them.
`list_images(detected_only=True)` includes an image if any of its detections was positive, so a later negative re-check never hides a sighting.

## Duplicate frames

`save_image` skips a frame, and returns the existing record with `duplicate=True`, when either:
- it is byte-identical to an earlier frame of the session, or
- it is nearly identical (64-bit difference hash within 2 bits) to the previous frame and the drone moved less than 5 cm, i.e. it is hovering.

Frames taken at a different position are always kept, even if they look the same, so the search coverage log stays complete.
Frames without a position are skipped only when byte-identical, because a coarse hash can miss a small target appearing.
Tune this with `ImageStore(root, near_duplicate_bits=2, min_move=0.05)`, or pass `near_duplicate_bits=None` to turn near-duplicate skipping off.

## Showing images on the website

The backend serves the `drone_images/` folder as static files (for example at `/images/`) and returns `export_session(..., url_prefix="/images/")` as JSON.
Each entry includes a `url` for an `<img>` tag, the metadata, and `detection` with `bbox` for drawing a box over the image.

## Git

The root `.gitignore` excludes `drone_images/`, `*.sqlite3` (and its WAL files), `*.db`, `*.pt` and `*.onnx`.

## Test

```bash
python3 -m unittest image_store.test_image_store -v     # from the repo root
```

The tests cover:
- saving an image, then reading its metadata, bytes and detection back by ID from a new Python process (a restart)
- duplicate and hover skipping
- listing, filtering and the website export
- rejecting bad input
