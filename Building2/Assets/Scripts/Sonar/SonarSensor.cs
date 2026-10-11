using System;
using System.Collections.Generic;
using UnityEngine;

/// Where an echo really came from. Ground truth for tests and the debug view only:
/// the drone's navigation must never read it.
public enum EchoSource { None, Object, Ground, False }

/// One sample ray's result in one pulse: what the drone's sonar measured.
public struct SonarMeasurement
{
    public int beam;           // index into SonarSensor.BeamDirections
    public Vector3 origin;
    public Vector3 direction;  // unit vector this sample was fired along
    public bool hasReturn;     // false = no echo within max range; then range is +Infinity
    public float range;        // measured distance in meters (noisy), or +Infinity
    public Vector3 hitPoint;   // origin + direction * range (the sensor's estimate); unused without a return
    public float strength;     // 0..1 estimated echo strength
    public float timestamp;    // sensor clock (s) when the pulse left the drone

    public float RoundTripSeconds => hasReturn ? 2f * range / SonarSensor.SpeedOfSound : float.PositiveInfinity;

    // ---- ground truth (tests / debug view only) ----
    public EchoSource debugSource;
    public Collider debugCollider;
    public float debugTrueRange;
}

/// All measurements from one pulse.
public class SonarPulse
{
    public int index;
    public float time;
    public Vector3 origin;
    public SonarMeasurement[] samples;  // beams * (1 + subRays)
    public int samplesPerBeam;
    public float milliseconds;          // CPU time spent on the physics queries

    /// The nearest returned sample of each beam, or a no-return sample of that beam.
    public SonarMeasurement Beam(int beam)
    {
        var best = samples[beam * samplesPerBeam];
        for (int i = 1; i < samplesPerBeam; i++)
        {
            var s = samples[beam * samplesPerBeam + i];
            if (s.hasReturn && (!best.hasReturn || s.range < best.range)) best = s;
        }
        return best;
    }
}

[Serializable]
public class SonarSettings
{
    [Header("Pattern")]
    public int horizontalBeams = 16;                         // 22.5 deg apart
    public float[] elevations = { -30f, 0f, 30f };          // rows, degrees
    public bool upAndDown = true;
    [Tooltip("Extra sample rays per beam, spread around the beam axis.")]
    public int subRays = 4;
    [Tooltip("Angle between a sub-ray and its beam axis (degrees). 11.25 = halfway to the next beam.")]
    public float subRayAngle = 11.25f;
    [Tooltip("Turn the sub-ray pattern a little every pulse, so successive pulses fill the gaps.")]
    public bool rotatePattern = true;
    [Tooltip("Shift the whole beam pattern every pulse by an evenly spread offset (within one beam spacing), " +
             "like a sonar head steering between pings, so the main beams sweep the gaps between them.")]
    public bool ditherPattern = true;
    [Tooltip("SphereCast radius of the main sample of each beam (0 = thin ray).")]
    public float sphereRadius = 0.1f;
    [Tooltip("SphereCast radius of the extra samples (0 = thin rays). 0.1 roughly doubled thin-wire detection in tests.")]
    public float subRaySphereRadius = 0.1f;

    [Header("Range and rate")]
    public float maxRange = 6f;
    public float pulseRate = 10f;

    [Header("Imperfections")]
    [Tooltip("Debugging: no noise, no missed or false echoes.")]
    public bool perfect = false;
    public float rangeNoise = 0.02f;            // m, 1 sigma
    public float rangeNoisePerMeter = 0.01f;    // extra sigma per meter of range
    public float missProbability = 0.02f;       // any echo
    [Tooltip("Objects thinner than this (m) reflect less and are missed more often.")]
    public float smallTargetSize = 0.1f;
    public float smallTargetMissProbability = 0.3f;
    [Tooltip("Surfaces hit at more than this angle from head-on scatter the sound away.")]
    public float grazingAngle = 75f;
    public float grazingMissProbability = 0.5f;
    public float falseEchoProbability = 0.002f; // per sample per pulse
    public int seed = 12345;

    [Header("World")]
    [Tooltip("Height of the ground. The scene has no ground collider, so the ground is a known plane.")]
    public float groundY = -0.5f;
    public LayerMask layers = Physics.DefaultRaycastLayers & ~(1 << 2);  // never Ignore Raycast (the drone)
}

/// Ported from the parking-lot project (Unity/Assets/Scripts/SonarSensor.cs); only the update hook differs.
/// Simulated sonar: every pulse fires a fixed pattern of beams (each a SphereCast plus a few
/// thin sample rays) and reports what came back. It approximates echoes with Unity physics
/// queries; it does not simulate sound waves.
public class SonarSensor : MonoBehaviour
{
    public const float SpeedOfSound = 343f;
    static readonly float GoldenAngle = 137.50776f;

    public SonarSettings settings = new SonarSettings();
    [Tooltip("Pause pulsing (the simulation pauses it while paused).")]
    public bool paused;

    /// Nominal beam axes (before the per-pulse dither), in the drone's frame.
    public Vector3[] BeamDirections { get; private set; }
    Vector2[] beamAzEl;   // degrees
    public SonarPulse LastPulse { get; private set; }
    /// Pulses fired so far; also sets where the rotating sub-ray pattern starts.
    public int PulseCount { get; set; }
    public int QueriesPerPulse => (BeamDirections?.Length ?? 0) * (1 + settings.subRays);
    public float AverageMilliseconds { get; private set; }
    /// Sensor time in seconds (advanced by Tick); pulse timestamps use it.
    public float Clock { get => clock; set => clock = value; }
    public event Action<SonarPulse> PulseCompleted;

    float clock, nextPulse;
    System.Random rng;
    int patternPhase;   // where the dither / rotation sequence starts (from the seed), like switching on at a random moment

    void Awake() => Init();

    /// Builds the beam table and the random generator; safe to call again after changing settings.
    public void Init()
    {
        var azEl = new List<Vector2>();
        foreach (float el in settings.elevations)
            for (int i = 0; i < settings.horizontalBeams; i++)
                azEl.Add(new Vector2(i * 360f / settings.horizontalBeams, el));
        if (settings.upAndDown) { azEl.Add(new Vector2(0f, 90f)); azEl.Add(new Vector2(0f, -90f)); }
        beamAzEl = azEl.ToArray();
        BeamDirections = new Vector3[beamAzEl.Length];
        for (int b = 0; b < beamAzEl.Length; b++) BeamDirections[b] = Axis(beamAzEl[b], Vector2.zero);
        rng = new System.Random(settings.seed);
        patternPhase = new System.Random(settings.seed ^ 0x5f3759df).Next(100000);
        PulseCount = 0;
    }

    [Tooltip("Pulse on its own every frame. Off when a controller calls Tick itself.")]
    public bool autoTick = true;

    void Update()
    {
        if (paused || !autoTick) return;
        Tick(Time.deltaTime);
    }

    /// Advances the sensor clock and fires any pulses that are due.
    public void Tick(float dt)
    {
        clock += dt;
        if (clock - nextPulse > 3f / settings.pulseRate) nextPulse = clock;  // don't try to catch up after a hitch
        while (clock >= nextPulse)
        {
            Pulse(transform.position);
            nextPulse += 1f / settings.pulseRate;
        }
    }

    /// Fires one pulse from `origin` now and notifies listeners.
    public SonarPulse Pulse(Vector3 origin)
    {
        if (BeamDirections == null) Init();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (!Physics.autoSyncTransforms) Physics.SyncTransforms();

        var s = settings;
        int per = 1 + s.subRays;
        var pulse = new SonarPulse
        {
            index = PulseCount, time = clock, origin = origin, samplesPerBeam = per,
            samples = new SonarMeasurement[BeamDirections.Length * per],
        };
        int n = PulseCount + patternPhase;
        float spin = s.rotatePattern ? n * GoldenAngle : 0f;
        var rot = transform.rotation;
        var dither = Dither(n);

        for (int b = 0; b < BeamDirections.Length; b++)
        {
            var axis = rot * Axis(beamAzEl[b], dither);
            pulse.samples[b * per] = Sample(b, origin, axis, s.sphereRadius, pulse.time);
            if (s.subRays == 0) continue;
            // Two unit vectors at right angles to the beam, to spread the sub-rays around it.
            var side = Mathf.Abs(axis.y) > 0.99f ? Vector3.right : Vector3.Cross(Vector3.up, axis).normalized;
            var up = Vector3.Cross(axis, side);
            for (int k = 0; k < s.subRays; k++)
            {
                float phi = (spin + k * 360f / s.subRays) * Mathf.Deg2Rad;
                var offset = (side * Mathf.Cos(phi) + up * Mathf.Sin(phi)) * Mathf.Sin(s.subRayAngle * Mathf.Deg2Rad);
                var dir = (axis * Mathf.Cos(s.subRayAngle * Mathf.Deg2Rad) + offset).normalized;
                pulse.samples[b * per + 1 + k] = Sample(b, origin, dir, s.subRaySphereRadius, pulse.time);
            }
        }

        pulse.milliseconds = (float)sw.Elapsed.TotalMilliseconds;
        AverageMilliseconds = PulseCount == 0 ? pulse.milliseconds : Mathf.Lerp(AverageMilliseconds, pulse.milliseconds, 0.05f);
        PulseCount++;
        LastPulse = pulse;
        PulseCompleted?.Invoke(pulse);
        return pulse;
    }

    SonarMeasurement Sample(int beam, Vector3 origin, Vector3 dir, float radius, float time)
    {
        var s = settings;
        var m = new SonarMeasurement
        {
            beam = beam, origin = origin, direction = dir, timestamp = time,
            range = float.PositiveInfinity, debugTrueRange = float.PositiveInfinity,
        };

        // Nearest solid object along the sample (triggers such as the goal cube don't echo).
        bool hitObject = radius > 0f
            ? Physics.SphereCast(origin, radius, dir, out var hit, s.maxRange, s.layers, QueryTriggerInteraction.Ignore)
            : Physics.Raycast(origin, dir, out hit, s.maxRange, s.layers, QueryTriggerInteraction.Ignore);
        float objectDist = hitObject ? Vector3.Distance(origin, hit.point) : float.PositiveInfinity;

        // The ground is a known plane, not a collider.
        float groundDist = dir.y < -1e-4f ? (s.groundY - origin.y) / dir.y : float.PositiveInfinity;
        if (groundDist < 0f || groundDist > s.maxRange) groundDist = float.PositiveInfinity;

        float trueDist;
        float cosIncidence, size;
        if (objectDist <= groundDist && hitObject && objectDist <= s.maxRange)
        {
            trueDist = objectDist;
            m.debugSource = EchoSource.Object;
            m.debugCollider = hit.collider;
            cosIncidence = Mathf.Clamp01(Vector3.Dot(-dir, hit.normal));
            var e = hit.collider.bounds.size;
            size = Mathf.Min(e.x, e.y, e.z);
        }
        else if (!float.IsPositiveInfinity(groundDist))
        {
            trueDist = groundDist;
            m.debugSource = EchoSource.Ground;
            cosIncidence = -dir.y;
            size = float.PositiveInfinity;
        }
        else
        {
            trueDist = float.PositiveInfinity;
            cosIncidence = 0f;
            size = 0f;
        }

        if (!float.IsPositiveInfinity(trueDist))
        {
            float sizeFactor = Mathf.Clamp01(size / s.smallTargetSize);
            float miss = s.missProbability + s.smallTargetMissProbability * (1f - sizeFactor);
            if (Mathf.Acos(cosIncidence) * Mathf.Rad2Deg > s.grazingAngle) miss += s.grazingMissProbability;
            if (s.perfect || Rand() >= miss)
            {
                float noise = s.perfect ? 0f : Gaussian() * (s.rangeNoise + s.rangeNoisePerMeter * trueDist);
                m.hasReturn = true;
                m.range = Mathf.Max(0f, trueDist + noise);
                m.debugTrueRange = trueDist;
                m.strength = Mathf.Clamp01(Mathf.Max(cosIncidence, 0.05f) * Mathf.Sqrt(Mathf.Max(sizeFactor, 0.05f))
                                           * (1f - 0.5f * trueDist / s.maxRange));
            }
            else
            {
                m.debugSource = EchoSource.None;  // the echo was lost
                m.debugCollider = null;
            }
        }

        // Spurious echo (electrical noise, crosstalk): a random range on this sample.
        if (!s.perfect && Rand() < s.falseEchoProbability)
        {
            float r = 0.3f + Rand() * (s.maxRange - 0.3f);
            if (!m.hasReturn || r < m.range)
            {
                m.hasReturn = true;
                m.range = r;
                m.strength = 0.2f;
                m.debugSource = EchoSource.False;
                m.debugCollider = null;
                m.debugTrueRange = float.PositiveInfinity;
            }
        }

        if (m.hasReturn) m.hitPoint = origin + dir * m.range;
        return m;
    }

    /// Pattern offset (azimuth, elevation in degrees) for pulse n: a Halton point inside one beam
    /// spacing, so successive pulses spread the beams evenly over the gaps between them.
    public Vector2 Dither(int n)
    {
        if (!settings.ditherPattern) return Vector2.zero;
        float azStep = 360f / Mathf.Max(1, settings.horizontalBeams);
        float elStep = settings.elevations.Length > 1 ? Mathf.Abs(settings.elevations[1] - settings.elevations[0]) : 30f;
        return new Vector2((Halton(n + 1, 2) - 0.5f) * azStep, (Halton(n + 1, 3) - 0.5f) * elStep);
    }

    /// Unit vector for a beam at (azimuth, elevation) plus an offset. Vertical beams tilt away from
    /// straight up/down by the size of the elevation offset; their azimuth offset (+-half a beam gap)
    /// is stretched to a full turn so the tilt direction also varies all the way around.
    static Vector3 Axis(Vector2 azEl, Vector2 offset)
    {
        float el = Mathf.Abs(azEl.y) >= 90f
            ? azEl.y - Mathf.Sign(azEl.y) * Mathf.Abs(offset.y)
            : azEl.y + offset.y;
        return Quaternion.Euler(-el, azEl.x + offset.x * (Mathf.Abs(azEl.y) >= 90f ? 16f : 1f), 0f) * Vector3.forward;
    }

    static float Halton(int i, int b)
    {
        float f = 1f, r = 0f;
        for (; i > 0; i /= b) { f /= b; r += f * (i % b); }
        return r;
    }

    float Rand() => (float)rng.NextDouble();

    float Gaussian()
    {
        double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
    }
}
