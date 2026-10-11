using System.Collections.Generic;
using UnityEngine;

/// Per-cell coverage of a scan region (1 m cells), kept separately for geometric coverage (inside an image's
/// footprint) and visual observability (the ground was actually visible from the camera).
///
/// Visibility comes from the scene: a ray from the camera to each cell's ground point. What it hits decides:
///   clear                → Seen
///   a moving car/person  → OccludedMoving (confirmed; revisit later)
///   anything else        → HiddenStatic (building, tree, parked car: no revisit can see under it from above)
/// A clear ray within `nearMover` m of a moving car/person counts as Suspected: the cell may be partly hidden
/// (a small object at the car's edge). Suspected road cells get one more look.
/// Assumes flat ground at `groundY`.
public class ScanCoverage
{
    public enum State : byte { Unseen, Seen, Suspected, OccludedMoving, HiddenStatic }

    public class Cell
    {
        public Vector2Int c;
        public bool road;
        public State state = State.Unseen;
        public int inFootprint;      // images (saved) whose footprint contained the cell
        public int looks;            // revisit attempts spent on it
        public List<int> blockedBy = new List<int>();   // sequence numbers of images where a mover hid it
    }

    public readonly Dictionary<Vector2Int, Cell> cells = new Dictionary<Vector2Int, Cell>();
    public float groundY = -0.5f;
    public float groundTolerance = 0.6f;    // hits this close to the ground are the ground (or the lost item itself)
    public float nearMover = 1.0f;
    /// m² of the region under buildings: not part of `cells`, never scanned (reported separately).
    public int buildings;

    public ScanCoverage(IEnumerable<Vector2Int> regionCells, float groundY)
    {
        this.groundY = groundY;
        foreach (var c in regionCells)
            cells[c] = new Cell { c = c, road = CityLife.IsRoad(c.x, c.y) };
    }

    /// Result of looking at one image's cells (computed at capture time, applied once the image is saved).
    public class Observation
    {
        public List<(Vector2Int c, State s)> cells = new List<(Vector2Int, State)>();
        public int confirmed, suspected, hidden;
        public string Status => confirmed > 0 ? "confirmed" : suspected > 0 ? "suspected" : "unobstructed";
    }

    public Observation Observe(Vector3 camPos, Vector2 footprint)
    {
        var obs = new Observation();
        var cam2 = new Vector2(camPos.x, camPos.z);
        var movers = MoverBounds(camPos, footprint);
        int xMin = Mathf.CeilToInt(cam2.x - footprint.x * 0.5f), xMax = Mathf.FloorToInt(cam2.x + footprint.x * 0.5f);
        int zMin = Mathf.CeilToInt(cam2.y - footprint.y * 0.5f), zMax = Mathf.FloorToInt(cam2.y + footprint.y * 0.5f);
        for (int x = xMin; x <= xMax; x++)
            for (int z = zMin; z <= zMax; z++)
            {
                var key = new Vector2Int(x, z);
                if (!cells.ContainsKey(key)) continue;
                var s = Classify(camPos, new Vector3(x, groundY, z), movers);
                obs.cells.Add((key, s));
                if (s == State.OccludedMoving) obs.confirmed++;
                else if (s == State.Suspected) obs.suspected++;
                else if (s == State.HiddenStatic) obs.hidden++;
            }
        return obs;
    }

    State Classify(Vector3 cam, Vector3 ground, List<Bounds> movers)
    {
        var d = ground - cam;
        float dist = d.magnitude;
        // Everything but layer 2 (the drone itself); triggers ignored.
        if (Physics.Raycast(cam, d / dist, out var hit, dist, ~(1 << 2), QueryTriggerInteraction.Ignore)
            && hit.point.y > groundY + groundTolerance)
            return CityLife.IsMover(hit.collider.transform) ? State.OccludedMoving : State.HiddenStatic;
        foreach (var b in movers)
        {
            var e = b; e.Expand(new Vector3(nearMover * 2f, 0f, nearMover * 2f));
            if (ground.x >= e.min.x && ground.x <= e.max.x && ground.z >= e.min.z && ground.z <= e.max.z) return State.Suspected;
        }
        return State.Seen;
    }

    static List<Bounds> MoverBounds(Vector3 cam, Vector2 footprint)
    {
        var list = new List<Bounds>();
        var half = new Vector3(footprint.x * 0.5f + 3f, (cam.y + 2f) * 0.5f + 1f, footprint.y * 0.5f + 3f);
        var center = new Vector3(cam.x, cam.y * 0.5f, cam.z);
        var seen = new HashSet<Transform>();
        foreach (var col in Physics.OverlapBox(center, half, Quaternion.identity, ~(1 << 2), QueryTriggerInteraction.Ignore))
            if (CityLife.IsMover(col.transform) && seen.Add(col.transform.root))
                if (PropSet.RendererBounds(col.transform.root.gameObject, out var b)) list.Add(b);
        return list;
    }

    /// Applies a saved image's observation. A cell once Seen stays Seen; HiddenStatic only replaces Unseen.
    public void Apply(Observation obs, int sequence)
    {
        foreach (var (c, s) in obs.cells)
        {
            var cell = cells[c];
            cell.inFootprint++;
            if (cell.state == State.Seen) continue;
            switch (s)
            {
                case State.Seen: cell.state = State.Seen; break;
                case State.Suspected: if (cell.state != State.OccludedMoving) cell.state = State.Suspected; break;
                case State.OccludedMoving:
                    cell.state = State.OccludedMoving;   // confirmed beats suspected
                    cell.blockedBy.Add(sequence);
                    break;
                case State.HiddenStatic: if (cell.state == State.Unseen) cell.state = State.HiddenStatic; break;
            }
        }
    }

    /// Cells that still need a look: never in a saved image (gaps, failed saves), hidden by a mover, or suspected on a road.
    public bool NeedsRevisit(Cell c, int maxLooks) =>
        c.looks < maxLooks && (c.state == State.Unseen || c.state == State.OccludedMoving || (c.state == State.Suspected && c.road));

    public Dictionary<string, int> Counts()
    {
        var d = new Dictionary<string, int>
        {
            ["cells"] = cells.Count, ["buildings"] = buildings, ["seen"] = 0, ["suspected"] = 0, ["occluded_moving"] = 0, ["hidden_static"] = 0,
            ["unseen"] = 0, ["road_cells"] = 0, ["road_seen"] = 0, ["in_footprint"] = 0,
        };
        foreach (var c in cells.Values)
        {
            if (c.inFootprint > 0) d["in_footprint"]++;
            if (c.road) d["road_cells"]++;
            switch (c.state)
            {
                case State.Seen: d["seen"]++; if (c.road) d["road_seen"]++; break;
                case State.Suspected: d["suspected"]++; break;
                case State.OccludedMoving: d["occluded_moving"]++; break;
                case State.HiddenStatic: d["hidden_static"]++; break;
                default: d["unseen"]++; break;
            }
        }
        return d;
    }
}
