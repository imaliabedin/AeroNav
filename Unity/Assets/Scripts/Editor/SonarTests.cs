using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// Batch-mode checks for the sonar work. Results go to the log ([SonarTest] lines).
///   Unity -batchmode -projectPath Unity -executeMethod SonarTests.FullKnowledgePaths
public static class SonarTests
{
    static string OutDir => System.Environment.GetEnvironmentVariable("SONAR_TEST_OUT") ?? Path.GetTempPath();

    static void Log(string msg) => Debug.Log("[SonarTest] " + msg);

    static void RunAndExit(System.Func<bool> test)
    {
        bool ok = false;
        try { ok = test(); }
        catch (System.Exception e) { Debug.LogException(e); }
        Log(ok ? "RESULT PASS" : "RESULT FAIL");
        EditorApplication.Exit(ok ? 0 : 1);
    }

    // ---------- Full-Knowledge regression: A* paths on fixed maps ----------

    /// Full-Knowledge A* paths for the ParkingLot scene and two synthetic maps, as "name: cells".
    public static List<string> FullKnowledgePathList()
    {
        var lines = new List<string>();

        EditorSceneManager.OpenScene("Assets/Scenes/ParkingLot.unity", OpenSceneMode.Single);
        var map = Object.FindAnyObjectByType<CellMap>();
        map.RebuildRegistry();
        lines.Add(PathLine("parkinglot", AStarPlanner.FindPath(map, map.droneStart)));

        // Synthetic: a wall with one gap between start and goal.
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        map = new GameObject("Map").AddComponent<CellMap>();
        map.props = new GameObject("Objects").AddComponent<PropSet>();
        for (int z = -4; z <= 4; z++) if (z != 3) for (int y = 0; y <= 3; y++) map.Place(new Vector3Int(5, y, z), CellType.Wall);
        map.Place(new Vector3Int(10, 1, 0), CellType.Win);
        lines.Add(PathLine("wallgap", AStarPlanner.FindPath(map, new Vector3Int(0, 1, 0))));

        // Synthetic: a placed prop (street light) on the straight line.
        var catalog = AssetDatabase.LoadAssetAtPath<PropCatalog>("Assets/Props/PropCatalog.asset");
        map.props.catalog = catalog;
        map.Clear();
        map.props.Place(catalog.IndexOf("Street light"), new Vector3(5f, -0.5f, 0f), 0f);
        map.Place(new Vector3Int(10, 1, 0), CellType.Win);
        lines.Add(PathLine("streetlight", AStarPlanner.FindPath(map, new Vector3Int(0, 1, 0))));
        return lines;
    }

    static string PathLine(string name, AStarPlanner.Result r) =>
        $"{name}: found={r.found} expanded={r.expanded} cells={string.Join(" ", r.path.Select(p => $"{p.x},{p.y},{p.z}"))}";

    public static void FullKnowledgePaths() => RunAndExit(() =>
    {
        var lines = FullKnowledgePathList();
        File.WriteAllLines(Path.Combine(OutDir, "fk_paths.txt"), lines);
        foreach (var l in lines) Log(l.Length > 160 ? l.Substring(0, 160) + "..." : l);
        return lines.All(l => l.Contains("found=True"));
    });

    // ---------- Sensor detection performance ----------

    static readonly Vector3 SensorPos = new Vector3(0f, 1f, 0f);   // flight height: 1.5 m above the ground at y = -0.5
    const float GroundY = -0.5f;
    const int PulsesPerCase = 5;

    class Case
    {
        public string group, label;
        public float distance, bearing;
        public System.Func<GameObject> build;
        public bool primitive = true;   // convex colliders: true distance from Collider.ClosestPoint
    }

    class Outcome
    {
        public int pulses, detectedPulses;
        public bool within;
        public readonly List<float> rangeErrors = new List<float>();    // measured - true distance to the reflecting point (noise)
        public readonly List<float> beyondNearest = new List<float>();  // measured - distance to the obstacle's nearest point
    }

    /// The ablation steps: each adds one improvement to the previous one.
    static readonly string[] Configs =
        { "A ray only", "B +SphereCast 0.1", "C +4 sub-rays", "D +sub-ray rotation", "E +pattern dither", "F +sub-ray SphereCast" };

    /// Settings for an ablation step; F is the default SonarSettings.
    static SonarSettings Settings(string config, bool perfect)
    {
        var s = new SonarSettings { perfect = perfect };
        char c = config[0];
        if (c <= 'A') s.sphereRadius = 0f;
        if (c <= 'B') s.subRays = 0;
        if (c <= 'C') s.rotatePattern = false;
        if (c <= 'D') s.ditherPattern = false;
        if (c <= 'E') s.subRaySphereRadius = 0f;
        return s;
    }

    static Vector3 At(float distance, float bearingDeg, float y) =>
        new Vector3(distance * Mathf.Sin(bearingDeg * Mathf.Deg2Rad), y, distance * Mathf.Cos(bearingDeg * Mathf.Deg2Rad));

    static GameObject Pole(float radius, Vector3 xz)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);   // CapsuleCollider, radius 0.5 * scale.x
        go.transform.position = new Vector3(xz.x, GroundY + 2f, xz.z);  // ground to 3.5 m
        go.transform.localScale = new Vector3(2f * radius, 2f, 2f * radius);
        return go;
    }

    static GameObject Box(Vector3 center, Vector3 size, float yaw)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.transform.SetPositionAndRotation(center, Quaternion.Euler(0f, yaw, 0f));
        go.transform.localScale = size;
        return go;
    }

    static GameObject SceneProp(string name, Vector3 xz)
    {
        var catalog = AssetDatabase.LoadAssetAtPath<PropCatalog>("Assets/Props/PropCatalog.asset");
        var root = PropSet.Build(catalog.Get(catalog.IndexOf(name)));
        root.transform.position = new Vector3(xz.x, GroundY, xz.z);
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>())    // same colliders PropSet.Place adds
            if (mf.sharedMesh != null && mf.GetComponent<Collider>() == null)
                mf.gameObject.AddComponent<MeshCollider>().sharedMesh = mf.sharedMesh;
        return root;
    }

    static List<Case> Cases()
    {
        var list = new List<Case>();
        float[] distances = { 1f, 2f, 3f, 4f, 5f, 6f };

        foreach (float r in new[] { 0.025f, 0.05f, 0.1f })
            foreach (float d in distances)
                for (float b = 0f; b <= 20f; b += 2.5f)
                {
                    float rr = r, dd = d, bb = b;
                    list.Add(new Case { group = $"pole r={r * 100:0.#}cm", distance = d, bearing = b,
                        build = () => Pole(rr, At(dd, bb, 0f)) });
                }

        foreach (float t in new[] { 0.01f, 0.02f, 0.04f })
            foreach (float d in distances)
                foreach (float h in new[] { -0.5f, 0f, 0.5f, 1f })
                    foreach (float yawOff in new[] { 0f, 45f })
                        foreach (float b in new[] { 0f, 11.25f })
                        {
                            float tt = t, dd = d, hh = h, yo = yawOff, bb = b;
                            // 8 m long horizontal wire; its center is `d` away along bearing `b`.
                            list.Add(new Case { group = $"wire {t * 100:0.#}cm", distance = d, bearing = b, label = $"h{h:+0.0;-0.0} yaw{yawOff}",
                                build = () => Box(At(dd, bb, SensorPos.y + hh), new Vector3(8f, tt, tt), bb + yo) });   // yaw = bearing: crosses the line of sight
                        }

        foreach (var (orient, yawOff) in new[] { ("face-on", 0f), ("45deg", 45f), ("edge-on", 90f) })
            foreach (float d in distances)
                foreach (float b in new[] { 0f, 5.625f, 11.25f })
                {
                    float dd = d, bb = b, yo = yawOff;
                    // 0.5 m wide, 2 m tall, 3 cm thick plank standing on the ground.
                    list.Add(new Case { group = $"barrier 3cm {orient}", distance = d, bearing = b,
                        build = () => Box(At(dd, bb, GroundY + 1f), new Vector3(0.5f, 2f, 0.03f), bb + yo) });
                }

        foreach (string prop in new[] { "Street light", "Traffic cone", "Bush" })
            foreach (float d in distances)
                foreach (float b in new[] { 0f, 5.625f, 11.25f })
                {
                    string pp = prop; float dd = d, bb = b;
                    list.Add(new Case { group = $"scene {prop}", distance = d, bearing = b, primitive = false,
                        build = () => SceneProp(pp, At(dd, bb, 0f)) });
                }
        return list;
    }

    static Outcome Evaluate(SonarSensor sensor, Case c, int phase)
    {
        var go = c.build();
        Physics.SyncTransforms();
        var cols = new HashSet<Collider>(go.GetComponentsInChildren<Collider>());
        float trueNearest = c.primitive
            ? cols.Min(col => Vector3.Distance(SensorPos, col.ClosestPoint(SensorPos)))
            : float.NaN;

        var o = new Outcome();
        sensor.PulseCount = phase;
        for (int p = 0; p < PulsesPerCase; p++)
        {
            var pulse = sensor.Pulse(SensorPos);
            float best = float.PositiveInfinity, bestTrue = 0f;
            foreach (var m in pulse.samples)
                if (m.debugSource == EchoSource.Object && cols.Contains(m.debugCollider)
                    && !SonarMap.IsGroundEcho(m, GroundY, 0.2f) && m.range < best)
                {
                    best = m.range;
                    bestTrue = m.debugTrueRange;
                }
            o.pulses++;
            if (float.IsPositiveInfinity(best)) continue;
            o.detectedPulses++;
            o.within = true;
            o.rangeErrors.Add(best - bestTrue);
            if (c.primitive) o.beyondNearest.Add(best - trueNearest);
        }
        Object.DestroyImmediate(go);
        return o;
    }

    static SonarSensor NewSensor()
    {
        var go = new GameObject("Sonar (test)");
        go.transform.position = SensorPos;
        var sensor = go.AddComponent<SonarSensor>();
        sensor.Init();   // Awake doesn't run in edit mode
        return sensor;
    }

    static string Pct(int n, int d) => d == 0 ? "-" : $"{100f * n / d:0}%";
    static float Median(List<float> v) { var s = v.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
    static float Sd(List<float> v) { float m = v.Average(); return Mathf.Sqrt(v.Sum(x => (x - m) * (x - m)) / v.Count); }

    /// Runs `cases` with one setting; returns per-group summary lines.
    static List<string> Measure(SonarSensor sensor, List<Case> cases, SonarSettings settings, string tag, List<string> csv)
    {
        sensor.settings = settings;
        sensor.Init();
        var byGroup = new Dictionary<string, List<(Case c, Outcome o)>>();
        for (int i = 0; i < cases.Count; i++)
        {
            var o = Evaluate(sensor, cases[i], (i * 7919) % 1000);
            if (!byGroup.TryGetValue(cases[i].group, out var l)) byGroup[cases[i].group] = l = new List<(Case, Outcome)>();
            l.Add((cases[i], o));
            csv?.Add($"{tag},{cases[i].group},{cases[i].label},{cases[i].distance},{cases[i].bearing},{o.detectedPulses},{o.pulses},{(o.within ? 1 : 0)}");
        }

        var lines = new List<string>();
        foreach (var kv in byGroup)
        {
            var rows = kv.Value;
            int dp = rows.Sum(r => r.o.detectedPulses), pp = rows.Sum(r => r.o.pulses);
            int w = rows.Count(r => r.o.within);
            var errs = rows.SelectMany(r => r.o.rangeErrors).ToList();
            var beyond = rows.SelectMany(r => r.o.beyondNearest).ToList();
            string perDist = string.Join(" ", rows.GroupBy(r => r.c.distance).OrderBy(g => g.Key)
                .Select(g => $"{g.Key:0}m:{Pct(g.Count(r => r.o.within), g.Count())}"));
            string err = errs.Count == 0 ? "-"
                : $"noise mean {errs.Average():+0.000;-0.000} sd {Sd(errs):0.000} |max| {errs.Max(Mathf.Abs):0.000} m";
            string far = beyond.Count == 0 ? "" : $"; beyond nearest point: median {Median(beyond):0.00} m";
            lines.Add($"{tag,-22} {kv.Key,-22} per-pulse {Pct(dp, pp),5}  within-5 {Pct(w, rows.Count),5}  [{perDist}]  range {err}{far}");
        }
        return lines;
    }

    public static void Detection() => RunAndExit(() =>
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var sensor = NewSensor();
        var all = Cases();
        var thin = all.Where(c => c.group.StartsWith("pole r=2.5") || c.group.StartsWith("pole r=5") || c.group.StartsWith("wire")).ToList();
        var csv = new List<string> { "config,group,label,distance,bearing,detectedPulses,pulses,within5" };

        Log($"pattern: {sensor.BeamDirections?.Length ?? 0} beams x {1 + sensor.settings.subRays} samples; {all.Count} cases, {PulsesPerCase} pulses each");
        Log("=== Ablation, perfect sensor (geometry only): each config adds one improvement ===");
        foreach (var cfg in Configs)
            foreach (var l in Measure(sensor, thin, Settings(cfg, true), cfg, csv)) Log(l);

        Log("=== E (thin sub-rays) with noise, missed and false echoes: all obstacles ===");
        foreach (var l in Measure(sensor, all, Settings("E", false), "E noisy", csv)) Log(l);
        Log("=== F = default sensor (sub-rays as SphereCasts) with noise: all obstacles ===");
        foreach (var l in Measure(sensor, all, Settings("F", false), "F noisy (default)", csv)) Log(l);

        File.WriteAllLines(Path.Combine(OutDir, "sonar_detection.csv"), csv);
        Object.DestroyImmediate(sensor.gameObject);
        return true;
    });

    // ---------- Navigation ----------

    /// SHA-256 of each FullKnowledgePathList() line, recorded before any navigation code changed.
    static readonly Dictionary<string, string> BaselinePathHashes = new Dictionary<string, string>
    {
        ["parkinglot"] = "bb1c85cc9c24a9d44bf03cad14caa32c26c7309eab51d5e431bb90618bda01c6",
        ["wallgap"] = "543d42399d8eda2043df6ed6369ad748427a7d99bdeb14e3a9fd5b842bf46c54",
        ["streetlight"] = "25eda01ce10c467e07594c6c0320d70373594d7d60b96ba4eddfe8442ea5d662",
    };

    static string Sha256(string s)
    {
        using (var sha = System.Security.Cryptography.SHA256.Create())
            return string.Concat(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s)).Select(b => b.ToString("x2")));
    }

    // Edit mode never calls Awake, and CellMap.Instance is only set in CellMap.Awake. Test-only access.
    static void CallAwake(Object o) =>
        o.GetType().GetMethod("Awake", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(o, null);
    static void SetMapInstance(CellMap map) =>
        typeof(CellMap).GetProperty("Instance").GetSetMethod(true).Invoke(null, new object[] { map });

    class Rig
    {
        public Drone drone;
        public SonarSensor sensor;
        public SonarNavigator nav;
        public CollisionMonitor monitor;
    }

    /// Adds sonar, monitor and navigator to a drone, as SonarBootstrap does at play time.
    static Rig Equip(Drone drone, SonarNavigator.Mode mode, int seed, float speedCap = 8f)
    {
        var go = drone.gameObject;
        var r = new Rig { drone = drone };
        r.sensor = go.AddComponent<SonarSensor>();
        r.sensor.settings.seed = seed;
        r.sensor.Init();
        r.monitor = go.AddComponent<CollisionMonitor>();
        r.monitor.drone = drone;
        r.nav = go.AddComponent<SonarNavigator>();
        r.nav.drone = drone; r.nav.sensor = r.sensor; r.nav.monitor = r.monitor;
        r.nav.mode = mode;
        r.nav.sonarSpeedCap = speedCap;
        r.nav.Init();
        return r;
    }

    static Drone NewDrone(Vector3Int start)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);   // same body as DroneSimSetup builds
        go.name = "Drone (test)";
        go.transform.position = start;
        go.transform.localScale = Vector3.one * 0.4f;
        go.GetComponent<Renderer>().enabled = false;
        var drone = go.AddComponent<Drone>();
        CallAwake(drone);
        return drone;
    }

    class Flight
    {
        public bool reached;
        public int collisions, replans, moves, pulses;
        public float seconds, maxPulseMs, avgPulseMs;
        public float minAltFt = float.PositiveInfinity, maxAltFt = float.NegativeInfinity;   // after take-off
        public string lastHit;
        public override string ToString() =>
            $"{(reached ? "REACHED" : "stopped")} moves {moves} replans {replans} collisions {collisions}" +
            (collisions > 0 ? $" ({lastHit})" : "") + $" time {seconds:0.0}s pulses {pulses} pulse {avgPulseMs:0.00}/{maxPulseMs:0.00} ms avg/max" +
            (minAltFt <= maxAltFt ? $" altitude {minAltFt:0.0}-{maxAltFt:0.0} ft" : "");
    }

    /// Flies like play mode does, at 60 frames per second of simulated time.
    static Flight Fly(Rig r, float speed, float maxSeconds = 90f)
    {
        r.drone.cellsPerSecond = speed;
        r.drone.Run();
        const float dt = 1f / 60f;
        float t = 0f, maxMs = 0f, sumMs = 0f;
        int pulses0 = r.sensor.PulseCount;
        r.sensor.PulseCompleted += OnP;
        void OnP(SonarPulse p) { maxMs = Mathf.Max(maxMs, p.milliseconds); sumMs += p.milliseconds; }
        float minAlt = float.PositiveInfinity, maxAlt = float.NegativeInfinity;
        var startCell = Vector3Int.RoundToInt(r.drone.transform.position);
        // Take-off and landing columns may be below the band; the cruise in between must not.
        var columns = new List<Vector2Int> { new Vector2Int(startCell.x, startCell.z) };
        if (CellMap.Instance != null)
            columns.AddRange(CellMap.Instance.Cells.Where(kv => kv.Value.type == CellType.Win).Select(kv => new Vector2Int(kv.Key.x, kv.Key.z)));
        while (r.drone.Running && t < maxSeconds)
        {
            r.sensor.Tick(dt);
            r.drone.Advance(dt);
            r.monitor.Check();
            t += dt;
            // Altitude while cruising, i.e. outside the take-off and landing columns.
            var pos = r.drone.transform.position;
            if (columns.All(c => Mathf.Abs(pos.x - c.x) > 0.5f || Mathf.Abs(pos.z - c.y) > 0.5f))
            {
                float ft = (pos.y - GroundY) / 0.3048f;
                minAlt = Mathf.Min(minAlt, ft); maxAlt = Mathf.Max(maxAlt, ft);
            }
        }
        r.sensor.PulseCompleted -= OnP;
        int pulses = r.sensor.PulseCount - pulses0;
        return new Flight
        {
            reached = r.drone.Status == Drone.State.Arrived, collisions = r.monitor.Collisions, lastHit = r.monitor.LastHit,
            replans = r.drone.Replans, moves = r.drone.Steps, seconds = t, pulses = pulses,
            maxPulseMs = maxMs, avgPulseMs = pulses > 0 ? sumMs / pulses : 0f,
            minAltFt = minAlt, maxAltFt = maxAlt,
        };
    }

    /// An empty scene with a CellMap, a goal 14 cells ahead and `obstacles` built in it.
    static CellMap SyntheticScene(System.Action<CellMap> obstacles)
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var map = new GameObject("Map").AddComponent<CellMap>();
        map.props = new GameObject("Objects").AddComponent<PropSet>();
        map.Place(new Vector3Int(14, 1, 0), CellType.Win);
        obstacles(map);
        map.RebuildRegistry();
        SetMapInstance(map);
        Physics.SyncTransforms();
        return map;
    }

    // Obstacles on the straight line from (0,1,0) to the goal at (14,1,0). Walls are map cubes (Full-Knowledge
    // mode knows them); poles, wires and planks are plain colliders that only the sonar can find.
    static readonly (string name, System.Action<CellMap> build)[] Scenarios =
    {
        // The drone flies in the 8-10 ft band, so it must go around walls, not over them: give it room
        // (ground-level markers widen the known area without blocking the flight level).
        ("unknown wall", m =>
        {
            for (int z = -3; z <= 3; z++) for (int y = 0; y <= 3; y++) m.Place(new Vector3Int(7, y, z), CellType.Wall);
            m.Place(new Vector3Int(0, 0, -8), CellType.Wall); m.Place(new Vector3Int(14, 0, 8), CellType.Wall);
        }),
        ("pole r=2.5cm on path", m => Pole(0.025f, new Vector3(7f, 0f, 0f))),
        ("pole r=5cm off-center", m => Pole(0.05f, new Vector3(7f, 0f, 0.15f))),
        ("wire 1cm across path", m => Box(new Vector3(7f, 1f, 0f), new Vector3(0.01f, 0.01f, 8f), 0f)),
        ("wire 2cm at 45deg", m => Box(new Vector3(7f, 1.05f, 0f), new Vector3(0.02f, 0.02f, 8f), 45f)),
        ("barrier 3cm edge-on", m => Box(new Vector3(7f, 0.5f, 0f), new Vector3(0.5f, 2f, 0.03f), 0f)),
        ("slalom of poles", m => { Pole(0.05f, new Vector3(4f, 0f, 0f)); Pole(0.05f, new Vector3(7f, 0f, 0.2f)); Pole(0.05f, new Vector3(10f, 0f, -0.2f)); }),
        ("wall with gap + pole in gap", m =>
        {
            // A 5-cell gap (z 1..5): the safety margin keeps the drone 0.6 m from every echo (gap edges and pole),
            // so narrower gaps with a pole in them are correctly impassable.
            for (int z = -6; z <= 6; z++) if (z < 1 || z > 5) for (int y = 0; y <= 4; y++) m.Place(new Vector3Int(7, y, z), CellType.Wall);
            Pole(0.025f, new Vector3(9f, 0f, 3f));
        }),
    };

    public static void Navigation() => RunAndExit(() =>
    {
        bool ok = true;

        // 1. Full-Knowledge regression: identical paths to the recording made before the change.
        foreach (var line in FullKnowledgePathList())
        {
            string name = line.Substring(0, line.IndexOf(':'));
            bool same = Sha256(line) == BaselinePathHashes[name];
            ok &= same;
            Log($"full-knowledge path '{name}': {(same ? "IDENTICAL to baseline" : "CHANGED")}");
        }

        // 2-4, 6. Sonar mode in synthetic scenes, 5 seeds each, at the 8 cells/s cap.
        int runs = 0, reached = 0, collided = 0;
        foreach (var (name, build) in Scenarios)
        {
            var results = new List<Flight>();
            for (int seed = 1; seed <= 10; seed++)
            {
                SyntheticScene(build);
                var rig = Equip(NewDrone(new Vector3Int(0, 1, 0)), SonarNavigator.Mode.Sonar, seed);
                results.Add(Fly(rig, 8f));
            }
            runs += results.Count;
            reached += results.Count(f => f.reached);
            collided += results.Count(f => f.collisions > 0);
            Log($"sonar '{name}': reached {results.Count(f => f.reached)}/{results.Count}, collisions {results.Sum(f => f.collisions)}, " +
                $"replans {string.Join(",", results.Select(f => f.replans))}, moves {string.Join(",", results.Select(f => f.moves))}");
            foreach (var f in results.Where(f => !f.reached || f.collisions > 0)) Log($"    {f}");
        }
        Log($"sonar synthetic total: reached {reached}/{runs}, runs with a collision {collided}/{runs}");
        ok &= reached == runs && collided == 0;

        // Speed: how fast can the drone go before the sensor can't keep up? Seeds also change where the
        // pulse pattern starts, so each run samples the obstacles from different spots.
        foreach (float speed in new[] { 4f, 8f, 12f, 16f, 24f, 32f })
        {
            int rr = 0, cc = 0, n = 0;
            foreach (var (name, build) in Scenarios)
                for (int seed = 1; seed <= 10; seed++)
                {
                    SyntheticScene(build);
                    var rig = Equip(NewDrone(new Vector3Int(0, 1, 0)), SonarNavigator.Mode.Sonar, seed, speed);
                    var f = Fly(rig, speed);
                    n++; if (f.reached) rr++; if (f.collisions > 0) cc++;
                    if (f.collisions > 0 && speed <= 12f) Log($"    {speed:0} cells/s {name} seed {seed}: {f}");
                }
            Log($"speed {speed,2:0} cells/s ({60f / speed:0.0} pulses per 6 m of flight): reached {rr}/{n}, runs with a collision {cc}/{n}");
        }

        // 6, 7. The real ParkingLot route, both modes.
        foreach (var mode in new[] { SonarNavigator.Mode.FullKnowledge, SonarNavigator.Mode.Sonar })
            for (int seed = 1; seed <= (mode == SonarNavigator.Mode.Sonar ? 3 : 1); seed++)
            {
                EditorSceneManager.OpenScene("Assets/Scenes/ParkingLot.unity", OpenSceneMode.Single);
                var map = Object.FindAnyObjectByType<CellMap>();
                map.RebuildRegistry();
                SetMapInstance(map);
                var drone = Object.FindAnyObjectByType<Drone>();
                CallAwake(drone);
                drone.ResetTo(map.droneStart);
                var f = Fly(Equip(drone, mode, seed), 8f);
                Log($"parking lot, {mode}, seed {seed}: {f}");
                if (mode == SonarNavigator.Mode.Sonar) ok &= f.reached && f.collisions == 0;
                else ok &= f.reached && f.moves == 107;   // the baseline path has 108 cells
            }

        // The ParkingLot route with new obstacles across the whole aisle (z 7..23): a wall at x = 30 (added to the map,
        // so Full-Knowledge mode knows it) and a fence of 2.5 cm poles 1 m apart at x = 50 (plain colliders only the
        // sonar can find).
        foreach (var mode in new[] { SonarNavigator.Mode.FullKnowledge, SonarNavigator.Mode.Sonar })
            for (int seed = 1; seed <= (mode == SonarNavigator.Mode.Sonar ? 5 : 1); seed++)
            {
                EditorSceneManager.OpenScene("Assets/Scenes/ParkingLot.unity", OpenSceneMode.Single);
                var map = Object.FindAnyObjectByType<CellMap>();
                map.RebuildRegistry();
                for (int z = 7; z <= 23; z++) for (int y = 0; y <= 2; y++) map.Place(new Vector3Int(30, y, z), CellType.Wall);
                for (int z = 7; z <= 23; z++) Pole(0.025f, new Vector3(50f, 0f, z));
                SetMapInstance(map);
                Physics.SyncTransforms();
                var drone = Object.FindAnyObjectByType<Drone>();
                CallAwake(drone);
                drone.ResetTo(map.droneStart);
                var rig = Equip(drone, mode, seed);
                var f = Fly(rig, 8f);
                var route = rig.drone.Plan.path;
                Log($"parking lot + wall and pole fence across the aisle, {mode}, seed {seed}: {f}; " +
                    $"crossed x=30 at {string.Join(" ", route.Where(c => c.x == 30))}, x=50 at {string.Join(" ", route.Where(c => c.x == 50))}");
                // Full-Knowledge mode is informational here: its route may or may not meet the fence it doesn't know.
                if (mode == SonarNavigator.Mode.Sonar) ok &= f.reached && f.collisions == 0 && f.replans > 0;
            }
        return ok;
    });

    // ---------- Drone City scene: intact, renders, and sonar flights work in it ----------

    public static void DroneCity() => RunAndExit(() =>
    {
        EditorSceneManager.OpenScene("Assets/Scenes/DroneCity.unity", OpenSceneMode.Single);
        var all = Resources.FindObjectsOfTypeAll<GameObject>().Where(g => g.scene.IsValid()).ToList();
        int missingScripts = all.Sum(g => GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(g));
        // Unity's fake-null means `is MeshFilter` can't be used with GetComponent here.
        int missingMeshes = all.Count(g => { var mf = g.GetComponent<MeshFilter>(); return mf != null && mf.sharedMesh == null; });
        int renderers = all.Count(g => g.GetComponent<Renderer>() != null);
        var backdrop = all.FirstOrDefault(g => g.name == "Backdrop");
        Log($"DroneCity scene: {all.Count} objects, {renderers} renderers, missing scripts {missingScripts}, missing meshes {missingMeshes}, " +
            $"backdrop {(backdrop ? backdrop.transform.childCount + " blocks" : "MISSING")}, " +
            $"simulation {Object.FindAnyObjectByType<SimulationManager>() != null}, drone {Object.FindAnyObjectByType<Drone>() != null}");

        // Overview picture (the drone body is left out so URP doesn't rewrite its materials).
        var cam = new GameObject("Overview (test)").AddComponent<Camera>();
        cam.cullingMask = ~(1 << 2);
        cam.farClipPlane = 1500f;
        cam.transform.position = new Vector3(40f, 120f, -110f);
        cam.transform.LookAt(new Vector3(40f, 0f, 30f));
        ShaderUtil.allowAsyncCompilation = false;
        RenderPng(cam, 1280, 720, out _);
        File.WriteAllBytes(Path.Combine(OutDir, "dronecity_overview.png"), RenderPng(cam, 1280, 720, out _));
        Object.DestroyImmediate(cam.gameObject);

        // Sonar flights in the city scene: the lot route with the web demo's surprise obstacles, and a target on a
        // city street east of the lot, beyond a block of buildings (only reachable by hearing the buildings).
        bool ok = missingScripts == 0 && missingMeshes == 0 && backdrop != null;
        foreach (bool street in new[] { false, true })
            for (int seed = 1; seed <= 3; seed++)
            {
                EditorSceneManager.OpenScene("Assets/Scenes/DroneCity.unity", OpenSceneMode.Single);
                int solid = CityColliders.Add();
                var map = Object.FindAnyObjectByType<CellMap>();
                map.RebuildRegistry();
                if (street)
                {
                    foreach (var kv in map.Cells.Where(kv => kv.Value.type == CellType.Win).ToList()) map.Remove(kv.Key);
                    var goal = FreeStreetCell(new Vector2Int(126, 140), new Vector2Int(20, 45), 2);
                    map.Place(new Vector3Int(goal.x, 0, goal.z), CellType.Win);   // on the ground: it lands
                }
                else WebAutopilot.PlaceSurpriseObstacles(map);
                SetMapInstance(map);
                Physics.SyncTransforms();
                var drone = Object.FindAnyObjectByType<Drone>();
                CallAwake(drone);
                drone.ResetTo(map.droneStart);
                var rig = Equip(drone, SonarNavigator.Mode.Sonar, seed);
                var f = Fly(rig, 6f, 240f);
                var goalNow = map.Cells.First(kv => kv.Value.type == CellType.Win).Key;
                Log($"Drone City {(street ? "street target " + goalNow : "lot route + surprise obstacles")}, seed {seed} " +
                    $"(city parts made solid: {solid}): {f}");
                ok &= f.reached && f.collisions == 0 && f.minAltFt >= 8f - 0.01f && f.maxAltFt <= 10f + 0.01f;
            }
        return ok;
    });

    /// A grid cell at level y whose surroundings (drone-sized box) touch no collider, searched in the given x/z ranges.
    static Vector3Int FreeStreetCell(Vector2Int xRange, Vector2Int zRange, int y)
    {
        for (int x = xRange.x; x <= xRange.y; x++)
            for (int z = zRange.x; z <= zRange.y; z++)
            {
                var c = new Vector3Int(x, y, z);
                if (!Physics.CheckBox(c, new Vector3(2f, 0.6f, 2f), Quaternion.identity, ~(1 << 2), QueryTriggerInteraction.Ignore)
                    && !Physics.CheckBox(c - new Vector3(0f, 1.5f, 0f), new Vector3(2f, 0.6f, 2f), Quaternion.identity, ~(1 << 2), QueryTriggerInteraction.Ignore))
                    return c;
            }
        throw new System.Exception("no free street cell found");
    }

    /// Heights of the car models as the traffic builds them (PropSet.Build), vs. the drone's flight level.
    public static void CarHeights() => RunAndExit(() =>
    {
        var catalog = AssetDatabase.LoadAssetAtPath<PropCatalog>("Assets/Props/PropCatalog.asset");
        foreach (var name in new[] { "Sedan", "SUV", "Taxi", "Police car", "Van" })
        {
            var go = PropSet.Build(catalog.Get(catalog.IndexOf(name)));
            PropSet.RendererBounds(go, out var b);
            Log($"{name}: size {b.size.x:0.00} x {b.size.y:0.00} x {b.size.z:0.00} m, top {b.max.y:0.00} m above its base");
            Object.DestroyImmediate(go);
        }
        Log("drone body bottom while cruising: 2.30 m above the ground (center 2.5 m, half-height 0.2 m)");
        return true;
    });

    // ---------- Collision monitor and belief map ----------

    public static void SafetyAndBelief() => RunAndExit(() =>
    {
        bool ok = true;

        // 4. Blind flight into a wall: the monitor must stop the drone at the wall, at normal and very high speed.
        foreach (float speed in new[] { 8f, 64f })
        {
            SyntheticScene(m => { for (int z = -2; z <= 2; z++) for (int y = 0; y <= 2; y++) m.Place(new Vector3Int(7, y, z), CellType.Wall); });
            var rig = Equip(NewDrone(new Vector3Int(0, 1, 0)), SonarNavigator.Mode.Sonar, 1, speed);
            rig.sensor.settings.maxRange = 0.01f;   // blind: the belief stays empty
            var f = Fly(rig, speed);
            float x = rig.drone.transform.position.x;
            bool stoppedAtWall = f.collisions == 1 && !f.reached && x <= 6.5f - 0.2f + 0.02f && x > 5.5f;
            ok &= stoppedAtWall;
            Log($"blind flight at {speed} cells/s: collisions {f.collisions} ({f.lastHit}), drone stopped at x = {x:0.000} " +
                $"(wall face at 6.5, body half-width 0.2) -> {(stoppedAtWall ? "stopped at the wall" : "FAILED")}");
        }

        // Full-Knowledge mode only records: a pole unknown to its map is flown through and the run continues.
        SyntheticScene(m => Pole(0.05f, new Vector3(7f, 0f, 0f)));
        var fk = Fly(Equip(NewDrone(new Vector3Int(0, 1, 0)), SonarNavigator.Mode.FullKnowledge, 1), 8f);
        Log($"full-knowledge through an unmapped pole: {fk} (expected: collision recorded, run not stopped)");
        ok &= fk.reached && fk.collisions == 1;

        // 5. Belief map: a real pole becomes blocked, and is released after the pole is removed.
        SyntheticScene(m => { });
        // Full-Knowledge mode so the navigator doesn't also feed these pulses into its own belief.
        var r = Equip(NewDrone(new Vector3Int(0, 1, 0)), SonarNavigator.Mode.FullKnowledge, 3);
        var map = new SonarMap { bounds = new BoundsInt(-10, 0, -10, 21, 6, 21) };
        var pole = Pole(0.025f, new Vector3(3f, 0f, 0f));
        Physics.SyncTransforms();
        var poleCell = new Vector3Int(3, 1, 0);
        int toBlock = -1, toRelease = -1;
        for (int i = 0; i < 20; i++)
        {
            map.Integrate(r.sensor.Pulse(r.drone.transform.position), 6f, null, null);
            if (toBlock < 0 && map.IsBlocked(poleCell)) toBlock = i + 1;
        }
        Object.DestroyImmediate(pole);
        Physics.SyncTransforms();
        for (int i = 0; i < 60; i++)
        {
            map.Integrate(r.sensor.Pulse(r.drone.transform.position), 6f, null, null);
            if (map.Occupied.Count == 0) { toRelease = i + 1; break; }
        }
        Log($"belief: 2.5 cm pole at 3 m blocked after {toBlock} pulse(s); after removing it, all cells released after {toRelease} pulse(s)");
        ok &= toBlock > 0 && toBlock <= 5 && toRelease > 0;

        // False echoes in an empty scene: how many phantom cells appear, and how long does each one last?
        foreach (float rate in new[] { 0.002f, 0.02f })
        {
            r.sensor.settings.falseEchoProbability = rate;
            map.Clear();
            var since = new Dictionary<Vector3Int, int>();
            var lifetimes = new List<int>();
            int pulsesWithPhantom = 0, sumPhantoms = 0;
            for (int i = 0; i < 1000; i++)
            {
                map.Integrate(r.sensor.Pulse(r.drone.transform.position), 6f, null, null);
                foreach (var c in map.Occupied) if (!since.ContainsKey(c)) since[c] = i;
                foreach (var c in since.Keys.Where(c => !map.IsBlocked(c)).ToList()) { lifetimes.Add(i - since[c]); since.Remove(c); }
                if (map.Occupied.Count > 0) pulsesWithPhantom++;
                sumPhantoms += map.Occupied.Count;
            }
            var stillBlocked = since.OrderBy(kv => kv.Value).Select(kv => $"{kv.Key} ({1000 - kv.Value} pulses)").Take(4).ToList();
            lifetimes.AddRange(since.Values.Select(v => 1000 - v));
            int longest = lifetimes.Count == 0 ? 0 : lifetimes.Max();
            if (stillBlocked.Count > 0) Log($"    still blocked at the end (drone at {Vector3Int.RoundToInt(r.drone.transform.position)}): {string.Join(", ", stillBlocked)}");
            Log($"belief, false echoes {rate * 100:0.0}% per sample ({rate / 0.002f:0}x default), 1000 pulses: phantom cells present in " +
                $"{pulsesWithPhantom / 10f:0.0}% of pulses, average {sumPhantoms / 1000f:0.00} at a time, {lifetimes.Count} phantom cells, " +
                $"longest lasted {longest} pulses ({longest / 10f:0.0} s)");
            ok &= longest <= 30;
        }
        return ok;
    });

    // ---------- Visualization renders, ignores the drone itself, and stays out of the vision camera ----------

    static byte[] RenderPng(Camera cam, int w, int h, out Color32[] pixels)
    {
        var rt = new RenderTexture(w, h, 24);
        cam.targetTexture = rt;
        cam.Render();
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        cam.targetTexture = null;
        pixels = tex.GetPixels32();
        var png = tex.EncodeToPNG();
        Object.DestroyImmediate(tex);
        rt.Release();
        return png;
    }

    public static void Visualization() => RunAndExit(() =>
    {
        EditorSceneManager.OpenScene("Assets/Scenes/ParkingLot.unity", OpenSceneMode.Single);
        var drone = Object.FindAnyObjectByType<Drone>();
        var map = Object.FindAnyObjectByType<CellMap>();
        // A spot next to parked cars: 4 m into the first aisle.
        drone.transform.position = map.droneStart + new Vector3Int(4, 0, 0);

        var sensor = drone.gameObject.AddComponent<SonarSensor>();
        sensor.Init();
        var vis = drone.gameObject.AddComponent<SonarVisualizer>();
        vis.sensor = sensor;
        vis.view = SonarVisualizer.View.EchoesAndRays;
        var belief = new SonarMap();
        vis.belief = belief;

        var camGo = new GameObject("Overview (test)");
        var cam = camGo.AddComponent<Camera>();
        // Don't render the drone body (layer 2): rendering it makes URP rewrite the drone materials' legacy _Color.
        cam.cullingMask = ~(1 << 2);
        cam.transform.position = drone.transform.position + new Vector3(0f, 9f, -7f);
        cam.transform.LookAt(drone.transform.position);
        vis.alsoDrawOn = cam;
        vis.Hook();

        int selfHits = 0;
        for (int i = 0; i < 6; i++)
        {
            sensor.Clock = i * 0.1f;
            var p = sensor.Pulse(drone.transform.position);
            belief.Integrate(p, sensor.settings.maxRange, null, null);
            foreach (var m in p.samples) if (m.debugCollider != null && m.debugCollider.gameObject.layer == 2) selfHits++;
        }
        sensor.Clock = 0.5f + 0.25f;   // the last pulse's ring is 3 m out

        ShaderUtil.allowAsyncCompilation = false;   // no placeholder shading in the first frames
        RenderPng(cam, 960, 540, out _);            // warm-up
        File.WriteAllBytes(Path.Combine(OutDir, "sonar_view.png"), RenderPng(cam, 960, 540, out var withOverlay));
        vis.view = SonarVisualizer.View.Off;
        File.WriteAllBytes(Path.Combine(OutDir, "sonar_view_off.png"), RenderPng(cam, 960, 540, out var without));
        int changed = withOverlay.Where((c, i) => !c.Equals(without[i])).Count();

        // The drone's own vision camera must not receive the overlay.
        var dc = drone.gameObject.AddComponent<DroneCamera>();
        dc.EnsureCamera();
        vis.view = SonarVisualizer.View.EchoesAndRays;
        var a = dc.CaptureJpg(100);
        vis.Unhook();
        var b = dc.CaptureJpg(100);
        bool visionClean = a.SequenceEqual(b);

        Log($"visualization: {changed} pixels changed by the overlay; self-echoes {selfHits} (expect 0); " +
            $"belief cells {belief.Occupied.Count}; vision camera image identical with overlay on: {visionClean}");
        return changed > 1000 && selfHits == 0 && visionClean;
    });

    // ---------- False echoes in an empty scene, and occlusion ----------

    public static void EmptyAndOcclusion() => RunAndExit(() =>
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var sensor = NewSensor();
        sensor.settings = new SonarSettings();
        sensor.Init();

        // Empty scene: only the ground plane. Any non-ground echo is a false positive.
        var map = new SonarMap();
        const int N = 2000;
        int falseSamples = 0, pulsesWithFalse = 0, pulsesWithBlocked = 0, groundReturns = 0, maxBlocked = 0;
        long samples = 0;
        for (int i = 0; i < N; i++)
        {
            var p = sensor.Pulse(SensorPos);
            int f = 0;
            foreach (var m in p.samples)
            {
                samples++;
                if (!m.hasReturn) continue;
                if (map.IsGroundEcho(m)) groundReturns++; else f++;
            }
            falseSamples += f;
            if (f > 0) pulsesWithFalse++;
            map.Integrate(p, sensor.settings.maxRange, null, null);
            if (map.Occupied.Count > 0) pulsesWithBlocked++;
            maxBlocked = Mathf.Max(maxBlocked, map.Occupied.Count);
        }
        Log($"empty scene, {N} pulses: false echoes {falseSamples} of {samples} samples ({100.0 * falseSamples / samples:0.000}%), " +
            $"pulses with >=1 false echo {Pct(pulsesWithFalse, N)}, ground returns {groundReturns}; " +
            $"belief map had a blocked cell after {Pct(pulsesWithBlocked, N)} of pulses (max {maxBlocked} cells)");

        // Occlusion: a 2 m wide, 6 m tall wall 2 m ahead hides a pole 4 m ahead (no sample can pass over it
        // within range); a second pole beside the wall stays visible.
        var wall = Box(At(2f, 0f, 2f), new Vector3(2f, 6f, 0.1f), 0f);
        var hidden = Pole(0.05f, At(4f, 0f, 0f));
        var visible = Pole(0.05f, At(4f, 40f, 0f));
        Physics.SyncTransforms();
        var hc = hidden.GetComponent<Collider>();
        var vc = visible.GetComponent<Collider>();
        int hiddenHits = 0, visibleHits = 0, beyondWall = 0;
        for (int i = 0; i < 200; i++)
        {
            var p = sensor.Pulse(SensorPos);
            foreach (var m in p.samples)
            {
                if (m.debugCollider == hc) hiddenHits++;
                if (m.debugCollider == vc) visibleHits++;
                // A real (non-false) echo from beyond the wall in a direction the wall covers would mean seeing through it.
                bool throughWall = Mathf.Abs(Mathf.Atan2(m.direction.x, m.direction.z) * Mathf.Rad2Deg) < 20f
                                   && Mathf.Abs(m.direction.y) < 0.3f && m.direction.z > 0f;
                if (throughWall && m.hasReturn && m.debugSource == EchoSource.Object && m.range > 2.3f) beyondWall++;
            }
        }
        Log($"occlusion, 200 pulses: hidden pole echoes {hiddenHits} (expect 0), echoes from beyond the wall {beyondWall} (expect 0), " +
            $"visible pole echoes {visibleHits} (expect > 0)");
        return hiddenHits == 0 && beyondWall == 0 && visibleHits > 0;
    });
}
