using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// Connects the sonar to the existing drone navigation.
/// Sonar mode (default): the planner knows only the goal and the outer bounds; obstacles come from a
/// belief map built from echoes, and the drone replans when a newly confirmed obstacle blocks its path.
/// Full-Knowledge mode: the original behavior (A* over the real map). N toggles in Build mode.
public class SonarNavigator : MonoBehaviour
{
    public enum Mode { Sonar, FullKnowledge }

    public Mode mode = Mode.Sonar;
    [Tooltip("Max cells/s in Sonar mode, so the drone can't outrun its sensor.")]
    public float sonarSpeedCap = 8f;
    [Tooltip("Pulses fired at the start position before the first plan.")]
    public int warmupPulses = 3;

    [Header("Altitude band (Sonar mode)")]
    [Tooltip("Cruise only between these heights above the ground. Below the band the drone may only fly straight up " +
             "from its start (take-off) and straight down onto its target (landing).")]
    public bool enforceAltitudeBand = true;
    public float minAltitudeFeet = 8f;
    public float maxAltitudeFeet = 10f;
    [Tooltip("Height of a real car (m). The scene scale is the typical parked car's height divided by this, so the band " +
             "is 8-10 ft relative to everything in the scene (the car models here are about 1.5x real size).")]
    public float realCarHeight = 1.5f;

    float sceneScale = -1f;

    /// How much bigger than real life this scene is, judged by its parked cars (1 if it has none).
    public float SceneScale
    {
        get
        {
            if (sceneScale > 0f) return sceneScale;
            sceneScale = 1f;
            var map = CellMap.Instance;
            if (map != null && map.props != null)
            {
                var heights = new List<float>();
                foreach (var p in map.props.GetComponentsInChildren<Prop>())
                    if (System.Array.IndexOf(CarNames, p.entryName) >= 0 && PropSet.RendererBounds(p.gameObject, out var b))
                        heights.Add(b.size.y);
                if (heights.Count > 0)
                {
                    heights.Sort();
                    sceneScale = Mathf.Max(1f, heights[heights.Count / 2] / realCarHeight);
                }
            }
            return sceneScale;
        }
    }
    static readonly string[] CarNames = { "Sedan", "SUV", "Taxi", "Police car", "Van" };

    /// Real-world feet for a scene height (meters above the ground).
    public float RealFeet(float sceneMeters) => sceneMeters / SceneScale / 0.3048f;

    /// Grid levels (cell y) whose centers lie inside the altitude band (scaled to the scene).
    public Vector2Int BandLevels
    {
        get
        {
            float ground = sensor ? sensor.settings.groundY : -0.5f;
            float k = 0.3048f * SceneScale;
            int lo = Mathf.CeilToInt(minAltitudeFeet * k + ground - 1e-4f);
            int hi = Mathf.FloorToInt(maxAltitudeFeet * k + ground + 1e-4f);
            return new Vector2Int(lo, Mathf.Max(lo, hi));
        }
    }

    public Drone drone;
    public SonarSensor sensor;
    public CollisionMonitor monitor;
    public SonarVisualizer visualizer;

    public SonarMap Belief { get; } = new SonarMap();
    public int ObstacleCellsConfirmed { get; private set; }
    public int ReplanRequests { get; private set; }

    readonly List<Vector3Int> newlyBlocked = new List<Vector3Int>();
    readonly List<Vector3Int> released = new List<Vector3Int>();
    bool initialized;

    void Start() => Init();

    /// Wires the components together (Start does this; tests call it directly).
    public void Init()
    {
        if (initialized) return;
        if (!drone) drone = GetComponent<Drone>();
        if (!sensor) sensor = GetComponent<SonarSensor>();
        if (!monitor) monitor = GetComponent<CollisionMonitor>();
        if (!visualizer) visualizer = GetComponent<SonarVisualizer>();
        sensor.PulseCompleted += OnPulse;
        drone.PlanningStarted += OnPlanningStarted;
        if (monitor) monitor.drone = drone;
        if (visualizer)
        {
            visualizer.belief = Belief;
            visualizer.status = Status;
        }
        ApplyMode();
        initialized = true;
    }

    void OnDestroy()
    {
        if (!initialized) return;
        if (sensor) sensor.PulseCompleted -= OnPulse;
        if (drone) drone.PlanningStarted -= OnPlanningStarted;
    }

    void Update()
    {
        var sim = SimulationManager.Instance;
        var kb = Keyboard.current;
        if (kb != null && kb.nKey.wasPressedThisFrame && sim != null && sim.Mode == SimulationManager.SimMode.Build)
        {
            mode = mode == Mode.Sonar ? Mode.FullKnowledge : Mode.Sonar;
            ApplyMode();
        }
    }

    /// Hands the drone the planner inputs for the current mode.
    public void ApplyMode()
    {
        bool sonar = mode == Mode.Sonar;
        drone.plannerBlocked = sonar ? IsBlockedForPlanning : (System.Func<Vector3Int, bool>)null;
        drone.plannerBounds = sonar ? KnownBounds() : (BoundsInt?)null;
        drone.speedCap = sonar ? sonarSpeedCap : float.PositiveInfinity;
        if (monitor) monitor.haltOnCollision = sonar;
        Belief.bounds = drone.plannerBounds ?? Belief.bounds;
        Belief.groundY = sensor.settings.groundY;
    }

    /// The known outer boundary: the box around the area's cubes (the lot's barrier and the goal), the drone
    /// and the city (if the scene has one), with one cell of margin. Vertically: the altitude band, or from the
    /// ground to two cells above the highest cube. Only the box is passed on; what is inside stays unknown.
    BoundsInt KnownBounds()
    {
        var start = Vector3Int.RoundToInt(drone.transform.position);
        Vector3Int min = start, max = start;
        var map = CellMap.Instance;
        if (map != null)
            foreach (var kv in map.Cells)
            {
                min = Vector3Int.Min(min, kv.Key);
                max = Vector3Int.Max(max, kv.Key);
            }
        if (CityColliders.Area is Bounds city)
        {
            min = Vector3Int.Min(min, Vector3Int.FloorToInt(city.min));
            max = Vector3Int.Max(max, Vector3Int.CeilToInt(city.max));
        }
        min -= Vector3Int.one;
        max += new Vector3Int(1, 2, 1);
        min.y = 0;
        if (enforceAltitudeBand)
        {
            var band = BandLevels;
            min.y = 0;        // take-off and landing columns reach the ground; IsBlockedForPlanning keeps the rest in the band
            max.y = band.y;
        }
        return new BoundsInt(min, max - min + Vector3Int.one);
    }

    readonly List<Vector2Int> columns = new List<Vector2Int>();   // x/z of the take-off and landing columns

    /// Planner obstacle test: the sonar belief, plus the altitude band (outside the take-off and landing columns).
    bool IsBlockedForPlanning(Vector3Int c)
    {
        if (Belief.IsBlocked(c)) return true;
        if (!enforceAltitudeBand) return false;
        var band = BandLevels;
        if (c.y >= band.x && c.y <= band.y) return false;
        foreach (var col in columns) if (col.x == c.x && col.y == c.z) return false;
        return true;
    }

    /// Take-off column (where the drone is now) and a landing column over every goal.
    void SetColumns()
    {
        columns.Clear();
        var p = Vector3Int.RoundToInt(drone.transform.position);
        columns.Add(new Vector2Int(p.x, p.z));
        var map = CellMap.Instance;
        if (map != null)
            foreach (var kv in map.Cells)
                if (kv.Value.type == CellType.Win) columns.Add(new Vector2Int(kv.Key.x, kv.Key.z));
    }

    void OnPlanningStarted()
    {
        if (mode != Mode.Sonar) return;
        SetColumns();
        ApplyMode();   // the map may have been edited since the last run
        Belief.Clear();
        ObstacleCellsConfirmed = 0;
        ReplanRequests = 0;
        if (monitor) monitor.ResetCount();
        for (int i = 0; i < warmupPulses; i++) sensor.Pulse(drone.transform.position);
    }

    void OnPulse(SonarPulse pulse)
    {
        if (mode != Mode.Sonar) return;
        Belief.Integrate(pulse, sensor.settings.maxRange, newlyBlocked, released);
        ObstacleCellsConfirmed += newlyBlocked.Count;
        if (newlyBlocked.Count > 0 && drone.Running && RemainingPathBlocked())
        {
            ReplanRequests++;
            drone.RequestReplan();
        }
    }

    /// True if a cell still ahead on the drone's plan is now believed blocked.
    bool RemainingPathBlocked()
    {
        var plan = drone.Plan;
        if (plan == null || !plan.found) return false;
        for (int i = drone.PathIndex + 1; i < plan.path.Count; i++)
            if (Belief.IsBlocked(plan.path[i])) return true;
        return false;
    }

    string Status() =>
        $"navigation: <b>{(mode == Mode.Sonar ? "SONAR" : "FULL KNOWLEDGE")}</b> (N in build mode)" +
        (mode == Mode.Sonar ? $"   altitude {RealFeet(drone.transform.position.y - sensor.settings.groundY):0.0} ft" +
                              (enforceAltitudeBand ? $" (band {minAltitudeFeet:0}-{maxAltitudeFeet:0} ft)" : "") +
                              $"   belief: {Belief.Occupied.Count} blocked cells   replans: {drone.Replans}" : "") +
        (monitor ? $"   collisions: {monitor.Collisions}" + (monitor.Collisions > 0 ? $" (last: {monitor.LastHit})" : "") : "");

    GUIStyle banner;

    void OnGUI()
    {
        if (!monitor || monitor.Collisions == 0 || mode != Mode.Sonar || drone.Running) return;
        if (banner == null)
        {
            banner = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, richText = true };
            banner.normal.textColor = Color.white;
        }
        GUI.Label(new Rect(0, Screen.height * 0.55f, Screen.width, 60),
            $"<size=32><b><color=#FF5050>COLLISION - drone stopped ({monitor.LastHit})</color></b></size>", banner);
    }
}

/// Adds the sonar, its view, the collision monitor and the navigator to the scene's drone at
/// play time, so no scene edit is needed. Skipped if the scene already has a SonarNavigator.
static class SonarBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Attach()
    {
        var sim = SimulationManager.Instance;
        if (sim == null || sim.drone == null || Object.FindAnyObjectByType<SonarNavigator>() != null) return;
        var go = sim.drone.gameObject;
        // Unity's fake-null means ?? can't be used with GetComponent here.
        var sensor = go.GetComponent<SonarSensor>();
        if (sensor == null) sensor = go.AddComponent<SonarSensor>();
        var vis = go.GetComponent<SonarVisualizer>();
        if (vis == null) vis = go.AddComponent<SonarVisualizer>();
        vis.sensor = sensor;
        var monitor = go.GetComponent<CollisionMonitor>();
        if (monitor == null) monitor = go.AddComponent<CollisionMonitor>();
        monitor.drone = sim.drone;
        if (go.GetComponent<DroneMotionVisual>() == null) go.AddComponent<DroneMotionVisual>().drone = sim.drone;
        var nav = go.AddComponent<SonarNavigator>();
        nav.drone = sim.drone;
        nav.sensor = sensor;
        nav.monitor = monitor;
        nav.visualizer = vis;
    }
}
