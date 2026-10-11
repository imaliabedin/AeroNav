using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

/// Debug view of the sonar. Everything drawn comes from the sensor's measurements, except the
/// expanding rings, which only illustrate the pulse: sound is slowed to `visualSpeed` so it can
/// be seen, and the rings themselves detect nothing. V cycles the view.
public class SonarVisualizer : MonoBehaviour
{
    public enum View { Off, Echoes, EchoesAndRays }

    public SonarSensor sensor;
    public View view = View.Echoes;
    [Tooltip("Speed of the drawn wavefront (m/s). Real sound (343 m/s) would cross 6 m in one frame.")]
    public float visualSpeed = 12f;
    /// The drone's belief map, drawn as red boxes (set by SonarNavigator; may be null).
    public SonarMap belief;
    /// Extra HUD lines (set by SonarNavigator).
    public Func<string> status;
    /// A camera that renders into a texture but should still get the overlay (tests). Others with a target texture,
    /// such as the drone's vision camera, never do, so stored images stay clean.
    public Camera alsoDrawOn;

    static readonly Color Wave = new Color(0.3f, 0.85f, 1f, 0.55f);
    static readonly Color ObstacleEcho = new Color(1f, 0.55f, 0.1f, 1f);
    static readonly Color GroundEcho = new Color(0.65f, 0.5f, 0.35f, 0.8f);
    static readonly Color NoReturn = new Color(0.8f, 0.8f, 0.8f, 0.12f);
    static readonly Color Returning = new Color(1f, 1f, 0.4f, 1f);
    static readonly Color BeliefBox = new Color(1f, 0.15f, 0.15f, 0.9f);

    readonly List<SonarPulse> pulses = new List<SonarPulse>();
    Material lineMat;
    bool hooked;
    GUIStyle label, panel;

    void OnEnable() => Hook();
    void OnDisable() => Unhook();

    public void Hook()
    {
        if (hooked) return;
        if (!sensor) sensor = GetComponent<SonarSensor>();
        if (sensor) sensor.PulseCompleted += OnPulse;
        RenderPipelineManager.endCameraRendering += OnEndCameraRendering;  // URP / SRP
        Camera.onPostRender += OnPostRenderCamera;                           // built-in pipeline
        hooked = true;
    }

    public void Unhook()
    {
        if (!hooked) return;
        if (sensor) sensor.PulseCompleted -= OnPulse;
        RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
        Camera.onPostRender -= OnPostRenderCamera;
        hooked = false;
    }

    void OnPulse(SonarPulse p)
    {
        pulses.Add(p);
        if (pulses.Count > 16) pulses.RemoveAt(0);
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && kb.vKey.wasPressedThisFrame) view = (View)(((int)view + 1) % 3);
    }

    float Now => sensor ? sensor.Clock : 0f;
    float MaxRange => sensor ? sensor.settings.maxRange : 6f;

    bool IsGround(SonarMeasurement m) =>
        belief != null ? belief.IsGroundEcho(m) : SonarMap.IsGroundEcho(m, sensor.settings.groundY, 0.2f);

    // ---------- drawing ----------

    void OnEndCameraRendering(ScriptableRenderContext ctx, Camera cam) => Draw(cam);
    void OnPostRenderCamera(Camera cam) => Draw(cam);

    void Draw(Camera cam)
    {
        if (view == View.Off || sensor == null || pulses.Count == 0) return;
        if (cam.targetTexture != null && cam != alsoDrawOn) return;   // not into the vision camera's images
        if (cam.cameraType != CameraType.Game && cam.cameraType != CameraType.SceneView) return;
        if (lineMat == null) CreateMaterial();

        // GL draws into the active render target; make sure that is this camera's.
        var prevTarget = RenderTexture.active;
        if (cam.targetTexture != null) RenderTexture.active = cam.targetTexture;
        GL.PushMatrix();
        GL.LoadProjectionMatrix(cam.projectionMatrix);
        GL.modelview = cam.worldToCameraMatrix;
        lineMat.SetPass(0);
        GL.Begin(GL.LINES);

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
                Circle(p.origin, travelled, Vector3.right, Vector3.up, c * new Color(1, 1, 1, 0.5f));
                Circle(p.origin, travelled, Vector3.forward, Vector3.up, c * new Color(1, 1, 1, 0.5f));
            }

            foreach (var m in p.samples)
            {
                if (!m.hasReturn || travelled < m.range) continue;      // the wave hasn't reached it yet
                bool ground = IsGround(m);
                float fade = Mathf.Clamp01(1f - (travelled - m.range) / (range + 1f));
                var col = ground ? GroundEcho : ObstacleEcho; col.a *= fade;
                Cross(m.hitPoint, ground ? 0.05f : 0.1f, col);
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
            foreach (var cell in belief.Occupied) WireBox(cell, 0.9f, BeliefBox);

        GL.End();
        GL.PopMatrix();
        RenderTexture.active = prevTarget;
    }

    void CreateMaterial()
    {
        // From Resources so the shader is included in player builds (WebGL strips unreferenced shaders).
        var template = Resources.Load<Material>("SonarLines");
        lineMat = template ? new Material(template) : new Material(Shader.Find("Hidden/Internal-Colored"));
        lineMat.hideFlags = HideFlags.HideAndDontSave;
        lineMat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        lineMat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        lineMat.SetInt("_Cull", (int)CullMode.Off);
        lineMat.SetInt("_ZWrite", 0);
        lineMat.SetInt("_ZTest", (int)CompareFunction.Always);   // overlay: always visible
    }

    static void Line(Vector3 a, Vector3 b, Color c)
    {
        GL.Color(c);
        GL.Vertex(a);
        GL.Vertex(b);
    }

    static void Circle(Vector3 center, float r, Vector3 u, Vector3 v, Color c)
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

    static void Cross(Vector3 p, float s, Color c)
    {
        Line(p - Vector3.right * s, p + Vector3.right * s, c);
        Line(p - Vector3.up * s, p + Vector3.up * s, c);
        Line(p - Vector3.forward * s, p + Vector3.forward * s, c);
    }

    static void WireBox(Vector3 center, float size, Color c)
    {
        float h = size * 0.5f;
        var p = new Vector3[8];
        for (int i = 0; i < 8; i++)
            p[i] = center + new Vector3((i & 1) == 0 ? -h : h, (i & 2) == 0 ? -h : h, (i & 4) == 0 ? -h : h);
        int[] e = { 0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 0, 4, 1, 5, 2, 6, 3, 7 };
        for (int i = 0; i < e.Length; i += 2) Line(p[e[i]], p[e[i + 1]], c);
    }

    // ---------- labels and panel ----------

    void OnGUI()
    {
        if (sensor == null) return;
        if (label == null)
        {
            label = new GUIStyle(GUI.skin.label) { fontSize = 11, richText = true, alignment = TextAnchor.MiddleCenter };
            label.normal.textColor = Color.white;
            panel = new GUIStyle(GUI.skin.label) { fontSize = 12, richText = true, wordWrap = true };
            panel.normal.textColor = Color.white;
        }

        var last = sensor.LastPulse;
        var cam = Camera.main;
        // Measured distance of each horizontal-row beam that echoed off an obstacle.
        if (view != View.Off && last != null && cam != null)
        {
            int row = Array.IndexOf(sensor.settings.elevations, 0f);
            for (int i = 0; row >= 0 && i < sensor.settings.horizontalBeams; i++)
            {
                var m = last.Beam(row * sensor.settings.horizontalBeams + i);
                if (!m.hasReturn || IsGround(m)) continue;
                var sp = cam.WorldToScreenPoint(m.hitPoint);
                if (sp.z <= 0f) continue;
                GUI.Label(new Rect(sp.x - 30, Screen.height - sp.y - 18, 60, 16), $"<b>{m.range:0.0} m</b>", label);
            }
        }

        int echoes = 0, ground = 0, none = 0;
        float nearest = float.PositiveInfinity;
        if (last != null)
            foreach (var m in last.samples)
            {
                if (!m.hasReturn) { none++; continue; }
                if (IsGround(m)) { ground++; continue; }
                echoes++;
                nearest = Mathf.Min(nearest, m.range);
            }

        var s = sensor.settings;
        string text =
            $"<b>SONAR</b>  view: <b>{view}</b> (V)\n" +
            $"{sensor.BeamDirections?.Length} beams x {1 + s.subRays} samples = {sensor.QueriesPerPulse} queries/pulse at {s.pulseRate:0} Hz, " +
            $"{sensor.AverageMilliseconds:0.00} ms/pulse\n" +
            $"last pulse: {echoes} obstacle echoes" + (echoes > 0 ? $" (nearest {nearest:0.00} m)" : "") +
            $", {ground} ground, {none} no return (max {s.maxRange:0} m)\n" +
            (status != null ? status() + "\n" : "") +
            "<color=#9fe8ff>Rings are illustrative (sound slowed to " + $"{visualSpeed:0}" + " m/s); only the sampled rays detect. " +
            "Orange = measured echo, red boxes = the drone's belief, not the real map.</color>";

        var r = new Rect(10, Screen.height - 132, 640, 122);
        var prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.65f);
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = prev;
        GUI.Label(new Rect(r.x + 8, r.y + 4, r.width - 16, r.height - 8), text, panel);
    }
}
