using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// Drone Sim/Build Parking Lot Scene: (re)builds materials, the prop catalog and a new
/// ParkingLot scene with the drone, manager, camera rig, cubes and placed objects.
public static class DroneSimSetup
{
    const string MatDir = "Assets/Materials";
    const string ScenePath = "Assets/Scenes/ParkingLot.unity";
    const string CatalogPath = "Assets/Props/PropCatalog.asset";
    const string DronePrefabPath = "Assets/Prefabs/Drone.prefab";
    static readonly string[] DroneMaterialPaths =
    {
        "Assets/Materials/Battery_Dark.mat",
        "Assets/Materials/Drone_Body.mat",
        "Assets/Materials/Drone_Rotor.mat",
        "Assets/Materials/Drone_Shell.mat",
        "Assets/Materials/LED_Blue.mat",
        "Assets/Materials/LED_Red.mat",
        "Assets/Materials/Lens_Glass.mat",
        "Assets/Materials/Motor_Metal.mat",
    };

    // name, model path, real-world size (m), model rotation, turn so the long axis runs along Z
    static readonly (string name, string path, float size, Vector3 rot, bool alongZ)[] Models =
    {
        ("Sedan",        "Assets/Models/Cars/sedan.fbx",             4.5f,  Vector3.zero,           true),
        ("SUV",          "Assets/Models/Cars/suv.fbx",               4.8f,  Vector3.zero,           true),
        ("Taxi",         "Assets/Models/Cars/taxi.fbx",              4.5f,  Vector3.zero,           true),
        ("Police car",   "Assets/Models/Cars/police.fbx",            4.7f,  Vector3.zero,           true),
        ("Van",          "Assets/Models/Cars/van.fbx",               5.2f,  Vector3.zero,           true),
        ("Pine tree",    "Assets/Models/Nature/tree_pineTallA.fbx",  7f,    Vector3.zero,           false),
        ("Round tree",   "Assets/Models/Nature/tree_default.fbx",    5f,    Vector3.zero,           false),
        ("Bush",         "Assets/Models/Nature/plant_bushLarge.fbx", 1.6f,  Vector3.zero,           false),
        ("Street light", "Assets/Models/Roads/light-square.fbx",     5.5f,  Vector3.zero,           false),
        ("Traffic cone", "Assets/Models/Cars/cone.fbx",              0.7f,  Vector3.zero,           false),
        ("Phone",        "Assets/Models/Items/phone.obj",            0.15f, new Vector3(90, 0, 0),  false),
        ("Keys",         "Assets/Models/Items/key.obj",              0.12f, new Vector3(90, 0, 0),  false),
        ("Wallet",       "Assets/Models/Items/wallet.obj",           0.18f, Vector3.zero,           false),
    };

    [MenuItem("Drone Sim/Build Parking Lot Scene")]
    public static void BuildParkingLotScene()
    {
        if (EditorApplication.isPlaying)
        {
            Debug.LogWarning("Exit Play mode before building the scene.");
            return;
        }
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        if (!UpgradeDroneMaterials()) return;

        var catalog = BuildCatalog();

        var wall = LitMaterial("Wall", new Color(0.55f, 0.57f, 0.6f));
        var win = TransparentLitMaterial("Win", new Color(0.1f, 0.9f, 0.25f, 0.35f), new Color(0.03f, 0.35f, 0.08f));
        var invisible = TransparentLitMaterial("InvisibleWall", new Color(0.55f, 0.75f, 1f, 0.15f), Color.black);
        var ghost = UnlitMaterial("Ghost", new Color(1f, 0.85f, 0.1f));
        var path = UnlitMaterial("Path", new Color(1f, 0.85f, 0.1f));
        var trail = UnlitMaterial("Trail", new Color(0.2f, 0.6f, 1f));
        var asphalt = LitMaterial("Asphalt", new Color(0.16f, 0.16f, 0.17f));
        var sidewalk = LitMaterial("Sidewalk", new Color(0.62f, 0.62f, 0.6f));
        var paint = UnlitMaterial("LinePaint", new Color(0.92f, 0.92f, 0.9f));

        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        // Map + objects
        var mapGo = new GameObject("Map");
        var map = mapGo.AddComponent<CellMap>();
        map.wallMaterial = wall;
        map.winMaterial = win;
        map.invisibleMaterial = invisible;
        var props = new GameObject("Objects").AddComponent<PropSet>();
        props.catalog = catalog;
        map.props = props;

        BuildParkingLot(map, props, asphalt, sidewalk, paint);

        // Keep the controller collider and A* movement separate from the visual model.
        var droneGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
        droneGo.name = "Drone";
        droneGo.layer = 2; // Ignore Raycast
        droneGo.transform.position = map.droneStart;
        droneGo.transform.localScale = Vector3.one * 0.4f;
        droneGo.GetComponent<Renderer>().enabled = false;
        AddDroneVisual(droneGo.transform);
        var rb = droneGo.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;
        var drone = droneGo.AddComponent<Drone>();
        drone.pathMaterial = path;
        drone.trailMaterial = trail;

        // Camera rig
        var cam = Camera.main != null ? Camera.main : Object.FindAnyObjectByType<Camera>();
        if (cam == null)
        {
            cam = new GameObject("Main Camera").AddComponent<Camera>();
            cam.tag = "MainCamera";
        }
        cam.transform.position = new Vector3(LotX * 0.5f, 52f, -30f);
        cam.transform.rotation = Quaternion.LookRotation(new Vector3(LotX * 0.5f, 0f, LotZ * 0.45f) - cam.transform.position);
        cam.farClipPlane = 500f;
        // Unity's fake-null means ?? can't be used with GetComponent here.
        var fly = cam.GetComponent<FlyCamera>();
        if (fly == null) fly = cam.gameObject.AddComponent<FlyCamera>();
        var builder = cam.GetComponent<MapBuilder>();
        if (builder == null) builder = cam.gameObject.AddComponent<MapBuilder>();
        builder.map = map;
        builder.props = props;
        builder.ghostMaterial = ghost;

        // Manager
        var sim = new GameObject("Simulation").AddComponent<SimulationManager>();
        sim.drone = drone;
        sim.map = map;
        sim.builder = builder;
        sim.flyCamera = fly;

        EditorSceneManager.SaveScene(scene, ScenePath);
        Selection.activeGameObject = droneGo;
        Debug.Log($"[DroneSim] Parking lot built: {map.Count} cubes, {props.Count} objects, drone at {map.droneStart} → {ScenePath}");
    }

    [MenuItem("Drone Sim/Replace Parking Lot Drone Visual")]
    public static void ReplaceParkingLotDroneVisual()
    {
        if (EditorApplication.isPlaying)
        {
            Debug.LogWarning("Exit Play mode before replacing the drone visual.");
            return;
        }
        if (!UpgradeDroneMaterials()) return;

        var scene = EditorSceneManager.OpenScene(ScenePath);
        var drone = GameObject.Find("Drone");
        if (drone == null)
        {
            Debug.LogError($"No Drone object found in {ScenePath}.");
            return;
        }

        var existing = drone.transform.Find("Drone Visual");
        if (existing != null) Object.DestroyImmediate(existing.gameObject);
        var renderer = drone.GetComponent<Renderer>();
        if (renderer != null) renderer.enabled = false;
        AddDroneVisual(drone.transform);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
    }

    static GameObject AddDroneVisual(Transform parent)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(DronePrefabPath);
        if (prefab == null)
        {
            Debug.LogError($"Drone prefab not found at {DronePrefabPath}.");
            return null;
        }

        var visual = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        visual.name = "Drone Visual";
        visual.transform.SetParent(parent, false);
        visual.transform.localPosition = Vector3.zero;
        visual.transform.localRotation = Quaternion.identity;
        visual.transform.localScale = Vector3.one * 2.5f;
        SetLayerRecursively(visual.transform, 2);
        return visual;
    }

    static bool UpgradeDroneMaterials()
    {
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
        {
            Debug.LogError("Could not find the Universal Render Pipeline/Lit shader for the drone materials.");
            return false;
        }

        foreach (var path in DroneMaterialPaths)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                Debug.LogError($"Drone material not found at {path}.");
                return false;
            }
            if (material.shader == shader) continue;

            var color = material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white;
            var metallic = material.HasProperty("_Metallic") ? material.GetFloat("_Metallic") : 0f;
            var smoothness = material.HasProperty("_Glossiness") ? material.GetFloat("_Glossiness") : 0.5f;
            var emission = material.HasProperty("_EmissionColor") ? material.GetColor("_EmissionColor") : Color.black;
            var hasEmission = material.IsKeywordEnabled("_EMISSION");
            material.shader = shader;
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);
            if (hasEmission)
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", emission);
            }
            EditorUtility.SetDirty(material);
        }
        AssetDatabase.SaveAssets();
        return true;
    }

    static void SetLayerRecursively(Transform root, int layer)
    {
        root.gameObject.layer = layer;
        foreach (Transform child in root) SetLayerRecursively(child, layer);
    }

    // ---------- parking lot layout (1 unit = 1 m; asphalt top at y = -0.5, the builder's ground plane) ----------
    // One 40 x 30 m quadrant (two rows of bays facing a centre aisle) tiled 2 x 2 into an 80 x 60 m lot.
    // Each quadrant gets its own random cars and dropped items; the sidewalk and barrier wrap the whole lot.

    const float QuadX = 40f, QuadZ = 30f;
    const int QuadsX = 2, QuadsZ = 2;
    const float LotX = QuadX * QuadsX, LotZ = QuadZ * QuadsZ;
    const float BayW = 2.7f, BayD = 5.5f;
    const int BaysPerRow = 12;
    const float FirstBayX = 3.8f;
    const float RowAz = 1.5f;               // row A bays span z 1.5 .. 7 (quadrant-local)
    const float RowBz = QuadZ - 1.5f - BayD; // row B bays span z 23 .. 28.5

    static void BuildParkingLot(CellMap map, PropSet props, Material asphalt, Material sidewalk, Material paint)
    {
        var ground = new GameObject("Ground").transform;
        Slab(ground, "Asphalt", asphalt, new Vector3(LotX * 0.5f, -0.55f, LotZ * 0.5f), new Vector3(LotX, 0.1f, LotZ));
        // Sidewalk ring, 3 m wide, a curb's height above the asphalt
        const float sw = 3f, top = -0.42f, h = 0.2f;
        Slab(ground, "Sidewalk S", sidewalk, new Vector3(LotX * 0.5f, top - h * 0.5f, -sw * 0.5f), new Vector3(LotX + 2 * sw, h, sw));
        Slab(ground, "Sidewalk N", sidewalk, new Vector3(LotX * 0.5f, top - h * 0.5f, LotZ + sw * 0.5f), new Vector3(LotX + 2 * sw, h, sw));
        Slab(ground, "Sidewalk W", sidewalk, new Vector3(-sw * 0.5f, top - h * 0.5f, LotZ * 0.5f), new Vector3(sw, h, LotZ));
        Slab(ground, "Sidewalk E", sidewalk, new Vector3(LotX + sw * 0.5f, top - h * 0.5f, LotZ * 0.5f), new Vector3(sw, h, LotZ));

        // ---- cubes: one L-shaped low barrier on the north and east sidewalks of the whole lot ----
        map.Clear();
        int bx = Mathf.RoundToInt(LotX) + 1, bz = Mathf.RoundToInt(LotZ) + 1;
        for (int x = -1; x <= bx; x++) map.Place(new Vector3Int(x, 0, bz), CellType.Wall);
        for (int z = 0; z < bz; z++) map.Place(new Vector3Int(bx, 0, z), CellType.Wall);
        // Start in the south-west quadrant's aisle, goal at the far end of the north-east quadrant's aisle
        map.droneStart = new Vector3Int(1, 1, Mathf.RoundToInt(QuadZ * 0.5f));
        var goal = new Vector3Int(Mathf.RoundToInt(LotX) - 2, 1, Mathf.RoundToInt(QuadZ * 1.5f));
        map.Place(goal, CellType.Win);

        // ---- objects (deterministic; each quadrant draws its own random layout) ----
        props.Clear();
        var rng = new System.Random(7);
        int quadIndex = 0;
        for (int qz = 0; qz < QuadsZ; qz++)
            for (int qx = 0; qx < QuadsX; qx++)
                BuildQuadrant(props, ground, paint, new Vector3(qx * QuadX, 0f, qz * QuadZ), quadIndex++, rng);

        int Id(string n) => props.catalog.IndexOf(n);

        // Trees and bushes along the outer south and west sidewalks
        for (float x = 2f; x < LotX - 1f; x += 6f)
        {
            props.Place(Id(rng.Next(2) == 0 ? "Round tree" : "Pine tree"), new Vector3(x, -0.42f, -1.5f), rng.Next(360));
            props.Place(Id("Bush"), new Vector3(x + 3f, -0.42f, -1.5f), rng.Next(360));
        }
        for (float z = 4f; z < LotZ - 1f; z += 7.5f)
            props.Place(Id(rng.Next(2) == 0 ? "Pine tree" : "Round tree"), new Vector3(-1.5f, -0.42f, z), rng.Next(360));

        // Cones guarding the goal
        foreach (var d in new[] { new Vector2(-2f, -2.5f), new Vector2(-2f, 2.5f), new Vector2(1.5f, -2.5f), new Vector2(1.5f, 2.5f) })
            props.Place(Id("Traffic cone"), new Vector3(goal.x + d.x, -0.5f, goal.z + d.y), 0f);
    }

    /// One quadrant: bay markings, aisle dashes, street lights, a random set of parked cars and a
    /// random scatter of dropped items (phones, keys, wallets) and stray cones.
    static void BuildQuadrant(PropSet props, Transform ground, Material paint, Vector3 o, int index, System.Random rng)
    {
        int Id(string n) => props.catalog.IndexOf(n);

        // Bay markings: separators for two rows plus a back line, and a dashed aisle centre line
        var lines = new GameObject($"Markings {index + 1}").transform;
        lines.SetParent(ground, false);
        const float lineY = -0.495f, lw = 0.12f;
        foreach (float z0 in new[] { RowAz, RowBz })
        {
            for (int i = 0; i <= BaysPerRow; i++)
                Slab(lines, "Bay line", paint, o + new Vector3(FirstBayX + i * BayW, lineY, z0 + BayD * 0.5f), new Vector3(lw, 0.01f, BayD));
            float backZ = z0 == RowAz ? z0 : z0 + BayD;
            Slab(lines, "Back line", paint, o + new Vector3(FirstBayX + BaysPerRow * BayW * 0.5f, lineY, backZ), new Vector3(BaysPerRow * BayW, 0.01f, lw));
        }
        for (float x = 2f; x < QuadX - 2f; x += 4f)
            Slab(lines, "Centre dash", paint, o + new Vector3(x + 1f, lineY, QuadZ * 0.5f), new Vector3(2f, 0.01f, lw));

        // Parked cars: each quadrant fills a different share of its bays
        var cars = new[] { Id("Sedan"), Id("SUV"), Id("Taxi"), Id("Police car"), Id("Van") };
        double emptyChance = 0.25 + rng.NextDouble() * 0.35;
        var parked = new System.Collections.Generic.List<Vector3>();
        foreach (float z0 in new[] { RowAz, RowBz })
            for (int i = 0; i < BaysPerRow; i++)
            {
                if (rng.NextDouble() < emptyChance) continue;
                var center = o + new Vector3(FirstBayX + (i + 0.5f) * BayW, -0.5f, z0 + BayD * 0.5f);
                float yaw = (rng.Next(2) == 0 ? 0f : 180f) + (float)(rng.NextDouble() * 6 - 3);
                props.Place(cars[rng.Next(cars.Length)], center, yaw);
                parked.Add(center);
            }

        // Street lights along the aisle
        foreach (float x in new[] { 6f, 13f, 28f, 35f })
            props.Place(Id("Street light"), o + new Vector3(x, -0.5f, QuadZ * 0.5f + 1.5f), 0f);

        // Dropped items: 2-4 small things beside random parked cars, on the aisle side of the bay
        var items = new[] { Id("Phone"), Id("Keys"), Id("Wallet") };
        int drops = parked.Count == 0 ? 0 : 2 + rng.Next(3);
        for (int d = 0; d < drops; d++)
        {
            var car = parked[rng.Next(parked.Count)];
            float side = car.z - o.z < QuadZ * 0.5f ? 1f : -1f; // toward the aisle
            var pos = car + new Vector3((float)(rng.NextDouble() * 2 - 1) * 1.2f, 0f, side * (BayD * 0.5f + 0.3f + (float)rng.NextDouble() * 0.8f));
            props.Place(items[rng.Next(items.Length)], pos, rng.Next(360));
        }

        // A couple of stray cones somewhere in the aisle
        int cones = rng.Next(3);
        for (int c = 0; c < cones; c++)
            props.Place(Id("Traffic cone"), o + new Vector3(4f + (float)rng.NextDouble() * (QuadX - 8f), -0.5f, QuadZ * 0.5f + (float)(rng.NextDouble() * 6 - 3)), 0f);
    }

    /// A collider-less box used for ground, sidewalks and paint (scenery, not part of the cell map).
    internal static void Slab(Transform parent, string name, Material mat, Vector3 center, Vector3 size)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.transform.localScale = size;
        go.GetComponent<Renderer>().sharedMaterial = mat;
        go.isStatic = true;
    }

    // ---------- prop catalog ----------

    static PropCatalog BuildCatalog()
    {
        // Mesh colliders on placed objects need readable meshes in player builds. Reimport first:
        // a reimport after the catalog is loaded would leave us holding a stale catalog object.
        foreach (var m in Models)
        {
            if (AssetImporter.GetAtPath(m.path) is ModelImporter importer && !importer.isReadable)
            {
                importer.isReadable = true;
                importer.SaveAndReimport();
            }
        }

        if (!AssetDatabase.IsValidFolder("Assets/Props")) AssetDatabase.CreateFolder("Assets", "Props");
        var catalog = AssetDatabase.LoadAssetAtPath<PropCatalog>(CatalogPath);
        if (catalog == null)
        {
            catalog = ScriptableObject.CreateInstance<PropCatalog>();
            AssetDatabase.CreateAsset(catalog, CatalogPath);
        }

        catalog.entries.Clear();
        foreach (var m in Models)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(m.path);
            if (prefab == null)
            {
                Debug.LogWarning($"[DroneSim] Model missing: {m.path}");
                continue;
            }
            var rot = m.rot;
            if (m.alongZ && LongAxisIsX(prefab)) rot.y += 90f;
            catalog.entries.Add(new PropCatalog.Entry { name = m.name, prefab = prefab, size = m.size, modelRotation = rot });
        }
        EditorUtility.SetDirty(catalog);
        AssetDatabase.SaveAssets();
        return AssetDatabase.LoadAssetAtPath<PropCatalog>(CatalogPath);
    }

    static bool LongAxisIsX(GameObject prefab)
    {
        var tmp = Object.Instantiate(prefab);
        bool x = PropSet.RendererBounds(tmp, out var b) && b.size.x > b.size.z;
        Object.DestroyImmediate(tmp);
        return x;
    }

    // ---------- materials ----------

    internal static Material LitMaterial(string name, Color color, Color? emission = null)
    {
        var mat = GetOrCreate(name, "Universal Render Pipeline/Lit");
        mat.SetColor("_BaseColor", color);
        if (emission.HasValue)
        {
            mat.EnableKeyword("_EMISSION");
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            mat.SetColor("_EmissionColor", emission.Value);
        }
        EditorUtility.SetDirty(mat);
        return mat;
    }

    /// URP Lit in Transparent/Alpha mode, so the drone stays visible inside the win cube.
    static Material TransparentLitMaterial(string name, Color color, Color emission)
    {
        var mat = LitMaterial(name, color, emission);
        mat.SetFloat("_Surface", 1f);   // Transparent
        mat.SetFloat("_Blend", 0f);     // Alpha
        mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
        mat.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetFloat("_ZWrite", 0f);
        mat.SetOverrideTag("RenderType", "Transparent");
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.SetShaderPassEnabled("ShadowCaster", false);
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static Material UnlitMaterial(string name, Color color)
    {
        var mat = GetOrCreate(name, "Universal Render Pipeline/Unlit");
        mat.SetColor("_BaseColor", color);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static Material GetOrCreate(string name, string shaderName)
    {
        if (!AssetDatabase.IsValidFolder(MatDir)) AssetDatabase.CreateFolder("Assets", "Materials");
        string path = $"{MatDir}/{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(Shader.Find(shaderName));
            AssetDatabase.CreateAsset(mat, path);
        }
        return mat;
    }
}
