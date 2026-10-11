using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// Drone Sim/Build City Backdrop: purely decorative city blocks around the parking lot — streets,
/// sidewalks, shops and towers to the north/east/west, a suburb to the south, parks and street trees.
/// Nothing here has colliders or belongs to the cell map, and anything whose shadow could fall on the
/// lot is swapped for something shorter. No cars: the dataset labels cars, so background ones would
/// show up in images unlabeled.
public static class CityBackdrop
{
    const string RootName = "Backdrop";
    const string CommercialDir = "Assets/Models/City/Commercial/";
    const string SuburbanDir = "Assets/Models/City/Suburban/";
    const string NatureDir = "Assets/Models/Nature/";
    const string StreetLightPath = "Assets/Models/Roads/light-square.fbx";

    // The lot including its sidewalk ring (x -3..83, z -3..63); nothing may cast a shadow on it.
    static readonly Rect Lot = new Rect(-3f, -3f, 86f, 66f);

    // City grid: 10 m streets on a 44 m pitch, so blocks are 34 m with a 3 m sidewalk round each.
    // The lot sits in the gap between the x = -8 / 88 and z = -8 / 68 streets (its ring road).
    const float Street = 10f, Walk = 3f;
    static readonly float[] XStreets = { -184, -140, -96, -52, -8, 88, 132, 176, 220, 264 };
    static readonly float[] ZStreets = { -140, -96, -52, -8, 68, 112, 156, 200, 244 };
    const float SplitX = 40f; // extra north-south street splitting the lot's column outside the lot row

    const float RoadTop = -0.5f, WalkTop = -0.42f, LawnTop = -0.41f, FieldTop = -0.62f;
    const float CityScale = 9f, FarScale = 18f, HouseScale = 8f, LightScale = 9.2f;

    // Kenney building fronts face -Z, so a model faces outward normal n with this yaw.
    static float FacingYaw(Vector3 n) => Mathf.Atan2(n.x, n.z) * Mathf.Rad2Deg + 180f;

    class Model
    {
        public GameObject prefab;
        public Vector3 size;   // unscaled bounds size
        public Vector3 center; // unscaled bounds centre (pivot offset)
    }

    static Vector2 shadowPerMeter; // horizontal shadow offset per metre of height
    static System.Random rng;
    static Transform root;

    [MenuItem("Drone Sim/Build City Backdrop")]
    public static void BuildInOpenScene()
    {
        if (EditorApplication.isPlaying) { Debug.LogWarning("Exit Play mode before building the backdrop."); return; }
        var asphalt = DroneSimSetup.LitMaterial("Asphalt", new Color(0.16f, 0.16f, 0.17f));
        var sidewalk = DroneSimSetup.LitMaterial("Sidewalk", new Color(0.62f, 0.62f, 0.6f));
        var paint = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/LinePaint.mat");
        Build(asphalt, sidewalk, paint);
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
    }

    public static void Build(Material asphalt, Material sidewalk, Material paint)
    {
        var old = GameObject.Find(RootName);
        if (old != null) Object.DestroyImmediate(old);
        root = new GameObject(RootName).transform;
        rng = new System.Random(11);

        var sun = Object.FindAnyObjectByType<Light>();
        foreach (var l in Object.FindObjectsByType<Light>())
            if (l.type == LightType.Directional) { sun = l; break; }
        var f = sun != null ? sun.transform.forward : Quaternion.Euler(50f, 330f, 0f) * Vector3.forward;
        shadowPerMeter = new Vector2(f.x, f.z) / Mathf.Max(0.05f, -f.y);

        var grass = DroneSimSetup.LitMaterial("Grass", new Color(0.4f, 0.58f, 0.3f));
        var m = LoadModels();

        BuildStreets(asphalt, grass, paint);
        BuildBlocks(m, sidewalk, grass);

        // Soft haze so the far skyline fades instead of competing with the lot.
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = new Color(0.8f, 0.85f, 0.9f);
        RenderSettings.fogStartDistance = 150f;
        RenderSettings.fogEndDistance = 480f;

        Debug.Log($"[DroneSim] City backdrop built: {root.GetComponentsInChildren<Transform>().Length} objects");
    }

    // ---------- streets ----------

    static void BuildStreets(Material asphalt, Material grass, Material paint)
    {
        var t = Group("Streets");
        float x0 = XStreets[0] - Street * 0.5f, x1 = XStreets[XStreets.Length - 1] + Street * 0.5f;
        float z0 = ZStreets[0] - Street * 0.5f, z1 = ZStreets[ZStreets.Length - 1] + Street * 0.5f;

        // Grass under everything (below the lot's asphalt), reaching past the far clip so there's no edge.
        DroneSimSetup.Slab(t, "Field", grass, new Vector3(40f, FieldTop - 0.05f, 30f), new Vector3(1400f, 0.1f, 1400f));

        // East-west streets sit 3 mm lower than north-south ones so crossings don't z-fight.
        foreach (float z in ZStreets)
        {
            DroneSimSetup.Slab(t, "Street EW", asphalt, new Vector3((x0 + x1) * 0.5f, RoadTop - 0.053f, z), new Vector3(x1 - x0, 0.1f, Street));
            Dashes(t, paint, new Vector3(x0, RoadTop + 0.002f, z), Vector3.right, x1 - x0, CrossX);
        }
        foreach (float x in XStreets)
        {
            DroneSimSetup.Slab(t, "Street NS", asphalt, new Vector3(x, RoadTop - 0.05f, (z0 + z1) * 0.5f), new Vector3(Street, 0.1f, z1 - z0));
            Dashes(t, paint, new Vector3(x, RoadTop + 0.005f, z0), Vector3.forward, z1 - z0, CrossZ);
        }
        // The split street stops at the lot's ring road on both sides.
        foreach (var (a, b) in new[] { (z0, Lot.yMin), (Lot.yMax, z1) })
        {
            DroneSimSetup.Slab(t, "Street NS", asphalt, new Vector3(SplitX, RoadTop - 0.05f, (a + b) * 0.5f), new Vector3(Street, 0.1f, b - a));
            Dashes(t, paint, new Vector3(SplitX, RoadTop + 0.005f, a), Vector3.forward, b - a, CrossZ);
        }
    }

    static bool CrossX(float x)
    {
        if (Mathf.Abs(x - SplitX) < Street * 0.5f + 1f) return true;
        foreach (float s in XStreets) if (Mathf.Abs(x - s) < Street * 0.5f + 1f) return true;
        return false;
    }

    static bool CrossZ(float z)
    {
        foreach (float s in ZStreets) if (Mathf.Abs(z - s) < Street * 0.5f + 1f) return true;
        return false;
    }

    /// Dashed centre line like the lot's aisle: 2 m dashes every 4 m, skipping intersections.
    static void Dashes(Transform t, Material paint, Vector3 start, Vector3 dir, float length, System.Func<float, bool> skip)
    {
        bool alongX = dir.x != 0f;
        for (float s = 2f; s + 2f < length; s += 4f)
        {
            var c = start + dir * (s + 1f);
            if (skip(alongX ? c.x : c.z)) continue;
            DroneSimSetup.Slab(t, "Dash", paint, c, alongX ? new Vector3(2f, 0.01f, 0.12f) : new Vector3(0.12f, 0.01f, 2f));
        }
    }

    // ---------- blocks ----------

    class Models
    {
        public List<(Model m, float s)> shops = new(), towers = new(), far = new(), houses = new();
        public List<Model> trees = new(), streetTrees = new(), bushes = new(), flowers = new();
        public Model fence, light;
    }

    static void BuildBlocks(Models m, Material sidewalk, Material grass)
    {
        var xs = new List<(float a, float b)>();
        var zs = new List<(float a, float b)>();
        for (int i = 0; i + 1 < XStreets.Length; i++) xs.Add((XStreets[i] + Street * 0.5f, XStreets[i + 1] - Street * 0.5f));
        for (int i = 0; i + 1 < ZStreets.Length; i++) zs.Add((ZStreets[i] + Street * 0.5f, ZStreets[i + 1] - Street * 0.5f));

        int n = 0;
        foreach (var z in zs)
            foreach (var x in xs)
            {
                var r = Rect.MinMaxRect(x.a, z.a, x.b, z.b);
                if (Mathf.Approximately(x.a, Lot.xMin))
                {
                    if (Mathf.Approximately(z.a, Lot.yMin)) continue; // the lot itself
                    // The lot's column outside the lot row is split in two by the SplitX street.
                    Block(m, Rect.MinMaxRect(r.xMin, r.yMin, SplitX - Street * 0.5f, r.yMax), sidewalk, grass, ++n);
                    Block(m, Rect.MinMaxRect(SplitX + Street * 0.5f, r.yMin, r.xMax, r.yMax), sidewalk, grass, ++n);
                }
                else Block(m, r, sidewalk, grass, ++n);
            }
    }

    static void Block(Models m, Rect r, Material sidewalk, Material grass, int index)
    {
        var t = Group($"Block {index}");
        DroneSimSetup.Slab(t, "Sidewalk", sidewalk, new Vector3(r.center.x, WalkTop - 0.1f, r.center.y), new Vector3(r.width, 0.2f, r.height));

        bool park = r.Contains(new Vector2(-30f, 88f)) || r.Contains(new Vector2(110f, -30f));
        bool suburb = r.center.y < Lot.yMin;
        if (park || suburb)
            DroneSimSetup.Slab(t, "Lawn", grass, new Vector3(r.center.x, LawnTop - 0.1f, r.center.y), new Vector3(r.width - 2 * Walk, 0.2f, r.height - 2 * Walk));

        if (park) Park(m, t, r);
        else if (suburb) Suburb(m, t, r);
        else Downtown(m, t, r);
    }

    /// Shops and offices lining two opposite sides of the block. Beside the lot they face it (east/west);
    /// elsewhere they face the east-west streets. Towers only north of the lot, low-detail ones far out.
    static void Downtown(Models m, Transform t, Rect r)
    {
        float dist = Vector2.Distance(r.center, Lot.center);
        var pool = new List<(Model m, float s)>(dist > 210f ? m.far : m.shops);
        if (r.center.y > Lot.yMax + 40f && dist <= 210f) { pool.AddRange(m.towers); pool.AddRange(m.towers); }

        bool besideLot = r.center.y > Lot.yMin && r.center.y < Lot.yMax;
        float depth = ((besideLot ? r.width : r.height) - 2 * Walk - 2f) * 0.5f;
        foreach (var (a, b, n) in besideLot ? SideEdges(r, Walk) : EndEdges(r, Walk))
            Row(m, t, pool, a, b, n, depth, WalkTop, 0.18f);

        StreetLights(m, t, r);
    }

    /// Houses facing the east-west streets, a front yard with a fence, street trees on the sidewalk.
    static void Suburb(Models m, Transform t, Rect r)
    {
        const float yard = 3f;
        float depth = (r.height - 2 * (Walk + yard) - 2f) * 0.5f;
        foreach (var (a, b, n) in EndEdges(r, Walk + yard))
        {
            var placed = Row(m, t, m.houses, a, b, n, depth, LawnTop, 0.1f);
            // Fence along the front of each lawn, with a gap at the house's door.
            foreach (var (centre, width) in placed)
            {
                var along = (b - a).normalized;
                var front = centre + n * yard * 0.8f;
                float seg = m.fence.size.x * 5f;
                for (float s = -width * 0.5f; s + seg <= width * 0.5f + 0.01f; s += seg)
                {
                    if (Mathf.Abs(s + seg * 0.5f) < 1.6f) continue;
                    Place(m.fence, 5f, front + along * (s + seg * 0.5f), FacingYaw(n), t, LawnTop);
                }
            }
        }
        StreetTrees(m, t, r, m.streetTrees, 11f);
    }

    static void Park(Models m, Transform t, Rect r)
    {
        var inner = Rect.MinMaxRect(r.xMin + Walk + 1.5f, r.yMin + Walk + 1.5f, r.xMax - Walk - 1.5f, r.yMax - Walk - 1.5f);
        var spots = new List<Vector2>();
        Scatter(m.trees, 4.5f, 7.5f, 14, 6f);
        Scatter(m.bushes, 0, 0, 14, 3f);
        Scatter(m.flowers, 0, 0, 18, 1.5f);
        StreetTrees(m, t, r, m.streetTrees, 11f);

        void Scatter(List<Model> set, float hMin, float hMax, int count, float spacing)
        {
            for (int i = 0, tries = 0; i < count && tries < count * 30; tries++)
            {
                var p = new Vector2(Rand(inner.xMin, inner.xMax), Rand(inner.yMin, inner.yMax));
                bool clear = true;
                foreach (var q in spots) if ((q - p).sqrMagnitude < spacing * spacing) { clear = false; break; }
                if (!clear) continue;
                var model = Pick(set);
                float s = hMax > 0 ? Rand(hMin, hMax) / model.size.y : Rand(3.2f, 4.5f);
                if (ShadesLot(p, new Vector2(model.size.x, model.size.z) * s * 0.5f, model.size.y * s)) continue;
                Place(model, s, new Vector3(p.x, 0f, p.y), Rand(0f, 360f), t, LawnTop);
                spots.Add(p);
                i++;
            }
        }
    }

    // ---------- rows, lights, trees ----------

    /// Fills the line a→b with buildings whose fronts sit on it, facing outward normal n, each no deeper
    /// than maxDepth. Occasionally leaves a gap with a tree and bushes. Returns (front centre, width).
    static List<(Vector3, float)> Row(Models m, Transform t, List<(Model m, float s)> pool, Vector3 a, Vector3 b, Vector3 n,
                                       float maxDepth, float baseY, float gapChance)
    {
        var placed = new List<(Vector3, float)>();
        var along = (b - a).normalized;
        float length = Vector3.Distance(a, b), cursor = Rand(0f, 2f);
        float yaw = FacingYaw(n);
        while (cursor < length - 4f)
        {
            if (rng.NextDouble() < gapChance) { Pocket(m, t, a + along * (cursor + 3f) - n * 3f, baseY); cursor += 6f; continue; }

            bool done = false;
            foreach (var (model, s) in Shuffled(pool))
            {
                float w = model.size.x * s, d = model.size.z * s, h = model.size.y * s;
                if (cursor + w > length || d > maxDepth) continue;
                var centre = a + along * (cursor + w * 0.5f) - n * (d * 0.5f);
                var half = Mathf.Abs(n.z) > 0.5f ? new Vector2(w, d) * 0.5f : new Vector2(d, w) * 0.5f;
                if (ShadesLot(new Vector2(centre.x, centre.z), half, h)) continue;
                Place(model, s, centre, yaw, t, baseY);
                placed.Add((a + along * (cursor + w * 0.5f), w));
                cursor += w + Rand(1f, 3f);
                done = true;
                break;
            }
            if (!done) { Pocket(m, t, a + along * (cursor + 3f) - n * 3f, baseY); cursor += 6f; }
        }
        return placed;
    }

    /// A small tree with a couple of bushes, used for gaps between buildings.
    static void Pocket(Models m, Transform t, Vector3 p, float baseY)
    {
        var tree = Pick(m.trees);
        float s = Rand(4f, 6f) / tree.size.y;
        if (!ShadesLot(new Vector2(p.x, p.z), new Vector2(tree.size.x, tree.size.z) * s * 0.5f, tree.size.y * s))
            Place(tree, s, p, Rand(0f, 360f), t, baseY);
        for (int i = 0; i < 2; i++)
            Place(Pick(m.bushes), Rand(3f, 4f), p + new Vector3(Rand(-2f, 2f), 0f, Rand(-2f, 2f)), Rand(0f, 360f), t, baseY);
    }

    static void StreetLights(Models m, Transform t, Rect r)
    {
        foreach (var (a, b, n) in AllEdges(r, 0.6f))
        {
            var along = (b - a).normalized;
            float length = Vector3.Distance(a, b);
            for (float s = 6f; s < length - 4f; s += 22f)
            {
                var p = a + along * s;
                float h = m.light.size.y * LightScale;
                if (!ShadesLot(new Vector2(p.x, p.z), Vector2.one, h)) Place(m.light, LightScale, p, FacingYaw(n), t, WalkTop);
            }
        }
    }

    static void StreetTrees(Models m, Transform t, Rect r, List<Model> set, float spacing)
    {
        foreach (var (a, b, _) in AllEdges(r, Walk * 0.5f))
        {
            var along = (b - a).normalized;
            float length = Vector3.Distance(a, b);
            for (float s = 4f; s < length - 3f; s += spacing)
            {
                var model = Pick(set);
                float sc = Rand(5f, 7f) / model.size.y;
                var p = a + along * s;
                if (!ShadesLot(new Vector2(p.x, p.z), new Vector2(model.size.x, model.size.z) * sc * 0.5f, model.size.y * sc))
                    Place(model, sc, p, Rand(0f, 360f), t, WalkTop);
            }
        }
    }

    // Edges of r pulled in by `inset`, as (start, end, outward normal).
    static IEnumerable<(Vector3, Vector3, Vector3)> EndEdges(Rect r, float inset)
    {
        yield return (new Vector3(r.xMin + inset, 0, r.yMin + inset), new Vector3(r.xMax - inset, 0, r.yMin + inset), Vector3.back);
        yield return (new Vector3(r.xMax - inset, 0, r.yMax - inset), new Vector3(r.xMin + inset, 0, r.yMax - inset), Vector3.forward);
    }

    static IEnumerable<(Vector3, Vector3, Vector3)> SideEdges(Rect r, float inset)
    {
        yield return (new Vector3(r.xMin + inset, 0, r.yMax - inset), new Vector3(r.xMin + inset, 0, r.yMin + inset), Vector3.left);
        yield return (new Vector3(r.xMax - inset, 0, r.yMin + inset), new Vector3(r.xMax - inset, 0, r.yMax - inset), Vector3.right);
    }

    static IEnumerable<(Vector3, Vector3, Vector3)> AllEdges(Rect r, float inset)
    {
        foreach (var e in EndEdges(r, inset)) yield return e;
        foreach (var e in SideEdges(r, inset)) yield return e;
    }

    /// True if an object with this footprint and height would throw any shadow onto the lot.
    static bool ShadesLot(Vector2 centre, Vector2 half, float height)
    {
        var off = shadowPerMeter * height;
        var box = Rect.MinMaxRect(
            centre.x - half.x + Mathf.Min(0f, off.x), centre.y - half.y + Mathf.Min(0f, off.y),
            centre.x + half.x + Mathf.Max(0f, off.x), centre.y + half.y + Mathf.Max(0f, off.y));
        return box.Overlaps(Lot);
    }

    // ---------- models ----------

    static Models LoadModels()
    {
        var m = new Models();
        foreach (var c in "abcdefghijklmn") m.shops.Add((Load($"{CommercialDir}building-{c}.fbx"), CityScale));
        foreach (var c in "abcde") m.towers.Add((Load($"{CommercialDir}building-skyscraper-{c}.fbx"), CityScale));
        foreach (var c in "abcdefghijklm") m.far.Add((Load($"{CommercialDir}low-detail-building-{c}.fbx"), FarScale));
        foreach (var c in "ab") m.far.Add((Load($"{CommercialDir}low-detail-building-wide-{c}.fbx"), FarScale));
        foreach (var c in "abcdefghijklmnopqrstu") m.houses.Add((Load($"{SuburbanDir}building-type-{c}.fbx"), HouseScale));
        foreach (var n in new[] { "tree_oak", "tree_detailed", "tree_fat", "tree_default", "tree_cone", "tree_pineRoundA", "tree_pineDefaultA", "tree_small" })
            m.trees.Add(Load($"{NatureDir}{n}.fbx"));
        foreach (var n in new[] { "tree_simple", "tree_small", "tree_tall", "tree_oak" }) m.streetTrees.Add(Load($"{NatureDir}{n}.fbx"));
        m.streetTrees.Add(Load($"{SuburbanDir}tree-large.fbx"));
        m.streetTrees.Add(Load($"{SuburbanDir}tree-small.fbx"));
        foreach (var n in new[] { "plant_bush", "plant_bushDetailed", "plant_bushSmall", "plant_bushLarge", "grass_large" }) m.bushes.Add(Load($"{NatureDir}{n}.fbx"));
        foreach (var n in new[] { "flower_redA", "flower_yellowA", "flower_purpleA" }) m.flowers.Add(Load($"{NatureDir}{n}.fbx"));
        m.fence = Load($"{SuburbanDir}fence.fbx");
        m.light = Load(StreetLightPath);
        return m;
    }

    static readonly Dictionary<string, Model> cache = new();

    static Model Load(string path)
    {
        if (cache.TryGetValue(path, out var hit) && hit.prefab != null) return hit;
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null) throw new System.Exception($"[DroneSim] Backdrop model missing: {path}");
        var tmp = Object.Instantiate(prefab);
        PropSet.RendererBounds(tmp, out var b);
        Object.DestroyImmediate(tmp);
        var m = new Model { prefab = prefab, size = b.size, center = new Vector3(b.center.x, b.min.y, b.center.z) };
        cache[path] = m;
        return m;
    }

    /// Instantiates a model so its footprint is centred on pos and its base sits at baseY.
    static GameObject Place(Model m, float scale, Vector3 pos, float yaw, Transform parent, float baseY)
    {
        var go = (GameObject)PrefabUtility.InstantiatePrefab(m.prefab, parent);
        var rot = Quaternion.Euler(0f, yaw, 0f);
        go.transform.localScale = Vector3.one * scale;
        go.transform.SetPositionAndRotation(new Vector3(pos.x, baseY, pos.z) - rot * (m.center * scale), rot);
        foreach (var c in go.GetComponentsInChildren<Collider>()) Object.DestroyImmediate(c);
        foreach (var tr in go.GetComponentsInChildren<Transform>()) tr.gameObject.isStatic = true;
        return go;
    }

    static Transform Group(string name)
    {
        var t = new GameObject(name).transform;
        t.SetParent(root, false);
        t.gameObject.isStatic = true;
        return t;
    }

    static float Rand(float a, float b) => a + (float)rng.NextDouble() * (b - a);
    static T Pick<T>(List<T> list) => list[rng.Next(list.Count)];

    static List<T> Shuffled<T>(List<T> list)
    {
        var copy = new List<T>(list);
        for (int i = copy.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }
        return copy;
    }
}
