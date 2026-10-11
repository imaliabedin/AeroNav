# Area-scan images: storage format and AI results

The website's area scan (`city.html` → Unity `ScanMission`) sends every image to `scan_server.py`, which stores it
here and queues it for the AI model (`VisionServer/server.py`, engine `items`: YOLO11n trained on this simulator for
wallet, keys, phone, purse). One background worker sends each image to `POST /detect?target=all&conf=0.25`:

| Model answer for the scan's target class | What happens |
|---|---|
| one or more objects ≥ conf | image moves (rename, no copy) to `detections/`; one record per object in `detections/detections.json` |
| none | image file deleted; its metadata stays in the manifest (`inference.status = "negative"`, `image_discarded`) |
| model unreachable / error / timeout (3 tries) | image kept in `images/`, `inference.status = "failed"`; never counted as negative. `POST /api/scans/<id>/retry` re-queues |
| camera outside 2.5–4 m | not sent (`skipped_altitude`); Unity doesn't take such photos anyway |

`scan_server.py --keep-negatives` keeps negative images; `--conf` changes the threshold; `--no-ai` stores only.

## Where images are

```
image_store/scans/                       (git-ignored; change with: python3 scan_server.py --root DIR)
  <scan_id>/                             scan-YYYYMMDD-HHMMSS-xxxxxx (UTC, unique, never reused)
    manifest.json                        the scan record + one entry per capture
    images/<seq>_<image_id>.jpg          640 × 640 JPEG, straight down, image top = world +Z; while waiting for
                                         the model, or if inference failed
    detections/<seq>_<image_id>.jpg      images where the model found the target
    detections/detections.json           one record per detected object (below)
    ground_truth.json                    true object position, EVALUATION ONLY: never give this to the model
```

Files are never overwritten (exclusive create), and manifest writes are atomic.

## Reading images (Python, no model dependencies)

```python
from image_store.scans import ScanStore
store = ScanStore("image_store/scans")

store.list_scans()                         # newest first: scan_id, status, target_category, counts, times
m = store.manifest(scan_id)                # whole record (region, config, route, summary, images)
for img in store.images(scan_id):          # saved images in capture order; img["path"] = absolute file path
    ...
for batch in store.iter_batches(scan_id, 16):   # batch_size=1 for one at a time
    ...
store.read_image(scan_id, image_id)        # bytes
store.detections(scan_id)   # {"inference": counts, "sightings": [...], "detections": [...]}
```

Over HTTP (while `scan_server.py` runs): `GET /api/scans`, `GET /api/scans/<scan_id>`,
`GET /api/scans/<scan_id>/detections`, `GET /api/scans/<scan_id>/image/<image_id>`, `GET /api/vision` (model reachable?).

## Detection records (`detections/detections.json`)

`detection_id` (`<image_id>-<k>`), `scan_id`, `image_id`, `sequence_number`, `timestamp`, `image_path`, `category`,
`label`, `confidence`, `bbox` (normalized x, y, w, h, origin top-left), `bbox_pixels`, `world_position`,
`drone_position`, `camera_position`, `camera_orientation`, `camera`, `ground_footprint`, `engine`.

`world_position` is an **estimate**: the box center projected through the straight-down camera onto the flat ground
plane (`x = cx + (u − 0.5)·size_x`, `z = cz − (v − 0.5)·size_z`), with `approximate: true` and `error_estimate_m`.
Measured against the true hidden positions in 5 browser scans: 0–8 cm. It is never the drone's position.

`sightings` (from the API) groups detections within 0.75 m into one object on the ground, with `photos_detected` and
`photos_covering` (analyzed photos whose footprint contained that spot). A real object is found in most photos that
covered it; a one-off false alarm is not.

## Coordinates

Unity world meters: **x east, y up, z north**. Ground plane y = `config.ground_y` (−0.5). `[x, z]` pairs are ground
positions; `[x, y, z]` triples are 3-D. Grid cells are 1 m, centered on integer x, z.

To turn a pixel `(px, py)` (origin top-left) into a ground position, the camera looks straight down and the image top
is +z, so: `x = fp.center[0] + (px / width − 0.5) · fp.size[0]`, `z = fp.center[1] − (py / height − 0.5) · fp.size[1]`
where `fp = entry["ground_footprint"]`. Assumes flat ground.

## manifest.json

| Field | Meaning |
|---|---|
| `format_version` | 1 |
| `scan_id`, `scan_region_id` | identifiers |
| `status` | `running`, `complete` (all open ground seen clearly), `incomplete`, `stopped` |
| `started_at`, `ended_at` | UTC ISO-8601 |
| `target_category` | `wallet`, `keys`, `phone` or `purse` (what the user is looking for) |
| `region` | `corners` (ordered counter-clockwise from the corner with the smallest x+z), `area_m2`, `cells` |
| `config` | `altitude_m`, `overlap`, `vertical_fov_deg`, `aspect`, speeds, `max_looks`, `ground_y`, assumptions |
| `route` | `footprint_m`, `lane_spacing_m`, `capture_spacing_max_m`, `lanes` (a, b, number of captures) |
| `summary` | at the end: `status`, counts, `coverage`, `not_clearly_seen` (cells by reason, see below) |
| `counts` | `saved`, `failed`, `duplicates` |
| `images` | entries below |

Image entry:

| Field | Meaning |
|---|---|
| `scan_id`, `image_id`, `scan_region_id`, `sequence_number` | identifiers (sequence = capture order) |
| `timestamp` | capture time (UTC, from the simulation); `received_at`: when stored; `sim_time`: seconds into the mission |
| `image_path` | relative to the scan folder; `format`, `size_bytes`, `sha256` |
| `capture_status` | `saved` or `failed` (failed entries have `error` and no file) |
| `capture_reason` | `lane` (planned lawnmower position) or `revisit` (another look at blocked/missed ground) |
| `lane_index`, `revisit_round` | where in the route |
| `drone_position`, `camera_position` | `[x, y, z]` at the moment of capture |
| `camera_orientation` | `euler_deg`, `forward` (≈ [0, −1, 0]), `image_up` (≈ [0, 0, 1]) |
| `ground_footprint` | `center` [x, z], `size` [x extent, z extent] m, `altitude` m, `corners` [[x, z] × 4] |
| `planned_waypoint` | [x, z] the planner wanted (the actual position is `camera_position`) |
| `actual_speed` | m/s at capture |
| `occlusion_status` | `confirmed` (a moving car/person hid some ground), `suspected` (ground next to one), `unobstructed` |
| `occluded_regions` | `confirmed_cells`, `suspected_cells` ([x, z] lists), `hidden_static_cells` (count, under buildings/trees) |
| `revisit_required` | true if this image left road ground that needs another look |
| `camera` | `vertical_fov_deg`, `width`, `height`, `format`, `jpeg_quality`, mount description |

`summary.not_clearly_seen` lists 1 m cells (`[x, z]`) by reason: `occluded_moving` (traffic never moved away within
`max_looks` looks), `suspected`, `unseen` (never inside a saved image: e.g. failed saves), `hidden_static`
(under a roof, tree or parked car: not visible from above). A negative result from the model only covers the cells
that were seen clearly.

## Model input

Only the JPEG goes to the model. `ground_truth.json` is never read by the pipeline; it is for scoring afterwards.
