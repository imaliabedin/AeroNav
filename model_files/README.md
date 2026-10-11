# Model files (copies kept in the repository)

`VisionServer/models/` is git-ignored, so the trained models are kept here. Put them back with:

```bash
cp model_files/drone_items*.pt VisionServer/models/
unzip -o model_files/model_onnx.zip -d /tmp/onnx && cp /tmp/onnx/best.onnx VisionServer/models/rescue_people.onnx
```

| File | What |
|---|---|
| `drone_items.pt` (+ `_v1`, `_v2`) | Object Finder: YOLO11n for wallet, keys, phone, purse (from DroneScanner_AI.zip) |
| `model_onnx.zip` → `best.onnx` | Rescue Mission: YOLOv8m-worldv2 person detector, 640 x 640 (zipped: the .onnx is 112 MB, over GitHub's 100 MB file limit) |

`yolov8s-worldv2.pt` and the CLIP weights are public and download automatically on first use.
