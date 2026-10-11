using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

/// Ported from the parking-lot project (Unity/Assets/Scripts/SonarVisualizer.cs): the same rings, echoes,
/// returning echoes, rays and belief boxes. Here they are built as a line mesh on `layer`, so only cameras that
/// render that layer show them (each drone's own camera), and the stats panel is left to the search HUD.
/// Everything drawn comes from the sensor's measurements, except the expanding rings, which only illustrate
/// the pulse: sound is slowed to `visualSpeed` so it can be seen. V cycles the view.
public class SonarVisualizer : MonoBehaviour
{
    public enum View { Off, Echoes, EchoesAndRays }

    public SonarSensor sensor;
    public View view = View.Echoes;
    [Tooltip("Speed of the drawn wavefront (m/s). Real sound (343 m/s) would cross 6 m in one frame.")]
    public float visualSpeed = 12f;
    /// The drone's belief map, drawn as red boxes (may be null).
    public SonarMap belief;
    /// Which belief cells to draw (null = all). Keeps the view to the cells near the drone's flight height.
    public Func<Vector3Int, bool> showBeliefCell;
    /// Layer of the line mesh; only cameras rendering this layer show the sonar.
    public int layer;
    [Tooltip("Pulses kept on screen. Indoors nearly every sample echoes, so fewer than the original's 16 keeps it readable.")]
    public int pulsesShown = 5;
    [Tooltip("Also draw the two vertical rings. Off for an on-board camera: it sits at their centre and sees them as lines.")]
    public bool verticalRings = false;
    [Tooltip("Half-size of an echo cross (m).")]
    public float echoSize = 0.035f;

    static readonly Color Wave = new Color(0.3f, 0.85f, 1f, 0.55f);
    static readonly Color ObstacleEcho = new Color(1f, 0.55f, 0.1f, 1f);
    static readonly Color GroundEcho = new Color(0.65f, 0.5f, 0.35f, 0.8f);
    static readonly Color NoReturn = new Color(0.8f, 0.8f, 0.8f, 0.12f);
    static readonly Color Returning = new Color(1f, 1f, 0.4f, 1f);
    static readonly Color BeliefBox = new Color(1f, 0.15f, 0.15f, 0.55f);

    readonly List<SonarPulse> pulses = new List<SonarPulse>();
    readonly List<Vector3> verts = new List<Vector3>();
    readonly List<Color> colors = new List<Color>();
    readonly List<int> indices = new List<int>();
    Mesh mesh;
    GameObject holder;
    bool hooked;

    void OnEnable() => Hook();
    void OnDisable() => Unhook();

    void OnDestroy()
    {
        if (holder == null) return;
        if (Application.isPlaying) Destroy(holder); else DestroyImmediate(holder);
    }

    public void Hook()
    {
        if (hooked) return;
        if (!sensor) sensor = GetComponent<SonarSensor>();
        if (sensor) sensor.PulseCompleted += OnPulse;
        hooked = true;
    }

    public void Unhook()
    {
        if (!hooked) return;
        if (sensor) sensor.PulseCompleted -= OnPulse;
        hooked = false;
    }

    void OnPulse(SonarPulse p)
    {
        pulses.Add(p);
        while (pulses.Count > Mathf.Max(1, pulsesShown)) pulses.RemoveAt(0);
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && kb.vKey.wasPressedThisFrame) view = (View)(((int)view + 1) % 3);
    }

    void LateUpdate() => Refresh();

    float Now => sensor ? sensor.Clock : 0f;
    float MaxRange => sensor ? sensor.settings.maxRange : 6f;

    bool IsGround(SonarMeasurement m) =>
        belief != null ? belief.IsGroundEcho(m) : SonarMap.IsGroundEcho(m, sensor.settings.groundY, 0.2f);

    /// Rebuilds the line mesh for the current sensor time (LateUpdate does this; batch rendering calls it).
    public void Refresh()
    {
        if (holder == null) CreateHolder();
        verts.Clear();
        colors.Clear();
        if (view != View.Off && sensor != null && pulses.Count > 0) BuildLines();
        indices.Clear();
        for (int i = 0; i < verts.Count; i++) indices.Add(i);
        mesh.Clear();
        mesh.SetVertices(verts);
        mesh.SetColors(colors);
        mesh.SetIndices(indices, MeshTopology.Lines, 0);
        mesh.bounds = new Bounds(transform.position, Vector3.one * (2f * MaxRange + 2f));
    }

    void BuildLines()
    {
        float now = Now, range = MaxRange;
        foreach (var p in pulses)
        {
            float travelled = (now - p.time) * visualSpeed;   // how far the drawn sound has gone
            if (travelled < 0f || travelled > 2f * range + 1f) continue;

            // Illustrative wavefront: three great circles of a sphere growing at visualSpeed.
            if (travelled <= range)
            {
                var c = Wave; c.a *= 1f - travelled / range;
                Circle(p.origin, travelled, Vector3.right, Vector3.forward, c);
                if (verticalRings)
                {
                    Circle(p.origin, travelled, Vector3.right, Vector3.up, c * new Color(1, 1, 1, 0.5f));
                    Circle(p.origin, travelled, Vector3.forward, Vector3.up, c * new Color(1, 1, 1, 0.5f));
                }
            }

            foreach (var m in p.samples)
            {
                if (!m.hasReturn || travelled < m.range) continue;      // the wave hasn't reached it yet
                bool ground = IsGround(m);
                float fade = Mathf.Clamp01(1f - (travelled - m.range) / (range + 1f));
                var col = ground ? GroundEcho : ObstacleEcho; col.a *= fade;
                Cross(m.hitPoint, ground ? echoSize * 0.5f : echoSize, col);
                // The echo travelling back to the drone.
                float back = travelled - m.range;
                if (!ground && back < m.range)
                {
                    var head = m.hitPoint - m.direction * back;
                    Line(head, head - m.direction * Mathf.Min(0.35f, m.range - back), Returning);
                }
            }
        }

        // Every sample of the latest pulse: where it hit, or how far it looked without an echo.
        if (view == View.EchoesAndRays)
        {
            var last = pulses[pulses.Count - 1];
            foreach (var m in last.samples)
            {
                if (m.hasReturn)
                {
                    var c = IsGround(m) ? GroundEcho : ObstacleEcho; c.a = 0.45f;
                    Line(m.origin, m.hitPoint, c);
                }
                else Line(m.origin, m.origin + m.direction * range, NoReturn);
            }
        }

        if (belief != null)
            foreach (var cell in belief.Occupied)
                if (showBeliefCell == null || showBeliefCell(cell))
                    WireBox(belief.CenterOf(cell), belief.cellSize * 0.9f, BeliefBox);
    }

    void CreateHolder()
    {
        holder = new GameObject($"{name} sonar view") { layer = layer };
        mesh = new Mesh { name = "SonarLines" };
        mesh.MarkDynamic();
        holder.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = holder.AddComponent<MeshRenderer>();
        r.sharedMaterial = OverlayMaterial(true);
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
    }

    /// Unlit vertex-coloured, alpha-blended material. `onTop` draws over everything, like the original GL overlay.
    public static Material OverlayMaterial(bool onTop)
    {
        var m = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
        m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        m.SetInt("_Cull", (int)CullMode.Off);
        m.SetInt("_ZWrite", 0);
        m.SetInt("_ZTest", (int)(onTop ? CompareFunction.Always : CompareFunction.LessEqual));
        m.renderQueue = (int)RenderQueue.Overlay;
        return m;
    }

    void Line(Vector3 a, Vector3 b, Color c)
    {
        verts.Add(a); colors.Add(c);
        verts.Add(b); colors.Add(c);
    }

    void Circle(Vector3 center, float r, Vector3 u, Vector3 v, Color c)
    {
        const int n = 48;
        var prev = center + u * r;
        for (int i = 1; i <= n; i++)
        {
            float a = i * Mathf.PI * 2f / n;
            var p = center + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * r;
            Line(prev, p, c);
            prev = p;
        }
    }

    void Cross(Vector3 p, float s, Color c)
    {
        Line(p - Vector3.right * s, p + Vector3.right * s, c);
        Line(p - Vector3.up * s, p + Vector3.up * s, c);
        Line(p - Vector3.forward * s, p + Vector3.forward * s, c);
    }

    void WireBox(Vector3 center, float size, Color c)
    {
        float h = size * 0.5f;
        Span<Vector3> p = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
            p[i] = center + new Vector3((i & 1) == 0 ? -h : h, (i & 2) == 0 ? -h : h, (i & 4) == 0 ? -h : h);
        ReadOnlySpan<int> e = stackalloc int[] { 0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 0, 4, 1, 5, 2, 6, 3, 7 };
        for (int i = 0; i < e.Length; i += 2) Line(p[e[i]], p[e[i + 1]], c);
    }
}
