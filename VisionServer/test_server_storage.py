"""Frame storage in the vision server, with a fake detector (no model needed).

    cd VisionServer && python3 -m unittest test_server_storage -v
"""

import contextlib
import io
import json
import os
import sqlite3
import sys
import tempfile
import threading
import unittest
import urllib.request
from http.server import ThreadingHTTPServer

from PIL import Image

import server
from image_store import ImageStore

LEGACY_KEYS = {"detected", "category", "confidence", "hasBox", "bbox", "engine", "latencyMs", "error", "candidates"}
STORAGE_KEYS = {"imageId", "duplicate", "sessionId", "capturedAt", "receivedAt", "storageError"}


class FakeEngine:
    name = "fake"

    def detect(self, image, target):
        return [(0.8, (0.4, 0.4, 0.1, 0.1), target)]


class BrokenStore:
    def save_image(self, *a, **k):
        raise OSError("disk full")


def jpeg(shade=60):
    buf = io.BytesIO()
    Image.new("RGB", (64, 64), (shade, 60, 60)).save(buf, format="JPEG")
    return buf.getvalue()


class ServerStorageTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.store = ImageStore(self.tmp.name)
        self.httpd = None

    def tearDown(self):
        if self.httpd:
            self.httpd.shutdown()
            self.httpd.server_close()
        self.store.close()
        self.tmp.cleanup()

    def serve(self, store, enabled=True):
        self.httpd = ThreadingHTTPServer(("127.0.0.1", 0), server.make_handler(FakeEngine(), store, enabled))
        threading.Thread(target=self.httpd.serve_forever, daemon=True).start()

    def post(self, query, data=None):
        url = f"http://127.0.0.1:{self.httpd.server_address[1]}/detect?{query}"
        req = urllib.request.Request(url, data or jpeg(), {"Content-Type": "image/jpeg"})
        with urllib.request.urlopen(req, timeout=10) as r:
            self.assertEqual(r.status, 200)
            return json.load(r)

    def detection_rows(self, image_id):
        db = sqlite3.connect(os.path.join(self.tmp.name, "index.sqlite3"))
        try:
            return [json.loads(r[0]) for r in db.execute(
                "SELECT raw_json FROM detections WHERE image_id=? ORDER BY detection_id", (image_id,))]
        finally:
            db.close()

    def test_session_frame_is_stored_with_metadata_and_detection(self):
        self.serve(self.store)
        res = self.post("target=phone&conf=0.3&session=run-test-1&x=12.5&y=1&z=30.25&alt=1.38"
                        "&capturedAt=2026-10-10T15:30:12.123%2B00:00")
        self.assertTrue(res["detected"])
        self.assertEqual(res["storageError"], "")
        self.assertFalse(res["duplicate"])
        self.assertEqual(res["capturedAt"], "2026-10-10T15:30:12.123+00:00")
        self.assertNotEqual(res["receivedAt"], res["capturedAt"])  # receipt time kept separately

        img = self.store.get_image(res["imageId"])
        self.assertEqual(img.session_id, "run-test-1")
        self.assertEqual(img.target, "phone")
        self.assertEqual(img.position, (12.5, 1.0, 30.25))
        self.assertAlmostEqual(img.altitude, 1.38)
        self.assertEqual(img.captured_at, "2026-10-10T15:30:12.123+00:00")
        det = self.store.get_detection(res["imageId"])
        self.assertTrue(det.detected)
        self.assertAlmostEqual(det.confidence, 0.8)
        self.assertEqual(det.raw["frame"]["receivedAt"], res["receivedAt"])
        self.assertEqual(det.raw["frame"]["position"], [12.5, 1.0, 30.25])

    def test_request_without_session_is_unchanged_and_not_stored(self):
        self.serve(self.store)
        res = self.post("target=key&conf=0.3")
        self.assertEqual(set(res), LEGACY_KEYS)
        self.assertEqual(self.store.list_sessions(), [])

    def test_storage_disabled_ignores_session(self):
        self.serve(None, enabled=False)
        res = self.post("target=key&conf=0.3&session=run-off")
        self.assertEqual(set(res), LEGACY_KEYS)
        self.assertTrue(res["detected"])

    def test_storage_failure_still_returns_detection_and_logs(self):
        for broken in (BrokenStore(), None):  # save fails / store never opened
            self.serve(broken)
            err = io.StringIO()
            with contextlib.redirect_stderr(err):
                res = self.post("target=phone&conf=0.3&session=run-x&x=1&y=1&z=1")
            self.assertTrue(res["detected"])
            self.assertAlmostEqual(res["confidence"], 0.8)
            self.assertEqual(res["error"], "")
            self.assertEqual(res["imageId"], "")
            self.assertTrue(res["storageError"])
            self.assertIn("[storage] ERROR", err.getvalue())
            self.assertIn("session=run-x", err.getvalue())
            self.httpd.shutdown()
            self.httpd.server_close()
            self.httpd = None

    def test_bad_metadata_is_a_storage_error_not_a_detection_error(self):
        self.serve(self.store)
        with contextlib.redirect_stderr(io.StringIO()):
            for q in ("x=abc&y=1&z=1", "x=1", "capturedAt=yesterday", "capturedAt=2026-10-10T15:30:12"):
                res = self.post(f"target=key&conf=0.3&session=run-bad&{q}")
                self.assertTrue(res["detected"], q)
                self.assertTrue(res["storageError"], q)
            res = self.post("target=key&conf=0.3&session=../escape")
            self.assertTrue(res["storageError"])
        self.assertEqual(self.store.list_sessions(), [])

    def test_duplicate_frame_appends_detection_without_overwriting(self):
        self.serve(self.store)
        q = "target=phone&conf=0.3&session=run-dup&x=1&y=1&z=1&alt=1.4&capturedAt=2026-10-10T15:30:12.000%2B00:00"
        first = self.post(q)
        second = self.post(q.replace("15:30:12.000", "15:30:12.400") + "&ignored=1")
        self.assertFalse(first["duplicate"])
        self.assertTrue(second["duplicate"])
        self.assertEqual(second["imageId"], first["imageId"])
        self.assertTrue(second["detected"])  # the current result is still returned

        rows = self.detection_rows(first["imageId"])
        self.assertEqual(len(rows), 2)  # history kept, nothing overwritten
        self.assertIsNone(rows[0]["frame"]["duplicateOf"])
        self.assertEqual(rows[1]["frame"]["duplicateOf"], first["imageId"])
        self.assertEqual(rows[1]["frame"]["capturedAt"], "2026-10-10T15:30:12.400+00:00")
        # The image row keeps the first frame's metadata.
        self.assertEqual(self.store.get_image(first["imageId"]).captured_at, "2026-10-10T15:30:12.000+00:00")
        self.assertEqual(len(self.store.list_images("run-dup")), 1)


if __name__ == "__main__":
    unittest.main()
