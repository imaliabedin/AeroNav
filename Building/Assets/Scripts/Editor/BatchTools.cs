using System.Collections.Generic;
using System.IO;
using System.Linq;
using Drone2.Building;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// Headless helpers, run with -executeMethod: measure the furniture models, and render
/// screenshots of a generated building for checking without opening the Editor UI.
static class BatchTools
{
    static string Arg(string name, string fallback)
    {
        var args = System.Environment.GetCommandLineArgs();
        int i = System.Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }

    /// Logs each model's size and which end is taller (the back of beds, sofas and chairs).
    public static void Measure()
    {
        var names = new[]
        {
            "bedDouble", "bedSingle", "loungeSofa", "chair", "chairDesk", "toilet", "desk", "kitchenFridge",
            "kitchenCabinet", "kitchenStove", "bookcaseClosed", "cabinetTelevision", "televisionModern",
            "bathtub", "lampSquareCeiling", "lampWall", "bathroomMirror", "hoodModern", "kitchenCabinetUpper",
            "pottedPlant", "doorway", "wall",
        };
        foreach (var n in names)
        {
            var src = Resources.Load<GameObject>("Furniture/" + n);
            if (src == null) { Debug.Log($"MEASURE {n}: missing"); continue; }
            var go = Object.Instantiate(src);
            var rs = go.GetComponentsInChildren<Renderer>();
            var b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            // Mean z and x of the top 30% of vertices, relative to the bounds centre.
            var high = new List<Vector3>();
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
                foreach (var v in mf.sharedMesh.vertices)
                {
                    var w = mf.transform.TransformPoint(v);
                    if (w.y > b.min.y + 0.7f * b.size.y) high.Add(w - b.center);
                }
            var mean = high.Count > 0 ? high.Aggregate(Vector3.zero, (a, v) => a + v) / high.Count : Vector3.zero;
            Debug.Log($"MEASURE {n}: size {b.size.x:F3} x {b.size.y:F3} x {b.size.z:F3}  rootScale {src.transform.localScale}  rootRot {src.transform.eulerAngles}  topMean x {mean.x:F3} z {mean.z:F3}");
            Object.DestroyImmediate(go);
        }
    }

    public static void Setup() => BuildingSceneSetup.CreateScene();

    /// Generates a building with -seed and writes PNGs to -out: exterior, each floor cut away, each person.
    public static void Snapshot()
    {
        int seed = int.Parse(Arg("-seed", "12345"));
        string outDir = Arg("-out", "Screenshots");
        Directory.CreateDirectory(outDir);

        if (!File.Exists("Assets/Scenes/BurningBuilding.unity")) BuildingSceneSetup.CreateScene();
        EditorSceneManager.OpenScene("Assets/Scenes/BurningBuilding.unity");
        var gen = Object.FindFirstObjectByType<BuildingGenerator>();
        gen.seed = seed;
        gen.floors = int.Parse(Arg("-floors", gen.floors.ToString()));
        RegionSpace.LogFailures = true;
        gen.Generate();
        RegionSpace.LogFailures = false;

        var cam = new GameObject("ShotCam").AddComponent<Camera>();
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 500;

        // Exterior.
        cam.transform.position = new Vector3(-22, 20, -28);
        cam.transform.LookAt(new Vector3(0, 4, 0));
        cam.fieldOfView = 50;
        // The first batch-mode render comes out unlit; warm up before the real shot.
        Shot(cam, Path.Combine(outDir, "warmup.png"), 320, 200);
        File.Delete(Path.Combine(outDir, "warmup.png"));
        Shot(cam, Path.Combine(outDir, $"s{seed}_exterior.png"));

        // Each floor from above with the floors above hidden.
        for (int f = 0; f < gen.floors; f++)
        {
            gen.ShowFloorsUpTo(f);
            float y = f * gen.floorHeight;
            cam.transform.position = new Vector3(0, y + 24, -9);
            cam.transform.LookAt(new Vector3(0, y, 0.5f));
            cam.fieldOfView = 55;
            Shot(cam, Path.Combine(outDir, $"s{seed}_floor{f + 1}.png"));
        }

        // Eye-level view down the main corridor of each floor, all floors shown so ceilings are in place.
        gen.ShowFloorsUpTo(-1);
        var corridor = gen.Plan.mainCorridor;
        for (int f = 0; f < gen.floors; f++)
        {
            var a = gen.Root.TransformPoint(new Vector3(corridor.xMin + 0.6f, f * gen.floorHeight + 1.7f, corridor.center.y));
            var b = gen.Root.TransformPoint(new Vector3(corridor.xMax, f * gen.floorHeight + 1.2f, corridor.center.y));
            cam.transform.position = a;
            cam.transform.LookAt(b);
            cam.fieldOfView = 70;
            Shot(cam, Path.Combine(outDir, $"s{seed}_hall{f + 1}.png"), 1200, 800);
        }

        // Close-ups of each person.
        foreach (var p in gen.people)
        {
            gen.ShowFloorsUpTo(p.floor);
            var c = p.Bounds.center;
            var room = gen.RegionBounds(gen.Plan.floors[p.floor].regions[p.roomId]);
            var offset = (c - new Vector3(room.center.x, c.y, room.center.z));
            var dir = offset.sqrMagnitude > 0.1f ? -offset.normalized : Vector3.back;
            cam.transform.position = c + dir * 2.4f + Vector3.up * 1.2f;
            cam.transform.LookAt(c);
            cam.fieldOfView = 60;
            Shot(cam, Path.Combine(outDir, $"s{seed}_person{p.id}.png"), 900, 700);
        }
        gen.ShowFloorsUpTo(-1);

        var lines = new List<string> { $"seed {seed}" };
        foreach (var fp in gen.Plan.floors)
            foreach (var r in fp.regions)
                lines.Add($"floor {fp.floor + 1} region {r.id} {r.type} {r.role} rect {r.rect}");
        foreach (var p in gen.people)
            lines.Add($"person {p.id} floor {p.floor + 1} room {p.roomId} {p.roomRole} pose {p.pose} at {p.transform.position}");
        File.WriteAllLines(Path.Combine(outDir, $"s{seed}_info.txt"), lines);
        Object.DestroyImmediate(cam.gameObject);
    }

    /// Eye-level views from inside burning rooms, plus the exterior, for checking fire and smoke.
    public static void FireShots()
    {
        int seed = int.Parse(Arg("-seed", "12345"));
        string outDir = Arg("-out", "Screenshots");
        Directory.CreateDirectory(outDir);
        if (!File.Exists("Assets/Scenes/BurningBuilding.unity")) BuildingSceneSetup.CreateScene();
        EditorSceneManager.OpenScene("Assets/Scenes/BurningBuilding.unity");
        var gen = Object.FindFirstObjectByType<BuildingGenerator>();
        gen.seed = seed;
        gen.floors = int.Parse(Arg("-floors", gen.floors.ToString()));
        gen.Generate();

        var cam = new GameObject("ShotCam").AddComponent<Camera>();
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 500;
        cam.fieldOfView = 50;
        cam.transform.position = new Vector3(-22, 20, -28);
        cam.transform.LookAt(new Vector3(0, 4, 0));
        Shot(cam, Path.Combine(outDir, "warmup.png"), 320, 200);
        File.Delete(Path.Combine(outDir, "warmup.png"));

        float h = gen.floors * gen.floorHeight;
        string tag = $"f{gen.floors}_s{seed}";
        cam.transform.position = new Vector3(-24, h * 0.9f + 4, -30);
        cam.transform.LookAt(new Vector3(0, h * 0.45f, 0));
        Shot(cam, Path.Combine(outDir, $"{tag}_exterior.png"));
        cam.transform.position = new Vector3(26, h * 0.7f + 3, -26);
        cam.transform.LookAt(new Vector3(0, h * 0.45f, 0));
        Shot(cam, Path.Combine(outDir, $"{tag}_exterior2.png"));

        var fires = new List<Transform>();
        foreach (var t in gen.Root.GetComponentsInChildren<Transform>())
            if (t.name == "Fire") fires.Add(t);
        Debug.Log($"FIRESHOTS fires {fires.Count}, people {gen.people.Count}");
        for (int i = 0; i < Mathf.Min(fires.Count, int.Parse(Arg("-count", "4"))); i++)
        {
            var f = fires[i];
            var space = gen.roomSpaces.Find(r => r.parent == f.parent) ?? gen.hallSpaces.Find(r => r.parent == f.parent);
            var c = space.parent.TransformPoint(new Vector3(space.interior.center.x, space.floorY, space.interior.center.y));
            var away = new Vector3(c.x - f.position.x, 0, c.z - f.position.z);
            if (away.sqrMagnitude < 0.25f) away = Vector3.right;
            var pos = f.position + away.normalized * 2.6f;
            var lo = space.parent.TransformPoint(new Vector3(space.interior.xMin + 0.35f, 0, space.interior.yMin + 0.35f));
            var hi = space.parent.TransformPoint(new Vector3(space.interior.xMax - 0.35f, 0, space.interior.yMax - 0.35f));
            pos.x = Mathf.Clamp(pos.x, Mathf.Min(lo.x, hi.x), Mathf.Max(lo.x, hi.x));
            pos.z = Mathf.Clamp(pos.z, Mathf.Min(lo.z, hi.z), Mathf.Max(lo.z, hi.z));
            cam.transform.position = new Vector3(pos.x, space.parent.TransformPoint(new Vector3(0, space.floorY + 1.5f, 0)).y, pos.z);
            cam.transform.LookAt(f.position + Vector3.up * 0.9f);
            cam.fieldOfView = 75;
            Shot(cam, Path.Combine(outDir, $"{tag}_fire{i + 1}.png"), 1400, 900);
        }
        var corridor = gen.Plan.mainCorridor;
        for (int fl = 0; fl < gen.floors; fl++)
        {
            var a = gen.Root.TransformPoint(new Vector3(corridor.xMin + 0.6f, fl * gen.floorHeight + 1.7f, corridor.center.y));
            var b = gen.Root.TransformPoint(new Vector3(corridor.xMax, fl * gen.floorHeight + 1.2f, corridor.center.y));
            cam.transform.position = a;
            cam.transform.LookAt(b);
            cam.fieldOfView = 70;
            Shot(cam, Path.Combine(outDir, $"{tag}_hall{fl + 1}.png"), 1200, 800);
        }
        Object.DestroyImmediate(cam.gameObject);
    }

    /// Every pose side by side (sitting ones on a box seat), for checking the rigs.
    public static void PoseGallery()
    {
        string outDir = Arg("-out", "Screenshots");
        Directory.CreateDirectory(outDir);
        EditorSceneManager.OpenScene("Assets/Scenes/BurningBuilding.unity");
        var gen = Object.FindFirstObjectByType<BuildingGenerator>();
        var pal = new Palette(gen.baseMaterial, gen.glassMaterial);
        var rng = new Rng(7);
        var root = new GameObject("Gallery").transform;
        pal.Box(root, new Vector3(-2, -0.1f, -2), new Vector3(26, 0, 4), new Color(0.6f, 0.5f, 0.4f), "Floor");
        pal.Box(root, new Vector3(-2, 0, 1.0f), new Vector3(26, 2.5f, 1.2f), new Color(0.85f, 0.85f, 0.8f), "Wall");
        var poses = (PersonPose[])System.Enum.GetValues(typeof(PersonPose));
        for (int i = 0; i < poses.Length; i++)
        {
            float x = i * 2.4f;
            bool sitting = poses[i] is PersonPose.SittingOnFurniture or PersonPose.SittingHeadInHands;
            if (sitting) pal.Box(root, new Vector3(x - 0.4f, 0, -0.4f), new Vector3(x + 0.4f, 0.48f, 0.4f), new Color(0.8f, 0.3f, 0.3f), "Seat");
            float z = poses[i] is PersonPose.SittingAgainstWall or PersonPose.HuddledKneesUp ? 0.8f : poses[i] == PersonPose.CrouchingInCorner ? 0.6f : 0f;
            float yaw = poses[i] is PersonPose.LyingOnBack or PersonPose.LyingFaceDown ? 90 : 180;
            People.Spawn(pal, rng, root, i + 1, poses[i], new Vector3(x, 0, z), yaw, 0.48f);
        }
        var cam = new GameObject("ShotCam").AddComponent<Camera>();
        cam.transform.position = new Vector3(11, 4.5f, -9);
        cam.transform.LookAt(new Vector3(11, 0.5f, 0));
        cam.fieldOfView = 70;
        Shot(cam, Path.Combine(outDir, "warmup.png"), 320, 200);
        Shot(cam, Path.Combine(outDir, "poses_front.png"), 2000, 800);
        cam.transform.position = new Vector3(11, 2.5f, -6);
        cam.transform.rotation = Quaternion.Euler(10, 50, 0);
        cam.transform.position = new Vector3(-4, 2.2f, -4);
        cam.transform.LookAt(new Vector3(8, 0.6f, 0.5f));
        Shot(cam, Path.Combine(outDir, "poses_side.png"), 2000, 800);
        for (int i = 0; i < poses.Length; i++)
        {
            var target = new Vector3(i * 2.4f, 0.55f, 0.3f);
            cam.transform.position = target + new Vector3(1.6f, 0.9f, -1.9f);
            cam.transform.LookAt(target);
            cam.fieldOfView = 50;
            Shot(cam, Path.Combine(outDir, $"pose_{poses[i]}.png"), 500, 450);
        }
        File.Delete(Path.Combine(outDir, "warmup.png"));
    }

    static void Shot(Camera cam, string path, int w = 1600, int h = 1000)
    {
        var rt = new RenderTexture(w, h, 24);
        cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        tex.Apply();
        File.WriteAllBytes(path, tex.EncodeToPNG());
        cam.targetTexture = null;
        RenderTexture.active = null;
        Object.DestroyImmediate(rt);
        Object.DestroyImmediate(tex);
    }
}
