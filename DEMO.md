# Drone search demo: setup and run

The drone navigates by **sonar** everywhere: in the Unity simulation and on the website. It knows only
where the landing zone / goal is and the outer edge of the area. It finds every obstacle by sending
simulated sound pulses and listening for echoes, builds a map of what it believes is blocked, and
replans when a new obstacle is in its way. A separate collision check stops it if it ever touches something.

| Part | Folder / file | What it does |
|---|---|---|
| Website Flight Lab | `portal.html`, `sonar.js`, `sonar-core.js` | Watch the drone fly by sonar in the browser; drop objects in its way |
| Unity simulation | `Unity/` | The same sonar navigation in the parking-lot scene (`ParkingLot`) and the city version (`DroneCity`) |
| Drone City on the web | `city.html`, `drone-city/` | The Unity city scene built for the browser (WebGL), flying sonar-only missions |
| Vision server | `VisionServer/` | Receives each frame, runs the detector, replies found / not found |
| Image storage | `image_store/` | Saves every frame and its detection result per flight (session) |

## Watch the drone on the website (no installs needed)

In Terminal:

```bash
cd path/to/HAADRONES1
python3 -m http.server 8000
```

Then open **http://localhost:8000/portal.html** in Chrome or Safari and press **Run sonar flight**.
Or open **http://localhost:8000/portal.html?autorun=1&follow=1** to start it automatically, with the camera following the drone.

What you see:
- **Grey lines:** the sonar's sample rays (250 per pulse, 10 pulses per second).
- **Orange dots and yellow dashes:** measured echoes, and echoes travelling back to the drone.
- **Blue rings:** the pulse spreading out. They are illustrative only (slowed down); only the rays detect anything.
- **Red boxes:** what the drone *believes* is blocked, built only from echoes. This is not the real map.
- **Yellow line:** its current route. It changes when the sonar finds something in the way.
- **SONAR panel:** echoes in the last pulse, nearest echo, believed cells, replans and collisions. The Rays, Rings, Belief and Follow buttons toggle the views.

Try it: drag a **Street light**, **Pine tree** or a car from *Unity objects* onto the drone's route, or around the
landing zone, while it flies. The drone discovers the object only when the echoes come back.

**Drone City (the main demo):** open **http://localhost:8000** and press **Launch SkyRL**.
It is the Unity simulation running in the browser: the parking lot inside a city. Use the buttons on top:
**Place target** (then click on the lot), **Wall / Street light / Tree / Van / Cone** (click to place, Ctrl+click removes),
**Run drone**, **Stop**, **Add surprise obstacles**, **Clear obstacles**. The drone navigates by sonar only.
The first load downloads about 50 MB. To rebuild it after changing the Unity project: Unity menu **Drone Sim → Build Web (Drone City)**.

The website and the vision part below are independent: the website needs no Python server or Unity.

**Honest status:** the vision detector does not recognize keys or phones in this scene yet, so expect **NOT FOUND**.
Sonar navigation, the camera, the server connection and image storage all work and can be shown.

## Requirements

- macOS (tested) with Unity Hub and **Unity 6000.6.4f1**, the version in `Unity/ProjectSettings/ProjectVersion.txt`
- Python 3.9 or newer (`python3 --version`)
- About 1 GB of free disk space and internet for the first start; the model files download automatically

## One-time setup

Open **Terminal** (Applications → Utilities → Terminal) and run:

```bash
cd path/to/HAADRONES1/VisionServer
python3 -m venv --system-site-packages .venv          # private Python environment for the server
.venv/bin/pip install -r requirements.txt             # detector libraries (several minutes)
.venv/bin/yolo settings weights_dir="$PWD/models" runs_dir="$PWD/models/runs" sync=False
```

The first time the server starts, it downloads two model files (about 380 MB) into `VisionServer/models/`.
This can take around 10 minutes on a slow connection. They are never committed to Git.

## Run the demo

**1. Start the vision server** (Terminal window 1; leave it open):

```bash
cd path/to/HAADRONES1/VisionServer
.venv/bin/python server.py
```

Wait for these two lines:

```
Frame storage: .../HAADRONES1/drone_images
Vision server: engine=yolo-world on http://127.0.0.1:8765  (POST /detect, GET /health)
```

**2. Open the simulation.**
- In Unity Hub choose **Add → Add project from disk** and select the `HAADRONES1/Unity` folder, then open it.
- In the Project window open **Assets → Scenes → ParkingLot** (double-click).
- Press **Play** (▶ at the top).

You should see:
- a HUD at the top left containing `vision: KEY (T)  idle`;
- a **DRONE CAM** picture at the top right, showing the ground under the drone.

**3. Fly.**
- Press **T** to switch the target between KEY and PHONE.
- Press **Enter** to launch the drone. It navigates by sonar (bottom-left **SONAR** panel; **V** changes the sonar view).
  The HUD changes to **SEARCHING** and its `frames` count goes up.
- **N** (in build mode) switches to the old full-knowledge navigation, only for comparison.
- The drone follows its yellow path to the green goal cube. The HUD then shows **NOT FOUND**, and the big banner says **GOAL REACHED**.
- **Esc** returns to build mode. **Enter** again starts a new flight, which is a new session.
- Other keys: **F** follows the drone with the camera, **+** / **−** change the drone's speed.

Terminal window 1 prints one line per frame, for example:

```
[detect] phone detected=False conf=0.00 70 ms  image=2fa66a25b2e54db7a4d5a3567cf9b2cd
```

`image=…` means that frame was saved. `(dup)` after it means the frame was identical to an earlier one, so no second copy was saved.

## Check that images were saved

Saved files go to `HAADRONES1/drone_images/`:

```
drone_images/
  index.sqlite3                          metadata for every image and detection
  sessions/run-YYYYMMDD-HHMMSS-xxxxxx/   one folder per flight, one .jpg per frame
```

Open the `drone_images/sessions/` folder in Finder to see the pictures.
To list sessions and image details, open Terminal window 2 and run:

```bash
cd path/to/HAADRONES1
python3 -c "from image_store import ImageStore; print(ImageStore('drone_images').list_sessions())"
```

Each entry shows a `session_id` and how many `images` were saved. For per-image details (position, altitude, time, detection), see "Looking at saved sessions" in [VisionServer/README.md](VisionServer/README.md).

## Troubleshooting

| Problem | Cause and fix |
|---|---|
| HUD shows **VISION SERVER OFFLINE** | The server isn't running, or crashed. Check Terminal window 1 and start it again (step 1). The drone still flies, but nothing is saved. |
| `OSError: [Errno 48] Address already in use` | A server is already running on port 8765. Run `lsof -nP -iTCP:8765 -sTCP:LISTEN` to see its PID, then `kill <PID>`, then start the server again. |
| `ModuleNotFoundError` (e.g. `ultralytics`) | You didn't use the server's own Python. Run `.venv/bin/python server.py` from inside `VisionServer/`, or redo the one-time setup. |
| First start hangs for minutes | It's downloading the model files. Leave it running; it only happens once. |
| Download fails (no internet) | Copy `VisionServer/models/` from a teammate's computer into the same place. |
| No `image=…` in the server lines | Storage is off: either the server was started with `--no-store`, or **Store Frames** is unticked on the Drone's TargetDetector in Play mode. |
| `[storage] ERROR ...` in Terminal | The frame wasn't saved, for example because the disk is full or the folder isn't writable. Detection still works. Read the message after `ERROR`. |
| Unity asks to upgrade or change version | Open the project with exactly Unity **6000.6.4f1** from Unity Hub. |

## Tests (optional)

```bash
cd path/to/HAADRONES1
node tests/sonar_web.test.js                                          # website sonar navigation
python3 -m unittest image_store.test_image_store -v                   # image storage
cd VisionServer && python3 -m unittest test_server_storage -v        # server + storage, no model needed
```

Unity sonar tests run in batch mode, for example
`Unity -batchmode -projectPath Unity -executeMethod SonarTests.Navigation` (also `Detection`, `SafetyAndBelief`, `Visualization`).
The Unity menu **Drone Sim → Sonar Demo** plays a sonar-only flight with surprise obstacles.

More detail: [VisionServer/README.md](VisionServer/README.md) and [image_store/README.md](image_store/README.md).
