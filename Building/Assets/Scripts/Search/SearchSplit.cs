using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// How the drones divide the building before they start. All strategies use only what the drones know
/// up front (footprint, floors, stair core, their own start positions), not the floor plan.
public enum SplitMode
{
    /// Each spot goes to the drone that can get there soonest, with the split shifted until every drone
    /// has about the same number of spots. (Default.)
    BalancedNearest,
    /// Each spot goes to the drone that can get there soonest, however uneven that is.
    NearestDrone,
    /// Whole floors in contiguous blocks, one block per drone, ordered by start floor.
    FloorBlocks,
    /// Every floor is cut into side-by-side strips (along the building's length), one per drone.
    VerticalStrips,
}

public static class SearchSplit
{
    public struct Start
    {
        public int floor;
        public Vector2Int cell;
    }

    /// Fills grid.owner for every spot. `climbCost` is the estimated extra travel (m) per floor of stairs.
    public static void Assign(SearchGrid grid, IList<Start> drones, SplitMode mode, float climbCost)
    {
        int k = drones.Count;
        if (k == 1) { ForEachSpot(grid, (f, x, z) => grid.owner[f, x, z] = 0); return; }

        switch (mode)
        {
            case SplitMode.FloorBlocks:
            {
                var order = Enumerable.Range(0, k).OrderBy(i => drones[i].floor).ToArray();
                for (int f = 0; f < grid.floors; f++)
                {
                    int block = Mathf.Min(k - 1, f * k / grid.floors);
                    for (int x = 0; x < grid.nx; x++)
                        for (int z = 0; z < grid.nz; z++)
                            if (grid.state[f, x, z] != SearchGrid.Spot.None) grid.owner[f, x, z] = order[block];
                }
                break;
            }
            case SplitMode.VerticalStrips:
            {
                var order = Enumerable.Range(0, k).OrderBy(i => drones[i].cell.x).ThenBy(i => i).ToArray();
                ForEachSpot(grid, (f, x, z) => grid.owner[f, x, z] = order[Mathf.Min(k - 1, x * k / grid.nx)]);
                break;
            }
            default:
            {
                // Estimated travel cost from each drone's start to every spot.
                var spots = new List<(int f, int x, int z)>();
                ForEachSpot(grid, (f, x, z) => spots.Add((f, x, z)));
                var cost = new float[k, spots.Count];
                for (int d = 0; d < k; d++)
                    for (int i = 0; i < spots.Count; i++)
                        cost[d, i] = Travel(grid, drones[d], spots[i].f, spots[i].x, spots[i].z, climbCost);

                var offset = new float[k];
                var counts = new int[k];
                int rounds = mode == SplitMode.BalancedNearest ? 200 : 1;
                float target = spots.Count / (float)k;
                for (int round = 0; round < rounds; round++)
                {
                    System.Array.Clear(counts, 0, k);
                    for (int i = 0; i < spots.Count; i++)
                    {
                        int best = 0;
                        for (int d = 1; d < k; d++)
                            if (cost[d, i] + offset[d] < cost[best, i] + offset[best]) best = d;
                        var s = spots[i];
                        grid.owner[s.f, s.x, s.z] = best;
                        counts[best]++;
                    }
                    if (mode != SplitMode.BalancedNearest) break;
                    // Make over-loaded drones look further away, under-loaded ones nearer.
                    float worst = 0f;
                    for (int d = 0; d < k; d++)
                    {
                        float excess = (counts[d] - target) / target;
                        worst = Mathf.Max(worst, Mathf.Abs(excess));
                        offset[d] += excess * 8f * Mathf.Pow(0.97f, round);
                    }
                    if (worst < 0.02f) break;
                }
                break;
            }
        }
    }

    /// Straight-line travel estimate (m): across the floor, or to the stair core, up/down, and across.
    public static float Travel(SearchGrid grid, Start from, int f, int x, int z, float climbCost)
    {
        var a = new Vector2(from.cell.x, from.cell.y) * SearchGrid.Cell;
        var b = new Vector2(x, z) * SearchGrid.Cell;
        if (f == from.floor) return Vector2.Distance(a, b);
        var e = grid.StairEntry(0) - grid.origin;
        var stairs = new Vector2(e.x, e.z);
        return Vector2.Distance(a, stairs) + Mathf.Abs(f - from.floor) * climbCost + Vector2.Distance(stairs, b);
    }

    static void ForEachSpot(SearchGrid grid, System.Action<int, int, int> act)
    {
        for (int f = 0; f < grid.floors; f++)
            for (int x = 0; x < grid.nx; x++)
                for (int z = 0; z < grid.nz; z++)
                    if (grid.state[f, x, z] != SearchGrid.Spot.None) act(f, x, z);
    }
}
