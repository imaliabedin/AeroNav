"""Run from the repo root:  python3 -m unittest image_store.test_image_store -v"""

import io
import json
import os
import subprocess
import sys
import tempfile
import unittest

from image_store import ImageStore
from image_store import store as store_module

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RESULT = {"detected": True, "category": "phone", "confidence": 0.82, "hasBox": True,
          "bbox": {"x": 0.41, "y": 0.38, "w": 0.07, "h": 0.05}, "engine": "yolo-world", "latencyMs": 61.0, "error": ""}


def make_jpeg(shade=0, square_at=None):
    """A real 64x64 JPEG (Pillow) with an optional bright square, or minimal JPEG-marked bytes without Pillow."""
    if store_module.Image is None:
        return b"\xff\xd8\xff\xe0" + bytes([shade]) * 64 + os.urandom(8)
    im = store_module.Image.new("RGB", (64, 64), (60 + shade, 60, 60))
    if square_at is not None:
        x, y = square_at
        im.paste((250, 250, 250), (x, y, x + 20, y + 20))
    buf = io.BytesIO()
    im.save(buf, format="JPEG", quality=90)
    return buf.getvalue()


class ImageStoreTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = self.tmp.name

    def tearDown(self):
        self.tmp.cleanup()

    def test_save_persist_across_restart_and_detection_by_id(self):
        jpg = make_jpeg()
        with ImageStore(self.root) as s:
            rec = s.save_image(jpg, "run1", target="phone", position=(12.5, 1.0, 30.0), altitude=2.44)
            s.attach_detection(rec.image_id, RESULT)
        self.assertFalse(rec.duplicate)
        self.assertTrue(os.path.isfile(os.path.join(self.root, "sessions", "run1", rec.filename)))

        # "Restart": a brand-new Python process opens the same store and reads everything back by ID.
        code = (
            "import json, sys; from dataclasses import asdict; from image_store import ImageStore\n"
            "s = ImageStore(sys.argv[1]); i = s.get_image(sys.argv[2]); d = s.get_detection(sys.argv[2])\n"
            "print(json.dumps({'image': asdict(i), 'detection': asdict(d), 'bytes': s.image_path(sys.argv[2]).read_bytes().hex()}))"
        )
        out = subprocess.run([sys.executable, "-c", code, self.root, rec.image_id], cwd=REPO,
                             capture_output=True, text=True, check=True).stdout
        got = json.loads(out)
        img, det = got["image"], got["detection"]
        self.assertEqual(img["session_id"], "run1")
        self.assertEqual(img["filename"], rec.filename)
        self.assertEqual(img["target"], "phone")
        self.assertEqual(img["position"], [12.5, 1.0, 30.0])
        self.assertAlmostEqual(img["altitude"], 2.44)
        self.assertEqual(img["captured_at"], rec.captured_at)
        self.assertEqual(bytes.fromhex(got["bytes"]), jpg)
        self.assertTrue(det["detected"])
        self.assertEqual(det["category"], "phone")
        self.assertAlmostEqual(det["confidence"], 0.82)
        self.assertEqual(det["bbox"], RESULT["bbox"])
        self.assertEqual(det["raw"], RESULT)

    def test_exact_duplicate_is_not_saved_twice(self):
        jpg = make_jpeg()
        with ImageStore(self.root) as s:
            a = s.save_image(jpg, "run1", target="key")
            b = s.save_image(jpg, "run1", target="key")
            c = s.save_image(jpg, "run2", target="key")  # same frame in another session is kept
            self.assertTrue(b.duplicate)
            self.assertEqual(b.image_id, a.image_id)
            self.assertFalse(c.duplicate)
            self.assertEqual(len(s.list_images("run1")), 1)
            self.assertEqual(len(os.listdir(os.path.join(self.root, "sessions", "run1"))), 1)
            # Identical pixels at a different drone position are a new observation: keep them.
            p = s.save_image(jpg, "run3", position=(0, 1, 0))
            same_spot = s.save_image(jpg, "run3", position=(0, 1, 0.01))
            elsewhere = s.save_image(jpg, "run3", position=(5, 1, 0))
            self.assertTrue(same_spot.duplicate)
            self.assertEqual(same_spot.image_id, p.image_id)
            self.assertFalse(elsewhere.duplicate)

    @unittest.skipIf(store_module.Image is None, "Pillow not installed")
    def test_near_duplicate_only_when_hovering(self):
        with ImageStore(self.root) as s:
            a = s.save_image(make_jpeg(0), "run1", position=(1, 1, 1))
            hover = s.save_image(make_jpeg(1), "run1", position=(1, 1, 1.01))   # re-encoded, drone didn't move
            moved = s.save_image(make_jpeg(1), "run1", position=(2, 1, 1))      # same look, new place: keep
            new_view = s.save_image(make_jpeg(0, (20, 20)), "run1", position=(2, 1, 1.01))  # something appeared
            self.assertTrue(hover.duplicate)
            self.assertEqual(hover.image_id, a.image_id)
            self.assertFalse(moved.duplicate)
            self.assertFalse(new_view.duplicate)

    def test_list_sessions_export_and_filters(self):
        with ImageStore(self.root) as s:
            a = s.save_image(make_jpeg(0), "run1", target="phone")
            b = s.save_image(make_jpeg(30), "run1", target="phone")
            s.attach_detection(a.image_id, {"detected": False, "category": "phone", "confidence": 0.1, "hasBox": False,
                                            "bbox": {"x": 0, "y": 0, "w": 0, "h": 0}})
            s.attach_detection(b.image_id, RESULT)
            self.assertEqual([r.image_id for r in s.list_images("run1")], [a.image_id, b.image_id])
            self.assertEqual([r.image_id for r in s.list_images("run1", detected_only=True)], [b.image_id])
            self.assertIsNone(s.get_detection(a.image_id).bbox)  # hasBox False -> no box
            self.assertEqual(s.list_sessions()[0]["images"], 2)
            page = s.export_session("run1", url_prefix="/images/")
            json.dumps(page)  # must be JSON-serializable for the website
            self.assertEqual(page[1]["url"], f"/images/sessions/run1/{b.filename}")
            self.assertEqual(page[1]["detection"]["confidence"], 0.82)

    def test_list_detections_returns_full_history_oldest_first(self):
        negative = {"detected": False, "category": "phone", "confidence": 0.1, "hasBox": False}
        with ImageStore(self.root) as s:
            rec = s.save_image(make_jpeg(), "run1", target="phone")
            self.assertEqual(s.list_detections(rec.image_id), [])
            s.attach_detection(rec.image_id, RESULT)
            s.attach_detection(rec.image_id, negative)
            history = s.list_detections(rec.image_id)
            self.assertEqual([d.detected for d in history], [True, False])
            self.assertEqual(history[0].raw, RESULT)
            self.assertEqual(history[-1], s.get_detection(rec.image_id))  # latest is still the newest
            self.assertEqual(s.list_detections("missing"), [])

    def test_detected_only_keeps_earlier_positive_after_later_negative(self):
        # Regression: a duplicate frame re-checked as negative must not hide an earlier sighting.
        negative = {"detected": False, "category": "phone", "confidence": 0.1, "hasBox": False}
        jpg = make_jpeg()
        with ImageStore(self.root) as s:
            first = s.save_image(jpg, "run1", target="phone", position=(1, 1, 1))
            s.attach_detection(first.image_id, RESULT)
            dup = s.save_image(jpg, "run1", target="phone", position=(1, 1, 1))
            self.assertTrue(dup.duplicate)
            s.attach_detection(dup.image_id, negative)
            never = s.save_image(make_jpeg(40), "run1", target="phone")
            s.attach_detection(never.image_id, negative)
            self.assertEqual([r.image_id for r in s.list_images("run1", detected_only=True)], [first.image_id])

    def test_rejects_bad_input(self):
        with ImageStore(self.root) as s:
            with self.assertRaises(ValueError):
                s.save_image(b"not an image", "run1")
            with self.assertRaises(ValueError):
                s.save_image(make_jpeg(), "../escape")
            with self.assertRaises(KeyError):
                s.attach_detection("missing", RESULT)
            self.assertIsNone(s.get_image("missing"))


if __name__ == "__main__":
    unittest.main()
