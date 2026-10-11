"""Local image storage for drone search sessions: files on disk, metadata in SQLite.

Layout under `root`:
    index.sqlite3                      metadata index (images + detections)
    sessions/<session_id>/<image_id>.jpg|png

Thread-safe (one connection guarded by a lock), so a threaded web server can share one ImageStore.
"""

import hashlib
import io
import json
import os
import re
import sqlite3
import threading
import uuid
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import List, Optional, Sequence

try:  # optional: only used for near-duplicate detection
    from PIL import Image
except ImportError:  # pragma: no cover
    Image = None

SESSION_RE = re.compile(r"^[A-Za-z0-9_-]{1,64}$")

SCHEMA = """
CREATE TABLE IF NOT EXISTS images (
    image_id    TEXT PRIMARY KEY,
    session_id  TEXT NOT NULL,
    filename    TEXT NOT NULL,
    rel_path    TEXT NOT NULL,
    captured_at TEXT NOT NULL,
    target      TEXT,
    pos_x REAL, pos_y REAL, pos_z REAL,
    altitude    REAL,
    format      TEXT NOT NULL,
    width INTEGER, height INTEGER,
    size_bytes  INTEGER NOT NULL,
    sha256      TEXT NOT NULL,
    dhash       TEXT
);
CREATE INDEX IF NOT EXISTS images_session ON images(session_id, captured_at);
CREATE INDEX IF NOT EXISTS images_sha ON images(session_id, sha256);
CREATE TABLE IF NOT EXISTS detections (
    detection_id INTEGER PRIMARY KEY AUTOINCREMENT,
    image_id    TEXT NOT NULL REFERENCES images(image_id) ON DELETE CASCADE,
    created_at  TEXT NOT NULL,
    detected    INTEGER NOT NULL,
    category    TEXT,
    confidence  REAL,
    bbox_x REAL, bbox_y REAL, bbox_w REAL, bbox_h REAL,
    engine      TEXT,
    raw_json    TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS detections_image ON detections(image_id, detection_id);
"""


@dataclass
class ImageRecord:
    image_id: str
    session_id: str
    filename: str
    rel_path: str           # relative to the store root, '/'-separated (usable as a URL path)
    captured_at: str        # UTC ISO-8601
    target: Optional[str]
    position: Optional[Sequence[float]]
    altitude: Optional[float]
    format: str
    width: Optional[int]
    height: Optional[int]
    size_bytes: int
    sha256: str
    duplicate: bool = False  # True when save_image skipped the frame and returned the existing record


@dataclass
class DetectionRecord:
    image_id: str
    created_at: str
    detected: bool
    category: Optional[str]
    confidence: Optional[float]
    bbox: Optional[dict]    # normalized {x, y, w, h}, origin top-left
    engine: Optional[str]
    raw: dict               # the full result as given to attach_detection


def _now():
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds")


def _sniff_format(data: bytes) -> str:
    if data[:3] == b"\xff\xd8\xff":
        return "jpg"
    if data[:8] == b"\x89PNG\r\n\x1a\n":
        return "png"
    raise ValueError("image must be JPEG or PNG bytes")


def _dhash(data: bytes):
    """64-bit difference hash and image size, or (None, None, None) without Pillow."""
    if Image is None:
        return None, None, None
    with Image.open(io.BytesIO(data)) as im:
        w, h = im.size
        g = im.convert("L").resize((9, 8))
        px = list(g.getdata())
    bits = 0
    for row in range(8):
        for col in range(8):
            bits = (bits << 1) | (px[row * 9 + col] > px[row * 9 + col + 1])
    return f"{bits:016x}", w, h


class ImageStore:
    """
    near_duplicate_bits: frames whose difference hash is within this many bits of the session's
        previous frame AND whose drone position moved less than `min_move` are skipped as
        near-duplicates (e.g. hovering). Frames without a position are only de-duplicated when
        byte-identical, because a coarse hash can miss a small target entering the view.
        Byte-identical frames are skipped too, unless both have positions at least `min_move` apart.
        Set near_duplicate_bits=None to keep every non-identical frame.
    """

    def __init__(self, root="drone_images", near_duplicate_bits: Optional[int] = 2, min_move: float = 0.05):
        self.root = Path(root).resolve()
        (self.root / "sessions").mkdir(parents=True, exist_ok=True)
        self.near_duplicate_bits = near_duplicate_bits
        self.min_move = min_move
        self._lock = threading.Lock()
        self._db = sqlite3.connect(str(self.root / "index.sqlite3"), check_same_thread=False)
        self._db.row_factory = sqlite3.Row
        self._db.execute("PRAGMA foreign_keys = ON")
        self._db.execute("PRAGMA journal_mode = WAL")
        self._db.executescript(SCHEMA)
        self._db.commit()

    def close(self):
        with self._lock:
            self._db.close()

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()

    # ---------- write ----------

    def save_image(self, data: bytes, session_id: str, target: Optional[str] = None,
                   position: Optional[Sequence[float]] = None, altitude: Optional[float] = None,
                   captured_at: Optional[str] = None) -> ImageRecord:
        """Stores one JPEG/PNG frame. If it duplicates an earlier frame of the session, nothing is
        written and the earlier record is returned with `duplicate=True`."""
        if not SESSION_RE.match(session_id or ""):
            raise ValueError("session_id must be 1-64 characters of A-Z a-z 0-9 _ -")
        if position is not None and len(position) != 3:
            raise ValueError("position must be (x, y, z)")
        fmt = _sniff_format(data)
        sha = hashlib.sha256(data).hexdigest()
        dhash, width, height = _dhash(data)

        with self._lock:
            # Byte-identical frames are skipped unless taken somewhere else (plain ground can look identical).
            row = next((r for r in self._db.execute("SELECT * FROM images WHERE session_id=? AND sha256=?",
                                                    (session_id, sha))
                        if not self._moved(position, r)), None)
            if row is None:
                row = self._near_duplicate(session_id, dhash, position)
            if row is not None:
                rec = self._image(row)
                rec.duplicate = True
                return rec

            image_id = uuid.uuid4().hex
            filename = f"{image_id}.{fmt}"
            rel_path = f"sessions/{session_id}/{filename}"
            path = self.root / rel_path
            path.parent.mkdir(parents=True, exist_ok=True)
            tmp = path.with_suffix(".part")
            tmp.write_bytes(data)
            os.replace(tmp, path)  # never leave a half-written image under its final name
            x, y, z = position if position is not None else (None, None, None)
            try:
                self._db.execute(
                    "INSERT INTO images VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                    (image_id, session_id, filename, rel_path, captured_at or _now(), target,
                     x, y, z, altitude, fmt, width, height, len(data), sha, dhash))
                self._db.commit()
            except Exception:
                path.unlink(missing_ok=True)
                raise
            return self._image(self._db.execute("SELECT * FROM images WHERE image_id=?", (image_id,)).fetchone())

    def attach_detection(self, image_id: str, result: dict) -> DetectionRecord:
        """Records a detector result for an image. Accepts the vision server's JSON
        (detected, category, confidence, hasBox, bbox{x,y,w,h}, engine). Several results per
        image are kept; get_detection returns the latest."""
        bbox = result.get("bbox") if result.get("hasBox", result.get("bbox") is not None) else None
        b = bbox or {}
        with self._lock:
            if self._db.execute("SELECT 1 FROM images WHERE image_id=?", (image_id,)).fetchone() is None:
                raise KeyError(f"no image {image_id}")
            self._db.execute(
                "INSERT INTO detections (image_id, created_at, detected, category, confidence,"
                " bbox_x, bbox_y, bbox_w, bbox_h, engine, raw_json) VALUES (?,?,?,?,?,?,?,?,?,?,?)",
                (image_id, _now(), int(bool(result.get("detected"))), result.get("category"),
                 result.get("confidence"), b.get("x"), b.get("y"), b.get("w"), b.get("h"),
                 result.get("engine"), json.dumps(result)))
            self._db.commit()
        return self.get_detection(image_id)

    # ---------- read ----------

    def get_image(self, image_id: str) -> Optional[ImageRecord]:
        with self._lock:
            row = self._db.execute("SELECT * FROM images WHERE image_id=?", (image_id,)).fetchone()
        return self._image(row) if row else None

    def image_path(self, image_id: str) -> Optional[Path]:
        rec = self.get_image(image_id)
        return self.root / rec.rel_path if rec else None

    def get_detection(self, image_id: str) -> Optional[DetectionRecord]:
        with self._lock:
            row = self._db.execute("SELECT * FROM detections WHERE image_id=? ORDER BY detection_id DESC LIMIT 1",
                                   (image_id,)).fetchone()
        return self._detection(row) if row else None

    def list_detections(self, image_id: str) -> List[DetectionRecord]:
        """Every detection recorded for an image, oldest first (a duplicate frame adds one each time)."""
        with self._lock:
            rows = self._db.execute("SELECT * FROM detections WHERE image_id=? ORDER BY detection_id",
                                    (image_id,)).fetchall()
        return [self._detection(r) for r in rows]

    def list_images(self, session_id: str, detected_only: bool = False) -> List[ImageRecord]:
        """detected_only: images with at least one positive detection, even if a later one was negative,
        so a sighting is never hidden by a re-check."""
        sql = "SELECT * FROM images WHERE session_id=?"
        if detected_only:
            sql += " AND image_id IN (SELECT image_id FROM detections WHERE detected=1)"
        with self._lock:
            rows = self._db.execute(sql + " ORDER BY captured_at, rowid", (session_id,)).fetchall()
        return [self._image(r) for r in rows]

    def list_sessions(self) -> List[dict]:
        with self._lock:
            rows = self._db.execute(
                "SELECT session_id, COUNT(*) AS images, MIN(captured_at) AS started, MAX(captured_at) AS last"
                " FROM images GROUP BY session_id ORDER BY started").fetchall()
        return [dict(r) for r in rows]

    def export_session(self, session_id: str, url_prefix: str = "") -> List[dict]:
        """JSON-ready list of a session's images with their latest detection, for a web page.
        `url` is `url_prefix` + the image's path relative to the store root."""
        out = []
        for rec in self.list_images(session_id):
            d = asdict(rec)
            d.pop("duplicate")
            d["url"] = url_prefix + rec.rel_path
            det = self.get_detection(rec.image_id)
            d["detection"] = None if det is None else {k: v for k, v in asdict(det).items() if k not in ("raw", "image_id")}
            out.append(d)
        return out

    # ---------- internals ----------

    def _near_duplicate(self, session_id, dhash, position):
        if self.near_duplicate_bits is None or dhash is None or position is None:
            return None
        prev = self._db.execute("SELECT * FROM images WHERE session_id=? ORDER BY captured_at DESC, rowid DESC LIMIT 1",
                                (session_id,)).fetchone()
        if prev is None or prev["dhash"] is None or prev["pos_x"] is None:
            return None
        bits = bin(int(dhash, 16) ^ int(prev["dhash"], 16)).count("1")
        return prev if not self._moved(position, prev) and bits <= self.near_duplicate_bits else None

    def _moved(self, position, row):
        """True if both frames have positions at least `min_move` apart."""
        if position is None or row["pos_x"] is None:
            return False
        d = sum((a - b) ** 2 for a, b in zip(position, (row["pos_x"], row["pos_y"], row["pos_z"]))) ** 0.5
        return d >= self.min_move

    @staticmethod
    def _image(row) -> ImageRecord:
        pos = None if row["pos_x"] is None else (row["pos_x"], row["pos_y"], row["pos_z"])
        return ImageRecord(row["image_id"], row["session_id"], row["filename"], row["rel_path"], row["captured_at"],
                           row["target"], pos, row["altitude"], row["format"], row["width"], row["height"],
                           row["size_bytes"], row["sha256"])

    @staticmethod
    def _detection(row) -> DetectionRecord:
        bbox = None if row["bbox_x"] is None else {"x": row["bbox_x"], "y": row["bbox_y"], "w": row["bbox_w"], "h": row["bbox_h"]}
        return DetectionRecord(row["image_id"], row["created_at"], bool(row["detected"]), row["category"],
                               row["confidence"], bbox, row["engine"], json.loads(row["raw_json"]))
