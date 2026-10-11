# Lost-item detector (wallet, keys, phone, purse)

A YOLO11n model fine-tuned on images rendered through the drone's own `DroneCamera` in the `ParkingLot` scene,
so training images look exactly like flight frames. The server uses it automatically once
`VisionServer/models/drone_items.pt` exists (`--engine items`).

## Rebuild from scratch (about 1 hour, mostly training)

From the repo root, with Unity 6000.6.4f1 and Python with `ultralytics` installed:

```bash
U=/Applications/Unity/Hub/Editor/6000.6.4f1/Unity.app/Contents/MacOS/Unity
# 1. Render labeled frames (about 1.5 min per split). Different seeds keep the test scenes unseen.
DATASET_SPLIT=train DATASET_ENVS=500 DATASET_SEED=1 $U -batchmode -projectPath Unity -executeMethod DatasetGenerator.RunBatch -logFile -
DATASET_SPLIT=val   DATASET_ENVS=60  DATASET_SEED=2 $U -batchmode -projectPath Unity -executeMethod DatasetGenerator.RunBatch -logFile -
DATASET_SPLIT=test  DATASET_ENVS=80  DATASET_SEED=3 $U -batchmode -projectPath Unity -executeMethod DatasetGenerator.RunBatch -logFile -
# 2. Tiles + downscaled frames -> datasets/drone4/yolo
python3 VisionServer/train/prepare.py
# 3. Fine-tune models/yolo11n.pt (12 epochs, about 40 min on an M4 Pro) -> models/drone_items.pt
python3 VisionServer/train/train.py
# 4. Measure on unseen scenes and on VisionServer/test_images -> train/evaluation.json
python3 VisionServer/train/evaluate_items.py
```

`DatasetGenerator.cs` (Unity/Assets/Scripts/Editor): per scene, `LotRandomizer` re-rolls cars and dropped items,
20 extra target items and 8 distractors (laptop, backpack, cup...) are scattered over one quadrant, and the sun's
angle and brightness change. Six 1280x1280 frames are taken from 2.3-5.4 m (mostly the 3.6-5.4 m scan height).
Each target's mesh is projected into the image for a tight box; items mostly outside the frame or hidden behind
something are not labeled.

`prepare.py` turns each render into 640 px tiles at full resolution plus the whole frame at 640 px, so the
model works with both 1280 and 640 px drone cameras.

## The wallet model

The scene's original "Wallet" was a round pouch (`pouch.obj`). It is now a modern bifold wallet made by
`make_wallet.py` (`Unity/Assets/Models/Items/wallet.obj`): rounded-rectangle leather body, darker fold edge,
contrast stitching, card edges at the open side and a small metal logo plate. The dataset generator gives every
wallet a random leather color (black, dark brown, cognac, tan, navy, grey, burgundy) and scatters wallets twice
as often as the other items. `DatasetGenerator.RefreshSceneWallets` swaps the wallets saved in `ParkingLot`.

The current `drone_items.pt` was fine-tuned from the first model (`drone_items_v1.pt`, pouch wallet) for 6 epochs
on `datasets/drone5`:

```bash
python3 VisionServer/train/prepare.py --raw datasets/drone5/raw --out datasets/drone5/yolo
python3 VisionServer/train/train.py --data datasets/drone5/yolo/data.yaml --weights VisionServer/models/drone_items_v1.pt --epochs 6 --close-mosaic 2 --name drone5
python3 VisionServer/train/evaluate_items.py --test datasets/drone5/raw/fresh --out VisionServer/train/evaluation_fresh.json
```

`VisionServer/test_images` were captured before this change, so their wallet frames still show the old pouch.

## Limits

- Trained and tested only on this simulator's renders. It will not transfer to real photos without real training data.
- It knows these exact 3D models. A different wallet or key model needs new training images.
- Distractors (laptop, backpack, cups...) are in the training images as unlabeled background, which teaches the
  model to ignore them; other unseen objects may still cause false alarms.
