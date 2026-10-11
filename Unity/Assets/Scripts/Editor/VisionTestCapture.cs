using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Networking;

/// Test frames for the vision layer, rendered by the real <see cref="DroneCamera"/> in the ParkingLot scene.
/// Scene object positions are used only to aim the camera and to write the ground-truth manifest
/// for scoring; the detector itself only ever sees the pixels.
///   Unity -batchmode -projectPath Unity -executeMethod VisionTestCapture.RunBatch
///   Unity -batchmode -projectPath Unity -executeMethod VisionTestCapture.RunEndToEndBatch   (server must be running)
public static class VisionTestCapture
{
    const string ScenePath = "Assets/Scenes/ParkingLot.unity";
    static readonly float[] Altitudes = { 2.44f, 1.5f };
    // Experiment switch: VISION_TEST_FLIP_PHONES=1 turns phones screen-up in memory (scene is not saved).
    static bool FlipPhones => System.Environment.GetEnvironmentVariable("VISION_TEST_FLIP_PHONES") == "1";
    static string OutDir => Path.GetFullPath(Path.Combine(Application.dataPath,
        FlipPhones ? "../../VisionServer/test_images_flipped" : "../../VisionServer/test_images"));

    [System.Serializable] class Shot { public string file; public float altitude; public string truth; public string kind; }
    [System.Serializable] class Manifest { public List<Shot> shots = new List<Shot>(); }

    [MenuItem("Drone Sim/Vision/Capture Test Images")]
    public static void CaptureMenu()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        Capture();
        EditorUtility.RevealInFinder(OutDir);
    }

    public static void RunBatch() => RunAndExit(() => Capture());

    public static void RunEndToEndBatch() => RunAndExit(EndToEnd, exitWhenDone: false);

    static void RunAndExit(System.Action action, bool exitWhenDone = true)
    {
        try { action(); if (exitWhenDone) EditorApplication.Exit(0); }
        catch (System.Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
    }

    static DroneCamera MakeRig()
    {
        var rig = new GameObject("Vision Test Rig") { layer = 2, hideFlags = HideFlags.DontSave };
        var cam = rig.AddComponent<DroneCamera>();
        cam.EnsureCamera();
        return cam;
    }

    /// Puts the camera lens `altitude` m above the ground at (x, z).
    static void Aim(DroneCamera cam, float x, float z, float altitude)
    {
        cam.transform.position = new Vector3(x, cam.groundY + altitude, z) - cam.mountOffset;
    }

    static List<Shot> Capture()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        Directory.CreateDirectory(OutDir);
        foreach (var f in Directory.GetFiles(OutDir, "*.jpg")) File.Delete(f);

        var props = Object.FindObjectsByType<Prop>(FindObjectsSortMode.None).OrderBy(p => p.name).ThenBy(p => p.transform.position.x).ToList();
        var items = props.Where(p => p.entryName == "Keys" || p.entryName == "Phone" || p.entryName == "Wallet").ToList();
        var cars = props.Where(p => new[] { "Sedan", "SUV", "Taxi", "Police car", "Van" }.Contains(p.entryName)).ToList();
        if (FlipPhones)
            foreach (var p in items.Where(p => p.entryName == "Phone"))
                p.transform.GetChild(0).localRotation = Quaternion.Euler(180f, 0f, 0f) * p.transform.GetChild(0).localRotation;
        var cam = MakeRig();
        var manifest = new Manifest();
        var rng = new System.Random(1);

        void Shoot(string name, float x, float z, float alt, string truth, string kind)
        {
            Aim(cam, x, z, alt);
            string file = $"a{alt * 100:000}_{name}.jpg";
            File.WriteAllBytes(Path.Combine(OutDir, file), cam.CaptureJpg());
            manifest.shots.Add(new Shot { file = file, altitude = alt, truth = truth, kind = kind });
        }

        foreach (float alt in Altitudes)
        {
            float half = alt * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad); // half the ground footprint
            int n = 0;
            foreach (var it in items)
            {
                string truth = it.entryName == "Keys" ? "key" : it.entryName == "Phone" ? "phone" : "wallet";
                var p = it.transform.position;
                n++;
                Shoot($"{truth}{n:00}_center", p.x, p.z, alt, truth, "center");
                // Item near a corner of the frame (70% of the way to the edge).
                Shoot($"{truth}{n:00}_edge", p.x - half * 0.7f, p.z - half * 0.7f, alt, truth, "edge");
            }

            // Negatives: frames with no key/phone/wallet anywhere in view. Half next to cars (shadows, clutter).
            int negatives = 0, tries = 0;
            while (negatives < 12 && tries++ < 2000)
            {
                Vector3 pos;
                string kind;
                if (negatives % 2 == 0 && cars.Count > 0)
                {
                    var car = cars[rng.Next(cars.Count)].transform.position;
                    pos = car + new Vector3((float)(rng.NextDouble() * 4 - 2), 0f, (float)(rng.NextDouble() * 8 - 4));
                    kind = "near_car";
                }
                else
                {
                    pos = new Vector3((float)rng.NextDouble() * 80f, 0f, (float)rng.NextDouble() * 60f);
                    kind = "open";
                }
                bool clear = items.All(i => Mathf.Max(Mathf.Abs(i.transform.position.x - pos.x), Mathf.Abs(i.transform.position.z - pos.z)) > half + 0.3f);
                if (!clear) continue;
                negatives++;
                Shoot($"none{negatives:00}_{kind}", pos.x, pos.z, alt, "none", kind);
            }
        }

        Object.DestroyImmediate(cam.gameObject);
        File.WriteAllText(Path.Combine(OutDir, "manifest.json"), JsonUtility.ToJson(manifest, true));
        Debug.Log($"[VisionTest] {manifest.shots.Count} frames from DroneCamera → {OutDir}");
        return manifest.shots;
    }

    // ---------- end-to-end: DroneCamera frame → TargetDetector's HTTP client → server → parsed result ----------

    static void EndToEnd()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var props = Object.FindObjectsByType<Prop>(FindObjectsSortMode.None);
        var key = props.First(p => p.entryName == "Keys").transform.position;
        var phone = props.First(p => p.entryName == "Phone").transform.position;
        var cam = MakeRig();

        var cases = new List<(string name, Vector3 at, TargetCategory target)>
        {
            ("key frame, target key", key, TargetCategory.Key),
            ("phone frame, target phone", phone, TargetCategory.Phone),
            ("phone frame, target key", phone, TargetCategory.Key),
            ("empty frame, target phone", new Vector3(40f, 0f, 15f), TargetCategory.Phone),
        };
        var pending = new List<(string name, UnityWebRequest req, TargetCategory target, double t0)>();
        foreach (var c in cases)
        {
            Aim(cam, c.at.x, c.at.z, 2.44f);
            var req = TargetDetector.BuildRequest("http://127.0.0.1:8765", c.target, 0.3f, cam.CaptureJpg(), 10f);
            req.SendWebRequest();
            pending.Add((c.name, req, c.target, EditorApplication.timeSinceStartup));
        }
        Object.DestroyImmediate(cam.gameObject);

        EditorApplication.update += Poll;
        void Poll()
        {
            if (pending.Any(p => !p.req.isDone)) return;
            EditorApplication.update -= Poll;
            foreach (var p in pending)
            {
                var r = TargetDetector.ParseResponse(p.req, p.target);
                Debug.Log($"[VisionE2E] {p.name}: detected={r.detected} category={r.category} conf={r.confidence:0.000} " +
                          $"hasBox={r.hasBox} box=({r.bbox.x:0.000},{r.bbox.y:0.000},{r.bbox.w:0.000},{r.bbox.h:0.000}) " +
                          $"engine={r.engine} server={r.latencyMs:0}ms error='{r.error}'");
                p.req.Dispose();
            }
            EditorApplication.Exit(0);
        }
    }
}
