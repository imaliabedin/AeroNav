using System;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;

public enum TargetCategory { Key, Phone }

/// Normalized box in image coordinates: origin top-left, x/w across, y/h down, all 0..1.
[Serializable]
public class DetectionBox { public float x, y, w, h; }

/// One answer from the vision server (VisionServer/server.py) for one camera frame.
[Serializable]
public class DetectionResult
{
    public bool detected;
    public string category;
    public float confidence;
    public bool hasBox;
    public DetectionBox bbox;
    public string engine;
    public float latencyMs;   // server-side inference time
    public string error;
    // Set only when the frame was sent with a session and the server stores frames.
    public string imageId;
    public bool duplicate;      // frame matched an earlier one; imageId is that image's ID
    public string sessionId;
    public string capturedAt;
    public string receivedAt;
    public string storageError;
    [NonSerialized] public float roundTripMs;
}

/// Where and when a frame was captured; sent with it so the server can store it.
public class FrameMeta
{
    public string sessionId;
    public Vector3 position;     // Unity world coordinates (Y up, meters)
    public float altitude;       // camera height above the ground
    public DateTime capturedAtUtc;
}

/// Layer 1 perception: while the drone flies, grabs frames from its <see cref="DroneCamera"/>,
/// asks the vision server whether the selected target is in the image, and halts the drone
/// when it is found with enough confidence. Navigation itself stays in <see cref="Drone"/>.
public class TargetDetector : MonoBehaviour
{
    public enum DetectionStatus { Idle, Searching, TargetFound, NotFound, ServerOffline }

    public TargetCategory target = TargetCategory.Key;
    [Range(0f, 1f)] public float confidenceThreshold = 0.3f;
    [Tooltip("Positive frames in a row needed before reporting TARGET FOUND.")]
    public int framesToConfirm = 1;
    public string serverUrl = "http://127.0.0.1:8765";
    [Tooltip("Seconds between captures while the drone is running.")]
    public float captureInterval = 0.4f;
    public float requestTimeout = 5f;
    public bool haltDroneOnFound = true;
    public bool showCameraView = true;
    [Tooltip("Ask the vision server to save each frame (with session, position and time) via image_store.")]
    public bool storeFrames = true;

    public DroneCamera droneCamera;
    public Drone drone;

    public DetectionStatus Status { get; private set; } = DetectionStatus.Idle;
    public DetectionResult LastResult { get; private set; }
    public DetectionResult FoundResult { get; private set; }
    public int FramesAnalyzed { get; private set; }
    /// New for each drone launch, e.g. run-20261010-153012-a3f9c1.
    public string SessionId { get; private set; }
    public event Action<DetectionResult> TargetFound;

    bool inFlight;
    float nextCapture;
    int hitStreak;
    string lastStorageError;
    SimulationManager.SimMode lastMode = SimulationManager.SimMode.Build;

    void Start()
    {
        if (!drone) drone = GetComponent<Drone>();
        if (!droneCamera) droneCamera = GetComponent<DroneCamera>();
    }

    void Update()
    {
        var sim = SimulationManager.Instance;
        if (sim == null) return;

        var kb = Keyboard.current;
        if (kb != null && kb.tKey.wasPressedThisFrame && sim.Mode == SimulationManager.SimMode.Build)
            target = target == TargetCategory.Key ? TargetCategory.Phone : TargetCategory.Key;

        if (sim.Mode != lastMode)
        {
            if (sim.Mode == SimulationManager.SimMode.Running || sim.Mode == SimulationManager.SimMode.Build) ResetSearch();
            if (sim.Mode == SimulationManager.SimMode.Running) SessionId = NewSessionId();
            // The route ended without a confident detection.
            if ((sim.Mode == SimulationManager.SimMode.Won || sim.Mode == SimulationManager.SimMode.GaveUp)
                && Status != DetectionStatus.TargetFound)
                Status = DetectionStatus.NotFound;
            lastMode = sim.Mode;
        }

        if (sim.Mode == SimulationManager.SimMode.Running && Status == DetectionStatus.Idle) Status = DetectionStatus.Searching;

        bool searching = sim.Mode == SimulationManager.SimMode.Running && !sim.Paused
                         && (Status == DetectionStatus.Searching || Status == DetectionStatus.ServerOffline);
        if (searching && !inFlight && Time.time >= nextCapture && droneCamera)
        {
            nextCapture = Time.time + captureInterval;
            StartCoroutine(CaptureAndDetect());
        }
    }

    void ResetSearch()
    {
        Status = DetectionStatus.Idle;
        LastResult = null;
        FoundResult = null;
        FramesAnalyzed = 0;
        hitStreak = 0;
    }

    static string NewSessionId() => $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}".Substring(0, 26);

    IEnumerator CaptureAndDetect()
    {
        inFlight = true;
        var target = this.target;
        byte[] jpg = droneCamera.CaptureJpg();
        var meta = storeFrames && !string.IsNullOrEmpty(SessionId)
            ? new FrameMeta { sessionId = SessionId, position = transform.position, altitude = droneCamera.Altitude, capturedAtUtc = DateTime.UtcNow }
            : null;
        float t0 = Time.realtimeSinceStartup;
        using (var req = BuildRequest(serverUrl, target, confidenceThreshold, jpg, requestTimeout, meta))
        {
            yield return req.SendWebRequest();
            var result = ParseResponse(req, target);
            result.roundTripMs = (Time.realtimeSinceStartup - t0) * 1000f;
            inFlight = false;

            // Discard answers for an old target, or that arrived after the run ended.
            var sim = SimulationManager.Instance;
            if (target != this.target || sim == null || sim.Mode != SimulationManager.SimMode.Running) yield break;
            Handle(result);
        }
    }

    void Handle(DetectionResult result)
    {
        LastResult = result;
        if (!string.IsNullOrEmpty(result.error))
        {
            Status = DetectionStatus.ServerOffline;
            hitStreak = 0;
            return;
        }

        FramesAnalyzed++;
        Status = DetectionStatus.Searching;
        // Storage problems never affect detection; just report each new one once.
        if (!string.IsNullOrEmpty(result.storageError) && result.storageError != lastStorageError)
            Debug.LogWarning($"[Vision] frame not stored: {result.storageError}");
        lastStorageError = result.storageError;
        // Re-check the threshold here: the client's setting is the one that counts.
        bool positive = result.detected && result.confidence >= confidenceThreshold;
        hitStreak = positive ? hitStreak + 1 : 0;
        if (hitStreak < Mathf.Max(1, framesToConfirm)) return;

        Status = DetectionStatus.TargetFound;
        FoundResult = result;
        Debug.Log($"[Vision] TARGET FOUND: {result.category} conf {result.confidence:0.00} " +
                  $"box ({result.bbox.x:0.00},{result.bbox.y:0.00},{result.bbox.w:0.00},{result.bbox.h:0.00}) " +
                  $"at drone {transform.position}, frame {FramesAnalyzed}, session {SessionId}, " +
                  $"image {(string.IsNullOrEmpty(result.imageId) ? "(not stored)" : result.imageId)}");
        if (haltDroneOnFound && drone) drone.Halt();
        TargetFound?.Invoke(result);
    }

    // ---------- HTTP client (static so the editor capture test uses the exact same code) ----------

    public static string TargetName(TargetCategory t) => t == TargetCategory.Key ? "key" : "phone";

    public static UnityWebRequest BuildRequest(string serverUrl, TargetCategory target, float conf, byte[] jpg, float timeout,
                                               FrameMeta meta = null)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string url = $"{serverUrl.TrimEnd('/')}/detect?target={TargetName(target)}&conf={conf.ToString(inv)}";
        if (meta != null)
            url += $"&session={Uri.EscapeDataString(meta.sessionId)}" +
                   $"&x={meta.position.x.ToString("R", inv)}&y={meta.position.y.ToString("R", inv)}&z={meta.position.z.ToString("R", inv)}" +
                   $"&alt={meta.altitude.ToString("R", inv)}" +
                   $"&capturedAt={Uri.EscapeDataString(meta.capturedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'+00:00'", inv))}";
        var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
        {
            uploadHandler = new UploadHandlerRaw(jpg) { contentType = "image/jpeg" },
            downloadHandler = new DownloadHandlerBuffer(),
            timeout = Mathf.CeilToInt(timeout),
        };
        req.SetRequestHeader("Content-Type", "image/jpeg");
        return req;
    }

    public static DetectionResult ParseResponse(UnityWebRequest req, TargetCategory target)
    {
        if (req.result != UnityWebRequest.Result.Success)
            return new DetectionResult { category = TargetName(target), bbox = new DetectionBox(), error = $"server: {req.error}" };
        try
        {
            var r = JsonUtility.FromJson<DetectionResult>(req.downloadHandler.text);
            if (r.bbox == null) r.bbox = new DetectionBox();
            return r;
        }
        catch (Exception e)
        {
            return new DetectionResult { category = TargetName(target), bbox = new DetectionBox(), error = $"bad response: {e.Message}" };
        }
    }

    // ---------- HUD ----------

    /// One line for the main HUD.
    public string HudLine()
    {
        string status = Status switch
        {
            DetectionStatus.Searching => "<color=#FFD84A>SEARCHING</color>",
            DetectionStatus.TargetFound => "<color=#3CFF5A>TARGET FOUND</color>",
            DetectionStatus.NotFound => "<color=#FF5050>NOT FOUND</color>",
            DetectionStatus.ServerOffline => "<color=#FF5050>VISION SERVER OFFLINE</color>",
            _ => "idle",
        };
        string last = LastResult == null ? ""
            : !string.IsNullOrEmpty(LastResult.error) ? $"   {LastResult.error}"
            : $"   last: {LastResult.confidence:0.00}/{confidenceThreshold:0.00}  {LastResult.roundTripMs:0} ms";
        return $"vision: <b>{TargetName(target).ToUpper()}</b> (T)  {status}  frames {FramesAnalyzed}{last}";
    }

    /// Big banner text when found, else null.
    public string Banner() => Status == DetectionStatus.TargetFound
        ? $"<color=#3CFF5A>TARGET FOUND: {TargetName(target).ToUpper()} ({FoundResult.confidence:0.00})</color>"
        : null;

    GUIStyle label;

    void OnGUI()
    {
        if (!showCameraView || droneCamera == null || droneCamera.Target == null) return;
        if (label == null)
        {
            label = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 12 };
            label.normal.textColor = Color.white;
        }

        const float size = 260f;
        var view = new Rect(Screen.width - size - 10, 10, size, size);
        var prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.65f);
        GUI.DrawTexture(new Rect(view.x - 4, view.y - 4, view.width + 8, view.height + 26), Texture2D.whiteTexture);
        GUI.color = prev;
        GUI.DrawTexture(view, droneCamera.Target, ScaleMode.StretchToFill, false);

        // Box of the latest frame's best candidate: green if it passed the threshold, yellow otherwise.
        var r = Status == DetectionStatus.TargetFound ? FoundResult : LastResult;
        if (r != null && r.hasBox && string.IsNullOrEmpty(r.error))
        {
            bool pass = r.detected && r.confidence >= confidenceThreshold;
            if (pass || r.confidence >= confidenceThreshold * 0.5f)
                DrawBox(new Rect(view.x + r.bbox.x * size, view.y + r.bbox.y * size, r.bbox.w * size, r.bbox.h * size),
                        pass ? Color.green : Color.yellow);
        }

        GUI.Label(new Rect(view.x, view.yMax + 2, size, 20),
            $"<b>DRONE CAM</b> ↓  alt {droneCamera.Altitude:0.00} m   FOV {droneCamera.fieldOfView:0}°", label);
    }

    static void DrawBox(Rect r, Color c)
    {
        var prev = GUI.color;
        GUI.color = c;
        const float t = 2f;
        GUI.DrawTexture(new Rect(r.x - t, r.y - t, r.width + 2 * t, t), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.x - t, r.yMax, r.width + 2 * t, t), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.x - t, r.y, t, r.height), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.xMax, r.y, t, r.height), Texture2D.whiteTexture);
        GUI.color = prev;
    }
}

/// Adds the downward camera and detector to the scene's drone at play time, so no scene edit is
/// needed. Skipped if someone already added a TargetDetector in the scene.
static class VisionBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Attach()
    {
        var sim = SimulationManager.Instance;
        if (sim == null || sim.drone == null || UnityEngine.Object.FindAnyObjectByType<TargetDetector>() != null) return;

        var go = sim.drone.gameObject;
        var cam = go.GetComponent<DroneCamera>();
        if (cam == null) cam = go.AddComponent<DroneCamera>();
        var det = go.AddComponent<TargetDetector>();
        det.drone = sim.drone;
        det.droneCamera = cam;
        sim.detector = det;
    }
}
