"""Tests for the area-scan store and server: python3 -m unittest image_store.test_scans -v (from the repo root)."""

import base64
import json
import os
import stat
import tempfile
import threading
import unittest
import urllib.request
from functools import partial
from http.server import ThreadingHTTPServer
from pathlib import Path

from image_store.scans import ScanError, ScanStore

JPG = b"\xff\xd8\xff\xe0" + b"frame-1" + b"\xff\xd9"
JPG2 = b"\xff\xd8\xff\xe0" + b"frame-2" + b"\xff\xd9"
RECORD = {"target_category": "phone", "region": {"corners": [[0, 0], [10, 0], [10, 8], [0, 8]]},
          "config": {"altitude_m": 6}, "route": {"lanes": []}, "coordinate_frame": "world"}
META = {"sequence_number": 1, "timestamp": "2026-10-10T00:00:00.000Z", "drone_position": [1, 5.5, 2],
        "camera_position": [1, 5.38, 2], "ground_footprint": {"center": [1, 2], "size": [2.9, 2.9], "altitude": 2.5},
        "occlusion_status": "confirmed", "occluded_regions": {"confirmed_cells": [[1, 2]]}, "revisit_required": True,
        "image_b64": "ignored", "secret_truth": [9, 9]}


class ScanStoreTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.store = ScanStore(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def test_save_read_and_metadata(self):
        sid = self.store.create_scan(RECORD)["scan_id"]
        e = self.store.add_image(sid, JPG, META)
        self.assertEqual(e["capture_status"], "saved")
        self.assertEqual(e["scan_id"], sid)
        self.assertTrue(e["image_path"].startswith("images/00001_"))
        self.assertNotIn("image_b64", e)
        self.assertNotIn("secret_truth", e)          # unknown fields are dropped
        self.assertEqual(e["camera_position"], [1, 5.38, 2])
        # A fresh store (another process) reads it back.
        again = ScanStore(self.tmp.name)
        imgs = again.images(sid)
        self.assertEqual(len(imgs), 1)
        self.assertEqual(Path(imgs[0]["path"]).read_bytes(), JPG)
        self.assertEqual(again.read_image(sid, e["image_id"]), JPG)

    def test_two_scans_never_share_folders_or_overwrite(self):
        a = self.store.create_scan(RECORD)["scan_id"]
        b = self.store.create_scan(RECORD)["scan_id"]
        self.assertNotEqual(a, b)
        ea = self.store.add_image(a, JPG, META)
        eb = self.store.add_image(b, JPG, META)   # same seq number, other scan
        self.assertNotEqual(ea["image_id"], eb["image_id"])
        self.assertEqual(self.store.read_image(a, ea["image_id"]), JPG)
        e2 = self.store.add_image(a, JPG2, META)  # same seq number again in the same scan: new file
        self.assertNotEqual(e2["image_path"], ea["image_path"])
        self.assertEqual(len(self.store.images(a)), 2)

    def test_duplicate_bytes_not_saved_twice(self):
        sid = self.store.create_scan(RECORD)["scan_id"]
        first = self.store.add_image(sid, JPG, META)
        dup = self.store.add_image(sid, JPG, dict(META, sequence_number=2))
        self.assertTrue(dup["duplicate"])
        self.assertEqual(dup["image_id"], first["image_id"])
        m = self.store.manifest(sid)
        self.assertEqual(m["counts"], {"saved": 1, "failed": 0, "duplicates": 1})

    def test_failed_write_is_recorded(self):
        if os.geteuid() == 0:
            self.skipTest("root ignores permissions")
        sid = self.store.create_scan(RECORD)["scan_id"]
        images = Path(self.tmp.name) / sid / "images"
        images.chmod(stat.S_IRUSR | stat.S_IXUSR)
        try:
            with self.assertRaises(OSError):
                self.store.add_image(sid, JPG, META)
        finally:
            images.chmod(stat.S_IRWXU)
        m = self.store.manifest(sid)
        self.assertEqual(m["counts"]["failed"], 1)
        self.assertEqual(m["images"][0]["capture_status"], "failed")
        self.assertEqual(self.store.images(sid), [])
        self.assertEqual(len(self.store.images(sid, include_failed=True)), 1)

    def test_bad_input(self):
        sid = self.store.create_scan(RECORD)["scan_id"]
        with self.assertRaises(ScanError):
            self.store.add_image(sid, b"not an image", META)
        with self.assertRaises(ScanError):
            self.store.add_image("../etc", JPG, META)
        with self.assertRaises(ScanError):
            self.store.manifest("scan-20260101-000000-abcdef")

    def test_finish_list_batches_truth_and_detections(self):
        sid = self.store.create_scan(RECORD)["scan_id"]
        for i in range(5):
            self.store.add_image(sid, JPG[:-2] + bytes([i]) + b"\xff\xd9", dict(META, sequence_number=i + 1))
        self.store.set_ground_truth(sid, {"object": "Phone", "position": [3, -0.5, 4]})
        self.store.finish_scan(sid, {"status": "incomplete", "coverage": {"seen": 70}})
        self.assertEqual([len(b) for b in self.store.iter_batches(sid, 2)], [2, 2, 1])
        m = self.store.manifest(sid)
        self.assertEqual(m["status"], "incomplete")
        self.assertIsNotNone(m["ended_at"])
        self.assertNotIn("[3, -0.5, 4]", json.dumps(m))          # ground truth never in the manifest
        with self.assertRaises(ScanError):
            self.store.add_image(sid, JPG2, META)                   # finished scans take no more images
        self.assertEqual(self.store.list_scans()[0]["scan_id"], sid)

    def test_apply_inference_positive_negative(self):
        sid = self.store.create_scan(RECORD)["scan_id"]
        meta = dict(META, camera={"width": 640, "height": 640},
                    ground_footprint={"center": [10.0, 20.0], "size": [6.4, 6.4], "altitude": 6.0})
        pos = self.store.add_image(sid, JPG, meta)
        neg = self.store.add_image(sid, JPG2, dict(meta, sequence_number=2))
        box = {"x": 0.70, "y": 0.20, "w": 0.10, "h": 0.10}          # center (0.75, 0.25)
        result = {"engine": "items", "latencyMs": 30, "objects": [{"category": "phone", "confidence": 0.9, "bbox": box}]}
        self.store.apply_inference(sid, pos["image_id"], result, result["objects"])
        self.store.apply_inference(sid, neg["image_id"], {"engine": "items", "objects": []}, [])
        root = Path(self.tmp.name) / sid
        self.assertFalse((root / pos["image_path"]).exists())          # moved, not copied
        self.assertTrue((root / "detections" / Path(pos["image_path"]).name).exists())
        self.assertFalse((root / neg["image_path"]).exists())          # negative discarded
        d = self.store.detections(sid)
        self.assertEqual(d["inference"]["positive"], 1)
        self.assertEqual(d["inference"]["negative"], 1)
        det = d["detections"][0]
        self.assertEqual((det["image_id"], det["category"], det["confidence"]), (pos["image_id"], "phone", 0.9))
        w = det["world_position"]
        self.assertAlmostEqual(w["x"], 10.0 + 0.25 * 6.4, places=3)     # right of center = +x
        self.assertAlmostEqual(w["z"], 20.0 + 0.25 * 6.4, places=3)     # above center = +z
        self.assertEqual(det["bbox_pixels"], {"x": 448.0, "y": 128.0, "w": 64.0, "h": 64.0})
        s = d["sightings"]
        self.assertEqual((len(s), s[0]["photos_detected"], s[0]["photos_covering"]), (1, 1, 2))   # both photos covered it
        self.assertLessEqual(s[0]["photos_detected"], s[0]["photos_covering"])
        self.assertEqual(self.store.read_image(sid, pos["image_id"]), JPG)
        # Applying again never duplicates results.
        self.store.apply_inference(sid, pos["image_id"], result, result["objects"])
        self.assertEqual(len(self.store.detections(sid)["detections"]), 1)
        self.assertIsNone(self.store.mark_inference(sid, pos["image_id"], "queued"))
        m = self.store.manifest(sid)
        self.assertTrue(m["images"][1]["image_discarded"])
        self.assertEqual([e["image_id"] for e in self.store.images(sid)], [pos["image_id"]])


class StubVision:
    """Stand-in for VisionServer's HTTP contract (test plumbing only; the real model is tested end to end)."""

    def __init__(self, objects):
        from http.server import BaseHTTPRequestHandler
        outer = self
        self.calls = 0

        class H(BaseHTTPRequestHandler):
            def do_POST(self):
                self.rfile.read(int(self.headers["Content-Length"]))
                outer.calls += 1
                body = json.dumps({"engine": "stub", "error": "", "objects": objects}).encode()
                self.send_response(200); self.send_header("Content-Length", str(len(body))); self.end_headers()
                self.wfile.write(body)

            def log_message(self, *a):
                pass

        self.srv = ThreadingHTTPServer(("127.0.0.1", 0), H)
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()
        self.url = f"http://127.0.0.1:{self.srv.server_address[1]}"

    def close(self):
        self.srv.shutdown(); self.srv.server_close()


class InferenceWorkerTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.store = ScanStore(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def wait(self, sid, n):
        import time
        for _ in range(200):
            c = self.store.detections(sid)["inference"]
            if c["analyzed"] + c["failed"] >= n:
                return c
            time.sleep(0.05)
        self.fail("inference did not finish")

    def test_target_class_only_and_failures(self):
        import scan_server
        stub = StubVision([{"category": "key", "confidence": 0.8, "bbox": {"x": 0.4, "y": 0.4, "w": 0.1, "h": 0.1}},
                           {"category": "phone", "confidence": 0.95, "bbox": {"x": 0.1, "y": 0.1, "w": 0.1, "h": 0.1}}])
        try:
            inf = scan_server.Inference(self.store, stub.url, conf=0.25)
            sid = self.store.create_scan(dict(RECORD, target_category="keys"))["scan_id"]
            e = self.store.add_image(sid, JPG, META)
            inf.submit(sid, e["image_id"])
            c = self.wait(sid, 1)
            d = self.store.detections(sid)["detections"]
            self.assertEqual(c["positive"], 1)
            self.assertEqual([x["category"] for x in d], ["key"])        # the phone is not the target
        finally:
            stub.close()
        # Model server down: the image is kept and marked failed, not negative.
        inf = scan_server.Inference(self.store, "http://127.0.0.1:9", conf=0.25, timeout=1)
        sid = self.store.create_scan(RECORD)["scan_id"]
        e = self.store.add_image(sid, JPG2, META)
        inf.submit(sid, e["image_id"])
        c = self.wait(sid, 1)
        self.assertEqual((c["failed"], c["negative"]), (1, 0))
        self.assertEqual(len(self.store.images(sid)), 1)                # file kept for a retry
        self.assertIn("error", self.store.manifest(sid)["images"][0]["inference"])
        # Taken above the 2.5-4 m band: never sent to the model.
        stub = StubVision([])
        try:
            inf = scan_server.Inference(self.store, stub.url, conf=0.25)
            sid = self.store.create_scan(RECORD)["scan_id"]
            e = self.store.add_image(sid, JPG, dict(META, ground_footprint={"center": [1, 2], "size": [7, 7], "altitude": 6.0}))
            inf.submit(sid, e["image_id"])
            import time
            for _ in range(100):
                if self.store.detections(sid)["inference"]["skipped_altitude"]: break
                time.sleep(0.05)
            self.assertEqual(self.store.detections(sid)["inference"]["skipped_altitude"], 1)
            self.assertEqual(stub.calls, 0)
        finally:
            stub.close()


class ScanServerTest(unittest.TestCase):
    def setUp(self):
        import scan_server
        self.tmp = tempfile.TemporaryDirectory()
        scan_server.Handler.store = ScanStore(self.tmp.name)
        scan_server.Handler.inference = None
        self.srv = ThreadingHTTPServer(("127.0.0.1", 0), partial(scan_server.Handler, directory=str(scan_server.REPO)))
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()
        self.base = f"http://127.0.0.1:{self.srv.server_address[1]}"

    def tearDown(self):
        self.srv.shutdown()
        self.srv.server_close()
        self.tmp.cleanup()

    def call(self, path, body=None):
        data = json.dumps(body).encode() if body is not None else None
        req = urllib.request.Request(self.base + path, data=data, headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req) as r:
                return r.status, json.loads(r.read())
        except urllib.error.HTTPError as e:
            return e.code, json.loads(e.read())

    def test_flow(self):
        code, m = self.call("/api/scans", RECORD)
        self.assertEqual(code, 201)
        sid = m["scan_id"]
        code, e = self.call(f"/api/scans/{sid}/images", dict(META, image_b64=base64.b64encode(JPG).decode()))
        self.assertEqual(code, 201)
        self.assertEqual(e["capture_status"], "saved")
        code, _ = self.call(f"/api/scans/{sid}/images", dict(META, image_b64="!!!"))
        self.assertEqual(code, 400)
        self.assertEqual(self.call(f"/api/scans/{sid}/ground_truth", {"position": [1, 2, 3]})[0], 200)
        self.assertEqual(self.call(f"/api/scans/{sid}/finish", {"status": "complete"})[1]["status"], "complete")
        code, lst = self.call("/api/scans")
        self.assertEqual(lst["scans"][0]["counts"]["saved"], 1)
        with urllib.request.urlopen(self.base + f"/api/scans/{sid}") as r:
            self.assertEqual(json.loads(r.read())["images"][0]["image_id"], e["image_id"])
        with urllib.request.urlopen(self.base + "/city.html") as r:   # static site still served
            self.assertIn(b"Drone City", r.read())


if __name__ == "__main__":
    unittest.main()
