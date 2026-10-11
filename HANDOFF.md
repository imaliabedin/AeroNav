# Handoff: area search website, scanning and image storage

Status as of 2026-10-11. The AI model from DroneScanner_AI.zip is integrated (see "AI" below).

## Run

```bash
python3 scan_server.py        # serves the site + stores images; open http://localhost:8000 → Launch Drone → Object Finder
```

Flow: tutorial popup → click 4 ground corners (green = valid, blue = planned lanes) → pick Wallet/Keys/Phone/Purse →
Start scan → progress panel → result (complete / incomplete / stopped).
Automated: `city.html?autoscan=6,-18;34,-18;34,-1;6,-1&object=Phone`.

## What was built

| Piece | File |
|---|---|
| Region rules, footprint math, lawnmower planner (rectangles, trapezoids, any convex quad) | `Unity/Assets/Scripts/Scan/ScanPlanner.cs` |
| Per-cell coverage: seen / blocked by moving traffic (confirmed) / suspected / hidden under static objects / unseen | `Unity/Assets/Scripts/Scan/ScanCoverage.cs` |
| Corner selection + highlight, object placement, flight, distance-based capture, revisits, upload, page events | `Unity/Assets/Scripts/Scan/ScanMission.cs` |
| Unity → page events (`window` "scan" CustomEvent) | `Unity/Assets/Plugins/WebGL/ScanBridge.jslib` |
| Hooks: attaches `ScanControls`; sonar tools hand clicks back; Stop ends a scan | `Unity/Assets/Scripts/WebAutopilot.cs` |
| `CityLife.IsRoad` / `IsMover` helpers (street layout, moving cars and pedestrians) | `Unity/Assets/Scripts/CityLife.cs` |
| Website UI (old sonar tools kept under "Sonar demo tools") | `city.html` |
| Image server + storage + read API for the model | `scan_server.py`, `image_store/scans.py` |
| Storage format and AI contract | `image_store/SCANS.md` |
| Tests | `image_store/test_scans.py`, `Unity/Assets/Scripts/Editor/ScanTests.cs` |

Key decisions:
- **Footprint-based spacing.** The footprint is 2·h·tan(FOV/2): 6.9 m square at the default 6 m altitude with 60° FOV.
  Lanes and captures are spaced at footprint × (1 − overlap, default 15%), less a 0.25 m safety margin on each side.
  Lane end points come from the polygon's extent over each lane's whole strip.
- **Capture by position, not time.** An image is taken when the actual position passes the next planned point along the lane.
  The test gives the same image count at 2 and 10 m/s, with no gaps. Coverage is computed from where each image was actually taken.
- **Gimbal.** The camera points straight down before each capture, with image top = +Z. A render test checks the JPEG is not flipped.
- **Occlusion from scene geometry.** At capture time, a ray goes from the camera to each 1 m cell. If it hits a `Traffic …` car or
  `Pedestrian`, the cell is *confirmed* blocked. A clear ray within 1 m of a moving car or person counts as *suspected*.
  If the ray hits anything else above 0.6 m, the cell is under a static object (building, tree, parked car).
  After the lanes, unseen cells (gaps, failed saves), confirmed cells and suspected road cells are revisited after a 1.5 s pause.
  Each cell gets at most `maxLooks` (3) revisits; anything left after that is reported as unresolved and the scan is stored `incomplete`.
- **Coverage only counts when the image is saved.** If an upload fails, its cells stay unseen and are revisited.
- **Altitude.** The drone holds the scan altitude and climbs over static obstacles under or just ahead of it (pausing horizontal motion while it climbs).
- The hidden object's true position is sent only to `ground_truth.json`, which is never in the manifest and never served over HTTP.

## Tests (all passing)

```bash
python3 -m unittest image_store.test_scans -v
U=/Applications/Unity/Hub/Editor/6000.6.4f1/Unity.app/Contents/MacOS/Unity     # Unity must be closed
$U -batchmode -projectPath Unity -executeMethod ScanTests.Geometry -logFile geo.log
python3 scan_server.py --port 8765 --root /tmp/scans &  SCAN_SERVER=http://127.0.0.1:8765/ \
  $U -batchmode -projectPath Unity -executeMethod ScanTests.Mission -logFile mission.log    # grep "\[ScanTest\]"
```

Browser end-to-end check (headless Chrome, real WebGL build, `?autoscan`): 15 lane images and 7 revisit images, all saved.
Real CityLife traffic blocked the view in 6 images. The scan ended `incomplete`, with 3 m² of road not clearly seen after 3 looks, and said so.

## AI (2026-10-11)

- Model: `VisionServer/models/drone_items.pt` (YOLO11n, wallet/keys/phone/purse), served by `VisionServer/server.py`
  (engine `items`, port 8765). Archive files installed: `VisionServer/server.py`, `report.py`, `train/`, model weights
  (git-ignored), new `wallet.obj` + `PropCatalog.asset`, `DroneSimSetup.cs`, `DatasetGenerator.cs`, `ParkingLot.unity`.
- `scan_server.py` queues each saved image → worker → `/detect?target=all` → positive images to `detections/`,
  negatives deleted, failures kept and flagged. Page polls `GET /api/scans/<id>/detections`.
- Scans fly at 2.5 m (allowed 2.5–4 m; the model was trained on 2.3–5 m). Photos are not taken above 4 m
  (climbing over lamps/trees); those spots are revisited and otherwise reported unseen.
- Real browser runs (10×, region 6,-18 → 34,-1): 5 of 6 hidden objects found, in 2/2–4/5 of the photos covering them,
  position error 0–8 cm. The 6th (keys at the region edge next to street furniture) was in no photo: spot skipped for height.
- Known false alarms: city furniture the model never saw (a bench was a 0.82 "purse" at 6 m); low-confidence (~0.38)
  "keys" elsewhere. Sightings show "found in N of M photos" to tell them apart.
- Fixed: targets on sidewalks/lawns are placed on the drawn surface (those meshes have no colliders).
- Revisits hover beside lamp posts/trees when that keeps the drone under 4 m; after the change 6/6 browser scans found
  their object (0-6 cm), 12-19 m² per scan still unseen next to tall street furniture (reported, scan "incomplete").
- The page tags weak sightings "unconfirmed" (confidence < 0.5, or found in under half the photos covering the spot).

- Buildings are not searched: region cells under a large, tall city solid (CityColliders, >= 5 x 5 m, > 3 m high) are
  excluded (reported as `buildings`), no photo is planned over them, lanes are split around them.
- After a scan: no photo strip. The camera frames the whole street grid (fog off until the next scan), and a red beam
  rises from each sighting (a few pixels wide at any distance). Clicking a beam (Unity → "sighting_click") opens the
  photo full size in the page with its box, a zoom inset and location; ✕ / Esc / backdrop click closes it.

## Known limitations

- **Pedestrian count lowered from 1200 to 200** (`CityLife.people`). The existing WebGL build crashed in Chrome right after
  startup with a WebAssembly out-of-memory error. This happened even with the scan code disabled, so it was there before this work.
  With 200 pedestrians it loads and runs. Raise it again only after checking memory (WebGL max heap 2048 MB).

- Flat ground is assumed for footprints and pixel → ground mapping. Fixed yaw.
- The motion model is the scan pilot's own: acceleration plus slowing for turns. It does not use `Drone.cs` A* following, and flies
  at the scan altitude rather than the sonar band. Sonar obstacle avoidance is not active during a scan; static obstacles are handled by climbing.
- Cells under trees or roofs are reported as `hidden_static` and never revisited. The object is placed only on open ground, so it can't be there.
- Occlusion detection assumes the moving objects are CityLife's `Traffic …` cars and `Pedestrian`s. Other new moving objects need to be added to `CityLife.IsMover`.
- No visual check of image quality (blur, lighting). Each scan is about 15 to 60 images at 640 × 640.

## Next steps (AI integration)

1. Read images with `ScanStore.images()` / `iter_batches()`, or poll `GET /api/scans/<id>` during a scan.
2. Run the model and write results with `ScanStore.record_detection(scan_id, image_id, result)`. Use the pixel → ground formula in SCANS.md.
3. Show detections on the page. The result box in `city.html` currently says recognition is not connected.
4. Score the model against `ground_truth.json`, after detection only.
