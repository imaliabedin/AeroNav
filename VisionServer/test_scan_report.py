"""Area scan (target=all) and the /report endpoint, with a fake detector (no model needed).

    cd VisionServer && python3 -m unittest test_scan_report -v
"""

import io
import json
import os
import tempfile
import threading
import unittest
import urllib.request
from http.server import ThreadingHTTPServer

from PIL import Image

import report
import server
from image_store import ImageStore


class FakeScanEngine:
    name = "fake"

    def detect(self, image, target):
        return [(0.8, (0.4, 0.4, 0.1, 0.1), target)]

    def detect_all(self, image, conf):
        return [(0.9, (0.10, 0.10, 0.20, 0.30), "car", "car"),
                (0.6, (0.60, 0.60, 0.05, 0.05), "backpack", "backpack"),
                (0.1, (0.80, 0.10, 0.05, 0.05), "keys", "key")]   # below a 0.25 threshold


class KeyPhoneOnlyEngine:
    name = "key-phone-only"

    def detect(self, image, target):
        return []


def jpeg(shade=60):
    buf = io.BytesIO()
    Image.new("RGB", (64, 64), (shade, 60, 60)).save(buf, format="JPEG")
    return buf.getvalue()


def payload(session, image_id):
    return {
        "sessionId": session, "status": "complete", "startedAtUtc": "2026-10-10T12:00:00+00:00",
        "finishedAtUtc": "2026-10-10T12:01:00+00:00", "navigation": "Sonar", "durationSeconds": 60.0,
        "xMin": 0, "xMax": 4, "zMin": 0, "zMax": 2, "altitude": 4.5, "footprint": 5.2, "laneSpacing": 3.9,
        "confidenceThreshold": 0.25, "lanes": 1, "waypointsPlanned": 3, "waypointsReached": 3, "waypointsSkipped": 0,
        "framesAnalyzed": 10, "detectionsTotal": 4, "detectionsOutsideArea": 1, "coveragePercent": 87.5,
        "coverageCols": 4, "coverageRows": 2, "coverage": "11111110",
        "home": {"x": 1, "y": 1, "z": 1},
        "waypoints": [{"x": 0, "y": 4, "z": 1}, {"x": 4, "y": 4, "z": 1}, {"x": 1, "y": 1, "z": 1}],
        "path": [{"x": 0, "y": 4, "z": 1}, {"x": 2, "y": 4, "z": 1}, {"x": 4, "y": 4, "z": 1}],
        "objects": [
            {"id": 1, "category": "backpack", "label": "backpack", "confidence": 0.6, "observations": 3, "confirmed": True,
             "position": {"x": 2.1, "y": -0.4, "z": 1.0}, "hitObject": True, "imageId": image_id,
             "bbox": {"x": 0.25, "y": 0.25, "w": 0.2, "h": 0.2}, "firstSeenUtc": "2026-10-10T12:00:30+00:00",
             "nearestSceneObject": "Backpack", "nearestSceneDistance": 0.2},
            {"id": 2, "category": "phone", "label": "cell phone", "confidence": 0.3, "observations": 1, "confirmed": False,
             "position": {"x": 3.0, "y": -0.5, "z": 0.5}, "hitObject": False, "imageId": "",
             "bbox": {"x": 0, "y": 0, "w": 0, "h": 0}, "firstSeenUtc": "2026-10-10T12:00:40+00:00",
             "nearestSceneObject": "Sedan", "nearestSceneDistance": 2.5},
        ],
        "groundTruth": [
            {"name": "Backpack", "category": "backpack", "position": {"x": 2.0, "y": -0.5, "z": 1.1}, "matched": True},
            {"name": "Keys", "category": "key", "position": {"x": 3.5, "y": -0.5, "z": 1.5}, "matched": False},
        ],
    }


class ScanDetectionTest(unittest.TestCase):
    def test_all_returns_every_object_above_threshold(self):
        r = server.run_detection(FakeScanEngine(), jpeg(), "all", 0.25)
        self.assertEqual(r["error"], "")
        self.assertEqual([o["category"] for o in r["objects"]], ["car", "backpack"])
        self.assertTrue(r["detected"])
        self.assertEqual(r["category"], "car")
        self.assertEqual(r["bbox"], {"x": 0.1, "y": 0.1, "w": 0.2, "h": 0.3})

    def test_all_with_nothing_found(self):
        r = server.run_detection(FakeScanEngine(), jpeg(), "all", 0.95)
        self.assertEqual(r["objects"], [])
        self.assertFalse(r["detected"])
        self.assertEqual(r["category"], "all")

    def test_engine_without_scan_support_reports_error(self):
        r = server.run_detection(KeyPhoneOnlyEngine(), jpeg(), "all", 0.25)
        self.assertIn("does not support target=all", r["error"])

    def test_key_phone_requests_unchanged(self):
        r = server.run_detection(FakeScanEngine(), jpeg(), "phone", 0.3)
        self.assertNotIn("objects", r)
        self.assertTrue(r["detected"])
        self.assertEqual(r["category"], "phone")

    def test_iou(self):
        self.assertAlmostEqual(server._iou((0, 0, 1, 1), (0, 0, 1, 1)), 1.0)
        self.assertEqual(server._iou((0, 0, 1, 1), (2, 2, 1, 1)), 0.0)


class ReportTest(unittest.TestCase):
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

    def serve(self):
        handler = server.make_handler(FakeScanEngine(), self.store, True, os.path.join(self.tmp.name, "reports"))
        self.httpd = ThreadingHTTPServer(("127.0.0.1", 0), handler)
        threading.Thread(target=self.httpd.serve_forever, daemon=True).start()

    def post(self, path, data, ctype):
        url = f"http://127.0.0.1:{self.httpd.server_address[1]}{path}"
        with urllib.request.urlopen(urllib.request.Request(url, data, {"Content-Type": ctype}), timeout=10) as r:
            return json.load(r)

    def test_scan_frame_is_stored_and_report_has_evidence(self):
        self.serve()
        session = "run-20261010-120000-abc123"
        r = self.post(f"/detect?target=all&conf=0.25&session={session}&x=1&y=4&z=1&alt=4.5", jpeg(), "image/jpeg")
        self.assertTrue(r["imageId"])
        self.assertEqual(len(r["objects"]), 2)
        self.assertEqual(len(self.store.list_images(session, detected_only=True)), 1)

        out = self.post("/report", json.dumps(payload(session, r["imageId"])).encode(), "application/json")
        self.assertTrue(out["ok"], out["error"])
        folder = out["dir"]
        for f in ("report.html", "report.json", "scan.json", "map.png", "evidence/object_1.jpg", "evidence/object_1_crop.jpg"):
            self.assertTrue(os.path.exists(os.path.join(folder, f)), f)
        self.assertFalse(os.path.exists(os.path.join(folder, "evidence/object_2.jpg")))   # no stored frame
        rep = json.load(open(os.path.join(folder, "report.json")))
        v = rep["summary"]["validation"]
        self.assertEqual((v["sceneObjectsInArea"], v["found"], v["recallPercent"]), (2, 1, 50.0))
        self.assertEqual(rep["summary"]["objectsConfirmed"], 1)
        self.assertEqual(rep["summary"]["framesWithSightings"], 1)
        html = open(os.path.join(folder, "report.html")).read()
        self.assertIn("evidence/object_1_crop.jpg", html)
        self.assertIn("missed", html)

    def test_bad_session_is_rejected(self):
        self.serve()
        out = self.post("/report", json.dumps(payload("../etc", "")).encode(), "application/json")
        self.assertFalse(out["ok"])
        self.assertIn("sessionId", out["error"])

    def test_rebuild_from_saved_scan_json(self):
        session = "run-20261010-120000-def456"
        rec = self.store.save_image(jpeg(90), session, position=(1, 4, 1))
        p = payload(session, rec.image_id)
        out = report.build_report(p, self.store, os.path.join(self.tmp.name, "reports"))
        again = report.build_report(json.load(open(os.path.join(out["dir"], "scan.json"))), self.store,
                                    os.path.join(self.tmp.name, "reports"))
        self.assertEqual(out["html"], again["html"])
        with Image.open(os.path.join(out["dir"], "map.png")) as m:
            self.assertGreater(m.width, 100)


if __name__ == "__main__":
    unittest.main()
