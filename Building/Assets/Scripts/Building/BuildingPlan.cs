using System.Collections.Generic;
using UnityEngine;

namespace Drone2.Building
{
    public enum RegionType { Room, Corridor, Stairwell }

    public enum RoomRole { None, Bedroom, LivingRoom, Kitchen, Bathroom, Office, DiningRoom, Storage, Laundry }

    public enum EdgeKind { None, Wall, Door, Window, Entrance }

    /// A rectangle of grid cells on one floor: a room, a corridor or the stairwell.
    public class Region
    {
        public int id;
        public int floor;
        public RegionType type;
        public RectInt rect;
        public RoomRole role;

        public bool IsCorridor => type == RegionType.Corridor;
        public bool IsRoom => type == RegionType.Room;
    }

    /// One storey's grid. Cells hold region ids. Walls, doors and windows live on cell edges:
    /// vEdges[x, z] is the line x between cells (x-1, z) and (x, z);
    /// hEdges[x, z] is the line z between cells (x, z-1) and (x, z).
    public class FloorPlan
    {
        public int floor;
        public int width, depth;
        public int[,] cells;
        public readonly List<Region> regions = new();
        public EdgeKind[,] vEdges;
        public EdgeKind[,] hEdges;

        public FloorPlan(int floor, int width, int depth)
        {
            this.floor = floor;
            this.width = width;
            this.depth = depth;
            cells = new int[width, depth];
            vEdges = new EdgeKind[width + 1, depth];
            hEdges = new EdgeKind[width, depth + 1];
        }

        public Region RegionAt(int x, int z) =>
            x >= 0 && z >= 0 && x < width && z < depth && cells[x, z] >= 0 ? regions[cells[x, z]] : null;

        public IEnumerable<Region> Rooms
        {
            get { foreach (var r in regions) if (r.IsRoom) yield return r; }
        }

        public Region AddRegion(RegionType type, RectInt rect)
        {
            var region = new Region { id = regions.Count, floor = floor, type = type, rect = rect };
            regions.Add(region);
            for (int x = rect.xMin; x < rect.xMax; x++)
                for (int z = rect.yMin; z < rect.yMax; z++)
                    cells[x, z] = region.id;
            return region;
        }

        /// Regions on either side of an edge. A vertical edge has (left, right), a horizontal one (below, above).
        public (Region a, Region b) EdgeSides(bool vertical, int x, int z) =>
            vertical ? (RegionAt(x - 1, z), RegionAt(x, z)) : (RegionAt(x, z - 1), RegionAt(x, z));

        public EdgeKind GetEdge(bool vertical, int x, int z) => vertical ? vEdges[x, z] : hEdges[x, z];

        public void SetEdge(bool vertical, int x, int z, EdgeKind kind)
        {
            if (vertical) vEdges[x, z] = kind;
            else hEdges[x, z] = kind;
        }
    }

    /// A unit edge on the plan, used while choosing doors and windows.
    public struct Edge
    {
        public bool vertical;
        public int x, z;
        public Edge(bool vertical, int x, int z) { this.vertical = vertical; this.x = x; this.z = z; }
    }

    /// Whole-building layout. The main corridor and stairwell are shared by every floor (they stack);
    /// rooms, branch corridors and room roles are rolled per floor.
    public class BuildingPlan
    {
        public const int CorridorWidth = 2;
        public const int StairWidth = 3;
        public const int StairLength = 7;

        public int width, depth, floorCount;
        public RectInt mainCorridor;
        public RectInt stairwell;
        /// True when the stairwell sits north of the main corridor (its door is on its low-z side).
        public bool stairsNorth;
        public readonly List<FloorPlan> floors = new();

        public static BuildingPlan Generate(Rng rng, int width, int depth, int floorCount)
        {
            var plan = new BuildingPlan { width = width, depth = depth, floorCount = floorCount };
            int minZone = StairLength;
            int cz = rng.Range(minZone, depth - minZone - CorridorWidth + 1);
            plan.mainCorridor = new RectInt(0, cz, width, CorridorWidth);
            plan.stairsNorth = rng.Chance(0.5f);
            int sx = rng.Range(3, width - 3 - StairWidth + 1);
            plan.stairwell = plan.stairsNorth
                ? new RectInt(sx, cz + CorridorWidth, StairWidth, StairLength)
                : new RectInt(sx, cz - StairLength, StairWidth, StairLength);

            for (int f = 0; f < floorCount; f++)
                plan.floors.Add(plan.GenerateFloor(rng, f));
            return plan;
        }

        FloorPlan GenerateFloor(Rng rng, int f)
        {
            var fp = new FloorPlan(f, width, depth);
            for (int x = 0; x < width; x++)
                for (int z = 0; z < depth; z++)
                    fp.cells[x, z] = -1;

            fp.AddRegion(RegionType.Corridor, mainCorridor);
            fp.AddRegion(RegionType.Stairwell, stairwell);

            // Zones either side of the main corridor; the stairwell cuts one of them in two.
            var zones = new List<RectInt>();
            var south = new RectInt(0, 0, width, mainCorridor.yMin);
            var north = new RectInt(0, mainCorridor.yMax, width, depth - mainCorridor.yMax);
            foreach (var zone in new[] { south, north })
            {
                bool hasStairs = zone.Overlaps(stairwell);
                if (!hasStairs) { zones.Add(zone); continue; }
                zones.Add(new RectInt(zone.xMin, zone.yMin, stairwell.xMin - zone.xMin, zone.height));
                zones.Add(new RectInt(stairwell.xMax, zone.yMin, zone.xMax - stairwell.xMax, zone.height));
            }

            // Branch corridors run from the main corridor to the outer wall through wide zones.
            int branches = 0;
            var finalZones = new List<RectInt>();
            rng.Shuffle(zones);
            foreach (var zone in zones)
            {
                if (zone.width >= 12 && branches < 2 && rng.Chance(0.55f))
                {
                    int bx = rng.Range(zone.xMin + 5, zone.xMax - 5 - CorridorWidth + 1);
                    fp.AddRegion(RegionType.Corridor, new RectInt(bx, zone.yMin, CorridorWidth, zone.height));
                    finalZones.Add(new RectInt(zone.xMin, zone.yMin, bx - zone.xMin, zone.height));
                    finalZones.Add(new RectInt(bx + CorridorWidth, zone.yMin, zone.xMax - bx - CorridorWidth, zone.height));
                    branches++;
                }
                else finalZones.Add(zone);
            }

            var roomRects = new List<RectInt>();
            foreach (var zone in finalZones)
                if (zone.width > 0 && zone.height > 0)
                    SplitZone(rng, zone, roomRects);
            foreach (var r in roomRects)
                fp.AddRegion(RegionType.Room, r);

            BuildWalls(fp);
            PlaceDoors(rng, fp);
            PlaceWindows(rng, fp);
            AssignRoles(rng, fp);
            return fp;
        }

        static void SplitZone(Rng rng, RectInt r, List<RectInt> rooms)
        {
            // Rooms of roughly 20-60 m², never much longer than twice their width.
            const int minSide = 4;
            int area = r.width * r.height;
            int longSide = Mathf.Max(r.width, r.height), shortSide = Mathf.Min(r.width, r.height);
            bool mustSplit = area > 64 || longSide >= 8 && longSide > 2.2f * shortSide;
            bool maySplit = area > 48 && rng.Chance(0.35f);
            bool canX = r.width >= 2 * minSide, canZ = r.height >= 2 * minSide;
            if (!(mustSplit || maySplit) || !(canX || canZ)) { rooms.Add(r); return; }

            bool splitX = canX && canZ
                ? (r.width >= r.height ? rng.Chance(0.85f) : rng.Chance(0.15f))
                : canX;
            if (splitX)
            {
                int s = rng.Range(minSide, r.width - minSide + 1);
                SplitZone(rng, new RectInt(r.xMin, r.yMin, s, r.height), rooms);
                SplitZone(rng, new RectInt(r.xMin + s, r.yMin, r.width - s, r.height), rooms);
            }
            else
            {
                int s = rng.Range(minSide, r.height - minSide + 1);
                SplitZone(rng, new RectInt(r.xMin, r.yMin, r.width, s), rooms);
                SplitZone(rng, new RectInt(r.xMin, r.yMin + s, r.width, r.height - s), rooms);
            }
        }

        static void BuildWalls(FloorPlan fp)
        {
            for (int x = 0; x <= fp.width; x++)
                for (int z = 0; z < fp.depth; z++)
                    fp.vEdges[x, z] = WallBetween(fp.EdgeSides(true, x, z));
            for (int x = 0; x < fp.width; x++)
                for (int z = 0; z <= fp.depth; z++)
                    fp.hEdges[x, z] = WallBetween(fp.EdgeSides(false, x, z));
        }

        static EdgeKind WallBetween((Region a, Region b) s)
        {
            if (s.a == s.b) return EdgeKind.None;
            if (s.a != null && s.b != null && s.a.IsCorridor && s.b.IsCorridor) return EdgeKind.None;
            return EdgeKind.Wall;
        }

        /// Unit edges on the boundary between two regions (or a region and the outside when b is null).
        public static List<Edge> SharedEdges(FloorPlan fp, Region a, System.Func<Region, bool> other)
        {
            var edges = new List<Edge>();
            var r = a.rect;
            for (int z = r.yMin; z < r.yMax; z++)
            {
                if (other(fp.RegionAt(r.xMin - 1, z))) edges.Add(new Edge(true, r.xMin, z));
                if (other(fp.RegionAt(r.xMax, z))) edges.Add(new Edge(true, r.xMax, z));
            }
            for (int x = r.xMin; x < r.xMax; x++)
            {
                if (other(fp.RegionAt(x, r.yMin - 1))) edges.Add(new Edge(false, x, r.yMin));
                if (other(fp.RegionAt(x, r.yMax))) edges.Add(new Edge(false, x, r.yMax));
            }
            return edges;
        }

        /// True when an edge touches a corner of the rectangle, where a door would sit awkwardly.
        static bool AtCorner(RectInt r, Edge e) =>
            e.vertical ? (e.z == r.yMin || e.z == r.yMax - 1) : (e.x == r.xMin || e.x == r.xMax - 1);

        static Edge PickDoorEdge(Rng rng, Region room, Region neighbour, List<Edge> edges)
        {
            var inner = edges.FindAll(e => !AtCorner(room.rect, e) && (neighbour == null || !AtCorner(neighbour.rect, e)));
            return rng.PickFrom(inner.Count > 0 ? inner : edges);
        }

        void PlaceDoors(Rng rng, FloorPlan fp)
        {
            // Stairwell door, in the middle of its corridor end.
            int doorZ = stairsNorth ? stairwell.yMin : stairwell.yMax;
            fp.SetEdge(false, stairwell.xMin + 1, doorZ, EdgeKind.Door);

            var connected = new HashSet<Region>();
            foreach (var room in fp.Rooms)
            {
                var edges = SharedEdges(fp, room, n => n != null && n.IsCorridor);
                if (edges.Count == 0) continue;
                var e = PickDoorEdge(rng, room, null, edges);
                fp.SetEdge(e.vertical, e.x, e.z, EdgeKind.Door);
                connected.Add(room);
            }

            // Rooms without corridor frontage get a door into a neighbour that is already reachable.
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var room in fp.Rooms)
                {
                    if (connected.Contains(room)) continue;
                    var options = new List<Region>();
                    foreach (var other in fp.Rooms)
                        if (connected.Contains(other) && SharedEdges(fp, room, n => n == other).Count > 0)
                            options.Add(other);
                    if (options.Count == 0) continue;
                    var via = rng.PickFrom(options);
                    var e = PickDoorEdge(rng, room, via, SharedEdges(fp, room, n => n == via));
                    fp.SetEdge(e.vertical, e.x, e.z, EdgeKind.Door);
                    connected.Add(room);
                    changed = true;
                }
            }

            // A few extra doors between neighbouring rooms give more than one route.
            var rooms = new List<Region>(fp.Rooms);
            for (int i = 0; i < rooms.Count; i++)
                for (int j = i + 1; j < rooms.Count; j++)
                {
                    var shared = SharedEdges(fp, rooms[i], n => n == rooms[j]);
                    if (shared.Count < 3 || !rng.Chance(0.15f)) continue;
                    if (shared.Exists(e => fp.GetEdge(e.vertical, e.x, e.z) == EdgeKind.Door)) continue;
                    var e = PickDoorEdge(rng, rooms[i], rooms[j], shared);
                    fp.SetEdge(e.vertical, e.x, e.z, EdgeKind.Door);
                }
        }

        void PlaceWindows(Rng rng, FloorPlan fp)
        {
            foreach (var region in fp.regions)
            {
                if (region.type == RegionType.Stairwell) continue;
                var outer = SharedEdges(fp, region, n => n == null);
                // Group outer edges by side so each side gets at most one window.
                var sides = new Dictionary<(bool, int), List<Edge>>();
                foreach (var e in outer)
                {
                    var key = (e.vertical, e.vertical ? e.x : e.z);
                    if (!sides.TryGetValue(key, out var list)) sides[key] = list = new List<Edge>();
                    list.Add(e);
                }
                foreach (var side in sides.Values)
                {
                    side.Sort((p, q) => p.vertical ? p.z.CompareTo(q.z) : p.x.CompareTo(q.x));
                    if (region.IsCorridor)
                    {
                        // Corridor ends: the main corridor's ends are the ground-floor entrances.
                        bool mainEnd = side[0].vertical && region.id == 0;
                        var kind = mainEnd && fp.floor == 0 ? EdgeKind.Entrance : EdgeKind.Window;
                        foreach (var e in side) fp.SetEdge(e.vertical, e.x, e.z, kind);
                        continue;
                    }
                    if (side.Count < 3 || !rng.Chance(0.85f)) continue;
                    int w = side.Count >= 5 ? 2 : 1;
                    int start = rng.Range(1, side.Count - 1 - w + 1);
                    for (int i = start; i < start + w; i++)
                        fp.SetEdge(side[i].vertical, side[i].x, side[i].z, EdgeKind.Window);
                }
            }
        }

        static void AssignRoles(Rng rng, FloorPlan fp)
        {
            var rooms = new List<Region>(fp.Rooms);
            rng.Shuffle(rooms);
            // Smallest rooms first so a bathroom lands in a small room.
            rooms.Sort((a, b) => (a.rect.width * a.rect.height).CompareTo(b.rect.width * b.rect.height));

            var counts = new Dictionary<RoomRole, int>();
            int Count(RoomRole r) => counts.TryGetValue(r, out var c) ? c : 0;

            for (int i = 0; i < rooms.Count; i++)
            {
                var room = rooms[i];
                int area = room.rect.width * room.rect.height;
                RoomRole role;
                if (i == 0 && rooms.Count >= 3) role = RoomRole.Bathroom;
                else
                {
                    var roles = new[]
                    {
                        RoomRole.Bedroom, RoomRole.LivingRoom, RoomRole.Kitchen, RoomRole.Bathroom,
                        RoomRole.Office, RoomRole.DiningRoom, RoomRole.Storage, RoomRole.Laundry,
                    };
                    bool small = area <= 15;
                    var weights = new float[]
                    {
                        small ? 0.5f : 3f,                                   // bedroom
                        small || Count(RoomRole.LivingRoom) > 0 ? 0f : 2f,   // living room
                        area < 15 || Count(RoomRole.Kitchen) > 0 ? 0f : 2f,  // kitchen
                        Count(RoomRole.Bathroom) > 1 ? 0f : small ? 1.5f : 0.3f,
                        small ? 0.6f : 2f,                                   // office
                        small || Count(RoomRole.DiningRoom) > 0 ? 0f : 1f,   // dining room
                        small ? 1.5f : 0.2f,                                 // storage
                        Count(RoomRole.Laundry) > 0 ? 0f : small ? 1f : 0.2f,
                    };
                    // Roles already on this floor become less likely, for variety.
                    for (int k = 0; k < roles.Length; k++) weights[k] /= 1 + Count(roles[k]);
                    role = roles[rng.Weighted(weights)];
                }
                room.role = role;
                counts[role] = Count(role) + 1;
            }
        }
    }
}
