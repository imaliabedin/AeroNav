using System.Collections.Generic;
using UnityEngine;

/// 6-connected grid A* over the cell map. The drone has full knowledge of the world:
/// walls and placed props are obstacles, and the target is the nearest win cube.
public static class AStarPlanner
{
    const float DroneClearance = 0.3f;

    // Neighbour order: +X, -X, +Y, -Y, +Z, -Z
    public static readonly Vector3Int[] Dirs =
    {
        Vector3Int.right, Vector3Int.left, Vector3Int.up, Vector3Int.down,
        new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1)
    };

    public class Result
    {
        public bool found;
        public string failReason;
        public List<Vector3Int> path = new List<Vector3Int>();   // start..goal inclusive
        public HashSet<Vector3Int> closed = new HashSet<Vector3Int>();
        public Vector3Int goal;
        public int expanded;
        public float milliseconds;
    }

    public static Result FindPath(CellMap map, Vector3Int start, int maxExpansions = 200000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new Result();

        var goals = new List<Vector3Int>();
        var propBounds = new List<Bounds>();
        var min = start;
        var max = start;
        foreach (var kv in map.Cells)
        {
            min = Vector3Int.Min(min, kv.Key);
            max = Vector3Int.Max(max, kv.Key);
            if (kv.Value.type == CellType.Win) goals.Add(kv.Key);
        }
        if (map.props != null)
        {
            foreach (var prop in map.props.GetComponentsInChildren<Prop>())
            {
                if (prop == null || !PropSet.RendererBounds(prop.gameObject, out var bounds)) continue;
                min = Vector3Int.Min(min, Vector3Int.FloorToInt(bounds.min));
                max = Vector3Int.Max(max, Vector3Int.CeilToInt(bounds.max));
                bounds.Expand(Vector3.one * (DroneClearance * 2f));
                propBounds.Add(bounds);
            }
        }
        if (goals.Count == 0)
        {
            result.failReason = "no win cube";
            return result;
        }
        // Search space: the map's bounding box plus a one-cell margin, so open maps can be flown around.
        // Never below y = 0: the ground plane sits at y = -0.5.
        min -= Vector3Int.one;
        max += Vector3Int.one;
        min.y = Mathf.Max(min.y, 0);

        var blockedByProps = new HashSet<Vector3Int>();
        foreach (var bounds in propBounds)
        {
            int minX = Mathf.Max(min.x, Mathf.CeilToInt(bounds.min.x));
            int minY = Mathf.Max(min.y, Mathf.CeilToInt(bounds.min.y));
            int minZ = Mathf.Max(min.z, Mathf.CeilToInt(bounds.min.z));
            int maxX = Mathf.Min(max.x, Mathf.FloorToInt(bounds.max.x));
            int maxY = Mathf.Min(max.y, Mathf.FloorToInt(bounds.max.y));
            int maxZ = Mathf.Min(max.z, Mathf.FloorToInt(bounds.max.z));
            for (int x = minX; x <= maxX; x++)
                for (int y = minY; y <= maxY; y++)
                    for (int z = minZ; z <= maxZ; z++)
                    {
                        var p = new Vector3Int(x, y, z);
                        if (bounds.Contains(p)) blockedByProps.Add(p);
                    }
        }
        if (blockedByProps.Contains(start))
        {
            result.failReason = "start blocked by object";
            return result;
        }

        var g = new Dictionary<Vector3Int, int> { [start] = 0 };
        var cameFrom = new Dictionary<Vector3Int, Vector3Int>();
        var open = new MinHeap();
        open.Push(start, H(start, goals), 0);

        while (open.Count > 0)
        {
            var (cur, curG) = open.Pop();
            if (result.closed.Contains(cur) || curG > g[cur]) continue;   // stale heap entry
            result.closed.Add(cur);
            result.expanded++;

            var cell = map.Get(cur);
            if (cell != null && cell.type == CellType.Win)
            {
                result.found = true;
                result.goal = cur;
                for (var c = cur; ; c = cameFrom[c])
                {
                    result.path.Add(c);
                    if (c == start) break;
                }
                result.path.Reverse();
                break;
            }
            if (result.expanded >= maxExpansions) { result.failReason = "search limit"; break; }

            foreach (var d in Dirs)
            {
                var n = cur + d;
                if (n.x < min.x || n.y < min.y || n.z < min.z || n.x > max.x || n.y > max.y || n.z > max.z) continue;
                if (result.closed.Contains(n)) continue;
                if (blockedByProps.Contains(n)) continue;
                var nc = map.Get(n);
                if (nc != null && nc.type != CellType.Win) continue;   // solid

                int ng = curG + 1;
                if (g.TryGetValue(n, out int old) && old <= ng) continue;
                g[n] = ng;
                cameFrom[n] = cur;
                open.Push(n, ng + H(n, goals), ng);
            }
        }

        if (!result.found && result.failReason == null) result.failReason = "goal unreachable";
        result.milliseconds = (float)sw.Elapsed.TotalMilliseconds;
        return result;
    }

    // Manhattan distance to the nearest goal: admissible and consistent for unit 6-connected moves.
    static int H(Vector3Int p, List<Vector3Int> goals)
    {
        int best = int.MaxValue;
        foreach (var t in goals)
        {
            int d = Mathf.Abs(p.x - t.x) + Mathf.Abs(p.y - t.y) + Mathf.Abs(p.z - t.z);
            if (d < best) best = d;
        }
        return best;
    }

    /// Binary min-heap ordered by f, ties broken toward larger g (closer to the goal).
    class MinHeap
    {
        readonly List<(Vector3Int pos, int f, int g)> items = new List<(Vector3Int, int, int)>();
        public int Count => items.Count;

        static bool Less((Vector3Int pos, int f, int g) a, (Vector3Int pos, int f, int g) b)
            => a.f < b.f || (a.f == b.f && a.g > b.g);

        public void Push(Vector3Int pos, int f, int g)
        {
            items.Add((pos, f, g));
            int i = items.Count - 1;
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (!Less(items[i], items[parent])) break;
                (items[i], items[parent]) = (items[parent], items[i]);
                i = parent;
            }
        }

        public (Vector3Int pos, int g) Pop()
        {
            var top = items[0];
            int last = items.Count - 1;
            items[0] = items[last];
            items.RemoveAt(last);
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1, r = l + 1, s = i;
                if (l < items.Count && Less(items[l], items[s])) s = l;
                if (r < items.Count && Less(items[r], items[s])) s = r;
                if (s == i) break;
                (items[i], items[s]) = (items[s], items[i]);
                i = s;
            }
            return (top.pos, top.g);
        }
    }
}
