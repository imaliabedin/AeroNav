using System.Collections.Generic;
using UnityEngine;

/// The drone's belief about which grid cells are blocked, built only from sonar measurements.
/// Per pulse, a cell hit by one sample gains `hitWeight`, a cell hit by two or more samples gains
/// `multiHitWeight`, and a cell that samples passed through without an echo loses `clearWeight` per
/// sample (at most `maxClearPerPulse`). Echoes only mark cells the sensor can observe (within range).
/// A cell counts as blocked once its evidence reaches `occupyThreshold` and stays blocked until it
/// falls below `releaseThreshold`. So a single false echo never blocks a cell on its own.
public class SonarMap
{
    public float hitWeight = 1f;
    public float multiHitWeight = 2f;
    public float clearWeight = 0.2f;
    public float maxClearPerPulse = 1f;
    public float occupyThreshold = 2f;
    public float releaseThreshold = 1f;
    public float maxEvidence = 4f;
    [Tooltip("An echo blocks every cell whose center is within this distance (each axis) of the echo point. " +
             "0.6 covers the drone's 0.4 m body on the edges between cells.")]
    public float inflate = 0.6f;
    public float groundY = -0.5f;
    [Tooltip("Echoes within this distance (+3% of range) of where the ground should be are treated as ground.")]
    public float groundTolerance = 0.2f;

    /// Cells outside these bounds are ignored (the known outer boundary of the area).
    public BoundsInt bounds = new BoundsInt(-1000, 0, -1000, 2000, 1000, 2000);

    readonly Dictionary<Vector3Int, float> evidence = new Dictionary<Vector3Int, float>();
    readonly HashSet<Vector3Int> occupied = new HashSet<Vector3Int>();
    readonly HashSet<Vector3Int> touched = new HashSet<Vector3Int>();

    public bool IsBlocked(Vector3Int cell) => occupied.Contains(cell);
    public IReadOnlyCollection<Vector3Int> Occupied => occupied;
    public float Evidence(Vector3Int cell) => evidence.TryGetValue(cell, out var e) ? e : 0f;

    public void Clear()
    {
        evidence.Clear();
        occupied.Clear();
    }

    /// True if the echo is where the known ground plane should be along this sample.
    public static bool IsGroundEcho(SonarMeasurement m, float groundY, float tolerance)
    {
        if (!m.hasReturn || m.direction.y >= -1e-4f) return false;
        float expected = (groundY - m.origin.y) / m.direction.y;
        return Mathf.Abs(m.range - expected) <= tolerance + 0.03f * expected;
    }

    public bool IsGroundEcho(SonarMeasurement m) => IsGroundEcho(m, groundY, groundTolerance);

    readonly Dictionary<Vector3Int, int> hitsThisPulse = new Dictionary<Vector3Int, int>();
    readonly Dictionary<Vector3Int, int> seenThisPulse = new Dictionary<Vector3Int, int>();

    /// Adds one pulse. Fills the cells that became blocked and that were released (either list may be null).
    public void Integrate(SonarPulse pulse, float maxRange, List<Vector3Int> newlyBlocked, List<Vector3Int> released)
    {
        newlyBlocked?.Clear();
        released?.Clear();
        hitsThisPulse.Clear();
        seenThisPulse.Clear();

        foreach (var m in pulse.samples)
        {
            bool obstacle = m.hasReturn && !IsGroundEcho(m);
            // Free space along the sample, stopping short of whatever echoed.
            MarkSeen(m.origin, m.direction, m.hasReturn ? m.range - inflate - 0.1f : maxRange);
            if (obstacle) MarkHit(m.hitPoint, m.origin, maxRange);
        }

        touched.Clear();
        foreach (var kv in hitsThisPulse)
        {
            evidence[kv.Key] = Mathf.Min(maxEvidence, Evidence(kv.Key) + (kv.Value >= 2 ? multiHitWeight : hitWeight));
            touched.Add(kv.Key);
        }
        foreach (var kv in seenThisPulse)
        {
            var c = kv.Key;
            if (hitsThisPulse.ContainsKey(c) || !evidence.TryGetValue(c, out var e)) continue;
            evidence[c] = Mathf.Max(0f, e - Mathf.Min(maxClearPerPulse, clearWeight * kv.Value));
            touched.Add(c);
        }

        foreach (var c in touched)
        {
            float e = Evidence(c);
            if (!occupied.Contains(c) && e >= occupyThreshold)
            {
                occupied.Add(c);
                newlyBlocked?.Add(c);
            }
            else if (occupied.Contains(c) && e < releaseThreshold)
            {
                occupied.Remove(c);
                released?.Add(c);
            }
            if (e <= 0f) evidence.Remove(c);
        }
    }

    /// Adds the echo at p to every cell around it that the sensor at `origin` can observe.
    void MarkHit(Vector3 p, Vector3 origin, float maxRange)
    {
        var lo = Vector3Int.CeilToInt(p - Vector3.one * inflate);
        var hi = Vector3Int.FloorToInt(p + Vector3.one * inflate);
        for (int x = lo.x; x <= hi.x; x++)
            for (int y = lo.y; y <= hi.y; y++)
                for (int z = lo.z; z <= hi.z; z++)
                {
                    var c = new Vector3Int(x, y, z);
                    // A cell no sample can reach could never be cleared again from here.
                    if (!InBounds(c) || Vector3.Distance(c, origin) > maxRange) continue;
                    hitsThisPulse[c] = hitsThisPulse.TryGetValue(c, out var n) ? n + 1 : 1;
                }
    }

    /// Every cell the sample passed through before `until` (counted once per sample).
    void MarkSeen(Vector3 origin, Vector3 dir, float until)
    {
        var last = new Vector3Int(int.MinValue, 0, 0);
        for (float t = 0.3f; t <= until; t += 0.25f)
        {
            var c = Vector3Int.RoundToInt(origin + dir * t);
            if (c == last) continue;
            last = c;
            seenThisPulse[c] = seenThisPulse.TryGetValue(c, out var n) ? n + 1 : 1;
        }
    }

    bool InBounds(Vector3Int c) =>
        c.x >= bounds.xMin && c.x < bounds.xMax && c.y >= bounds.yMin && c.y < bounds.yMax && c.z >= bounds.zMin && c.z < bounds.zMax;
}
