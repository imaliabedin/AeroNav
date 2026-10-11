using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// Web build only: the controls behind the website's buttons (city.html calls these through
/// unityInstance.SendMessage("WebControls", ...)). Vision is switched off (it needs the local Python
/// server); the drone navigates by sonar only. Commands: Run, Stop, Tool(goal|wall|start|<object name>),
/// Clear, Surprise. The area scan has its own receiver, "ScanControls" (ScanMission). Placing a new goal replaces the old one. In the Editor it only runs if WEB_AUTOPILOT=1.
public class WebAutopilot : MonoBehaviour
{
    SimulationManager sim;
    HashSet<Vector3Int> originalCells;
    HashSet<Prop> originalProps;
    readonly List<GameObject> surprise = new List<GameObject>();
    HashSet<Vector3Int> goalsLastFrame = new HashSet<Vector3Int>();
    SimulationManager.SimMode lastMode;
    Vector3Int lastStart;
    bool ready;
    ScanMission scan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Attach()
    {
        bool web = Application.platform == RuntimePlatform.WebGLPlayer;
        bool forced = System.Environment.GetEnvironmentVariable("WEB_AUTOPILOT") == "1";
        if ((!web && !forced) || SimulationManager.Instance == null) return;
        new GameObject("WebControls").AddComponent<WebAutopilot>();
    }

    IEnumerator Start()
    {
        yield return null;   // let the vision and sonar bootstraps attach first
        yield return null;
        sim = SimulationManager.Instance;
        var drone = sim.drone;

        // Sonar only, and the website's buttons instead of the in-game text panels.
        foreach (var c in drone.GetComponents<TargetDetector>()) Destroy(c);
        foreach (var c in drone.GetComponents<DroneCamera>()) Destroy(c);
        sim.detector = null;
        SimulationManager.HideHud = true;
        var nav = drone.GetComponent<SonarNavigator>();
        if (nav) { nav.mode = SonarNavigator.Mode.Sonar; nav.ApplyMode(); }
        var vis = drone.GetComponent<SonarVisualizer>();
        if (vis) vis.view = SonarVisualizer.View.EchoesAndRays;
        sim.SetSpeed(6f);
        if (sim.builder) sim.builder.reach = 3000f;   // clicks anywhere in the city, not just near the camera

        // Start and target on the ground: the drone takes off and lands.
        sim.map.droneStart = new Vector3Int(sim.map.droneStart.x, 0, sim.map.droneStart.z);
        var goals = new List<Vector3Int>(Goals());
        foreach (var g in goals)
        {
            sim.map.Remove(g);
            sim.map.Place(new Vector3Int(g.x, 0, g.z), CellType.Win);
        }
        if (sim.Mode == SimulationManager.SimMode.Build) sim.drone.ResetTo(sim.map.droneStart);

        originalCells = new HashSet<Vector3Int>();
        foreach (var kv in sim.map.Cells) if (kv.Value.type == CellType.Wall) originalCells.Add(kv.Key);
        originalProps = new HashSet<Prop>(sim.map.props.GetComponentsInChildren<Prop>());
        Surprise();
        goalsLastFrame = Goals();
        lastStart = sim.map.droneStart;
        lastMode = sim.Mode;
        sim.ResetCamera();
        scan = ScanMission.Attach(sim);   // area search: the website's region / object / scan controls
        ready = true;
    }

    void Update()
    {
        if (!ready) return;

        // One goal only: when a new goal cube appears, remove the others; and put it on open ground.
        var goals = Goals();
        var added = new List<Vector3Int>();
        foreach (var g in goals) if (!goalsLastFrame.Contains(g)) added.Add(g);
        if (added.Count > 0)
        {
            foreach (var g in goals) sim.map.Remove(g);
            sim.map.Place(OpenGround(added[0]), CellType.Win);
            Physics.SyncTransforms();
        }
        goalsLastFrame = Goals();

        // Start on open ground too (a drone can't take off inside a building).
        if (sim.map.droneStart != lastStart)
        {
            sim.map.droneStart = OpenGround(sim.map.droneStart);
            if (sim.Mode == SimulationManager.SimMode.Build) sim.drone.ResetTo(sim.map.droneStart);
            lastStart = sim.map.droneStart;
        }

        // Camera: follow the drone in flight, show the whole lot while building.
        if (sim.Mode != lastMode && sim.flyCamera)
        {
            if (sim.Mode == SimulationManager.SimMode.Running)
            {
                sim.flyCamera.followOffset = new Vector3(0f, 9f, -8f);
                sim.flyCamera.follow = sim.drone.transform;
            }
            else if (sim.Mode == SimulationManager.SimMode.Build) sim.ResetCamera();
            lastMode = sim.Mode;
        }
    }

    /// The nearest ground-level cell (searching outwards up to 25 m) where a drone can take off or land:
    /// nothing solid in its column up to cruising height, with room for the drone and its safety margin.
    static Vector3Int OpenGround(Vector3Int c)
    {
        for (int r = 0; r <= 25; r++)
            for (int dx = -r; dx <= r; dx++)
                for (int dz = -r; dz <= r; dz++)
                {
                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != r) continue;   // this ring only
                    int x = c.x + dx, z = c.z + dz;
                    if (!Physics.CheckBox(new Vector3(x, 1f, z), new Vector3(0.85f, 1.45f, 0.85f), Quaternion.identity,
                                          ~(1 << 2), QueryTriggerInteraction.Ignore))
                        return new Vector3Int(x, 0, z);
                }
        return new Vector3Int(c.x, 0, c.z);
    }

    HashSet<Vector3Int> Goals()
    {
        var set = new HashSet<Vector3Int>();
        foreach (var kv in sim.map.Cells) if (kv.Value.type == CellType.Win) set.Add(kv.Key);
        return set;
    }

    // ---------- commands from the website ----------

    public void Run()
    {
        if (!ready || (scan && scan.Busy)) return;
        if (sim.Mode != SimulationManager.SimMode.Build) sim.EnterBuildMode();
        sim.StartSim();
    }

    /// Simulation speed multiplier ("1" or "5"): drone, scan, traffic, sonar and the run timer all follow
    /// Time.timeScale; the camera runs on real time.
    public void SetTimeScale(string value)
    {
        if (float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float k))
            Time.timeScale = Mathf.Clamp(k, 0.1f, 10f);
    }

    public void Stop()
    {
        if (scan && scan.Busy) scan.StopScan();
        if (ready) sim.EnterBuildMode();
    }

    /// goal | wall | start | a catalog object name (e.g. "Street light"). Then click on the lot to place.
    public void Tool(string name)
    {
        if (!ready || !sim.builder) return;
        if (scan && scan.Busy) return;
        if (sim.Mode != SimulationManager.SimMode.Build) sim.EnterBuildMode();
        if (scan) scan.Release();         // the build tools take the clicks again
        var b = sim.builder;
        switch (name)
        {
            case "goal": b.tool = MapBuilder.Tool.Win; break;
            case "wall": b.tool = MapBuilder.Tool.Wall; break;
            case "start": b.tool = MapBuilder.Tool.DroneStart; break;
            default:
                int i = b.props && b.props.catalog ? b.props.catalog.IndexOf(name) : -1;
                if (i >= 0) { b.tool = MapBuilder.Tool.Object; b.selectedProp = i; }
                break;
        }
    }

    /// Removes everything added since the page loaded (walls, objects, surprise obstacles).
    public void Clear()
    {
        if (!ready) return;
        sim.EnterBuildMode();
        var remove = new List<Vector3Int>();
        foreach (var kv in sim.map.Cells)
            if (kv.Value.type == CellType.Wall && !originalCells.Contains(kv.Key)) remove.Add(kv.Key);
        foreach (var c in remove) sim.map.Remove(c);
        foreach (var p in sim.map.props.GetComponentsInChildren<Prop>())
            if (!originalProps.Contains(p)) sim.map.props.Remove(p);
        foreach (var go in surprise) if (go) Destroy(go);
        surprise.Clear();
        Physics.SyncTransforms();
    }

    /// Puts the surprise obstacles (wall, pole fence, wire) back on the route.
    public void Surprise()
    {
        if (sim == null) return;
        foreach (var go in surprise) if (go) Destroy(go);
        surprise.Clear();
        var nav = sim.drone.GetComponent<SonarNavigator>();
        surprise.AddRange(PlaceSurpriseObstacles(sim.map, nav ? nav.BandLevels.x : 2));
    }

    /// Obstacles the planner is never told about, across the route along the aisle (z = 15): a 6 m wall across
    /// the whole aisle, a fence of 2.5 cm poles (8 m tall), and a 1 cm wire at the flight level (grid y).
    /// Returns the poles and the wire (the wall is cubes in the map).
    public static List<GameObject> PlaceSurpriseObstacles(CellMap map, int flightLevel = 2)
    {
        var made = new List<GameObject>();
        for (int z = 7; z <= 23; z++)
            for (int y = 0; y <= Mathf.Max(5, flightLevel + 1); y++) map.Place(new Vector3Int(12, y, z), CellType.Wall);
        for (int z = 7; z <= 23; z++)
        {
            var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pole.name = "Pole 2.5cm";
            pole.transform.position = new Vector3(30f, 3.5f, z);          // ground (-0.5) to 7.5
            pole.transform.localScale = new Vector3(0.05f, 4f, 0.05f);
            pole.GetComponent<Renderer>().sharedMaterial = map.wallMaterial;   // the default material is pink in URP builds
            made.Add(pole);
        }
        var wire = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wire.name = "Wire 1cm";
        wire.transform.position = new Vector3(45f, flightLevel, 15f);   // at flight height
        wire.transform.localScale = new Vector3(0.01f, 0.01f, 16f);
        wire.GetComponent<Renderer>().sharedMaterial = map.wallMaterial;
        made.Add(wire);
        Physics.SyncTransforms();
        return made;
    }
}
