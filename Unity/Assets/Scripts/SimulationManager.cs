using UnityEngine;
using UnityEngine.InputSystem;

/// Mode switching (build / running / won / gave up), global hotkeys and the debug HUD.
public class SimulationManager : MonoBehaviour
{
    public enum SimMode { Build, Running, Won, GaveUp }

    public static SimulationManager Instance { get; private set; }

    public Drone drone;
    public CellMap map;
    public MapBuilder builder;
    public FlyCamera flyCamera;
    /// Optional vision layer (added at play time by VisionBootstrap if not in the scene).
    public TargetDetector detector;
    /// Hide the text HUD (the website draws its own buttons); the result banner still shows.
    public static bool HideHud;

    public SimMode Mode { get; private set; } = SimMode.Build;
    public bool Paused { get; private set; }
    /// Show invisible walls as faint ghosts while building (H). They are never drawn during a run.
    public bool ShowInvisibleGhosts { get; private set; } = true;

    float runTime;
    static readonly float[] Speeds = { 1f, 2f, 4f, 8f, 16f, 32f, 64f };
    int speedIndex = 2;

    void Awake()
    {
        Instance = this;
        Application.runInBackground = true; // keep simulating when the window loses focus
    }

    void Start()
    {
        drone.cellsPerSecond = Speeds[speedIndex];
        EnterBuildMode();
    }

    // ---------- mode control (also callable from eval / other scripts) ----------

    public void EnterBuildMode()
    {
        Mode = SimMode.Build;
        Paused = false;
        runTime = 0f;
        drone.ResetTo(map.droneStart);
        map.ShowInvisible = ShowInvisibleGhosts;
        if (builder) builder.enabled = true;
    }

    public void StartSim()
    {
        drone.ResetTo(map.droneStart);
        Mode = SimMode.Running;
        Paused = false;
        runTime = 0f;
        map.ShowInvisible = false;
        if (builder) builder.enabled = false;
        drone.Run();
    }

    public void SetSpeed(float cellsPerSecond)
    {
        drone.cellsPerSecond = cellsPerSecond;
    }

    public void OnDroneWon() => Mode = SimMode.Won;
    public void OnDroneGaveUp() => Mode = SimMode.GaveUp;

    // ---------- input ----------

    void Update()
    {
        if (Mode == SimMode.Running && !Paused) runTime += Time.deltaTime;

        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)
            {
                if (Mode == SimMode.Build) StartSim();
                else EnterBuildMode();
            }
            if (kb.escapeKey.wasPressedThisFrame && Mode != SimMode.Build) EnterBuildMode();
            if (kb.rKey.wasPressedThisFrame) ResetCamera();
            if (kb.pKey.wasPressedThisFrame && Mode == SimMode.Running) Paused = !Paused;
            if (kb.equalsKey.wasPressedThisFrame || kb.numpadPlusKey.wasPressedThisFrame) ChangeSpeed(+1);
            if (kb.minusKey.wasPressedThisFrame || kb.numpadMinusKey.wasPressedThisFrame) ChangeSpeed(-1);
            if (kb.hKey.wasPressedThisFrame)
            {
                ShowInvisibleGhosts = !ShowInvisibleGhosts;
                if (Mode == SimMode.Build) map.ShowInvisible = ShowInvisibleGhosts;
            }
            if (kb.bKey.wasPressedThisFrame) drone.showPath = !drone.showPath;
            if (kb.fKey.wasPressedThisFrame && flyCamera) flyCamera.follow = flyCamera.follow ? null : drone.transform;
        }
    }

    void ChangeSpeed(int delta)
    {
        speedIndex = Mathf.Clamp(speedIndex + delta, 0, Speeds.Length - 1);
        drone.cellsPerSecond = Speeds[speedIndex];
    }

    /// Stops following and frames the whole map (plus the drone start).
    public void ResetCamera()
    {
        if (!flyCamera) return;
        Vector3Int min = map.droneStart, max = map.droneStart;
        foreach (var kv in map.Cells)
        {
            min = Vector3Int.Min(min, kv.Key);
            max = Vector3Int.Max(max, kv.Key);
        }
        var size = (Vector3)(max - min) + Vector3.one;
        flyCamera.follow = null;
        flyCamera.Frame((Vector3)(min + max) * 0.5f, size);
    }

    // ---------- HUD ----------
    // Uses a plain tinted rectangle + rich-text tags so it looks the same regardless of GUI skin.

    GUIStyle text;

    /// Screen area (IMGUI coords) covered by the HUD, so build clicks there are ignored.
    public Rect HudRect { get; private set; }

    void OnGUI()
    {
        if (text == null)
        {
            text = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperLeft, fontSize = 13, richText = true, wordWrap = false };
            text.normal.textColor = Color.white;
        }

        var plan = drone.Plan;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"<b>DRONE SIM</b>   mode: <b>{Mode}</b>{(Paused ? "  (PAUSED)" : "")}");
        sb.AppendLine($"speed: {drone.cellsPerSecond:0} cells/s   time: {runTime:0.0}s   invisible-wall ghosts: {(ShowInvisibleGhosts ? "on" : "off")}");
        sb.AppendLine($"drone: {drone.Status}   moves: {drone.Steps} / {drone.PathSteps}");
        sb.Append(plan == null
            ? "A*: not planned yet"
            : plan.found
                ? $"A*: path {drone.PathSteps} cells   expanded {plan.expanded} nodes   {plan.milliseconds:0.00} ms"
                : $"A*: no path ({plan.failReason})   expanded {plan.expanded} nodes");
        sb.AppendLine();
        if (detector) sb.AppendLine(detector.HudLine());
        sb.AppendLine();

        if (Mode == SimMode.Build)
        {
            int objects = map.props ? map.props.Count : 0;
            sb.AppendLine($"<b>BUILD</b>  tool: <b>{(builder ? builder.ToolName : "-")}</b>   cubes: {map.Count}   objects: {objects}");
            sb.AppendLine("LMB place · Ctrl+LMB remove · 1 Wall · 2 Win · 3 Drone start · 4 Toggle wall invisible");
            sb.AppendLine("Objects panel (left) · Z/X rotate object · Backspace clear · F5 save · F9 load");
            sb.AppendLine("<b><color=#FFD84A>Enter: launch drone</color></b>");
        }
        else
        {
            sb.AppendLine("Enter/Esc back to build · P pause · +/- speed");
        }
        sb.Append("Camera: hold RMB look · WASD move · Q/E down/up · Shift fast · R reset · F follow · H ghosts · B path");

        string hud = $"<color=#F0F0F0>{sb}</color>";
        var panel = new Rect(10, 10, 660, text.CalcHeight(new GUIContent(hud), 644f) + 14f);
        HudRect = HideHud ? Rect.zero : panel;
        if (!HideHud)
        {
            var prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.65f);
            GUI.DrawTexture(panel, Texture2D.whiteTexture);
            GUI.color = prev;
            GUI.Label(new Rect(panel.x + 8, panel.y + 6, panel.width - 16, panel.height - 12), hud, text);
        }

        string banner = detector && detector.Banner() != null ? detector.Banner()
                      : Mode == SimMode.Won ? "<color=#3CFF5A>GOAL REACHED</color>"
                      : Mode == SimMode.GaveUp ? $"<color=#FF5050>NO PATH ({plan?.failReason})</color>"
                      : null;
        if (banner != null)
        {
            var style = new GUIStyle(text) { alignment = TextAnchor.MiddleCenter };
            GUI.Label(new Rect(0, Screen.height * 0.4f, Screen.width, 80), $"<size=44><b>{banner}</b></size>", style);
        }
    }
}
