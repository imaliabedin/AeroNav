using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;

/// Area search: the user clicks four ground cells, picks an object type, and the drone flies a lawnmower over the
/// region at a fixed altitude, photographing the ground straight below it. Images go to scan_server.py
/// (image_store/scans/<scan_id>/), which also serves the website. Website commands arrive through
/// SendMessage("ScanControls", ...); state goes back to the page as JSON through ScanBridge.jslib (window "scan" events).
///
/// Capture is distance-based: each lane has planned capture positions (from the camera footprint and overlap), and an
/// image is taken when the drone's actual position reaches the next one, whatever its speed. Coverage is computed from
/// where each image was actually taken. Cells hidden by moving cars or people, cells never inside a saved image
/// (gaps, failed saves) and suspected road cells are revisited after the lanes, up to `maxLooks` times each.
///
/// Coordinates: world meters, Unity axes (x east, y up, z north), ground at y = groundY (-0.5). Grid cells are
/// 1 m with centers on integer x, z. This script never sends the hidden object's position with the images.
public class ScanMission : MonoBehaviour
{
    public enum Phase { Idle, Selecting, Ready, Starting, Scanning, Revisiting, Done, Stopped, Error }

    public ScanPlanner.Config config = new ScanPlanner.Config();
    public float cruiseSpeed = 5f;        // m/s along lanes
    public float turnSpeed = 1.5f;        // m/s at lane ends (the drone slows for the turn)
    public float acceleration = 3f;       // m/s²
    public float climbRate = 4f;          // m/s
    public float clearance = 1.5f;        // m above the highest static obstacle under the drone
    [Tooltip("Photos are taken (and sent to the AI) only while the camera is this high above the ground (m). " +
             "The model was trained on 2.3-5 m views. Higher (climbing over obstacles) the spot is left for a revisit.")]
    public float minCaptureAltitude = 2.5f, maxCaptureAltitude = 4f;
    public int maxLooks = 3;              // revisit captures per cell before it is reported unresolved
    public float revisitWait = 1.5f;      // s between looks at the same place (lets traffic move on)
    public int jpegQuality = 90;
    public string serverUrl;              // default: the page's own server (WebGL) or SCAN_SERVER / http://127.0.0.1:8000/
    public float groundY = -0.5f;

    public static readonly string[] Objects = { "Wallet", "Keys", "Phone", "Purse" };

    public Phase State { get; private set; } = Phase.Idle;
    public string ScanId { get; private set; }
    public ScanPlanner.Plan Plan { get; private set; }
    public ScanCoverage Coverage { get; private set; }
    public int Saved { get; private set; }
    public int Failed { get; private set; }
    /// Planned photos not taken because the drone was outside the capture altitude band (e.g. climbing over a tree).
    public int SkippedAltitude { get; private set; }
    public readonly List<Vector2> SkippedPositions = new List<Vector2>();
    public int Captured { get; private set; }
    public string Target { get; private set; } = "Phone";
    public int LaneIndex => laneIdx;
    public bool Busy => State == Phase.Starting || State == Phase.Scanning || State == Phase.Revisiting;
    public int PendingUploads => uploads.Count + (current != null ? 1 : 0);

    /// Set true by tests to drive the mission with Tick() instead of Update().
    public bool manualTick;

    SimulationManager sim;
    Transform drone;
    DroneCamera droneCam;
    readonly List<Vector2Int> corners = new List<Vector2Int>();
    Vector2[] region;
    GameObject placedTarget;
    Vector3 lastTargetPos;    // kept only here, never uploaded with images

    // flight
    class Leg { public Vector2 to; public float endSpeed; public int lane = -1; public bool revisit; }
    readonly List<Leg> legs = new List<Leg>();
    int legIdx, laneIdx = -1, nextCapture;
    float speed, hover, phaseTime;
    int revisitRound;
    Vector2 pos2;

    // uploads (one at a time, in capture order)
    class Upload { public string json; public int seq; public ScanCoverage.Observation obs; public int tries; public float notBefore; }
    readonly Queue<Upload> uploads = new Queue<Upload>();
    Upload current;
    UnityWebRequest req;
    UnityWebRequest control;     // create / finish requests
    System.Action<UnityWebRequest> controlDone;
    float clock;
    float missionStart = -1f, missionEnd = -1f;
    /// Mission duration in simulated seconds (= real-world flight time), the same at any playback speed.
    public float MissionSeconds => missionStart < 0f ? 0f : (missionEnd >= 0f ? missionEnd : clock) - missionStart;
    bool finishSent;

    // visuals (layer 2, so the drone camera never photographs them)
    LineRenderer outline, route;
    MeshFilter wall;          // fading green curtain rising from the outline
    readonly List<LineRenderer> posts = new List<LineRenderer>();
    Material lineMat;

#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] static extern void ScanEmit(string json);
#else
    static void ScanEmit(string json) { if (Verbose) Debug.Log("[Scan] " + json); }
#endif
    public static bool Verbose = true;

    // ---------- setup ----------

    public static ScanMission Attach(SimulationManager sim)
    {
        var go = new GameObject("ScanControls");
        var m = go.AddComponent<ScanMission>();
        m.sim = sim;
        return m;
    }

    void Awake() => Setup();

    /// Edit-mode tests: no Awake/Start/Update there, so they call this and then Tick().
    public void InitForTest(SimulationManager s, string server)
    {
        sim = s;
        serverUrl = server;
        manualTick = true;
        Setup();
        drone = sim.drone.transform;
    }

    void Setup()
    {
        if (outline != null) return;
        if (string.IsNullOrEmpty(serverUrl))
        {
            var page = Application.absoluteURL;
            if (Application.platform == RuntimePlatform.WebGLPlayer && !string.IsNullOrEmpty(page))
                serverUrl = page.Substring(0, page.LastIndexOf('/') + 1);
            else serverUrl = System.Environment.GetEnvironmentVariable("SCAN_SERVER") ?? "http://127.0.0.1:8000/";
        }
        if (!serverUrl.EndsWith("/")) serverUrl += "/";
        lineMat = HeightFade.SharedMaterial;   // fades with height, hidden behind buildings
        outline = NewLine("Scan region", 0.35f);
        var wallGo = new GameObject("Scan region wall") { layer = 2 };
        wallGo.transform.SetParent(transform, false);
        wall = wallGo.AddComponent<MeshFilter>();
        wall.sharedMesh = new Mesh { name = "Scan region wall" };
        var wr = wallGo.AddComponent<MeshRenderer>();
        wr.sharedMaterial = lineMat;
        wr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        wr.receiveShadows = false;
        route = NewLine("Scan route", 0.12f);
    }

    void Start()
    {
        if (sim == null) sim = SimulationManager.Instance;
        drone = sim.drone.transform;
        Emit(StateJson("Pick four corners to start."));
    }

    LineRenderer NewLine(string name, float width)
    {
        var go = new GameObject(name) { layer = 2 };
        go.transform.SetParent(transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.widthMultiplier = width;
        lr.sharedMaterial = lineMat;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.positionCount = 0;
        return lr;
    }

    static void Kill(Object o) { if (Application.isPlaying) Destroy(o); else DestroyImmediate(o); }

    /// Color with the shared height fade applied at the line's two ends (call after setting positions).
    static void SetColor(LineRenderer lr, Color c)
    {
        int n = lr.positionCount;
        lr.startColor = HeightFade.At(c, n > 0 ? lr.GetPosition(0).y : HeightFade.GroundY);
        lr.endColor = HeightFade.At(c, n > 0 ? lr.GetPosition(n - 1).y : HeightFade.GroundY);
    }

    // ---------- commands from the website ----------

    public void BeginSelect()
    {
        if (Busy) return;
        if (sim.builder) sim.builder.enabled = false;   // clicks select corners, not build tools
        State = region != null ? Phase.Ready : Phase.Selecting;
        Emit(StateJson("Click four corners on the ground."));
    }

    /// "x,z": adds a corner without a mouse click (city.html?autoscan=..., automated tests).
    public void AddCornerXZ(string xz)
    {
        var p = xz.Split(',');
        if (p.Length == 2 && int.TryParse(p[0], out int x) && int.TryParse(p[1], out int z)) AddCorner(new Vector2Int(x, z));
    }

    public void ResetRegion()
    {
        if (Busy) return;
        ClearRegion();
        State = Phase.Selecting;
        if (sim.builder) sim.builder.enabled = false;
        Emit(RegionJson(null));
    }

    public void UndoCorner()
    {
        if (Busy || corners.Count == 0) return;
        corners.RemoveAt(corners.Count - 1);
        State = Phase.Selecting;
        UpdateRegion();
    }

    public void SetObject(string name)
    {
        if (System.Array.IndexOf(Objects, name) >= 0) Target = name;
        Emit("{\"type\":\"object\",\"object\":" + Q(Target) + "}");
    }

    /// JSON: {"altitude":6,"overlap":0.15,"speed":5}. Any field may be omitted.
    public void SetConfig(string json)
    {
        if (Busy) return;
        var c = JsonUtility.FromJson<ConfigMsg>(json);
        if (c.altitude > 0f) config.altitude = Mathf.Clamp(c.altitude, minCaptureAltitude, maxCaptureAltitude);
        if (c.overlap > 0f) config.overlap = Mathf.Clamp(c.overlap, 0.02f, 0.5f);
        if (c.speed > 0f) cruiseSpeed = Mathf.Clamp(c.speed, 1f, 12f);
        UpdateRegion();
    }
    [System.Serializable] class ConfigMsg { public float altitude, overlap, speed; }

    /// Called by a non-builder tool on the website: the sonar demo's build tools take the clicks again.
    public void Release()
    {
        if (Busy) return;
        if (State == Phase.Selecting) State = corners.Count == 4 && region != null ? Phase.Ready : Phase.Idle;
        if (sim.builder) sim.builder.enabled = true;
    }

    public void StartScan()
    {
        if (Busy || region == null || (State == Phase.Selecting)) { Emit(StateJson("Select a valid region first.")); return; }
        if (!ControlIdle) return;
        sim.EnterBuildMode();                           // stops any sonar flight and resets the drone
        if (sim.builder) sim.builder.enabled = false;
        ClearSightings();
        PlaceTarget();
        ComputeOpenCells();
        Plan = PlanAroundBuildings();
        Coverage = new ScanCoverage(openCells, groundY) { buildings = buildingCells.Count };
        Saved = Failed = Captured = SkippedAltitude = 0;
        SkippedPositions.Clear();
        revisitRound = 0;
        finishSent = false;
        State = Phase.Starting;
        DrawRoute();
        Emit(StateJson("Creating scan record…"));
        Send("api/scans", ScanRecordJson(), r =>
        {
            if (r.result != UnityWebRequest.Result.Success)
            {
                State = Phase.Error;
                Emit(StateJson("Cannot reach the image server (" + r.error + "). Start it with: python3 scan_server.py"));
                return;
            }
            ScanId = JsonUtility.FromJson<Created>(r.downloadHandler.text).scan_id;
            if (string.IsNullOrEmpty(ScanId)) { State = Phase.Error; Emit(StateJson("The server did not return a scan id.")); return; }
            BeginFlight();
        });
    }
    [System.Serializable] class Created { public string scan_id; }

    public void StopScan()
    {
        if (!Busy) return;
        State = Phase.Stopped;
        legs.Clear();
        Finish("stopped");
    }

    // ---------- region selection ----------

    // ---------- AI sightings in the city (after a scan) ----------

    readonly List<LineRenderer> beams = new List<LineRenderer>();
    readonly List<Vector2> beamSpots = new List<Vector2>();
    static readonly Color BeamRed = new Color(1f, 0.12f, 0.1f);
    const float BeamHeight = 60f;
    int focusedBeam = -1;

    [System.Serializable] class SightingMsg { public float x, z; public bool weak; }
    [System.Serializable] class SightingList { public SightingMsg[] items; }

    /// JSON {"items":[{"x":..,"z":..,"weak":false}, ...]} in the page's order (card 1 = items[0]): a red beam rising
    /// straight up from each estimated ground position, plus a ring on the ground. Replaces any earlier beams.
    public void ShowSightings(string json)
    {
        ClearSightings();
        SetResultsView(true);
        var list = JsonUtility.FromJson<SightingList>(json);
        if (list?.items == null) return;
        foreach (var s in list.items)
        {
            var p = new Vector2(s.x, s.z);
            float a = s.weak ? 0.45f : 1f;
            var beam = NewLine("Sighting beam", s.weak ? 0.12f : 0.22f);
            beam.positionCount = 2;
            beam.SetPosition(0, new Vector3(p.x, groundY, p.y));
            beam.SetPosition(1, new Vector3(p.x, groundY + BeamHeight, p.y));
            beam.startColor = new Color(BeamRed.r, BeamRed.g, BeamRed.b, a);
            beam.endColor = new Color(BeamRed.r, BeamRed.g, BeamRed.b, a * 0.25f);   // solid at the spot, fainter up high
            beams.Add(beam);
            var ring = NewLine("Sighting ring", 0.08f);
            ring.loop = true;
            ring.positionCount = 24;
            for (int i = 0; i < 24; i++)
            {
                float t = i * Mathf.PI * 2f / 24;
                ring.SetPosition(i, new Vector3(p.x + Mathf.Cos(t) * 0.6f, groundY + 0.12f, p.y + Mathf.Sin(t) * 0.6f));
            }
            ring.startColor = ring.endColor = new Color(BeamRed.r, BeamRed.g, BeamRed.b, a);
            beams.Add(ring);
            beamSpots.Add(p);
        }
    }

    /// "index": flies the camera to that sighting (0-based, page order) and makes its beam stand out.
    public void FocusSighting(string index)
    {
        if (!int.TryParse(index, out int k) || k < 0 || k >= beamSpots.Count) return;
        focusedBeam = k;
        if (sim.flyCamera)
        {
            sim.flyCamera.follow = null;
            sim.flyCamera.Frame(new Vector3(beamSpots[k].x, groundY, beamSpots[k].y), new Vector3(8f, 0f, 8f));
        }
    }

    /// Keeps the beams a few pixels wide at any distance (the whole-city view is far away), and reports a click on
    /// one to the page ("sighting_click"). True if the click was used.
    bool UpdateBeams()
    {
        if (beamSpots.Count == 0) return false;
        var cam = sim.flyCamera ? sim.flyCamera.GetComponent<Camera>() : Camera.main;
        if (!cam) return false;
        float pxToWorld = 2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, Screen.height);
        for (int i = 0; i < beamSpots.Count; i++)
        {
            var basePos = new Vector3(beamSpots[i].x, groundY, beamSpots[i].y);
            float d = Vector3.Distance(cam.transform.position, basePos);
            float px = i == focusedBeam ? 9f : 5f;
            beams[2 * i].widthMultiplier = Mathf.Max(0.2f, d * pxToWorld * px);
        }
        var mouse = Mouse.current;
        if (mouse == null || !mouse.leftButton.wasPressedThisFrame || mouse.rightButton.isPressed) return false;
        var m = mouse.position.ReadValue();
        int best = -1; float bestD = 14f;                    // pixels
        for (int i = 0; i < beamSpots.Count; i++)
        {
            var a = cam.WorldToScreenPoint(new Vector3(beamSpots[i].x, groundY, beamSpots[i].y));
            var b = cam.WorldToScreenPoint(new Vector3(beamSpots[i].x, groundY + BeamHeight, beamSpots[i].y));
            if (a.z <= 0f && b.z <= 0f) continue;            // behind the camera
            float dist = DistToSegment(m, a, b);
            if (dist < bestD) { bestD = dist; best = i; }
        }
        if (best < 0) return false;
        focusedBeam = best;
        Emit("{\"type\":\"sighting_click\",\"index\":" + best + "}");
        return true;
    }

    static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = ab.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
        return Vector2.Distance(p, a + ab * t);
    }

    /// Whole-city view (after a scan, so every red beam is on screen).
    public void ShowWholeCity()
    {
        if (!sim.flyCamera) return;
        sim.flyCamera.follow = null;
        var cam = sim.flyCamera.GetComponent<Camera>();
        if (cam) cam.farClipPlane = Mathf.Max(cam.farClipPlane, 2500f);   // the city is ~450 m across
        // The street grid, not CityColliders.Area: that includes the backdrop's huge ground plane.
        var city = CityLife.StreetArea;
        sim.flyCamera.Frame(new Vector3(city.center.x, groundY, city.center.y), new Vector3(city.width, 0f, city.height) * 0.6f);
        if (fogWas == null) { fogWas = RenderSettings.fog; RenderSettings.fog = false; }   // distance fog washes out the overview
    }

    bool? fogWas;

    public void ClearSightings()
    {
        focusedBeam = -1;
        if (fogWas != null) { RenderSettings.fog = fogWas.Value; fogWas = null; }
        foreach (var b in beams) if (b) Kill(b.gameObject);
        beams.Clear();
        beamSpots.Clear();
        SetResultsView(false);
    }

    bool resultsView;
    SonarVisualizer.View sonarView;

    /// Results view: only the region and the red beams. Hides the lane lines, revisit posts, the drone's trail and the
    /// sonar's debug drawing; all of it comes back for the next scan or region.
    void SetResultsView(bool on)
    {
        if (on == resultsView || sim == null) return;
        resultsView = on;
        route.enabled = !on;
        foreach (var p in posts) if (p) p.enabled = !on;
        var trail = sim.drone.GetComponent<TrailRenderer>();
        if (trail) trail.enabled = !on;
        var vis = sim.drone.GetComponent<SonarVisualizer>();
        if (vis)
        {
            if (on) { sonarView = vis.view; vis.view = SonarVisualizer.View.Off; }
            else vis.view = sonarView;
            vis.hud = !on;
        }
    }

    void ClearRegion()
    {
        ClearSightings();
        corners.Clear();
        region = null;
        outline.positionCount = 0;
        wall.sharedMesh.Clear();
        route.positionCount = 0;
        foreach (var p in posts) Kill(p.gameObject);
        posts.Clear();
    }

    /// Adds a corner at a ground cell (also used by tests). A fifth click starts a new region.
    public void AddCorner(Vector2Int cell)
    {
        if (Busy) return;
        if (corners.Count >= 4) ClearRegion();
        if (corners.Contains(cell)) { Emit(RegionJson("That cell is already a corner.")); return; }
        corners.Add(cell);
        State = Phase.Selecting;
        UpdateRegion();
    }

    void UpdateRegion()
    {
        string error = null;
        region = null;
        if (corners.Count == 4)
        {
            var pts = new List<Vector2>();
            foreach (var c in corners) pts.Add(c);
            region = ScanPlanner.OrderAndValidate(pts, config, out error);
            State = region != null ? Phase.Ready : Phase.Selecting;
        }
        // outline: ordered polygon when complete, otherwise the clicks in order
        var poly = new List<Vector2>();
        if (region != null) poly.AddRange(region); else foreach (var c in corners) poly.Add(c);
        outline.loop = corners.Count == 4;
        outline.positionCount = poly.Count;
        for (int i = 0; i < poly.Count; i++) outline.SetPosition(i, new Vector3(poly[i].x, groundY + 0.2f, poly[i].y));
        var color = region != null ? new Color(0.2f, 1f, 0.45f) : corners.Count == 4 ? new Color(1f, 0.3f, 0.3f) : new Color(1f, 0.85f, 0.2f);
        SetColor(outline, color);
        BuildWall(poly, outline.loop, color);
        foreach (var p in posts) Kill(p.gameObject);
        posts.Clear();
        foreach (var c in corners) Post(new Vector2(c.x, c.y), 5f, color);
        if (region != null)
        {
            ComputeOpenCells();
            if (openCells.Count == 0) { region = null; State = Phase.Selecting; error = "The region is all buildings; buildings are not searched."; }
        }
        if (region != null) { Plan = PlanAroundBuildings(); DrawRoute(); }
        else route.positionCount = 0;
        Emit(RegionJson(error));
    }

    /// One vertical quad per outline edge, from the ground (opaque) to HeightFade.Height (transparent).
    void BuildWall(List<Vector2> poly, bool closed, Color c)
    {
        var mesh = wall.sharedMesh;
        mesh.Clear();
        int edges = closed ? poly.Count : poly.Count - 1;
        if (edges <= 0) return;
        float y0 = groundY, y1 = groundY + HeightFade.Height;
        var v = new List<Vector3>(); var col = new List<Color>(); var tri = new List<int>();
        for (int i = 0; i < edges; i++)
        {
            var a = poly[i]; var b = poly[(i + 1) % poly.Count];
            int k = v.Count;
            v.Add(new Vector3(a.x, y0, a.y)); v.Add(new Vector3(b.x, y0, b.y));
            v.Add(new Vector3(b.x, y1, b.y)); v.Add(new Vector3(a.x, y1, a.y));
            col.Add(HeightFade.At(c, y0)); col.Add(HeightFade.At(c, y0)); col.Add(HeightFade.At(c, y1)); col.Add(HeightFade.At(c, y1));
            tri.AddRange(new[] { k, k + 1, k + 2, k, k + 2, k + 3 });
        }
        mesh.SetVertices(v);
        mesh.SetColors(col);
        mesh.SetTriangles(tri, 0);
        mesh.RecalculateBounds();
    }

    // ---------- buildings: not searched ----------

    readonly HashSet<Vector2Int> buildingCells = new HashSet<Vector2Int>();
    readonly List<Vector2Int> openCells = new List<Vector2Int>();
    readonly HashSet<Vector2Int> openSet = new HashSet<Vector2Int>();

    /// Splits the region's 1 m cells into building cells (skipped) and open ground (searched).
    void ComputeOpenCells()
    {
        buildingCells.Clear(); openCells.Clear(); openSet.Clear();
        foreach (var c in ScanPlanner.Cells(region))
        {
            if (IsBuilding(c)) buildingCells.Add(c);
            else { openCells.Add(c); openSet.Add(c); }
        }
    }

    /// Under a building: the first city part straight above is a large, tall solid (CityColliders), not a tree,
    /// lamp post or other street furniture (those are small; the ground under them is still searched).
    bool IsBuilding(Vector2Int c)
    {
        if (!Physics.Raycast(new Vector3(c.x, 300f, c.y), Vector3.down, out var hit, 400f, 1 << CityColliders.Layer, QueryTriggerInteraction.Ignore))
            return false;
        if (CityLife.IsMover(hit.collider.transform)) return false;
        var b = hit.collider.bounds;
        return hit.point.y > groundY + 3f && b.size.x >= 5f && b.size.z >= 5f;
    }

    /// The lawnmower plan without photos over buildings: a planned photo is kept only if its footprint contains open
    /// ground, and lanes are split where they cross a building (the drone just transits there).
    ScanPlanner.Plan PlanAroundBuildings()
    {
        var plan = ScanPlanner.Build(region, config);
        if (buildingCells.Count == 0) return plan;
        var lanes = new List<ScanPlanner.Lane>();
        foreach (var lane in plan.lanes)
        {
            List<Vector2> seg = null;
            foreach (var cap in lane.captures)
            {
                if (TouchesOpenGround(cap, plan.footprint)) { (seg ??= new List<Vector2>()).Add(cap); continue; }
                if (seg != null) { lanes.Add(Segment(seg)); seg = null; }
            }
            if (seg != null) lanes.Add(Segment(seg));
        }
        plan.lanes = lanes;
        return plan;
    }

    static ScanPlanner.Lane Segment(List<Vector2> caps) =>
        new ScanPlanner.Lane { captures = caps, a = caps[0], b = caps[caps.Count - 1] };

    bool TouchesOpenGround(Vector2 p, Vector2 fp)
    {
        for (int x = Mathf.CeilToInt(p.x - fp.x * 0.5f); x <= Mathf.FloorToInt(p.x + fp.x * 0.5f); x++)
            for (int z = Mathf.CeilToInt(p.y - fp.y * 0.5f); z <= Mathf.FloorToInt(p.y + fp.y * 0.5f); z++)
                if (openSet.Contains(new Vector2Int(x, z))) return true;
        return false;
    }

    void Post(Vector2 p, float h, Color c)
    {
        var lr = NewLine("Post", 0.25f);
        lr.positionCount = 2;
        lr.SetPosition(0, new Vector3(p.x, groundY, p.y));
        lr.SetPosition(1, new Vector3(p.x, groundY + h, p.y));
        SetColor(lr, c);
        posts.Add(lr);
    }

    void DrawRoute()
    {
        var pts = new List<Vector3>();
        float y = groundY + config.altitude;
        foreach (var l in Plan.lanes) { pts.Add(new Vector3(l.a.x, y, l.a.y)); pts.Add(new Vector3(l.b.x, y, l.b.y)); }
        route.positionCount = pts.Count;
        route.SetPositions(pts.ToArray());
        SetColor(route, new Color(0.3f, 0.8f, 1f));
    }

    void Update()
    {
        if (manualTick) return;
        if (!UpdateBeams()) HandleClicks();
        // Small steps, so fast-forward (Time.timeScale 5) keeps captures on their planned positions.
        float dt = Time.deltaTime;
        int steps = Mathf.Clamp(Mathf.CeilToInt(dt / 0.02f), 1, 20);
        for (int i = 0; i < steps; i++) Tick(dt / steps);
    }

    void HandleClicks()
    {
        if (State != Phase.Selecting && State != Phase.Ready) return;
        if (sim.builder && sim.builder.enabled) return;     // a sonar build tool is active
        var mouse = Mouse.current;
        if (mouse == null || !mouse.leftButton.wasPressedThisFrame || mouse.rightButton.isPressed) return;
        var cam = sim.flyCamera ? sim.flyCamera.GetComponent<Camera>() : Camera.main;
        if (!cam) return;
        var ray = cam.ScreenPointToRay(mouse.position.ReadValue());
        Vector3 p;
        if (Physics.Raycast(ray, out var hit, 3000f, ~(1 << 2), QueryTriggerInteraction.Ignore) && !CityLife.IsMover(hit.collider.transform))
            p = hit.point;
        else if (new Plane(Vector3.up, new Vector3(0f, groundY, 0f)).Raycast(ray, out float t)) p = ray.GetPoint(t);
        else return;
        AddCorner(new Vector2Int(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.z)));
    }

    // ---------- the hidden object ----------

    void PlaceTarget()
    {
        if (placedTarget) Kill(placedTarget);
        var props = sim.map.props;
        int idx = props && props.catalog ? props.catalog.IndexOf(Target) : -1;
        var cells = openCells;
        if (idx < 0 || cells.Count == 0) { Debug.LogWarning("[Scan] cannot place " + Target); return; }
        for (int tries = 0; tries < 200; tries++)
        {
            var c = cells[Random.Range(0, cells.Count)];
            var p = new Vector2(c.x + Random.Range(-0.4f, 0.4f), c.y + Random.Range(-0.4f, 0.4f));
            if (!ScanPlanner.Contains(region, p) || !OpenSky(p, out float ground)) continue;
            var prop = props.Place(idx, new Vector3(p.x, ground, p.y), Random.Range(0f, 360f));
            if (prop == null) return;
            placedTarget = prop.gameObject;
            lastTargetPos = prop.transform.position;
            return;
        }
        Debug.LogWarning("[Scan] no open ground for the object in this region");
    }

    /// True if nothing static stands over this ground point (moving traffic is ignored); returns the ground height.
    bool OpenSky(Vector2 p, out float ground)
    {
        ground = groundY;
        var hits = Physics.SphereCastAll(new Vector3(p.x, 300f, p.y), 0.4f, Vector3.down, 400f, ~(1 << 2), QueryTriggerInteraction.Ignore);
        float top = float.MinValue;
        foreach (var h in hits)
        {
            if (CityLife.IsMover(h.collider.transform)) continue;
            if (h.distance == 0f) return false;      // started inside something
            top = Mathf.Max(top, h.point.y);
        }
        if (top > groundY + 0.3f) return false;
        if (top > float.MinValue) ground = Mathf.Max(groundY, top);
        ground = Mathf.Max(ground, VisibleSurface(p));
        return true;
    }

    /// Height of the drawn ground at p. Sidewalks, curbs and lawns are thin city meshes without colliders
    /// (CityColliders skips them; the ground plane stands in for them), so an object placed at the plane's height
    /// would be buried inside a sidewalk (8 cm higher). Briefly gives the flat meshes under p a collider and raycasts.
    float VisibleSurface(Vector2 p)
    {
        float top = float.MinValue;
        var backdrop = GameObject.Find(CityColliders.BackdropName);
        if (backdrop == null) return top;
        var origin = new Vector3(p.x, 50f, p.y);
        foreach (var r in backdrop.GetComponentsInChildren<MeshRenderer>())
        {
            var b = r.bounds;
            if (b.size.y >= 0.3f || p.x < b.min.x || p.x > b.max.x || p.y < b.min.z || p.y > b.max.z) continue;
            var mf = r.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null || r.GetComponent<Collider>() != null) continue;
            var mc = r.gameObject.AddComponent<MeshCollider>();
            mc.sharedMesh = mf.sharedMesh;
            Physics.SyncTransforms();
            if (mc.Raycast(new Ray(origin, Vector3.down), out var hit, 100f)) top = Mathf.Max(top, hit.point.y);
            DestroyImmediate(mc);   // gone before anything else raycasts this frame
        }
        return top;
    }

    // ---------- flight ----------

    void BeginFlight()
    {
        legs.Clear();
        var p = drone.position;
        pos2 = new Vector2(p.x, p.z);
        foreach (var lane in Plan.lanes)
        {
            legs.Add(new Leg { to = lane.a, endSpeed = turnSpeed });
            legs.Add(new Leg { to = lane.b, endSpeed = turnSpeed, lane = Plan.lanes.IndexOf(lane) });
        }
        if (legs.Count > 0) legs[legs.Count - 1].endSpeed = 0f;
        legIdx = 0; laneIdx = -1; nextCapture = 0; speed = 0f; hover = 0f; phaseTime = 0f;
        droneCam = drone.GetComponent<DroneCamera>();
        if (!droneCam) droneCam = drone.gameObject.AddComponent<DroneCamera>();
        droneCam.groundY = groundY;
        droneCam.EnsureCamera();
        droneCam.Cam.enabled = false;            // rendered on demand by CaptureJpg
        if (Mathf.Abs(droneCam.fieldOfView - config.verticalFovDeg) > 0.01f) droneCam.Cam.fieldOfView = config.verticalFovDeg;
        if (sim.flyCamera) { sim.flyCamera.followOffset = new Vector3(0f, 17f, -12f); sim.flyCamera.follow = drone; }
        missionStart = clock;
        missionEnd = -1f;
        State = Phase.Scanning;
        Emit(StateJson("Scanning."));
    }

    /// Advances the mission by dt seconds (Update calls it; tests call it directly).
    public void Tick(float dt)
    {
        clock += dt;
        PumpControl();
        PumpUploads();
        if (State != Phase.Scanning && State != Phase.Revisiting) return;
        phaseTime += dt;

        // Altitude: hold the scan height, climb over anything static under or just ahead of the drone.
        var p = drone.position;
        var ahead = legIdx < legs.Count ? (legs[legIdx].to - pos2) : Vector2.zero;
        if (ahead.sqrMagnitude > 1e-6f) ahead = ahead.normalized * (speed * 0.5f + 0.6f);   // about 0.5 s ahead
        float mount = droneCam ? -droneCam.mountOffset.y : 0f;   // the camera, not the drone body, sits at the scan altitude
        float wantY = Mathf.Max(groundY + config.altitude + mount, Mathf.Max(TopAt(pos2), TopAt(pos2 + ahead)) + clearance);
        float y = Mathf.MoveTowards(p.y, wantY, climbRate * dt);
        bool climbing = wantY - y > 1f;

        if (legIdx >= legs.Count)
        {
            drone.position = new Vector3(pos2.x, y, pos2.y);
            AfterLegs(dt);
            return;
        }
        var leg = legs[legIdx];
        if (leg.lane >= 0 && laneIdx != leg.lane) { laneIdx = leg.lane; nextCapture = 0; }

        // Speed: accelerate to cruise, brake so the leg ends at its end speed (slower for turns), stop to climb.
        float d = Vector2.Distance(pos2, leg.to);
        float vMax = Mathf.Min(cruiseSpeed, Mathf.Sqrt(leg.endSpeed * leg.endSpeed + 2f * acceleration * d));
        if (climbing) vMax = 0f;
        speed = Mathf.MoveTowards(speed, vMax, acceleration * dt);
        float step = speed * dt;
        bool arrived = step >= d;
        pos2 = arrived ? leg.to : Vector2.MoveTowards(pos2, leg.to, step);
        drone.position = new Vector3(pos2.x, y, pos2.y);

        // Distance-based capture along a lane, from the actual position.
        if (leg.lane >= 0)
        {
            var lane = Plan.lanes[leg.lane];
            float u = Vector2.Dot(pos2 - lane.a, lane.Dir);
            while (nextCapture < lane.captures.Count &&
                   (arrived || u >= Vector2.Dot(lane.captures[nextCapture] - lane.a, lane.Dir) - 0.02f))
            {
                Capture(lane.captures[nextCapture], "lane", leg.lane);
                nextCapture++;
            }
        }
        if (arrived)
        {
            if (leg.revisit)
            {
                // Hover, then look; if the view is still blocked, wait for traffic and look again.
                speed = 0f;
                hover += dt;
                bool settled = Mathf.Abs(wantY - y) < 0.1f;          // finished descending after an obstacle
                if (hover < 0.3f || (!settled && hover < 5f)) { pos2 = leg.to; return; }
                Capture(leg.to, "revisit", -1);
                hover = 0f;
            }
            speed = Mathf.Min(speed, leg.endSpeed);
            legIdx++;
            if (leg.lane >= 0) Emit(ProgressJson());
        }
    }

    float TopAt(Vector2 p)
    {
        float top = groundY;
        // 0.6 m: the drone's half-width plus a margin (it flies low, between lamp posts and trees where it can).
        foreach (var h in Physics.SphereCastAll(new Vector3(p.x, 300f, p.y), 0.6f, Vector3.down, 400f, ~(1 << 2), QueryTriggerInteraction.Ignore))
            if (!CityLife.IsMover(h.collider.transform) && h.distance > 0f) top = Mathf.Max(top, h.point.y);
        return top;
    }

    /// After the planned legs: wait for uploads, then revisit what still needs a look, or finish.
    void AfterLegs(float dt)
    {
        if (PendingUploads > 0) return;
        hover += dt;
        if (State == Phase.Revisiting && hover < revisitWait) return;
        hover = 0f;

        var need = new List<ScanCoverage.Cell>();
        foreach (var c in Coverage.cells.Values) if (Coverage.NeedsRevisit(c, maxLooks)) need.Add(c);
        if (need.Count == 0)
        {
            State = Phase.Done;
            Finish(Complete() ? "complete" : "incomplete");
            return;
        }

        // Group cells into revisit points: each point's image covers the cells within 80% of the footprint.
        var fp = ScanPlanner.Footprint(config.altitude, config.verticalFovDeg, config.aspect) * 0.8f;
        var points = new List<Vector2>();
        var left = new List<ScanCoverage.Cell>(need);
        var from = pos2;
        while (left.Count > 0)
        {
            int best = 0;
            for (int i = 1; i < left.Count; i++)
                if (Vector2.Distance(from, left[i].c) < Vector2.Distance(from, left[best].c)) best = i;
            Vector2 seed = left[best].c, min = seed, max = seed;
            var group = left.FindAll(c => ScanPlanner.InFootprint(seed, fp, c.c));
            foreach (var c in group) { min = Vector2.Min(min, c.c); max = Vector2.Max(max, c.c); }
            var center = (min + max) * 0.5f;
            points.Add(ClearSpot(center, group));
            left.RemoveAll(c => ScanPlanner.InFootprint(seed, fp, c.c));
            from = center;
        }
        foreach (var c in need) c.looks++;
        legs.Clear();
        foreach (var pt in points) legs.Add(new Leg { to = pt, endSpeed = 0f, revisit = true });
        legIdx = 0; laneIdx = -1;
        revisitRound++;
        foreach (var pt in points) Post(pt, 3f, new Color(1f, 0.3f, 0.9f));
        State = Phase.Revisiting;
        Emit(StateJson($"Revisiting {need.Count} cells at {points.Count} spots (round {revisitRound})."));
    }

    /// A hover point whose photo still covers all of `group`, preferring one the drone can hold below the capture
    /// ceiling: next to a lamp post or tree rather than over it (over it, it would climb above 4 m and not take the photo).
    Vector2 ClearSpot(Vector2 center, List<ScanCoverage.Cell> group)
    {
        float ceiling = groundY + maxCaptureAltitude - clearance;    // highest obstacle top the drone can stay low over
        if (TopAt(center) <= ceiling) return center;
        var fp = ScanPlanner.Footprint(config.altitude, config.verticalFovDeg, config.aspect) - Vector2.one * 0.6f;
        Vector2 best = center; float bestTop = TopAt(center), bestDist = 0f;
        for (float dx = -1.4f; dx <= 1.41f; dx += 0.35f)
            for (float dz = -1.4f; dz <= 1.41f; dz += 0.35f)
            {
                var c = center + new Vector2(dx, dz);
                if (!group.TrueForAll(g => ScanPlanner.InFootprint(c, fp, g.c))) continue;
                float top = TopAt(c), d = dx * dx + dz * dz;
                if (top < bestTop - 0.05f || (Mathf.Abs(top - bestTop) <= 0.05f && d < bestDist)) { best = c; bestTop = top; bestDist = d; }
            }
        return best;
    }

    bool Complete()
    {
        foreach (var c in Coverage.cells.Values)
            if (c.state != ScanCoverage.State.Seen && c.state != ScanCoverage.State.HiddenStatic) return false;
        return true;
    }

    // ---------- capture and upload ----------

    void Capture(Vector2 planned, string reason, int lane)
    {
        var camT = droneCam.Cam.transform;
        camT.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);   // gimbal: straight down, image top = +Z
        var camPos = camT.position;
        float altitude = camPos.y - groundY;
        if (altitude < minCaptureAltitude - 0.05f || altitude > maxCaptureAltitude + 0.05f)
        {
            // Not a scanning photo: the spot stays unseen and gets a revisit (where the drone may fly lower).
            SkippedAltitude++;
            SkippedPositions.Add(new Vector2(camPos.x, camPos.z));
            return;
        }
        var fp = ScanPlanner.Footprint(altitude, droneCam.Cam.fieldOfView, droneCam.width / (float)droneCam.height);
        byte[] jpg;
        try { jpg = droneCam.CaptureJpg(jpegQuality); }
        catch (System.Exception e) { Failed++; Debug.LogException(e); Emit(StateJson("Camera capture failed: " + e.Message)); return; }
        var obs = Coverage.Observe(camPos, fp);
        int seq = ++Captured;

        bool revisitNeeded = false;
        var confirmed = new StringBuilder("[");
        var suspected = new StringBuilder("[");
        foreach (var (c, s) in obs.cells)
        {
            if (s == ScanCoverage.State.OccludedMoving) { Cell(confirmed, c); revisitNeeded = true; }
            else if (s == ScanCoverage.State.Suspected) { Cell(suspected, c); if (Coverage.cells[c].road) revisitNeeded = true; }
        }
        confirmed.Append(']'); suspected.Append(']');
        var drone3 = drone.position;
        var sb = new StringBuilder("{");
        sb.Append("\"sequence_number\":").Append(seq);
        sb.Append(",\"timestamp\":").Append(Q(System.DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)));
        sb.Append(",\"sim_time\":").Append(F(clock));
        sb.Append(",\"capture_reason\":").Append(Q(reason));
        sb.Append(",\"lane_index\":").Append(lane);
        sb.Append(",\"revisit_round\":").Append(revisitRound);
        sb.Append(",\"drone_position\":").Append(V(drone3));
        sb.Append(",\"camera_position\":").Append(V(camPos));
        sb.Append(",\"camera_orientation\":{\"euler_deg\":").Append(V(camT.eulerAngles))
          .Append(",\"forward\":").Append(V(camT.forward)).Append(",\"image_up\":").Append(V(camT.up)).Append('}');
        sb.Append(",\"planned_waypoint\":[").Append(F(planned.x)).Append(',').Append(F(planned.y)).Append(']');
        sb.Append(",\"actual_speed\":").Append(F(speed));
        sb.Append(",\"ground_footprint\":{\"center\":[").Append(F(camPos.x)).Append(',').Append(F(camPos.z))
          .Append("],\"size\":[").Append(F(fp.x)).Append(',').Append(F(fp.y)).Append("],\"altitude\":").Append(F(altitude))
          .Append(",\"corners\":[");
        for (int i = 0; i < 4; i++)
        {
            float sx = (i == 1 || i == 2) ? 0.5f : -0.5f, sz = i >= 2 ? 0.5f : -0.5f;
            sb.Append(i > 0 ? "," : "").Append('[').Append(F(camPos.x + sx * fp.x)).Append(',').Append(F(camPos.z + sz * fp.y)).Append(']');
        }
        sb.Append("]}");
        sb.Append(",\"occlusion_status\":").Append(Q(obs.Status));
        sb.Append(",\"occluded_regions\":{\"confirmed_cells\":").Append(confirmed).Append(",\"suspected_cells\":").Append(suspected)
          .Append(",\"hidden_static_cells\":").Append(obs.hidden).Append('}');
        sb.Append(",\"revisit_required\":").Append(revisitNeeded ? "true" : "false");
        sb.Append(",\"camera\":{\"vertical_fov_deg\":").Append(F(droneCam.Cam.fieldOfView))
          .Append(",\"width\":").Append(droneCam.width).Append(",\"height\":").Append(droneCam.height)
          .Append(",\"format\":\"jpg\",\"jpeg_quality\":").Append(jpegQuality)
          .Append(",\"mount\":\"under the drone, looking straight down; image top = world +Z (north)\"}");
        sb.Append(",\"image_b64\":").Append(Q(System.Convert.ToBase64String(jpg)));
        sb.Append('}');
        uploads.Enqueue(new Upload { json = sb.ToString(), seq = seq, obs = obs });
    }

    void PumpUploads()
    {
        if (req != null)
        {
            if (!req.isDone) return;
            bool ok = req.result == UnityWebRequest.Result.Success;
            string err = req.error;
            req.Dispose();
            req = null;
            if (ok)
            {
                Saved++;
                Coverage.Apply(current.obs, current.seq);   // only saved images count as coverage
                current = null;
                Emit(ProgressJson());
            }
            else if (++current.tries < 2) { current.notBefore = clock + 0.5f; uploads.Enqueue(current); current = null; }
            else
            {
                Failed++;
                Emit(ProgressJson("Image " + current.seq + " was not saved: " + err));
                current = null;
            }
        }
        if (current == null && uploads.Count > 0 && ScanId != null)
        {
            var next = uploads.Peek();
            if (next.notBefore > clock) return;
            current = uploads.Dequeue();
            req = Post("api/scans/" + ScanId + "/images", current.json);
        }
    }

    void Finish(string status)
    {
        if (finishSent) return;
        finishSent = true;
        if (missionStart >= 0f) missionEnd = clock;
        ShowWholeCity();
        Emit(DoneJson(status));
        if (ScanId == null) return;
        // Wait for pending uploads in PumpControl before sending the summary.
        pendingFinish = status;
    }
    string pendingFinish;

    void PumpControl()
    {
        if (control != null)
        {
            if (!control.isDone) return;
            var r = control; control = null;
            controlDone?.Invoke(r);
            r.Dispose();
        }
        if (pendingFinish != null && PendingUploads == 0)
        {
            var status = pendingFinish;
            pendingFinish = null;
            Send("api/scans/" + ScanId + "/finish", FinishJson(status), r =>
            {
                if (r.result != UnityWebRequest.Result.Success) Emit(StateJson("Could not save the scan summary: " + r.error));
            });
            if (placedTarget)    // evaluation only: kept apart from the images and the manifest the model reads
                Send("api/scans/" + ScanId + "/ground_truth",
                     "{\"object\":" + Q(Target) + ",\"position\":" + V(lastTargetPos) + "}", null);
        }
        if (control == null && controlQueue.Count > 0)
        {
            var (path, json, done) = controlQueue.Dequeue();
            control = Post(path, json);
            controlDone = done;
        }
    }

    readonly Queue<(string, string, System.Action<UnityWebRequest>)> controlQueue = new Queue<(string, string, System.Action<UnityWebRequest>)>();
    void Send(string path, string json, System.Action<UnityWebRequest> done) => controlQueue.Enqueue((path, json, done));
    public bool ControlIdle => control == null && controlQueue.Count == 0 && pendingFinish == null;

    UnityWebRequest Post(string path, string json)
    {
        var r = new UnityWebRequest(serverUrl + path, "POST")
        {
            uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)),
            downloadHandler = new DownloadHandlerBuffer(),
            timeout = 20,
        };
        r.SetRequestHeader("Content-Type", "application/json");
        r.SendWebRequest();
        return r;
    }

    // ---------- JSON to the page and the server ----------

    void Emit(string json) => ScanEmit(json);

    string StateJson(string message) =>
        "{\"type\":\"state\",\"state\":" + Q(State.ToString().ToLowerInvariant()) + ",\"message\":" + Q(message) + ",\"object\":" + Q(Target) + "}";

    string RegionJson(string error)
    {
        var sb = new StringBuilder("{\"type\":\"region\",\"state\":").Append(Q(State.ToString().ToLowerInvariant()));
        sb.Append(",\"corners\":").Append(corners.Count).Append(",\"valid\":").Append(region != null ? "true" : "false");
        sb.Append(",\"error\":").Append(error == null ? "null" : Q(error));
        if (region != null && Plan != null)
        {
            sb.Append(",\"area_m2\":").Append(F(ScanPlanner.Area(region)));
            sb.Append(",\"buildings_m2\":").Append(buildingCells.Count);
            sb.Append(",\"lanes\":").Append(Plan.lanes.Count).Append(",\"planned_images\":").Append(Plan.CaptureCount);
            sb.Append(",\"footprint\":[").Append(F(Plan.footprint.x)).Append(',').Append(F(Plan.footprint.y)).Append(']');
        }
        return sb.Append('}').ToString();
    }

    string ProgressJson(string warning = null)
    {
        var sb = new StringBuilder("{\"type\":\"progress\",\"state\":").Append(Q(State.ToString().ToLowerInvariant()));
        sb.Append(",\"scan_id\":").Append(Q(ScanId));
        sb.Append(",\"lane\":").Append(Mathf.Max(0, laneIdx + 1)).Append(",\"lanes\":").Append(Plan != null ? Plan.lanes.Count : 0);
        sb.Append(",\"captured\":").Append(Captured).Append(",\"saved\":").Append(Saved).Append(",\"failed\":").Append(Failed);
        sb.Append(",\"skipped_altitude\":").Append(SkippedAltitude);
        sb.Append(",\"mission_s\":").Append(F(MissionSeconds));
        sb.Append(",\"pending\":").Append(PendingUploads).Append(",\"revisit_round\":").Append(revisitRound);
        sb.Append(",\"coverage\":").Append(CountsJson());
        if (warning != null) sb.Append(",\"warning\":").Append(Q(warning));
        return sb.Append('}').ToString();
    }

    string DoneJson(string status) =>
        "{\"type\":\"done\",\"status\":" + Q(status) + ",\"scan_id\":" + Q(ScanId) + ",\"saved\":" + Saved + ",\"failed\":" + Failed +
        ",\"mission_s\":" + F(MissionSeconds) + ",\"coverage\":" + CountsJson() + "}";

    string CountsJson()
    {
        if (Coverage == null) return "null";
        var sb = new StringBuilder("{");
        bool first = true;
        foreach (var kv in Coverage.Counts()) { sb.Append(first ? "" : ",").Append(Q(kv.Key)).Append(':').Append(kv.Value); first = false; }
        return sb.Append('}').ToString();
    }

    string ScanRecordJson()
    {
        var sb = new StringBuilder("{");
        sb.Append("\"target_category\":").Append(Q(Target.ToLowerInvariant()));
        sb.Append(",\"region\":{\"corners\":[");
        for (int i = 0; i < region.Length; i++) sb.Append(i > 0 ? "," : "").Append('[').Append(F(region[i].x)).Append(',').Append(F(region[i].y)).Append(']');
        sb.Append("],\"area_m2\":").Append(F(ScanPlanner.Area(region))).Append(",\"cells\":").Append(Coverage.cells.Count)
          .Append(",\"ordering\":\"counter-clockwise from the corner with the smallest x+z\"}");
        sb.Append(",\"config\":{\"altitude_m\":").Append(F(config.altitude)).Append(",\"overlap\":").Append(F(config.overlap))
          .Append(",\"vertical_fov_deg\":").Append(F(config.verticalFovDeg)).Append(",\"aspect\":").Append(F(config.aspect))
          .Append(",\"cruise_speed_mps\":").Append(F(cruiseSpeed)).Append(",\"turn_speed_mps\":").Append(F(turnSpeed))
          .Append(",\"max_looks\":").Append(maxLooks)
          .Append(",\"capture_altitude_m\":[").Append(F(minCaptureAltitude)).Append(',').Append(F(maxCaptureAltitude)).Append(']').Append(",\"ground_y\":").Append(F(groundY))
          .Append(",\"assumptions\":\"flat ground at ground_y; camera points straight down; drone yaw fixed (image top = +Z)\"}");
        sb.Append(",\"route\":{\"footprint_m\":[").Append(F(Plan.footprint.x)).Append(',').Append(F(Plan.footprint.y))
          .Append("],\"lane_spacing_m\":").Append(F(Plan.laneSpacing)).Append(",\"capture_spacing_max_m\":").Append(F(Plan.captureSpacing))
          .Append(",\"lanes_along\":").Append(Q(Plan.lanesAlongX ? "x" : "z")).Append(",\"lanes\":[");
        for (int i = 0; i < Plan.lanes.Count; i++)
        {
            var l = Plan.lanes[i];
            sb.Append(i > 0 ? "," : "").Append("{\"a\":[").Append(F(l.a.x)).Append(',').Append(F(l.a.y)).Append("],\"b\":[")
              .Append(F(l.b.x)).Append(',').Append(F(l.b.y)).Append("],\"captures\":").Append(l.captures.Count).Append('}');
        }
        sb.Append("]}");
        sb.Append(",\"coordinate_frame\":\"Unity world meters: x east, y up, z north; ground plane y = ground_y; [x, z] pairs are ground positions\"");
        return sb.Append('}').ToString();
    }

    string FinishJson(string status)
    {
        var sb = new StringBuilder("{\"status\":").Append(Q(status));
        sb.Append(",\"captured\":").Append(Captured).Append(",\"saved\":").Append(Saved).Append(",\"failed\":").Append(Failed);
        sb.Append(",\"skipped_altitude\":").Append(SkippedAltitude);
        sb.Append(",\"revisit_rounds\":").Append(revisitRound);
        sb.Append(",\"mission_seconds\":").Append(F(MissionSeconds));
        sb.Append(",\"coverage\":").Append(CountsJson());
        // Cells that were not seen clearly, by reason (so the AI side knows where the search was not conclusive).
        var groups = new Dictionary<string, StringBuilder>
        {
            ["unseen"] = new StringBuilder(), ["occluded_moving"] = new StringBuilder(),
            ["suspected"] = new StringBuilder(), ["hidden_static"] = new StringBuilder(),
        };
        if (Coverage != null)
            foreach (var c in Coverage.cells.Values)
            {
                string k = c.state switch
                {
                    ScanCoverage.State.Unseen => "unseen", ScanCoverage.State.OccludedMoving => "occluded_moving",
                    ScanCoverage.State.Suspected => "suspected", ScanCoverage.State.HiddenStatic => "hidden_static", _ => null,
                };
                if (k == null) continue;
                var g = groups[k];
                Cell(g, c.c);
            }
        sb.Append(",\"not_clearly_seen\":{");
        bool first = true;
        foreach (var kv in groups) { sb.Append(first ? "" : ",").Append(Q(kv.Key)).Append(":[").Append(kv.Value).Append(']'); first = false; }
        return sb.Append("}}").ToString();
    }

    static void Cell(StringBuilder sb, Vector2Int c)
    {
        if (sb.Length > 0 && sb[sb.Length - 1] != '[') sb.Append(',');
        sb.Append('[').Append(c.x).Append(',').Append(c.y).Append(']');
    }

    static string F(float v) => float.IsFinite(v) ? v.ToString("0.###", CultureInfo.InvariantCulture) : "null";
    static string V(Vector3 v) => "[" + F(v.x) + "," + F(v.y) + "," + F(v.z) + "]";
    static string Q(string s)
    {
        if (s == null) return "null";
        var sb = new StringBuilder("\"");
        foreach (char ch in s)
        {
            if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
            else if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4"));
            else sb.Append(ch);
        }
        return sb.Append('"').ToString();
    }
}
