using Drone2.Building;
using UnityEngine;

/// The search area of one generated building: a grid of spots (0.5 m apart) on every floor at flight
/// height, who has to search each spot, and whether it has been seen. The drones know only what is
/// known from outside: the footprint, the floor heights and where the stair core is (from the fire plan).
/// Interior walls and furniture they find with sonar.
public class SearchGrid
{
    public const float Cell = 0.5f;

    public enum Spot : byte
    {
        None,         // not part of the search (outer wall line, stair core)
        Unseen,
        Seen,
        Solid,        // never seen; believed blocked (inside a wall or furniture)
        Unreachable,  // never seen; free but no route to it (closed off, or by a fire)
    }

    public readonly int nx, nz, floors;
    public readonly float floorHeight, altitude;
    public readonly Vector3 origin;      // world position of building-local (0, 0, 0)
    public readonly RectInt stairwell;
    public readonly bool stairsNorth;
    public readonly Spot[,,] state;      // [floor, x, z]
    public readonly int[,,] owner;       // drone index that must search the spot
    public readonly int[,,] seenBy;
    public readonly int targets;

    public SearchGrid(BuildingGenerator gen, float altitude)
    {
        floors = gen.floors;
        floorHeight = gen.floorHeight;
        this.altitude = altitude;
        origin = gen.Root.position;
        stairwell = gen.Plan.stairwell;
        stairsNorth = gen.Plan.stairsNorth;
        nx = Mathf.RoundToInt(gen.width / Cell) + 1;
        nz = Mathf.RoundToInt(gen.depth / Cell) + 1;
        state = new Spot[floors, nx, nz];
        owner = new int[floors, nx, nz];
        seenBy = new int[floors, nx, nz];
        for (int f = 0; f < floors; f++)
            for (int x = 0; x < nx; x++)
                for (int z = 0; z < nz; z++)
                {
                    owner[f, x, z] = -1;
                    seenBy[f, x, z] = -1;
                    if (x == 0 || z == 0 || x == nx - 1 || z == nz - 1 || InStairwell(x, z)) continue;
                    state[f, x, z] = Spot.Unseen;
                    targets++;
                }
    }

    public bool InStairwell(int x, int z)
    {
        float lx = x * Cell, lz = z * Cell;
        return lx >= stairwell.xMin && lx <= stairwell.xMax && lz >= stairwell.yMin && lz <= stairwell.yMax;
    }

    public bool InInterior(int x, int z) => x > 0 && z > 0 && x < nx - 1 && z < nz - 1;

    public Vector3 World(int f, int x, int z) => origin + new Vector3(x * Cell, f * floorHeight + altitude, z * Cell);
    public Vector3 World(int f, Vector2Int c) => World(f, c.x, c.y);

    public Vector2Int CellOf(Vector3 world)
    {
        var l = world - origin;
        return new Vector2Int(Mathf.RoundToInt(l.x / Cell), Mathf.RoundToInt(l.z / Cell));
    }

    /// Floor whose storey contains this height (from its floor up to the next floor).
    public int StoreyOf(float worldY) => Mathf.Clamp(Mathf.FloorToInt((worldY - origin.y + 0.5f) / floorHeight), 0, floors - 1);

    /// Floor whose flight height is nearest to this height (continuous, for drawing climbs).
    public float FloorAt(float worldY) => (worldY - origin.y - altitude) / floorHeight;

    /// Sonar belief layer (cell index in y) holding the flight height of a floor.
    public int BeliefLayer(int f) => Mathf.RoundToInt((f * floorHeight + altitude) / Cell);

    public int Count(Spot s, int droneOwner = -2)
    {
        int n = 0;
        foreach (var f in Floors())
            for (int x = 0; x < nx; x++)
                for (int z = 0; z < nz; z++)
                    if (state[f, x, z] == s && (droneOwner == -2 || owner[f, x, z] == droneOwner)) n++;
        return n;
    }

    public int CountOwned(int drone)
    {
        int n = 0;
        foreach (var f in Floors())
            for (int x = 0; x < nx; x++)
                for (int z = 0; z < nz; z++)
                    if (state[f, x, z] != Spot.None && owner[f, x, z] == drone) n++;
        return n;
    }

    System.Collections.Generic.IEnumerable<int> Floors()
    {
        for (int f = 0; f < floors; f++) yield return f;
    }

    // ---- Stair core ---------------------------------------------------------------------------------

    /// A point in the stairwell: u across it (0..3 m), v from its door end (0..7 m), h above floor f.
    public Vector3 Stair(int f, float u, float v, float h) =>
        origin + new Vector3(stairwell.xMin + u, f * floorHeight + h, stairsNorth ? stairwell.yMin + v : stairwell.yMax - v);

    /// Where a drone waits just inside the stairwell door, at flight height.
    public Vector3 StairEntry(int f) => Stair(f, 1.5f, 0.5f, altitude);

    /// Route from floor f's stair entry to floor f+1's: over flight A, across the mid landing, back over
    /// flight B and out over the upper floor's landing. Heights keep about 1.1 m above the steps and stay
    /// clear of the flights of the floor above (switchback stairs, see BuildingGenerator.BuildStairs).
    public System.Collections.Generic.List<Vector3> ClimbUp(int f) => new()
    {
        StairEntry(f),
        Stair(f, 0.75f, 1.0f, altitude),
        Stair(f, 0.75f, 6.45f, altitude + 1.2f),
        Stair(f, 2.25f, 6.45f, altitude + 1.2f),
        Stair(f, 2.25f, 2.0f, floorHeight + altitude),
        Stair(f, 2.25f, 1.0f, floorHeight + altitude),
        StairEntry(f + 1),
    };
}
