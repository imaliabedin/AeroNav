using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// Renders a labeled training set for the lost-item detector through the drone's own DroneCamera, so training
/// images look exactly like flight frames. Each "environment" re-rolls the parked cars and dropped items
/// (LotRandomizer), scatters extra target items and distractors, and randomizes the sun.
/// Labels (YOLO format) come from each target's mesh projected into the image; hidden items are not labeled.
///
///   Unity -batchmode -projectPath Unity -executeMethod DatasetGenerator.RunBatch
/// Env: DATASET_OUT (default ../datasets/drone4/raw), DATASET_ENVS (default 400), DATASET_SIZE (default 1280),
///      DATASET_SEED (default 1; test splits use other seeds), DATASET_SPLIT (default train)
public static class DatasetGenerator
{
    /// Class order of the trained model (VisionServer/train/data.yaml must match).
    public static readonly string[] Classes = { "wallet", "keys", "phone", "purse" };
    static readonly string[] CatalogNames = { "Wallet", "Keys", "Phone", "Purse" };
    static readonly string[] Distractors = { "Laptop", "Backpack", "Headphones", "Umbrella", "Water bottle", "Soda can", "Coffee cup", "Traffic cone" };

    const float GroundY = -0.5f, QuadX = 40f, QuadZ = 30f;

    static string Env(string k, string d) => System.Environment.GetEnvironmentVariable(k) ?? d;

    public static void RunBatch()
    {
        int code = 1;
        try
        {
            string outDir = Path.GetFullPath(Env("DATASET_OUT", Path.Combine(Application.dataPath, "../../datasets/drone4/raw")));
            Generate(outDir, int.Parse(Env("DATASET_ENVS", "400")), int.Parse(Env("DATASET_SIZE", "1280")),
                     int.Parse(Env("DATASET_SEED", "1")), Env("DATASET_SPLIT", "train"));
            code = 0;
        }
        catch (System.Exception e) { Debug.LogException(e); }
        EditorApplication.Exit(code);
    }

    static void Generate(string outDir, int envs, int size, int seed, string split)
    {
        EditorSceneManager.OpenScene("Assets/Scenes/ParkingLot.unity", OpenSceneMode.Single);
        var props = Object.FindAnyObjectByType<PropSet>();
        var catalog = props.catalog;
        var rand = new GameObject("Randomizer (dataset)").AddComponent<LotRandomizer>();
        rand.props = props;
        var sun = Object.FindObjectsByType<Light>(FindObjectsSortMode.None).FirstOrDefault(l => l.type == LightType.Directional);
        float sunIntensity = sun ? sun.intensity : 1f;

        var camGo = new GameObject("Dataset camera");
        var cam = camGo.AddComponent<DroneCamera>();
        cam.width = cam.height = size;
        cam.EnsureCamera();

        string imgDir = Path.Combine(outDir, split, "images"), lblDir = Path.Combine(outDir, split, "labels");
        Directory.CreateDirectory(imgDir);
        Directory.CreateDirectory(lblDir);
        var rng = new System.Random(seed * 7919);
        var meta = new List<string>();
        int shots = 0, boxes = 0;
        var perClass = new int[Classes.Length];

        for (int env = 0; env < envs; env++)
        {
            int envSeed = seed * 100000 + env;
            rand.Randomize(envSeed);
            // Extra items in one quadrant, where this environment's shots are taken.
            var q = new Vector3(rng.Next(2) * QuadX, 0f, rng.Next(2) * QuadZ);
            var targets = new List<Prop>();
            for (int i = 0; i < 20; i++)
            {
                int c = PickClass(rng);
                targets.Add(Place(props, catalog.IndexOf(CatalogNames[c]), q, rng));
            }
            for (int i = 0; i < 8; i++) Place(props, catalog.IndexOf(Distractors[rng.Next(Distractors.Length)]), q, rng);
            // LotRandomizer's dropped items are targets too when they are of a target class.
            var labeled = props.GetComponentsInChildren<Prop>().Where(p => System.Array.IndexOf(CatalogNames, p.entryName) >= 0).ToList();
            foreach (var p in labeled)
            {
                TintWallet(p, rng);
                if (p.entryName == "Phone" && rng.NextDouble() < 0.5) FlipOver(p);   // screen up or screen down
            }
            if (sun)
            {
                sun.intensity = sunIntensity * (0.65f + 0.7f * (float)rng.NextDouble());
                sun.transform.rotation = Quaternion.Euler(30f + 50f * (float)rng.NextDouble(), 360f * (float)rng.NextDouble(), 0f);
            }
            Physics.SyncTransforms();

            for (int s = 0; s < 6; s++)
            {
                // Mostly near an item (so frames contain targets), sometimes anywhere in the quadrant (negatives, context).
                Vector3 center;
                if (s < 5)
                {
                    var t = targets[rng.Next(targets.Count)].transform.position;
                    center = t + new Vector3(Range(rng, -1.5f, 1.5f), 0f, Range(rng, -1.5f, 1.5f));
                }
                else center = q + new Vector3(Range(rng, 3f, QuadX - 3f), 0f, Range(rng, 3f, QuadZ - 3f));
                // Camera height above the ground: around the scan altitude (~4.4 m) plus lower passes (the original tests used 2.44 m).
                float alt = rng.NextDouble() < 0.7 ? Range(rng, 3.6f, 5.4f) : Range(rng, 2.3f, 3.6f);
                cam.transform.position = new Vector3(center.x, GroundY + alt - cam.mountOffset.y, center.z);
                cam.Cam.transform.position = cam.transform.position + cam.mountOffset;

                var lines = new List<string>();
                foreach (var p in labeled)
                {
                    int cls = System.Array.IndexOf(CatalogNames, p.entryName);
                    if (!Box(cam.Cam, p, out var box)) continue;
                    lines.Add($"{cls} {F(box.center.x)} {F(box.center.y)} {F(box.width)} {F(box.height)}");
                    perClass[cls]++;
                }
                string name = $"{split}_{seed}_{env:D4}_{s}";
                File.WriteAllBytes(Path.Combine(imgDir, name + ".jpg"), cam.CaptureJpg());
                File.WriteAllText(Path.Combine(lblDir, name + ".txt"), string.Join("\n", lines));
                meta.Add($"{{\"file\": \"{name}.jpg\", \"altitude\": {F(alt)}, \"x\": {F(center.x)}, \"z\": {F(center.z)}, \"boxes\": {lines.Count}}}");
                shots++;
                boxes += lines.Count;
            }
            RemoveExtras(props);
            if (env % 50 == 0) Debug.Log($"[Dataset] {split}: environment {env}/{envs}, {shots} images, {boxes} boxes");
        }
        File.WriteAllText(Path.Combine(outDir, split, "meta.jsonl"), string.Join("\n", meta));
        Debug.Log($"[Dataset] DONE {split}: {shots} images, {boxes} boxes " +
                  string.Join(", ", Classes.Select((c, i) => $"{c} {perClass[i]}")) + $" → {outDir}/{split}");
    }

    /// Share of the extra scattered targets per class (Classes order). Wallets get more since the model was redone.
    static readonly float[] ClassWeights = { 0.3f, 0.25f, 0.3f, 0.15f };

    static int PickClass(System.Random rng)
    {
        double r = rng.NextDouble() * ClassWeights.Sum();
        for (int i = 0; i < ClassWeights.Length; i++) { r -= ClassWeights[i]; if (r <= 0) return i; }
        return ClassWeights.Length - 1;
    }

    /// Leather colors of real wallets (linear _BaseColor): black, dark brown, cognac, tan, navy, grey, burgundy.
    static readonly Color[] WalletLeathers =
    {
        new Color(0.010f, 0.010f, 0.010f), new Color(0.045f, 0.025f, 0.017f), new Color(0.17f, 0.065f, 0.022f),
        new Color(0.30f, 0.16f, 0.07f), new Color(0.014f, 0.022f, 0.065f), new Color(0.055f, 0.056f, 0.060f),
        new Color(0.09f, 0.015f, 0.020f),
    };

    /// Gives a wallet a random leather color (and a matching darker fold edge), so the model learns the shape, not one color.
    public static void TintWallet(Prop p, System.Random rng)
    {
        if (p == null || p.entryName != "Wallet") return;
        var c = WalletLeathers[rng.Next(WalletLeathers.Length)] * (0.8f + 0.4f * (float)rng.NextDouble());
        c.a = 1f;
        TintWalletWith(p, c);
    }

    /// Re-places every "Wallet" already saved in the ParkingLot scene, so it uses the catalog's current model.
    ///   Unity -batchmode -projectPath Unity -executeMethod DatasetGenerator.RefreshSceneWallets
    public static void RefreshSceneWallets()
    {
        int code = 1;
        try
        {
            var scene = EditorSceneManager.OpenScene("Assets/Scenes/ParkingLot.unity", OpenSceneMode.Single);
            var props = Object.FindAnyObjectByType<PropSet>();
            int index = props.catalog.IndexOf("Wallet"), n = 0;
            foreach (var p in props.GetComponentsInChildren<Prop>().Where(p => p.entryName == "Wallet").ToList())
            {
                var pos = p.transform.position;
                float yaw = p.transform.eulerAngles.y;
                props.Remove(p);
                props.Place(index, pos, yaw);
                n++;
            }
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[Dataset] replaced {n} wallet(s) in the ParkingLot scene with {props.catalog.Get(index).prefab.name}");
            code = 0;
        }
        catch (System.Exception e) { Debug.LogException(e); }
        EditorApplication.Exit(code);
    }

    /// Renders wallets in every leather color next to keys, a phone and a purse, from 1.5 m and 4.4 m (1280 px),
    /// into DATASET_OUT/preview_*.jpg. A visual check of the model; nothing is saved to the scene.
    public static void Preview()
    {
        int code = 1;
        try
        {
            string outDir = Path.GetFullPath(Env("DATASET_OUT", Path.Combine(Application.dataPath, "../../datasets/drone4")));
            Directory.CreateDirectory(outDir);
            EditorSceneManager.OpenScene("Assets/Scenes/ParkingLot.unity", OpenSceneMode.Single);
            var props = Object.FindAnyObjectByType<PropSet>();
            foreach (var p in props.GetComponentsInChildren<Prop>().Where(p => Vector3.Distance(p.transform.position, new Vector3(20f, -0.5f, 15f)) < 4f).ToList())
                props.Remove(p);
            var rng = new System.Random(5);
            for (int i = 0; i < WalletLeathers.Length; i++)
            {
                var w = props.Place(props.catalog.IndexOf("Wallet"), new Vector3(18.8f + i * 0.4f, -0.5f, 15.25f), i * 25f);
                TintWalletWith(w, WalletLeathers[i]);
            }
            float x = 19.2f;
            foreach (var name in new[] { "Keys", "Phone", "Purse", "Wallet" })
            {
                props.Place(props.catalog.IndexOf(name), new Vector3(x, -0.5f, 14.7f), 20f);
                x += 0.55f;
            }
            Physics.SyncTransforms();
            var cam = new GameObject("Preview camera").AddComponent<DroneCamera>();
            cam.width = cam.height = 1280;
            cam.EnsureCamera();
            foreach (float alt in new[] { 1.5f, 4.4f })
            {
                cam.Cam.transform.position = new Vector3(20f, -0.5f + alt, 15f);
                File.WriteAllBytes(Path.Combine(outDir, $"preview_{alt:0.0}m.jpg"), cam.CaptureJpg());
            }
            Debug.Log($"[Dataset] preview written to {outDir}");
            code = 0;
        }
        catch (System.Exception e) { Debug.LogException(e); }
        EditorApplication.Exit(code);
    }

    /// Turns the item upside down (a phone then shows its screen) and puts it back on the ground.
    static void FlipOver(Prop p)
    {
        var model = p.transform.GetChild(0);
        model.localRotation = Quaternion.Euler(180f, 0f, 0f) * model.localRotation;
        if (PropSet.RendererBounds(p.gameObject, out var b)) model.position += Vector3.up * (p.transform.position.y - b.min.y);
    }

    /// Sets the wallet's leather (and its darker fold edge) to `c`.
    static void TintWalletWith(Prop p, Color c)
    {
        foreach (var r in p.GetComponentsInChildren<Renderer>())
        {
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null || !(mats[i].name.Contains("Leather") || mats[i].name.Contains("Fold"))) continue;
                var col = mats[i].name.Contains("Fold") ? new Color(c.r * 0.55f, c.g * 0.55f, c.b * 0.55f, 1f) : c;
                var mpb = new MaterialPropertyBlock();
                r.GetPropertyBlock(mpb, i);
                mpb.SetColor("_BaseColor", col);
                mpb.SetColor("_Color", col);
                r.SetPropertyBlock(mpb, i);
            }
        }
    }

    static readonly List<Prop> extras = new List<Prop>();

    static Prop Place(PropSet props, int index, Vector3 quadOrigin, System.Random rng)
    {
        var pos = quadOrigin + new Vector3(Range(rng, 2f, QuadX - 2f), GroundY, Range(rng, 1f, QuadZ - 1f));
        var prop = props.Place(index, pos, rng.Next(360));
        extras.Add(prop);
        return prop;
    }

    /// Removes this environment's extra items (LotRandomizer replaces its own cars and items next time).
    static void RemoveExtras(PropSet props)
    {
        foreach (var p in extras) if (p != null) props.Remove(p);
        extras.Clear();
    }

    static float Range(System.Random r, float a, float b) => a + (float)r.NextDouble() * (b - a);
    static string F(float v) => v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

    /// Tight box of the prop's mesh in image coordinates (YOLO: origin top-left, normalized). False when the item is
    /// mostly outside the frame or hidden (the camera's line of sight to its center hits something else first).
    static bool Box(Camera cam, Prop prop, out Rect box)
    {
        box = default;
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        bool any = false;
        foreach (var mf in prop.GetComponentsInChildren<MeshFilter>())
        {
            var mesh = mf.sharedMesh;
            if (mesh == null) continue;
            var m = mf.transform.localToWorldMatrix;
            foreach (var v in mesh.vertices)
            {
                var vp = cam.WorldToViewportPoint(m.MultiplyPoint3x4(v));
                if (vp.z <= 0f) return false;
                float u = vp.x, w = 1f - vp.y;
                x0 = Mathf.Min(x0, u); x1 = Mathf.Max(x1, u); y0 = Mathf.Min(y0, w); y1 = Mathf.Max(y1, w);
                any = true;
            }
        }
        if (!any) return false;
        float full = (x1 - x0) * (y1 - y0);
        float cx0 = Mathf.Clamp01(x0), cx1 = Mathf.Clamp01(x1), cy0 = Mathf.Clamp01(y0), cy1 = Mathf.Clamp01(y1);
        float vis = (cx1 - cx0) * (cy1 - cy0);
        if (full <= 0f || vis < 0.5f * full) return false;

        // Occlusion: look at the item's center from the camera.
        if (!PropSet.RendererBounds(prop.gameObject, out var b)) return false;
        var origin = cam.transform.position;
        var dir = b.center - origin;
        if (Physics.Raycast(origin, dir.normalized, out var hit, dir.magnitude + 0.5f, ~(1 << 2), QueryTriggerInteraction.Ignore)
            && hit.collider.GetComponentInParent<Prop>() != prop)
            return false;
        box = Rect.MinMaxRect(cx0, cy0, cx1, cy1);
        return true;
    }
}
