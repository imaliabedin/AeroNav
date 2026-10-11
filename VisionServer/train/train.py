"""Fine-tunes YOLO11n (models/yolo11n.pt) on the Unity dataset to find wallets, keys, phones and purses.

    python3 VisionServer/train/prepare.py
    python3 VisionServer/train/train.py [--epochs 12] [--batch 32] [--device mps]

The best weights are copied to VisionServer/models/drone_items.pt, which server.py uses for --engine items.
"""

import argparse
import shutil
from pathlib import Path

HERE = Path(__file__).resolve().parent
SERVER = HERE.parent
REPO = SERVER.parent


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default=str(REPO / "datasets" / "drone4" / "yolo" / "data.yaml"))
    ap.add_argument("--weights", default=str(SERVER / "models" / "yolo11n.pt"))
    ap.add_argument("--epochs", type=int, default=12)
    ap.add_argument("--batch", type=int, default=32)
    ap.add_argument("--imgsz", type=int, default=640)
    ap.add_argument("--device", default="mps")
    ap.add_argument("--name", default="drone4")
    ap.add_argument("--close-mosaic", type=int, default=3, help="final epochs without mosaic augmentation")
    ap.add_argument("--fraction", type=float, default=1.0, help="use part of the training set (quick timing runs)")
    args = ap.parse_args()

    from ultralytics import YOLO

    model = YOLO(args.weights)
    model.train(
        data=args.data, epochs=args.epochs, batch=args.batch, imgsz=args.imgsz, device=args.device,
        project=str(HERE / "runs"), name=args.name, exist_ok=True, fraction=args.fraction,
        patience=15, workers=8, seed=0, deterministic=False, plots=True,
        # Top-down camera: both flips are realistic; no rotation (boxes would loosen); moderate scale for altitude.
        fliplr=0.5, flipud=0.5, degrees=0.0, scale=0.35, translate=0.1, mosaic=1.0, close_mosaic=args.close_mosaic,
        hsv_h=0.015, hsv_s=0.5, hsv_v=0.4,
    )
    best = HERE / "runs" / args.name / "weights" / "best.pt"
    dst = SERVER / "models" / "drone_items.pt"
    shutil.copy2(best, dst)
    print(f"best weights -> {dst}")


if __name__ == "__main__":
    main()
