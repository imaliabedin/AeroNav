using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Drone2.Building;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering.Universal;

/// The website's Rescue Mission on top of the existing two-drone search (SearchCoordinator, SearchDrone, sonar,
/// SearchGrid). Adds what a mission needs and nothing else:
///   - a 20:00 countdown of simulated time, started by the page's Start button (not when the page loads)
///   - MISSION SUCCESSFUL when the drones finish and every reachable spot of the building has been seen
///     (SearchGrid: no Unseen spots left); MISSION FAILED when time runs out first. Exactly one, then it stops.
///   - the existing FireSpread throughout, and the existing BuildingCollapse at the deadline (failure only)
///   - speed 1x / 5x / 10x (Time.timeScale; the coordinator steps the drones in small steps)
///   - the AI: every `captureEvery` simulated seconds each drone takes a frame with its AI camera and sends it (one
///     request in flight per drone, so nothing queues up) to scan_server.py, which runs the ONNX person detector.
///     The AI camera sits at the drone's camera mount and copies the camera the model was trained with
///     (Editor/Dataset/PersonDatasetGenerator: 640 x 640, 70 deg FOV, 30 deg down, a spot headlight dimmed near walls,
///     the scene's own lighting). For each detection the box center is cast as a ray from that frame's camera pose
///     into the scene; the first hit is the estimated 3-D position (and floor), sent back to the server.
/// Commands (SendMessage "RescueControls"): StartMission, SetSpeed("1"|"5"|"10"). State goes to the page as JSON
/// through RescueBridge.jslib (window "rescue" events). In the Editor it attaches only if RESCUE_MISSION=1.
public class RescueMission : MonoBehaviour
{
    public enum Phase { Ready, Running, Successful, Failed }

    public float missionSeconds = 20f * 60f;
    public float captureEvery = 1f;         // simulated seconds between AI frames of one drone...
    [Tooltip("...and at least this many real seconds: the model takes ~0.15 s per frame and each AI frame renders the " +
             "scene up to 5 times, so frames are paced in real time and fast-forward doesn't slow the simulation down.")]
    public float minRealGap = 0.3f;   // two drones x ~0.15 s per frame: the person model's throughput
    [Tooltip("The AI camera turns this far further for each frame (a sweeping gimbal): 0, 90, 180, 270 deg from " +
             "the drone's heading, so a room is seen from every side, not just the direction of travel.")]
    public float sweepStep = 90f;
    public int captureSize = 640;           // the model's input size
    [Header("AI camera (as in the model's training images)")]
    public float aiFov = 70f, aiPitch = 30f;
    public float headlightIntensity = 2.25f, headlightRange = 12f, headlightAngle = 80f;
    [Tooltip("Fire spread for the mission (FireSpread's own default is 2 s between steps).")]
    public float fireInterval = 4f;
    public string serverUrl;

    public Phase State { get; private set; } = Phase.Ready;
    public float MissionTime { get; private set; }
    public float Remaining => Mathf.Max(0f, missionSeconds - MissionTime);
    public string MissionId { get; private set; }

    SearchCoordinator coord;
    FireSpread fire;
    int initialFires;
    Camera aiCam;
    Light headlight;
    RenderTexture aiRT;
    Texture2D aiTex;
    BuildingCollapse collapse;
    float speed = 1f, emitTimer;
    readonly Dictionary<int, float> nextCapture = new(), nextRealCapture = new();
    readonly HashSet<int> inFlight = new();
    readonly Dictionary<int, int> sweep = new();
    int framesSent, framesFailed, framesSkipped, framesUnfit, detections, sequence;
    [Tooltip("No AI frame when a surface is closer than this straight ahead, or a fire closer than fireClearance: " +
             "the model's training images always had a clear view (camera 0.25-0.3 m from walls and furniture).")]
    public float minClearView = 0.8f, fireClearance = 1.5f;
    [Tooltip("Estimated physical size (m) a detection must have to count as a person.")]
    public float minPersonSize = 0.25f, maxPersonSize = 2.2f;
    string lastError;
    bool creating;

#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] static extern void RescueEmit(string json);
#else
    static void RescueEmit(string json) { }
#endif

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Attach()
    {
        bool web = Application.platform == RuntimePlatform.WebGLPlayer;
        bool forced = System.Environment.GetEnvironmentVariable("RESCUE_MISSION") == "1";
        var c = Object.FindAnyObjectByType<SearchCoordinator>();
        if ((!web && !forced) || c == null) return;
        var m = new GameObject("RescueControls").AddComponent<RescueMission>();
        m.coord = c;
        c.autoStart = false;          // the mission starts when the user presses Start
        c.allowKeys = false;          // no rebuilding or pausing the building mid-mission
    }

    void Awake()
    {
        if (string.IsNullOrEmpty(serverUrl))
        {
            var page = Application.absoluteURL;
            serverUrl = Application.platform == RuntimePlatform.WebGLPlayer && !string.IsNullOrEmpty(page)
                ? page.Substring(0, page.LastIndexOf('/') + 1)
                : System.Environment.GetEnvironmentVariable("SCAN_SERVER") ?? "http://127.0.0.1:8000/";
        }
        if (!serverUrl.EndsWith("/")) serverUrl += "/";
    }

    void Start()
    {
        if (coord == null) coord = Object.FindAnyObjectByType<SearchCoordinator>();
        Time.timeScale = speed;
        // At 10x a frame covers ~0.3 s of mission time; allow up to 1 s so a slower frame rate doesn't stretch the
        // mission clock (the coordinator still moves the drones in 0.05 s steps).
        Time.maximumDeltaTime = 1f;
    }

    /// Editor tests: use this coordinator without the web page.
    public void InitForTest(SearchCoordinator c) => coord = c;

    // ---------- commands from the page ----------

    public void StartMission()
    {
        if (State != Phase.Ready || creating || coord == null || coord.Grid == null) return;
        creating = true;
        StartCoroutine(Post("api/rescue", MissionRecordJson(), r =>
        {
            creating = false;
            if (r.result == UnityWebRequest.Result.Success)
                MissionId = JsonUtility.FromJson<Created>(r.downloadHandler.text).scan_id;
            else lastError = "AI server not reachable (" + r.error + "): the mission runs, but frames are not analyzed.";
            fire = new FireSpread(coord.generator, coord, new FireSpread.Settings { interval = fireInterval }, coord.generator.seed + 7);
            initialFires = coord.Fires.Count;
            MissionTime = 0f;
            State = Phase.Running;
            coord.Begin();
            if (MissionId != null) StartCoroutine(Post("api/rescue/" + MissionId + "/ground_truth", GroundTruthJson(), null));
            Emit();
        }));
    }

    /// "off": no fire spread this mission (the building's starting fires still burn). Only before the start.
    public void SetFireSpread(string value)
    {
        if (State == Phase.Ready && value == "off") fireInterval = 1e9f;
    }

    public void SetSpeed(string value)
    {
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float k)) return;
        speed = Mathf.Clamp(k, 1f, 10f);
        Time.timeScale = speed;
        Emit();
    }

    [System.Serializable] class Created { public string scan_id; }

    // ---------- mission ----------

    void Update()
    {
        Step(Time.deltaTime);
        if ((emitTimer -= Time.unscaledDeltaTime) <= 0f) { emitTimer = 0.25f; Emit(); }
    }

    /// Editor tests: start without the website or the AI server.
    public void BeginOffline()
    {
        fire = new FireSpread(coord.generator, coord, new FireSpread.Settings { interval = fireInterval }, coord.generator.seed + 7);
        initialFires = coord.Fires.Count;
        MissionTime = 0f;
        State = Phase.Running;
        coord.Begin();
    }
    public bool Collapsing => collapse != null;
    public int CutOff => CutOffByFire();

    /// Advances the mission by dt of mission time (Update in play; tests call it directly).
    public void Step(float dt)
    {
        if (State == Phase.Running)
        {
            MissionTime += dt;
            fire?.Step(dt);
            if (coord.Complete && SearchVerified() && CutOffByFire() == 0)
            {
                State = Phase.Successful;              // checked before the deadline: both can't happen
                coord.Halt();
            }
            else if (MissionTime >= missionSeconds)
            {
                MissionTime = missionSeconds;
                State = Phase.Failed;
                coord.Halt();
                collapse = new BuildingCollapse(coord.generator, fire, coord.drones, coord.generator.seed + 11);
            }
            else CaptureFrames();
            if (State != Phase.Running) Emit();
        }
        collapse?.Step(dt);
    }

    /// The search is complete only when the drones are done and the grid has no unseen open spot left
    /// (spots closed off from the drones are counted separately and reported).
    bool SearchVerified() => coord.Grid != null && coord.Grid.Count(SearchGrid.Spot.Unseen) == 0;

    /// Spots the drones gave up on (Unreachable) because a fire that started during the mission blocks them: not
    /// searched, so the mission cannot succeed. Spots closed off by walls, or by the fires already burning when the
    /// mission started, are inaccessible from the outset and are only reported.
    int CutOffByFire()
    {
        var g = coord.Grid;
        if (g == null || coord.Fires.Count <= initialFires) return 0;
        const float near = 2f;
        int n = 0;
        for (int f = 0; f < g.floors; f++)
            for (int x = 0; x < g.nx; x++)
                for (int z = 0; z < g.nz; z++)
                {
                    if (g.state[f, x, z] != SearchGrid.Spot.Unreachable) continue;
                    var w = g.World(f, x, z);
                    for (int i = initialFires; i < coord.Fires.Count; i++)
                    {
                        var fi = coord.Fires[i];
                        if (fi.floor != f) continue;
                        float dx = fi.position.x - w.x, dz = fi.position.z - w.z;
                        if (dx * dx + dz * dz <= near * near) { n++; break; }
                    }
                }
        return n;
    }
    int cutOffCache;

    // ---------- AI frames ----------

    void CaptureFrames()
    {
        if (MissionId == null) return;
        foreach (var d in coord.drones)
        {
            if (d == null || d.cam == null) continue;
            if (!nextCapture.TryGetValue(d.id, out float due)) due = 0f;
            if (!nextRealCapture.TryGetValue(d.id, out float realDue)) realDue = 0f;
            if (MissionTime < due) continue;
            if (Time.unscaledTime < realDue) { framesSkipped++; nextCapture[d.id] = MissionTime + captureEvery; continue; }
            nextCapture[d.id] = MissionTime + captureEvery;
            nextRealCapture[d.id] = Time.unscaledTime + minRealGap;
            if (inFlight.Contains(d.id)) { framesSkipped++; continue; }   // the model is still busy with this drone's last frame
            bool clear = ClearView(d);
            float yaw = SweepYaw(d);
            sweep[d.id] = (sweep.TryGetValue(d.id, out int k) ? k + 1 : 1) % Mathf.Max(1, Mathf.RoundToInt(360f / sweepStep));
            if (!clear) { framesUnfit++; continue; }                       // not counted as "no person": just not analyzed
            StartCoroutine(SendFrame(d, yaw));
        }
    }

    /// Frame quality gate (the conditions of the model's training images): nothing right in front of the lens and no
    /// fire filling the view.
    float SweepYaw(SearchDrone d)
    {
        sweep.TryGetValue(d.id, out int k);
        return d.cam.transform.eulerAngles.y + k * sweepStep;
    }

    bool ClearView(SearchDrone d)
    {
        var pos = d.cam.transform.position;
        var fwd = Quaternion.Euler(aiPitch, SweepYaw(d), 0f) * Vector3.forward;
        if (Physics.Raycast(pos, fwd, minClearView, SearchCoordinator.WorldMask, QueryTriggerInteraction.Ignore)) return false;
        if (Physics.CheckSphere(pos, 0.3f, SearchCoordinator.WorldMask, QueryTriggerInteraction.Ignore)) return false;
        foreach (var f in coord.Fires)
        {
            var dv = f.position - pos; dv.y = 0f;
            if (f.floor == d.Floor && dv.magnitude < fireClearance) return false;
        }
        return true;
    }

    struct Pose { public Vector3 pos; public Quaternion rot; public float fov; public int floor; public float time; }

    IEnumerator SendFrame(SearchDrone d, float yaw)
    {
        inFlight.Add(d.id);
        var camPos = d.cam.transform.position;
        byte[] jpg = CaptureFrom(camPos, yaw, out var camRot);
        var pose = new Pose { pos = camPos, rot = camRot, fov = aiFov, floor = d.Floor, time = MissionTime };
        int seq = ++sequence;
        var sb = new StringBuilder("{");
        sb.Append("\"sequence_number\":").Append(seq);
        sb.Append(",\"timestamp\":").Append(Q(System.DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)));
        sb.Append(",\"sim_time\":").Append(F(pose.time));
        sb.Append(",\"drone_id\":").Append(d.id + 1).Append(",\"floor\":").Append(pose.floor + 1);
        sb.Append(",\"drone_position\":").Append(V(d.transform.position)).Append(",\"camera_position\":").Append(V(pose.pos));
        sb.Append(",\"camera_orientation\":{\"forward\":").Append(V(pose.rot * Vector3.forward)).Append(",\"image_up\":").Append(V(pose.rot * Vector3.up)).Append('}');
        sb.Append(",\"camera\":{\"vertical_fov_deg\":").Append(F(pose.fov)).Append(",\"width\":").Append(captureSize)
          .Append(",\"height\":").Append(captureSize).Append(",\"format\":\"jpg\",\"pitch_deg\":").Append(F(aiPitch))
          .Append(",\"mount\":\"AI camera at the drone camera mount, drone heading, tilted down, with headlight (matches the model's training camera)\"}");
        sb.Append(",\"image_b64\":").Append(Q(System.Convert.ToBase64String(jpg))).Append('}');
        string reply = null;
        yield return Post("api/rescue/" + MissionId + "/frames", sb.ToString(), r =>
        {
            if (r.result == UnityWebRequest.Result.Success) { framesSent++; reply = r.downloadHandler.text; }
            else { framesFailed++; lastError = "Frame not analyzed: " + r.error; }
        });
        inFlight.Remove(d.id);
        if (reply == null) yield break;
        var res = JsonUtility.FromJson<FrameResult>(reply);
        if (res == null || res.detections == null || res.detections.Length == 0) yield break;
        detections += res.detections.Length;
        // Locate each detection: a ray from this frame's camera pose through the box center, into the scene.
        var loc = new StringBuilder("{\"image_id\":").Append(Q(res.image_id)).Append(",\"positions\":[");
        for (int i = 0; i < res.detections.Length; i++)
        {
            var b = res.detections[i].bbox;
            float u = b.x + b.w * 0.5f, v = b.y + b.h * 0.5f;
            float t = Mathf.Tan(pose.fov * 0.5f * Mathf.Deg2Rad);   // square frame: same horizontally
            var dir = pose.rot * new Vector3((2f * u - 1f) * t, (1f - 2f * v) * t, 1f).normalized;
            loc.Append(i > 0 ? "," : "").Append("{\"detection_id\":").Append(Q(res.detections[i].detection_id));
            if (Physics.Raycast(pose.pos, dir, out var hit, 40f, SearchCoordinator.WorldMask, QueryTriggerInteraction.Ignore))
            {
                bool person = hit.collider.GetComponentInParent<TrappedPerson>() != null;
                // Physical size of the box at that distance: a person (standing, sitting or lying) is 0.25-2.2 m.
                // Bigger boxes are doorways, corridors and stairs the model mistakes for people.
                float span = 2f * hit.distance * t;
                float sw = b.w * span, sh = b.h * span, big = Mathf.Max(sw, sh);
                bool plausible = big <= maxPersonSize && big >= minPersonSize;
                loc.Append(",\"available\":true,\"x\":").Append(F(hit.point.x)).Append(",\"y\":").Append(F(hit.point.y)).Append(",\"z\":").Append(F(hit.point.z))
                   .Append(",\"floor\":").Append(coord.Grid.StoreyOf(hit.point.y + 0.1f) + 1).Append(",\"distance_m\":").Append(F(hit.distance))
                   .Append(",\"ray_hit\":").Append(Q(person ? "person collider" : "scene geometry"))
                   .Append(",\"size_m\":[").Append(F(sw)).Append(',').Append(F(sh)).Append("],\"plausible\":").Append(plausible ? "true" : "false")
                   .Append(",\"method\":\"ray from the frame's camera pose through the box center to the first scene surface\"");
            }
            else loc.Append(",\"available\":false,\"reason\":\"ray hit nothing within 40 m\"");
            loc.Append('}');
        }
        loc.Append("]}");
        yield return Post("api/rescue/" + MissionId + "/localize", loc.ToString(), null);
    }

    [System.Serializable] class Box { public float x, y, w, h; }
    [System.Serializable] class Det { public string detection_id; public float confidence; public Box bbox; }
    [System.Serializable] class FrameResult { public string image_id; public string status; public Det[] detections; }

    /// The drone camera's real view, square at the model's input size, without the sonar/drone overlay layers.
    void EnsureAiCamera()
    {
        if (aiCam != null) return;
        var go = new GameObject("AI Camera");
        aiCam = go.AddComponent<Camera>();
        aiCam.enabled = false;                                   // rendered on demand
        aiCam.fieldOfView = aiFov;
        aiCam.aspect = 1f;
        aiCam.nearClipPlane = 0.05f;
        aiCam.farClipPlane = 200f;
        aiCam.cullingMask = SearchCoordinator.WorldMask;         // no drones, sonar overlays or schematic
        var data = aiCam.GetUniversalAdditionalCameraData();
        data.renderPostProcessing = true;
        data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
        data.antialiasingQuality = AntialiasingQuality.High;
        var lightGo = new GameObject("AI Headlight");
        lightGo.transform.SetParent(go.transform, false);
        headlight = lightGo.AddComponent<Light>();
        headlight.type = LightType.Spot;
        headlight.color = new Color(1f, 0.96f, 0.88f);
        headlight.range = headlightRange;
        headlight.spotAngle = headlightAngle;
        headlight.innerSpotAngle = headlightAngle * 0.5f;
        headlight.shadows = LightShadows.None;
        headlight.enabled = false;                               // on only while the AI frame renders
        aiRT = new RenderTexture(captureSize, captureSize, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 1 };
        aiCam.targetTexture = aiRT;
        aiTex = new Texture2D(captureSize, captureSize, TextureFormat.RGB24, false, false);
    }

    /// One AI frame from the drone's camera mount: heading of the drone camera, 30 deg down, headlight on and
    /// dimmed when a surface is close (the training images' auto-exposure stand-in), then halved while the frame
    /// is blown out. Returns the JPEG and the exact pose used (for locating detections).
    /// An AI frame from any pose (also used by the editor test RescueTests).
    public byte[] CaptureFrom(Vector3 pos, float yaw, out Quaternion rot)
    {
        EnsureAiCamera();
        rot = Quaternion.Euler(aiPitch, yaw, 0f);
        aiCam.transform.SetPositionAndRotation(pos, rot);
        const float fullAt = 3f;
        float nearest = fullAt;
        var t = aiCam.transform;
        foreach (var dir in new[] { t.forward, t.forward + 0.35f * t.right, t.forward - 0.35f * t.right, t.forward + 0.35f * t.up, t.forward - 0.35f * t.up })
            if (Physics.Raycast(t.position, dir.normalized, out var hit, fullAt, SearchCoordinator.WorldMask, QueryTriggerInteraction.Ignore))
                nearest = Mathf.Min(nearest, hit.distance);
        float k = nearest / fullAt;
        headlight.intensity = headlightIntensity * Mathf.Clamp(k * k, 0.08f, 1f);
        headlight.enabled = true;
        try
        {
            coord.WithSceneLook(RenderAi);
            for (int retake = 0; retake < 4 && BlownOut() > 0.04f; retake++)
            {
                headlight.intensity *= 0.5f;
                coord.WithSceneLook(RenderAi);
            }
        }
        finally { headlight.enabled = false; }
        return aiTex.EncodeToJPG(95);
    }

    void RenderAi()
    {
        aiCam.Render();
        var prev = RenderTexture.active;
        RenderTexture.active = aiRT;
        aiTex.ReadPixels(new Rect(0, 0, captureSize, captureSize), 0, 0, false);
        aiTex.Apply(false);
        RenderTexture.active = prev;
    }

    float BlownOut()
    {
        var px = aiTex.GetPixelData<byte>(0);
        int blown = 0, n = px.Length / 3;
        for (int i = 0; i < px.Length; i += 3)
            if (px[i] > 235 && px[i + 1] > 235 && px[i + 2] > 235) blown++;
        return (float)blown / n;
    }

    void OnDestroy()
    {
        if (aiRT != null) { aiRT.Release(); Destroy(aiRT); }
        if (aiTex != null) Destroy(aiTex);
    }

    // ---------- server ----------

    IEnumerator Post(string path, string json, System.Action<UnityWebRequest> done)
    {
        using var r = new UnityWebRequest(serverUrl + path, "POST")
        {
            uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)),
            downloadHandler = new DownloadHandlerBuffer(),
            timeout = 30,
        };
        r.SetRequestHeader("Content-Type", "application/json");
        yield return r.SendWebRequest();
        done?.Invoke(r);
    }

    string MissionRecordJson() =>
        "{\"target_category\":\"person\",\"region\":{\"building\":\"Building2 four-storey burning building\",\"floors\":" + coord.generator.floors +
        "},\"config\":{\"mission_seconds\":" + F(missionSeconds) + ",\"capture_every_s\":" + F(captureEvery) + ",\"drones\":" + coord.drones.Count +
        "},\"coordinate_frame\":\"Unity world meters (x east, y up, z north); floors numbered from 1\"}";

    /// Evaluation only (stored apart from the frames and never shown as a result): where the people really are.
    string GroundTruthJson()
    {
        var sb = new StringBuilder("{\"people\":[");
        for (int i = 0; i < coord.generator.people.Count; i++)
        {
            var p = coord.generator.people[i];
            if (p == null) continue;
            var c = p.Bounds.center;
            sb.Append(i > 0 ? "," : "").Append("{\"x\":").Append(F(c.x)).Append(",\"y\":").Append(F(c.y)).Append(",\"z\":").Append(F(c.z))
              .Append(",\"floor\":").Append(coord.Grid.StoreyOf(p.Bounds.min.y + 0.1f) + 1).Append('}');
        }
        return sb.Append("]}").ToString();
    }

    // ---------- page ----------

    void Emit()
    {
        if (coord == null || coord.Grid == null) return;
        var g = coord.Grid;
        int seen = g.Count(SearchGrid.Spot.Seen), unseen = g.Count(SearchGrid.Spot.Unseen), closed = g.Count(SearchGrid.Spot.Unreachable);
        var sb = new StringBuilder("{\"type\":\"rescue\"");
        sb.Append(",\"state\":").Append(Q(State.ToString().ToLowerInvariant()));
        sb.Append(",\"remaining_s\":").Append(F(Remaining)).Append(",\"mission_s\":").Append(F(missionSeconds));
        sb.Append(",\"speed\":").Append(F(speed)).Append(",\"mission_id\":").Append(Q(MissionId));
        sb.Append(",\"coverage\":").Append(F(coord.Coverage())).Append(",\"seen\":").Append(seen).Append(",\"unseen\":").Append(unseen).Append(",\"closed_off\":").Append(closed);
        sb.Append(",\"search_complete\":").Append(coord.Complete ? "true" : "false");
        if (coord.Complete || State != Phase.Running) cutOffCache = CutOffByFire();
        sb.Append(",\"cut_off_by_fire\":").Append(cutOffCache);
        sb.Append(",\"fire_share\":").Append(F(fire != null ? fire.Share : 0f)).Append(",\"collapsing\":").Append(collapse != null ? "true" : "false");
        sb.Append(",\"frames\":{\"sent\":").Append(framesSent).Append(",\"failed\":").Append(framesFailed).Append(",\"skipped_busy\":").Append(framesSkipped).Append(",\"skipped_view\":").Append(framesUnfit)
          .Append(",\"detections\":").Append(detections).Append('}');
        sb.Append(",\"error\":").Append(Q(lastError));
        sb.Append(",\"drones\":[");
        for (int i = 0; i < coord.drones.Count; i++)
        {
            var d = coord.drones[i];
            sb.Append(i > 0 ? "," : "").Append("{\"id\":").Append(d.id + 1).Append(",\"floor\":").Append(d.Floor + 1).Append(",\"status\":").Append(Q(d.Status.ToString())).Append('}');
        }
        sb.Append("]}");
        RescueEmit(sb.ToString());
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
