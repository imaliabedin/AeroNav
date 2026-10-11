using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// Batch-mode checks for the area scan. Results go to the log ([ScanTest] lines).
///   Unity -batchmode -projectPath Unity -executeMethod ScanTests.Geometry
///   SCAN_SERVER=http://127.0.0.1:8765/ Unity -batchmode -projectPath Unity -executeMethod ScanTests.Mission
/// Mission needs scan_server.py running at SCAN_SERVER.
public static class ScanTests
{
    static int failures;
    static void Log(string msg) => Debug.Log("[ScanTest] " + msg);
    static void Check(bool ok, string what) { Log((ok ? "ok   " : "FAIL ") + what); if (!ok) failures++; }

    static void Finish()
    {
        Log(failures == 0 ? "RESULT PASS" : $"RESULT FAIL ({failures})");
        EditorApplication.Exit(failures == 0 ? 0 : 1);
    }

    // ---------- geometry: regions, lanes, footprint, camera ----------

    public static void Geometry()
    {
        try { GeometryChecks(); } catch (System.Exception e) { Debug.LogException(e); failures++; }
        Finish();
    }

    static void GeometryChecks()
    {
        var cfg = new ScanPlanner.Config();
        var fp = ScanPlanner.Footprint(6f, 60f, 1f);
        Check(Mathf.Abs(fp.x - 6.928f) < 0.01f && Mathf.Abs(fp.y - 6.928f) < 0.01f, $"footprint at 6 m, 60° = {fp}");

        // Ordering and validation.
        var order = ScanPlanner.OrderAndValidate(new[] { V(20, 15), V(0, 0), V(0, 15), V(20, 0) }, cfg, out var err);
        Check(order != null && order[0] == V(0, 0) && order[1] == V(20, 0) && order[2] == V(20, 15), "corners ordered ccw from min x+z");
        Check(ScanPlanner.OrderAndValidate(new[] { V(0, 0), V(10, 0), V(20, 0), V(5, 10) }, cfg, out err) == null, "three in a line rejected: " + err);
        Check(ScanPlanner.OrderAndValidate(new[] { V(0, 0), V(20, 0), V(5, 5), V(0, 20) }, cfg, out err) == null, "concave rejected: " + err);
        Check(ScanPlanner.OrderAndValidate(new[] { V(0, 0), V(2, 0), V(2, 2), V(0, 2) }, cfg, out err) == null, "tiny rejected: " + err);
        Check(ScanPlanner.OrderAndValidate(new[] { V(0, 0), V(0, 0), V(9, 9), V(0, 9) }, cfg, out err) == null, "duplicate rejected: " + err);
        Check(ScanPlanner.OrderAndValidate(new[] { V(0, 0), V(300, 0), V(300, 300), V(0, 300) }, cfg, out err) == null, "huge rejected: " + err);

        // Lanes over a rectangle, a trapezoid and a skewed quad: every cell inside some planned footprint,
        // spacing within the overlap budget, lane lengths follow the polygon.
        var shapes = new Dictionary<string, Vector2[]>
        {
            ["rectangle 40x23"] = new[] { V(0, 0), V(40, 0), V(40, 23), V(0, 23) },
            ["trapezoid"] = new[] { V(0, 0), V(50, 0), V(38, 30), V(12, 30) },
            ["skewed quad"] = new[] { V(3, -4), V(31, 2), V(26, 27), V(-2, 18) },
            ["narrow strip"] = new[] { V(0, 0), V(60, 0), V(60, 4), V(0, 4) },
        };
        foreach (var kv in shapes)
        {
            var poly = ScanPlanner.OrderAndValidate(kv.Value, cfg, out err);
            Check(poly != null, kv.Key + " valid " + err);
            if (poly == null) continue;
            var plan = ScanPlanner.Build(poly, cfg);
            int missed = ScanPlanner.Cells(poly).Count(c => !plan.lanes.Any(l => l.captures.Any(p => ScanPlanner.InFootprint(p, plan.footprint, c))));
            var usable = plan.footprint - Vector2.one * (2f * cfg.edgeMargin);
            float across = plan.lanesAlongX ? usable.y : usable.x;
            float along = plan.lanesAlongX ? usable.x : usable.y;
            float maxGap = 0f;
            foreach (var l in plan.lanes)
                for (int i = 1; i < l.captures.Count; i++) maxGap = Mathf.Max(maxGap, Vector2.Distance(l.captures[i - 1], l.captures[i]));
            string lens = string.Join(",", plan.lanes.Select(l => l.Length.ToString("0.0")));
            Check(missed == 0, $"{kv.Key}: {plan.lanes.Count} lanes, {plan.CaptureCount} images, spacing {plan.laneSpacing:0.00}/{maxGap:0.00} m, lane lengths [{lens}], missed cells {missed}");
            Check(plan.laneSpacing <= across * (1f - cfg.overlap) + 1e-3f && maxGap <= along * (1f - cfg.overlap) + 1e-3f, kv.Key + ": spacing keeps the configured overlap");
            // No unnecessary lanes: one fewer lane would exceed the overlap budget.
            float span = plan.lanesAlongX ? poly.Max(p => p.y) - poly.Min(p => p.y) : poly.Max(p => p.x) - poly.Min(p => p.x);
            int n = plan.lanes.Count;
            Check(n == 1 || (span - across) / (n - 2 == 0 ? 1e-6f : n - 2) > across * (1f - cfg.overlap) - 1e-3f, kv.Key + ": minimal lane count");
        }
        var trap = ScanPlanner.Build(ScanPlanner.OrderAndValidate(shapes["trapezoid"], cfg, out err), cfg);
        Check(trap.lanes.First().Length > trap.lanes.Last().Length + 10f, "trapezoid lanes shrink toward the short side");

        // The real camera: points straight down and its view matches the computed footprint.
        var go = new GameObject("Test drone");
        go.transform.position = new Vector3(10f, 5.62f, 20f);
        var dc = go.AddComponent<DroneCamera>();
        dc.EnsureCamera();
        var cam = dc.Cam;
        Check(Vector3.Dot(cam.transform.forward, Vector3.down) > 0.9999f, $"camera forward {cam.transform.forward} is straight down");
        Check(Vector3.Dot(cam.transform.up, Vector3.forward) > 0.9999f, "image top is world +Z");
        float alt = cam.transform.position.y - (-0.5f);
        var f = ScanPlanner.Footprint(alt, cam.fieldOfView, cam.aspect);
        var c0 = new Vector3(10f, -0.5f, 20f);
        bool inside = true, outside = true;
        foreach (var s in new[] { new Vector2(1, 1), new Vector2(-1, 1), new Vector2(1, -1), new Vector2(-1, -1) })
        {
            var pin = cam.WorldToViewportPoint(c0 + new Vector3(s.x * f.x * 0.49f, 0f, s.y * f.y * 0.49f));
            var pout = cam.WorldToViewportPoint(c0 + new Vector3(s.x * f.x * 0.51f, 0f, s.y * f.y * 0.51f));
            inside &= pin.x > 0f && pin.x < 1f && pin.y > 0f && pin.y < 1f;
            outside &= pout.x < 0f || pout.x > 1f || pout.y < 0f || pout.y > 1f;
        }
        Check(inside && outside, $"footprint {f.x:0.00} x {f.y:0.00} m matches the camera's view edges at {alt:0.00} m");

        // The saved JPEG is not flipped: a red marker north (+Z) of the drone shows in the image's top half,
        // a blue one east (+X) in its right half.
        var red = Marker(c0 + new Vector3(0f, 0.2f, f.y * 0.3f), Color.red);
        var blue = Marker(c0 + new Vector3(f.x * 0.3f, 0.2f, 0f), Color.blue);
        var tex = new Texture2D(2, 2);
        tex.LoadImage(dc.CaptureJpg());
        Vector2 Centroid(System.Func<Color, bool> match)
        {
            Vector2 sum = Vector2.zero; int n = 0;
            for (int y = 0; y < tex.height; y += 2)
                for (int x = 0; x < tex.width; x += 2)
                    if (match(tex.GetPixel(x, y))) { sum += new Vector2(x / (float)tex.width, 1f - y / (float)tex.height); n++; }   // (0,0) = top-left
            return n > 0 ? sum / n : new Vector2(-1f, -1f);
        }
        var r = Centroid(cl => cl.r > 0.6f && cl.g < 0.3f && cl.b < 0.3f);
        var bl = Centroid(cl => cl.b > 0.6f && cl.r < 0.3f && cl.g < 0.3f);
        Check(r.y >= 0f && r.y < 0.45f && Mathf.Abs(r.x - 0.5f) < 0.1f, $"north marker at image ({r.x:0.00}, {r.y:0.00}): top half");
        Check(bl.x > 0.55f && Mathf.Abs(bl.y - 0.5f) < 0.1f, $"east marker at image ({bl.x:0.00}, {bl.y:0.00}): right half");
        Object.DestroyImmediate(red); Object.DestroyImmediate(blue); Object.DestroyImmediate(tex);
        Object.DestroyImmediate(go);
    }

    static Vector2 V(float x, float z) => new Vector2(x, z);

    static GameObject Marker(Vector3 pos, Color c)
    {
        var m = GameObject.CreatePrimitive(PrimitiveType.Cube);
        m.transform.position = pos;
        m.transform.localScale = new Vector3(0.8f, 0.4f, 0.8f);
        var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color"));
        mat.color = c;
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
        m.GetComponent<Renderer>().sharedMaterial = mat;
        return m;
    }

    // ---------- full mission in the city: capture, storage, occlusion, revisits ----------

    public static void Mission()
    {
        try { MissionChecks(); } catch (System.Exception e) { Debug.LogException(e); failures++; }
        Finish();
    }

    static void MissionChecks()
    {
        string server = System.Environment.GetEnvironmentVariable("SCAN_SERVER") ?? "http://127.0.0.1:8765/";
        EditorSceneManager.OpenScene("Assets/Scenes/DroneCity.unity", OpenSceneMode.Single);
        CityColliders.Add();
        var sim = Object.FindAnyObjectByType<SimulationManager>();
        sim.map.RebuildRegistry();
        ScanMission.Verbose = false;
        // Region across the street at z = -8 (carriageway z -13..-3), south of the lot.
        var corners = new[] { new Vector2Int(6, -18), new Vector2Int(34, -18), new Vector2Int(34, -1), new Vector2Int(6, -1) };

        // 1) Lanes only (no revisits) at a slow and a fast cruise speed: same number of images, both cover every
        //    cell (no gaps from speed changes or turns). A parked "moving" car on the road is seen as an occlusion.
        foreach (float v in new[] { 2f, 10f })
        {
            var car = FakeCar(new Vector3(20f, -0.5f, -9f));
            var m = NewMission(sim, server, corners, v, 0);
            Run(m, 20000, null);
            var c = m.Coverage.Counts();
            Check(m.State == ScanMission.Phase.Done, $"speed {v}: finished ({m.State})");
            Check(m.Captured + m.SkippedAltitude == m.Plan.CaptureCount,
                  $"speed {v}: every planned position reached: {m.Captured} photos + {m.SkippedAltitude} skipped above 4 m = {m.Plan.CaptureCount}");
            Check(m.Saved == m.Captured && m.Failed == 0, $"speed {v}: all {m.Saved} saved");
            Check(c["occluded_moving"] > 0, $"speed {v}: car hides {c["occluded_moving"]} road cells (reported, not counted as seen)");
            // Gaps only where a photo was skipped for altitude (never between photos).
            var fp = ScanPlanner.Footprint(m.config.altitude, m.config.verticalFovDeg, m.config.aspect) + Vector2.one * 0.6f;
            int stray = m.Coverage.cells.Values.Count(cell => cell.state == ScanCoverage.State.Unseen &&
                            !m.SkippedPositions.Any(sp => ScanPlanner.InFootprint(sp, fp, cell.c)));
            Check(stray == 0, $"speed {v}: {c["unseen"]} unseen cells, all at skipped (too high) spots ({stray} elsewhere)");
            var man = Get(server + "api/scans/" + m.ScanId);
            var alts = System.Text.RegularExpressions.Regex.Matches(man, "\"altitude\": ([0-9.]+)").Select(x => float.Parse(x.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
            Check(alts.Count == m.Saved && alts.All(a => a >= 2.45f && a <= 4.05f), $"speed {v}: all {alts.Count} stored photos taken at 2.5-4 m (min {alts.DefaultIfEmpty().Min():0.00}, max {alts.DefaultIfEmpty().Max():0.00})");
            Log($"speed {v}: coverage " + string.Join(", ", c.Select(kv => kv.Key + "=" + kv.Value)));
            Object.DestroyImmediate(car);
            Object.DestroyImmediate(m.gameObject);
        }

        // 2) With revisits: the car drives off after the lanes; the hidden road cells get a second look and are seen.
        {
            var car = FakeCar(new Vector3(20f, -0.5f, -9f));
            var m = NewMission(sim, server, corners, 5f, 3);
            int carCells = 0;
            Run(m, 30000, mm =>
            {
                if (car != null && mm.State == ScanMission.Phase.Revisiting)
                {
                    carCells = mm.Coverage.Counts()["occluded_moving"];
                    Object.DestroyImmediate(car);   // the car has driven away
                    Physics.SyncTransforms();
                }
            });
            var c = m.Coverage.Counts();
            Check(carCells > 0, $"revisit: {carCells} occluded cells scheduled");
            Check(m.Captured + m.SkippedAltitude > m.Plan.CaptureCount, $"revisit: {m.Captured + m.SkippedAltitude - m.Plan.CaptureCount} revisit photos attempted");
            Check(c["occluded_moving"] == 0, "revisit: occluded cells resolved");
            Check(m.State == ScanMission.Phase.Done, "revisit: done");
            Log("revisit coverage " + string.Join(", ", c.Select(kv => kv.Key + "=" + kv.Value)));
            CheckManifest(server, m);
            if (car) Object.DestroyImmediate(car);
            Object.DestroyImmediate(m.gameObject);
        }

        // 3) A car that never leaves: still occluded after max looks, reported as incomplete (never "complete").
        {
            var car = FakeCar(new Vector3(20f, -0.5f, -9f));
            var m = NewMission(sim, server, corners, 5f, 2);
            Run(m, 30000, null);
            var c = m.Coverage.Counts();
            Check(c["occluded_moving"] > 0 && m.State == ScanMission.Phase.Done, $"stuck car: {c["occluded_moving"]} cells unresolved after {m.maxLooks} looks");
            var status = Get(server + "api/scans/" + m.ScanId);
            Check(status.Contains("\"status\": \"incomplete\""), "stuck car: scan stored as incomplete");
            Object.DestroyImmediate(car);
            Object.DestroyImmediate(m.gameObject);
        }

        // 4) Buildings are not searched: their ground is excluded, no photo is planned over them, and a full run
        //    never photographs a roof (every stored photo is 2.5-4 m above the street).
        {
            var bc = new[] { new Vector2Int(-30, 10), new Vector2Int(10, 10), new Vector2Int(10, 40), new Vector2Int(-30, 40) };
            var m = NewMission(sim, server, bc, 10f, 1);
            int buildings = m.Coverage.buildings;
            var openSet = new HashSet<Vector2Int>(m.Coverage.cells.Keys);
            int overBuildings = 0;
            foreach (var lane in m.Plan.lanes)
                foreach (var cap in lane.captures)
                    if (!openSet.Any(c => ScanPlanner.InFootprint(cap, m.Plan.footprint, c))) overBuildings++;
            Check(buildings > 100, $"buildings: {buildings} m² of the region excluded, {m.Coverage.cells.Count} m² of open ground searched");
            Check(overBuildings == 0, $"buildings: {m.Plan.CaptureCount} planned photos, none only over a building");
            Run(m, 60000, null);
            var c = m.Coverage.Counts();
            Check(m.State == ScanMission.Phase.Done && c["buildings"] == buildings, $"buildings: scan done, buildings reported separately ({c["buildings"]} m²), unseen {c["unseen"]}");
            Object.DestroyImmediate(m.gameObject);
        }

        // 5) Server unreachable: the scan does not start, and says why.
        {
            var m = NewMission(sim, "http://127.0.0.1:9/", corners, 5f, 0);
            Run(m, 400, null);
            Check(m.State == ScanMission.Phase.Error && m.Captured == 0, $"no server: refuses to scan ({m.State})");
            Object.DestroyImmediate(m.gameObject);
        }
    }

    /// Renders the region overlay (fading green wall, posts, route) among the city's buildings to PNGs in
    /// SCAN_TEST_OUT, for a visual check of the height fade and building occlusion.
    public static void Visual()
    {
        try
        {
            string outDir = System.Environment.GetEnvironmentVariable("SCAN_TEST_OUT") ?? System.IO.Path.GetTempPath();
            EditorSceneManager.OpenScene("Assets/Scenes/DroneCity.unity", OpenSceneMode.Single);
            var sim = Object.FindAnyObjectByType<SimulationManager>();
            var m = new GameObject("ScanControls").AddComponent<ScanMission>();
            ScanMission.Verbose = false;
            m.InitForTest(sim, "http://127.0.0.1:9/");
            // Across the street at x = -8, into the block west of it (buildings inside the region and around it).
            foreach (var c in new[] { new Vector2Int(-30, 10), new Vector2Int(10, 10), new Vector2Int(10, 40), new Vector2Int(-30, 40) }) m.AddCorner(c);
            Check(m.State == ScanMission.Phase.Ready, "region valid");
            var go = new GameObject("Test view");
            var cam = go.AddComponent<Camera>();
            cam.fieldOfView = 60f;
            cam.targetTexture = new RenderTexture(1280, 720, 24);
            var views = new Dictionary<string, (Vector3 pos, Vector3 look)>
            {
                ["overview"] = (new Vector3(-10f, 45f, -25f), new Vector3(-10f, 0f, 25f)),
                ["low"] = (new Vector3(30f, 8f, -5f), new Vector3(-10f, 2f, 25f)),
            };
            foreach (var kv in views)
            {
                go.transform.position = kv.Value.pos;
                go.transform.LookAt(kv.Value.look);
                // Sonar-style rings (same path as SonarVisualizer: line mesh + shared material), straddling a building.
                Graphics.DrawMesh(TestRings(new Vector3(-14f, 2f, 30f), 9f), Matrix4x4.identity, HeightFade.SharedMaterial, 0, cam);
                cam.Render();
                RenderTexture.active = cam.targetTexture;
                var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
                RenderTexture.active = null;
                var path = System.IO.Path.Combine(outDir, "scan_overlay_" + kv.Key + ".png");
                System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
                Log("wrote " + path);
            }
            var mat = HeightFade.SharedMaterial;
            Check(mat.GetInt("_ZTest") == (int)UnityEngine.Rendering.CompareFunction.LessEqual && mat.GetInt("_ZWrite") == 0, "shared material is depth-tested, no depth writes");
            Check(Mathf.Approximately(HeightFade.Alpha(HeightFade.GroundY), 1f) && HeightFade.Alpha(HeightFade.GroundY + HeightFade.Height) == 0f
                  && Mathf.Abs(HeightFade.Alpha(HeightFade.GroundY + HeightFade.Height * 0.5f) - 0.5f) < 1e-4f, "alpha 1 at ground, 0.5 halfway, 0 at the top");
        }
        catch (System.Exception e) { Debug.LogException(e); failures++; }
        Finish();
    }

    static Mesh TestRings(Vector3 o, float r)
    {
        var v = new List<Vector3>(); var c = new List<Color>(); var idx = new List<int>();
        var blue = new Color(0.3f, 0.85f, 1f, 1f);
        foreach (var (u, w) in new[] { (Vector3.right, Vector3.forward), (Vector3.right, Vector3.up), (Vector3.forward, Vector3.up) })
        {
            int first = v.Count;
            for (int i = 0; i < 96; i++)
            {
                float a = i * Mathf.PI * 2f / 96;
                var p = o + (u * Mathf.Cos(a) + w * Mathf.Sin(a)) * r;
                v.Add(p); c.Add(HeightFade.At(blue, p.y)); idx.Add(first + i); idx.Add(first + (i + 1) % 96);
            }
        }
        var m = new Mesh();
        m.SetVertices(v); m.SetColors(c); m.SetIndices(idx, MeshTopology.Lines, 0);
        return m;
    }

    static ScanMission NewMission(SimulationManager sim, string server, Vector2Int[] corners, float speed, int maxLooks)
    {
        var m = new GameObject("ScanControls").AddComponent<ScanMission>();
        m.InitForTest(sim, server);
        m.cruiseSpeed = speed;
        m.maxLooks = maxLooks;
        m.revisitWait = 0.2f;
        sim.drone.transform.position = new Vector3(20f, 0f, 5f);
        foreach (var c in corners) m.AddCorner(c);
        m.SetObject("Phone");
        m.StartScan();
        return m;
    }

    static void Run(ScanMission m, int maxTicks, System.Action<ScanMission> each)
    {
        for (int i = 0; i < maxTicks; i++)
        {
            m.Tick(0.05f);
            Physics.SyncTransforms();
            each?.Invoke(m);
            bool waiting = m.PendingUploads > 0 || !m.ControlIdle || m.State == ScanMission.Phase.Starting;
            if (waiting) System.Threading.Thread.Sleep(2);
            bool over = m.State == ScanMission.Phase.Done || m.State == ScanMission.Phase.Error || m.State == ScanMission.Phase.Stopped;
            if (over && m.ControlIdle) return;
        }
        Log($"ran out of ticks in state {m.State}");
    }

    /// A stand-in for a CityLife car: same name pattern, solid box.
    static GameObject FakeCar(Vector3 groundPos)
    {
        var car = GameObject.CreatePrimitive(PrimitiveType.Cube);
        car.name = "Traffic Test car";
        car.transform.position = groundPos + Vector3.up * 1.2f;
        car.transform.localScale = new Vector3(4.6f, 2.4f, 2.2f);
        Physics.SyncTransforms();
        return car;
    }

    static void CheckManifest(string server, ScanMission m)
    {
        var json = Get(server + "api/scans/" + m.ScanId);
        Check(json.Contains("\"status\": \"complete\"") || json.Contains("\"status\": \"incomplete\""), "manifest: final status stored");
        Check(json.Contains("\"occlusion_status\": \"confirmed\"") && json.Contains("\"revisit_required\": true"), "manifest: occluded images flagged");
        Check(json.Contains("\"capture_reason\": \"revisit\""), "manifest: revisit images stored");
        Check(json.Contains("\"camera_position\"") && json.Contains("\"ground_footprint\"") && json.Contains("\"timestamp\""), "manifest: position, footprint, timestamp");
        Check(!json.Contains("ground_truth") && !json.Contains("\"object\""), "manifest: no object position");
        Check(Get(server + "api/scans/" + m.ScanId + "/ground_truth").Contains("not found"), "server: ground truth not served");
    }

    static string Get(string url)
    {
        var r = UnityEngine.Networking.UnityWebRequest.Get(url);
        r.SendWebRequest();
        while (!r.isDone) System.Threading.Thread.Sleep(2);
        var text = r.downloadHandler.text;
        r.Dispose();
        return text;
    }
}
