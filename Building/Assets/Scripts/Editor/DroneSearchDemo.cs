using System.IO;
using Drone2.Building;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// Multi-drone search demo: a 4-storey building, two drones (first and fourth floor) that split it and
/// search it by sonar, with both drone cameras and the schematic on screen. Builds its own scene so
/// BurningBuilding stays as is. Menu: Drone 2 > Drone Search Demo.
/// Batch: -executeMethod DroneSearchDemo.Simulate [-drones 1|2] [-modes A,B] [-seed n] [-maxTime s]
///        -executeMethod DroneSearchDemo.Record -out dir [-every s] [-seed n]
static class DroneSearchDemo
{
    const string ScenePath = "Assets/Scenes/DroneSearchDemo.unity";

    [MenuItem("Drone 2/Drone Search Demo")]
    public static void Play()
    {
        if (!File.Exists(ScenePath)) CreateScene();
        EditorSceneManager.OpenScene(ScenePath);
        EditorApplication.EnterPlaymode();
    }

    [MenuItem("Drone 2/Recreate Drone Search Demo Scene")]
    public static void CreateScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // The building project's own lighting: its sun, then its fire mood (sky, haze, post-processing).
        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.shadows = LightShadows.Soft;
        RenderSettings.sun = sun;

        var gen = new GameObject("Building Generator").AddComponent<BuildingGenerator>();
        gen.baseMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/BuildingBase.mat");
        gen.glassMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/Glass.mat");
        gen.floors = 4;

        var coord = new GameObject("Search Coordinator").AddComponent<SearchCoordinator>();
        coord.generator = gen;
        coord.dronePrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Drone.prefab");
        BuildingSceneSetup.ApplyMood();

        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log("Created " + ScenePath);
    }

    /// Runs whole searches headless and logs coverage, collisions and time for each split mode.
    public static void Simulate()
    {
        int drones = int.Parse(Arg("-drones", "2"));
        float maxTime = float.Parse(Arg("-maxTime", "1500"));
        foreach (var modeName in Arg("-modes", "BalancedNearest").Split(','))
        {
            var (gen, coord) = Prepare(drones, (SplitMode)System.Enum.Parse(typeof(SplitMode), modeName));
            coord.Begin();
            const float dt = 0.05f;
            float nextLog = 60f;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!coord.Complete && coord.Elapsed < maxTime)
            {
                coord.Step(dt);
                if (coord.Elapsed >= nextLog)
                {
                    nextLog += 60f;
                    Debug.Log($"SIM {modeName} t={coord.Elapsed:0}s coverage {coord.Coverage() * 100f:0.0}% " +
                              string.Join(" ", coord.drones.ConvertAll(d => $"[d{d.id + 1} f{d.Floor + 1} {d.Status} {d.Debugging} replans {d.Replans}]")));
                }
            }
            Debug.Log($"SIMRESULT drones={drones} mode={modeName} seed={gen.seed} complete={coord.Complete} " +
                      $"time={coord.Elapsed:0.0}s wall={sw.Elapsed.TotalSeconds:0}s :: {coord.Summary()}");
            string dump = Arg("-dump", "");
            if (dump != "") File.WriteAllText(dump, MapDump(coord, gen));
        }
    }

    /// ASCII map per floor for debugging: walls from the plan (| -), sonar belief at flight height (#),
    /// spots seen (.), unseen (o), closed off (X), inside walls (s), stair entry (E), drones (1, 2).
    static string MapDump(SearchCoordinator coord, BuildingGenerator gen)
    {
        var g = coord.Grid;
        var sb = new System.Text.StringBuilder();
        for (int f = g.floors - 1; f >= 0; f--)
        {
            sb.AppendLine($"Floor {f + 1}");
            var entry = g.CellOf(g.StairEntry(f));
            for (int z = g.nz - 1; z >= 0; z--)
            {
                for (int x = 0; x < g.nx; x++)
                {
                    char ch = g.state[f, x, z] switch
                    {
                        SearchGrid.Spot.Seen => '.',
                        SearchGrid.Spot.Unseen => 'o',
                        SearchGrid.Spot.Unreachable => 'X',
                        SearchGrid.Spot.Solid => 's',
                        _ => ' ',
                    };
                    foreach (var d in coord.drones)
                        if (d.Belief.IsBlocked(new Vector3Int(x, g.BeliefLayer(f), z)) && d.Floor == f) ch = ch == ' ' ? '#' : ch == 'X' ? '%' : '#';
                    foreach (var d in coord.drones) if (d.IsHeat(f, x, z)) ch = '*';
                    if (x % 2 == 0 && z % 2 == 1 && gen.Plan.floors[f].GetEdge(true, x / 2, z / 2) == EdgeKind.Wall) ch = '|';
                    if (z % 2 == 0 && x % 2 == 1 && gen.Plan.floors[f].GetEdge(false, x / 2, z / 2) == EdgeKind.Wall) ch = '-';
                    if (new Vector2Int(x, z) == entry) ch = 'E';
                    foreach (var d in coord.drones)
                        if (d.Floor == f && g.CellOf(d.transform.position) == new Vector2Int(x, z)) ch = (char)('1' + d.id);
                    sb.Append(ch);
                }
                sb.AppendLine();
            }
            // Phantom blocks: believed-blocked cells on the edge of each drone's reachable area that are
            // not on a wall line of the plan, with their evidence and what physically overlaps them.
            foreach (var d in coord.drones)
            {
                if (d.Floor != f) continue;
                int L = g.BeliefLayer(f);
                var seen = new bool[g.nx, g.nz];
                var q = new System.Collections.Generic.Queue<Vector2Int>();
                var s0 = g.CellOf(d.transform.position);
                q.Enqueue(s0); seen[s0.x, s0.y] = true;
                var edge = new System.Collections.Generic.HashSet<Vector2Int>();
                while (q.Count > 0)
                {
                    var c = q.Dequeue();
                    foreach (var o in new[] { Vector2Int.right, Vector2Int.left, Vector2Int.up, Vector2Int.down })
                    {
                        var n = c + o;
                        if (!g.InInterior(n.x, n.y) || seen[n.x, n.y]) continue;
                        if (d.Belief.IsBlocked(new Vector3Int(n.x, L, n.y)) || d.IsHeat(f, n.x, n.y)) { edge.Add(n); continue; }
                        seen[n.x, n.y] = true;
                        q.Enqueue(n);
                    }
                }
                int reach = 0;
                foreach (var v in seen) if (v) reach++;
                sb.AppendLine($"  drone {d.id + 1} reaches {reach} cells; frontier blocks {edge.Count}");
                foreach (var c in edge)
                {
                    bool onWallLine = (c.x % 2 == 0 && WallNear(gen.Plan.floors[f], true, c)) || (c.y % 2 == 0 && WallNear(gen.Plan.floors[f], false, c));
                    if (onWallLine) continue;
                    var w = g.World(f, c);
                    var names = new System.Collections.Generic.List<string>();
                    foreach (var col in Physics.OverlapBox(w, new Vector3(0.45f, 0.3f, 0.45f)))
                        names.Add($"{col.name}<{col.transform.parent?.name}> @{col.bounds.center.y - g.origin.y:0.00}-{col.bounds.max.y - g.origin.y:0.00}");
                    sb.AppendLine($"    phantom? cell {c} heat={d.IsHeat(f, c.x, c.y)} ev={d.Belief.Evidence(new Vector3Int(c.x, L, c.y)):0.0} near: {string.Join(", ", names)}");
                }
            }
            // Doors the sonar believes blocked, and what is physically there.
            var fp = gen.Plan.floors[f];
            for (int v = 0; v < 2; v++)
                for (int x = 0; x <= fp.width; x++)
                    for (int z = 0; z <= fp.depth; z++)
                    {
                        bool vert = v == 0;
                        if (vert ? z >= fp.depth : x >= fp.width) continue;
                        if (fp.GetEdge(vert, x, z) != EdgeKind.Door) continue;
                        var cell = vert ? new Vector2Int(2 * x, 2 * z + 1) : new Vector2Int(2 * x + 1, 2 * z);
                        foreach (var d in coord.drones)
                        {
                            if (!d.Belief.IsBlocked(new Vector3Int(cell.x, g.BeliefLayer(f), cell.y))) continue;
                            var c = g.World(f, cell);
                            var names = new System.Collections.Generic.List<string>();
                            foreach (var col in Physics.OverlapBox(c, new Vector3(0.3f, 0.3f, 0.3f)))
                                names.Add($"{col.name}({col.transform.parent?.name})");
                            sb.AppendLine($"  door cell {cell} blocked for drone {d.id + 1} (evidence {d.Belief.Evidence(new Vector3Int(cell.x, g.BeliefLayer(f), cell.y)):0.0}); overlaps: {string.Join(", ", names)}");
                        }
                    }
        }
        return sb.ToString();
    }

    /// Runs one search and writes a frame of the full screen layout every `-every` simulated seconds.
    public static void Record()
    {
        string outDir = Arg("-out", "SearchFrames");
        float every = float.Parse(Arg("-every", "2"));
        float maxTime = float.Parse(Arg("-maxTime", "1500"));
        float startAt = float.Parse(Arg("-start", "0"));   // simulated seconds to skip before recording
        bool jpg = Arg("-jpg", "0") == "1";
        int w = int.Parse(Arg("-w", "1440")), h = int.Parse(Arg("-h", "810"));
        Directory.CreateDirectory(outDir);
        var (gen, coord) = Prepare(int.Parse(Arg("-drones", "2")),
            (SplitMode)System.Enum.Parse(typeof(SplitMode), Arg("-mode", "BalancedNearest")));

        var rt = new RenderTexture(w, h, 24);
        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        void Shot(string path)
        {
            foreach (var d in coord.drones)
            {
                d.visualizer.Refresh();
                foreach (var p in d.GetComponentsInChildren<PropellorSpin>()) p.transform.Rotate(0f, 47f, 0f);
            }
            coord.Schematic.Refresh();
            var cams = new System.Collections.Generic.List<Camera> { coord.SchematicCamera };
            foreach (var d in coord.drones) cams.Add(d.cam);
            foreach (var c in cams) c.targetTexture = rt;
            coord.Schematic.FitCamera();
            foreach (var c in cams) c.Render();
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            File.WriteAllBytes(jpg ? Path.ChangeExtension(path, ".jpg") : path, jpg ? tex.EncodeToJPG(88) : tex.EncodeToPNG());
            RenderTexture.active = null;
            foreach (var c in cams) c.targetTexture = null;
        }

        Shot(Path.Combine(outDir, "warmup.png"));   // the first batch render comes out unlit
        File.Delete(Path.Combine(outDir, jpg ? "warmup.jpg" : "warmup.png"));
        int frame = 0;
        if (startAt <= 0f) Shot(Path.Combine(outDir, $"frame_{frame++:D4}.png"));
        coord.Begin();
        float dt = Mathf.Min(0.05f, every);
        float next = Mathf.Max(every, startAt);
        while (!coord.Complete && coord.Elapsed < maxTime)
        {
            coord.Step(dt);
            if (coord.Elapsed >= next)
            {
                next += every;
                Shot(Path.Combine(outDir, $"frame_{frame++:D4}.png"));
            }
        }
        Shot(Path.Combine(outDir, $"frame_{frame++:D4}.png"));
        Debug.Log($"RECORDED {frame} frames to {outDir}; {coord.Summary()}");
    }

    /// True if the cell lies on (or at the end of) a wall segment of the plan along that line.
    static bool WallNear(FloorPlan fp, bool vertical, Vector2Int c)
    {
        if (vertical)
        {
            int x = c.x / 2;
            for (int z = (c.y - 1) / 2; z <= c.y / 2; z++)
                if (z >= 0 && z < fp.depth && x <= fp.width && fp.GetEdge(true, x, z) is EdgeKind.Wall or EdgeKind.Window) return true;
        }
        else
        {
            int z = c.y / 2;
            for (int x = (c.x - 1) / 2; x <= c.x / 2; x++)
                if (x >= 0 && x < fp.width && z <= fp.depth && fp.GetEdge(false, x, z) is EdgeKind.Wall or EdgeKind.Window) return true;
        }
        return false;
    }

    static (BuildingGenerator, SearchCoordinator) Prepare(int drones, SplitMode mode)
    {
        CreateScene();
        EditorSceneManager.OpenScene(ScenePath);
        var gen = Object.FindFirstObjectByType<BuildingGenerator>();
        var coord = Object.FindFirstObjectByType<SearchCoordinator>();
        gen.seed = int.Parse(Arg("-seed", "12345"));
        gen.fireAndSmoke = Arg("-smoke", "1") == "1";
        gen.Generate();
        coord.spawnFloors = drones == 1 ? new[] { 0 } : new[] { 0, gen.floors - 1 };
        coord.splitMode = mode;
        coord.Setup(gen);
        return (gen, coord);
    }

    static string Arg(string name, string fallback)
    {
        var args = System.Environment.GetCommandLineArgs();
        int i = System.Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }
}
