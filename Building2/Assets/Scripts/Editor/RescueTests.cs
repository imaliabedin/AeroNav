using System.Collections.Generic;
using System.IO;
using Drone2.Building;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Networking;

/// Checks the ONNX person model with the Rescue Mission's AI camera, on the real building. For every trapped person:
/// an AI frame from 3 m away (clear line of sight, 1.5 m up, 30 deg down, as in the model's training images), plus
/// frames of empty views. Each frame goes to the person model server; boxes are located by the same ray method as
/// the mission. Results go to the log ([RescueTest] lines) and the frames to RESCUE_TEST_OUT.
///   PEOPLE_SERVER=http://127.0.0.1:8767 Unity -batchmode -quit -projectPath Building2 -executeMethod RescueTests.ModelOnPeople
public static class RescueTests
{
    static void Log(string m) => Debug.Log("[RescueTest] " + m);

    public static void ModelOnPeople()
    {
        int fails = 0;
        try { fails = Run(); } catch (System.Exception e) { Debug.LogException(e); fails++; }
        Log(fails == 0 ? "RESULT PASS" : $"RESULT FAIL ({fails})");
        EditorApplication.Exit(fails == 0 ? 0 : 1);
    }

    static int Run()
    {
        string server = System.Environment.GetEnvironmentVariable("PEOPLE_SERVER") ?? "http://127.0.0.1:8767";
        string outDir = System.Environment.GetEnvironmentVariable("RESCUE_TEST_OUT") ?? Path.GetTempPath();
        DroneSearchDemo.CreateScene();
        EditorSceneManager.OpenScene("Assets/Scenes/DroneSearchDemo.unity");
        var gen = Object.FindFirstObjectByType<BuildingGenerator>();
        var coord = Object.FindFirstObjectByType<SearchCoordinator>();
        gen.seed = 12345;
        gen.Generate();
        coord.Setup(gen);
        Physics.SyncTransforms();
        var m = new GameObject("RescueControls").AddComponent<RescueMission>();
        m.InitForTest(coord);

        int found = 0, located = 0, views = 0, emptyViews = 0, falseAlarms = 0;
        foreach (var p in gen.people)
        {
            var c = p.Bounds.center;
            float floorY = p.Bounds.min.y;
            // A spot 2.5-3.5 m away with a clear view of the person.
            bool shot = false;
            for (int a = 0; a < 16 && !shot; a++)
                foreach (float dist in new[] { 3f, 2.5f, 3.5f })
                {
                    float ang = a * Mathf.PI / 8f;
                    var cam = new Vector3(c.x + Mathf.Cos(ang) * dist, floorY + 1.5f, c.z + Mathf.Sin(ang) * dist);
                    var to = c - cam;
                    if (Physics.CheckSphere(cam, 0.25f, SearchCoordinator.WorldMask, QueryTriggerInteraction.Ignore)) continue;
                    if (!Physics.Raycast(cam, to.normalized, out var hit, to.magnitude + 0.5f, SearchCoordinator.WorldMask, QueryTriggerInteraction.Ignore)
                        || hit.collider.GetComponentInParent<TrappedPerson>() != p) continue;
                    float yaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
                    var jpg = m.CaptureFrom(cam, yaw, out var rot);
                    File.WriteAllBytes(Path.Combine(outDir, $"rescue_person{views}.jpg"), jpg);
                    var dets = Detect(server, jpg);
                    views++;
                    bool any = false, near = false;
                    foreach (var d in dets)
                    {
                        any = true;
                        var w = Locate(cam, rot, m.aiFov, d);
                        float err = w.HasValue ? Vector2.Distance(new Vector2(w.Value.x, w.Value.z), new Vector2(c.x, c.z)) : -1f;
                        if (w.HasValue && err < 1f) near = true;
                        Log($"  person {views - 1}: conf {d.confidence:0.00} box ({d.bbox.x:0.00},{d.bbox.y:0.00},{d.bbox.w:0.00},{d.bbox.h:0.00}) located {(w.HasValue ? $"{err:0.00} m from the person" : "nowhere")}");
                    }
                    if (any) found++;
                    if (near) located++;
                    Log($"person view {views - 1} at {dist} m: {dets.Count} detection(s)");
                    shot = true;
                    break;
                }
        }
        // Empty views: away from every person.
        var rng = new System.Random(3);
        for (int tries = 0; tries < 400 && emptyViews < 12; tries++)
        {
            int f = rng.Next(gen.floors);
            var b = coord.Grid;
            int x = rng.Next(b.nx), z = rng.Next(b.nz);
            if (b.state[f, x, z] != SearchGrid.Spot.Unseen) continue;
            var cam = b.World(f, x, z);
            cam.y = b.origin.y + f * gen.floorHeight + 1.5f;
            float yaw = rng.Next(360);
            var fwd = Quaternion.Euler(0, yaw, 0) * Vector3.forward;
            bool personNear = gen.people.Exists(p => Vector3.Distance(p.Bounds.center, cam) < 8f);
            if (personNear || Physics.CheckSphere(cam, 0.25f, SearchCoordinator.WorldMask, QueryTriggerInteraction.Ignore)) continue;
            var jpg = m.CaptureFrom(cam, yaw, out _);
            File.WriteAllBytes(Path.Combine(outDir, $"rescue_empty{emptyViews}.jpg"), jpg);
            var dets = Detect(server, jpg);
            if (dets.Count > 0) { falseAlarms++; Log($"empty view {emptyViews}: {dets.Count} detection(s), best {dets[0].confidence:0.00}"); }
            emptyViews++;
        }
        Log($"people {gen.people.Count}: photographed {views}, detected in {found}, located within 1 m in {located}; empty views {emptyViews}, with a false alarm {falseAlarms}");
        int fails = 0;
        if (views == 0) { Log("FAIL no clear view of any person"); fails++; }
        if (found * 4 < views * 3) { Log($"FAIL person missed in {views - found} of {views} view(s)"); fails++; }   // 75%: dim, low-contrast views are hard for the model
        if (falseAlarms > emptyViews / 4) { Log($"FAIL {falseAlarms} of {emptyViews} empty views had a detection"); fails++; }
        return fails;
    }

    /// The mission's end states with the real search, fire and collapse, stepped headless (no AI, no website):
    /// FIRE_INTERVALS="100000,8,4" -> one run per fire spread interval (seconds between FireSpread steps).
    public static void MissionStates()
    {
        int fails = 0;
        try
        {
            var intervals = (System.Environment.GetEnvironmentVariable("FIRE_INTERVALS") ?? "100000,4").Split(',');
            foreach (var iv in intervals)
            {
                DroneSearchDemo.CreateScene();
                EditorSceneManager.OpenScene("Assets/Scenes/DroneSearchDemo.unity");
                var gen = Object.FindFirstObjectByType<BuildingGenerator>();
                var coord = Object.FindFirstObjectByType<SearchCoordinator>();
                gen.seed = 12345;
                gen.Generate();
                coord.Setup(gen);
                var m = new GameObject("RescueControls").AddComponent<RescueMission>();
                m.InitForTest(coord);
                m.fireInterval = float.Parse(iv, System.Globalization.CultureInfo.InvariantCulture);
                m.BeginOffline();
                const float dt = 0.05f;
                while (m.State == RescueMission.Phase.Running) { coord.Step(dt); m.Step(dt); }
                float endTime = m.MissionTime;
                bool collapsing = m.Collapsing;
                // Terminal state holds: more time changes nothing.
                for (int i = 0; i < 600; i++) { coord.Step(dt); m.Step(dt); }
                var g = coord.Grid;
                Log($"fire interval {iv} s: {m.State} at {endTime / 60f:0.0} min; coverage {coord.Coverage() * 100f:0.0}%, " +
                    $"left {g.Count(SearchGrid.Spot.Unseen)}, closed off {g.Count(SearchGrid.Spot.Unreachable)}, cut off by fire {m.CutOff}; " +
                    $"collapse {(collapsing ? "yes" : "no")}; after +30 s still {m.State}, search running {coord.Running}");
                if (m.State == RescueMission.Phase.Successful && (collapsing || endTime >= m.missionSeconds)) { Log("FAIL success with collapse or after the deadline"); fails++; }
                if (m.State == RescueMission.Phase.Failed && (!collapsing || endTime < m.missionSeconds)) { Log("FAIL failure without collapse or before the deadline"); fails++; }
                if (coord.Running) { Log("FAIL search still running after the end"); fails++; }
                Object.DestroyImmediate(m.gameObject);
            }
        }
        catch (System.Exception e) { Debug.LogException(e); fails++; }
        Log(fails == 0 ? "RESULT PASS" : $"RESULT FAIL ({fails})");
        EditorApplication.Exit(fails == 0 ? 0 : 1);
    }

    [System.Serializable] public class Box { public float x, y, w, h; }
    [System.Serializable] public class Obj { public string category; public float confidence; public Box bbox; }
    [System.Serializable] class Reply { public string error; public Obj[] objects; }

    static List<Obj> Detect(string server, byte[] jpg)
    {
        var r = new UnityWebRequest(server + "/detect?target=all&conf=0.4", "POST")
        { uploadHandler = new UploadHandlerRaw(jpg), downloadHandler = new DownloadHandlerBuffer() };
        r.SetRequestHeader("Content-Type", "image/jpeg");
        r.SendWebRequest();
        while (!r.isDone) System.Threading.Thread.Sleep(5);
        var reply = JsonUtility.FromJson<Reply>(r.downloadHandler.text);
        r.Dispose();
        if (reply == null || !string.IsNullOrEmpty(reply.error)) throw new System.Exception("model error: " + reply?.error);
        var list = new List<Obj>(reply.objects ?? new Obj[0]);
        list.Sort((a, b) => b.confidence.CompareTo(a.confidence));
        return list;
    }

    static Vector3? Locate(Vector3 pos, Quaternion rot, float fov, Obj d)
    {
        float u = d.bbox.x + d.bbox.w * 0.5f, v = d.bbox.y + d.bbox.h * 0.5f, t = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
        var dir = rot * new Vector3((2f * u - 1f) * t, (1f - 2f * v) * t, 1f).normalized;
        return Physics.Raycast(pos, dir, out var hit, 40f, SearchCoordinator.WorldMask, QueryTriggerInteraction.Ignore) ? hit.point : null;
    }
}
