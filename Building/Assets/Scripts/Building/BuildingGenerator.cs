using System.Collections.Generic;
using UnityEngine;

namespace Drone2.Building
{
    /// Generates a random low-poly multi-storey building: corridors, rooms with roles and furniture,
    /// hallway decorations, a stairwell, and people trapped in rooms. A new seed is rolled on every Play.
    public class BuildingGenerator : MonoBehaviour
    {
        [Header("Seed")]
        [Tooltip("Roll a new seed every time Play starts. Turn off to rebuild the same building from 'seed'.")]
        public bool randomSeedOnPlay = true;
        public int seed = 12345;

        [Header("Size (metres; one grid cell = 1 m)")]
        public int width = 25;
        public int depth = 20;
        [Range(1, 6)] public int floors = 3;
        public float floorHeight = 3f;

        [Header("People")]
        public int minPeople = 4;
        public int maxPeople = 5;

        [Header("Fire and smoke")]
        public bool fireAndSmoke = true;
        [Tooltip("Roughly the share of rooms on the fire's floor that burn (less on other floors).")]
        [Range(0f, 1f)] public float fireDensity = 0.3f;
        [Tooltip("How thick the smoke layers and plumes are.")]
        [Range(0f, 1f)] public float smokeDensity = 0.75f;

        [Header("Materials and models")]
        public Material baseMaterial;
        public Material glassMaterial;
        [Tooltip("Uniform scale applied to the Kenney furniture models.")]
        public float furnitureScale = 0.2f;
        [Tooltip("Yaw that turns the furniture models' fronts to +Z.")]
        public float furnitureYaw = 180f;

        const float SlabTop = 0.04f;      // floor covering thickness
        const float SlabDepth = 0.2f;     // whole slab (covering + structure)
        const float WallHalf = RegionSpace.WallHalf;
        const float ExteriorSkin = 0.15f; // extra thickness outside the outer wall line
        const float DoorHeight = 2.1f;
        const float SillHeight = 0.9f;

        public BuildingPlan Plan { get; private set; }
        public Transform Root { get; private set; }
        public readonly List<Transform> floorRoots = new();
        public Transform RoofRoot { get; private set; }
        public readonly List<RegionSpace> roomSpaces = new();
        public readonly List<RegionSpace> hallSpaces = new();
        public readonly List<TrappedPerson> people = new();

        /// Raised after every rebuild.
        public event System.Action<BuildingGenerator> Generated;

        Palette pal;
        Rng rng;
        Scheme scheme;

        void Start()
        {
            if (randomSeedOnPlay) seed = Random.Range(1, int.MaxValue);
            Generate();
        }

        [ContextMenu("Generate")]
        public void Generate()
        {
            Clear();
            rng = new Rng(seed);
            Plan = BuildingPlan.Generate(rng, width, depth, floors);
            pal = new Palette(baseMaterial, glassMaterial);
            scheme = new Scheme(rng);
            var kit = new PropKit { scale = furnitureScale, yawFix = furnitureYaw };
            var furnisher = new Furnisher(kit, pal, rng);

            Root = new GameObject("Building").transform;
            Root.SetParent(transform, false);
            Root.localPosition = new Vector3(-width / 2f, 0, -depth / 2f);

            BuildSite();
            for (int f = 0; f < floors; f++)
            {
                var floorRoot = new GameObject($"Floor {f + 1}").transform;
                floorRoot.SetParent(Root, false);
                floorRoots.Add(floorRoot);
                BuildFloor(Plan.floors[f], floorRoot);
            }
            BuildRoof();

            // Furniture and decorations need the shell's colliders in place for their raycasts.
            Physics.SyncTransforms();
            for (int f = 0; f < floors; f++)
            {
                var fp = Plan.floors[f];
                float y0 = f * floorHeight, ceiling = y0 + floorHeight - SlabDepth;
                foreach (var region in fp.regions)
                {
                    if (region.type == RegionType.Stairwell) continue;
                    var regionRoot = new GameObject(region.IsRoom ? $"Room {region.id} ({region.role})" : $"Hallway {region.id}").transform;
                    regionRoot.SetParent(floorRoots[f], false);
                    var space = new RegionSpace(region, fp, regionRoot, y0, ceiling);
                    if (region.IsRoom)
                    {
                        furnisher.FurnishRoom(space);
                        roomSpaces.Add(space);
                    }
                    else
                    {
                        furnisher.DecorateHallway(space, region.id == 0);
                        hallSpaces.Add(space);
                    }
                }
            }

            people.AddRange(People.Populate(pal, rng, roomSpaces, rng.Range(minPeople, maxPeople + 1)));

            if (fireAndSmoke) FireAndSmoke.Build(this, pal, seed);

            if (!Application.isPlaying)
                foreach (var t in Root.GetComponentsInChildren<Transform>(true))
                    t.gameObject.hideFlags = HideFlags.DontSave;

            Debug.Log($"Building generated (seed {seed}): {floors} floors, {roomSpaces.Count} rooms, {people.Count} people.");
            Generated?.Invoke(this);
        }

        [ContextMenu("Generate New Seed")]
        public void GenerateNewSeed()
        {
            seed = Random.Range(1, int.MaxValue);
            Generate();
        }

        [ContextMenu("Clear")]
        public void Clear()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
                if (transform.GetChild(i).name == "Building")
                    RegionSpace.Kill(transform.GetChild(i).gameObject);
            Root = null;
            RoofRoot = null;
            floorRoots.Clear();
            roomSpaces.Clear();
            hallSpaces.Clear();
            people.Clear();
        }

        /// Shows floors up to and including `topFloor` (0-based); -1 shows everything including the roof.
        public void ShowFloorsUpTo(int topFloor)
        {
            for (int f = 0; f < floorRoots.Count; f++)
                floorRoots[f].gameObject.SetActive(topFloor < 0 || f <= topFloor);
            if (RoofRoot != null) RoofRoot.gameObject.SetActive(topFloor < 0);
        }

        /// World-space bounds of a region's interior.
        public Bounds RegionBounds(Region region)
        {
            var r = region.rect;
            float y0 = region.floor * floorHeight;
            var min = Root.TransformPoint(new Vector3(r.xMin, y0, r.yMin));
            var max = Root.TransformPoint(new Vector3(r.xMax, y0 + floorHeight - SlabDepth, r.yMax));
            var b = new Bounds((min + max) / 2, Vector3.zero);
            b.Encapsulate(min);
            b.Encapsulate(max);
            return b;
        }

        // ---- Shell --------------------------------------------------------------------------

        void BuildSite()
        {
            var site = new GameObject("Site").transform;
            site.SetParent(Root, false);
            float pad = 2.5f;
            pal.Box(site, new Vector3(-pad, -0.35f, -pad), new Vector3(width + pad, -0.05f, depth + pad), scheme.sidewalk, "Sidewalk");
            pal.Box(site, new Vector3(-60, -0.45f, -60), new Vector3(width + 60, -0.35f, depth + 60), scheme.ground, "Ground");
        }

        /// The hole in floor slabs above the stairs, in grid coordinates.
        Rect StairHole()
        {
            var s = Plan.stairwell;
            return Plan.stairsNorth
                ? Rect.MinMaxRect(s.xMin, s.yMin + 1.5f, s.xMax, s.yMax)
                : Rect.MinMaxRect(s.xMin, s.yMin, s.xMax, s.yMax - 1.5f);
        }

        static IEnumerable<Rect> Minus(Rect outer, Rect hole)
        {
            if (!outer.Overlaps(hole)) { yield return outer; yield break; }
            if (hole.yMin > outer.yMin) yield return Rect.MinMaxRect(outer.xMin, outer.yMin, outer.xMax, hole.yMin);
            if (hole.yMax < outer.yMax) yield return Rect.MinMaxRect(outer.xMin, hole.yMax, outer.xMax, outer.yMax);
            float y0 = Mathf.Max(outer.yMin, hole.yMin), y1 = Mathf.Min(outer.yMax, hole.yMax);
            if (hole.xMin > outer.xMin) yield return Rect.MinMaxRect(outer.xMin, y0, hole.xMin, y1);
            if (hole.xMax < outer.xMax) yield return Rect.MinMaxRect(hole.xMax, y0, outer.xMax, y1);
        }

        static Rect ToRect(RectInt r) => new(r.x, r.y, r.width, r.height);

        float SlabBottom(int f) => f == 0 ? -0.3f : f * floorHeight - SlabDepth;

        void Slab(Transform parent, Rect r, float y0, float y1, Color c, string name)
            => pal.Box(parent, new Vector3(r.xMin, y0, r.yMin), new Vector3(r.xMax, y1, r.yMax), c, name);

        void BuildFloor(FloorPlan fp, Transform floorRoot)
        {
            int f = fp.floor;
            float y0 = f * floorHeight;
            var shell = new GameObject("Shell").transform;
            shell.SetParent(floorRoot, false);
            var hole = StairHole();
            var footprint = new Rect(0, 0, width, depth);

            // Structural slab (its underside is the ceiling of the floor below), then per-region floor coverings.
            // The outer walls' exterior skin extends down over the slab edge.
            if (f == 0) Slab(shell, footprint, SlabBottom(0), y0 - SlabTop, scheme.ceiling, "Slab");
            else foreach (var r in Minus(footprint, hole)) Slab(shell, r, SlabBottom(f), y0 - SlabTop, scheme.ceiling, "Slab");

            foreach (var region in fp.regions)
            {
                var rect = ToRect(region.rect);
                var color = scheme.Floor(region);
                if (region.type == RegionType.Stairwell && f > 0)
                    foreach (var r in Minus(rect, hole)) Slab(shell, r, y0 - SlabTop, y0, color, "Floor");
                else Slab(shell, rect, y0 - SlabTop, y0, color, "Floor");
            }

            BuildWalls(fp, shell, y0);
            BuildStairs(fp, shell, y0);
        }

        void BuildRoof()
        {
            RoofRoot = new GameObject("Roof").transform;
            RoofRoot.SetParent(Root, false);
            float y = floors * floorHeight;
            float e = ExteriorSkin;
            Slab(RoofRoot, new Rect(-e, -e, width + 2 * e, depth + 2 * e), y - SlabDepth, y + 0.05f, scheme.roof, "RoofSlab");
            // Parapet.
            float h = 0.6f, t = 0.25f;
            pal.Box(RoofRoot, new Vector3(-e, y, -e), new Vector3(width + e, y + h, -e + t), scheme.exterior, "Parapet");
            pal.Box(RoofRoot, new Vector3(-e, y, depth + e - t), new Vector3(width + e, y + h, depth + e), scheme.exterior, "Parapet");
            pal.Box(RoofRoot, new Vector3(-e, y, -e), new Vector3(-e + t, y + h, depth + e), scheme.exterior, "Parapet");
            pal.Box(RoofRoot, new Vector3(width + e - t, y, -e), new Vector3(width + e, y + h, depth + e), scheme.exterior, "Parapet");
            // Stair access hut over the stairwell and a couple of rooftop units.
            var s = Plan.stairwell;
            pal.Box(RoofRoot, new Vector3(s.xMin, y, s.yMin), new Vector3(s.xMax, y + 2.4f, s.yMax), scheme.exterior, "StairHut");
            pal.Box(RoofRoot, new Vector3(s.xMin - 0.2f, y + 2.4f, s.yMin - 0.2f), new Vector3(s.xMax + 0.2f, y + 2.55f, s.yMax + 0.2f), scheme.roof, "StairHutRoof");
            for (int i = 0; i < 2; i++)
            {
                var p = new Vector2(rng.Range(2f, width - 4f), rng.Range(2f, depth - 4f));
                if (new Rect(s.x - 2, s.y - 2, s.width + 4, s.height + 4).Contains(p)) continue;
                pal.Box(RoofRoot, new Vector3(p.x, y, p.y), new Vector3(p.x + 1.6f, y + 1.0f, p.y + 1.2f), new Color(0.7f, 0.72f, 0.74f), "RooftopUnit");
                pal.Box(RoofRoot, new Vector3(p.x + 0.3f, y + 1.0f, p.y + 0.2f), new Vector3(p.x + 1.1f, y + 1.1f, p.y + 1.0f), new Color(0.3f, 0.3f, 0.32f), "Fan");
            }
        }

        // ---- Walls --------------------------------------------------------------------------

        struct FaceKey
        {
            public EdgeKind kind;
            public int region; // -1 = outside
            public bool Same(FaceKey o) => kind == o.kind && region == o.region;
        }

        void BuildWalls(FloorPlan fp, Transform parent, float y0)
        {
            float top = y0 + floorHeight - SlabDepth;

            // Vertical lines (constant x), walked along z.
            for (int x = 0; x <= fp.width; x++)
                for (int side = 0; side < 2; side++)
                    WalkLine(fp, parent, y0, top, true, x, side, fp.depth);
            // Horizontal lines (constant z), walked along x.
            for (int z = 0; z <= fp.depth; z++)
                for (int side = 0; side < 2; side++)
                    WalkLine(fp, parent, y0, top, false, z, side, fp.width);
        }

        /// Walks one wall line and emits merged runs for one face (side 0 = low side, 1 = high side).
        void WalkLine(FloorPlan fp, Transform parent, float y0, float top, bool vertical, int line, int side, int length)
        {
            FaceKey Key(int i)
            {
                int x = vertical ? line : i, z = vertical ? i : line;
                var kind = fp.GetEdge(vertical, x, z);
                var (a, b) = fp.EdgeSides(vertical, x, z);
                var reg = side == 0 ? a : b;
                return new FaceKey { kind = kind, region = reg?.id ?? -1 };
            }

            int start = 0;
            var key = Key(0);
            for (int i = 1; i <= length; i++)
            {
                var next = i < length ? Key(i) : new FaceKey { kind = EdgeKind.None, region = -2 };
                if (i < length && next.Same(key)) continue;
                if (key.kind != EdgeKind.None)
                {
                    bool openBefore = start > 0 && IsOpening(Key(start - 1).kind);
                    bool openAfter = i < length && IsOpening(next.kind);
                    EmitRun(fp, parent, y0, top, vertical, line, side, start, i, key, openBefore, openAfter);
                }
                start = i;
                key = next;
            }
        }

        static bool IsOpening(EdgeKind k) => k is EdgeKind.Door or EdgeKind.Window or EdgeKind.Entrance;

        void EmitRun(FloorPlan fp, Transform parent, float y0, float top, bool vertical, int line, int side,
            int from, int to, FaceKey key, bool openBefore, bool openAfter)
        {
            bool outside = key.region < 0;
            float thick = outside ? ExteriorSkin + WallHalf : WallHalf;
            // Face slab spans [line - thick, line] on the low side, [line, line + thick] on the high side.
            float n0 = side == 0 ? line - thick : line;
            float n1 = side == 0 ? line : line + thick;
            Color color = outside ? scheme.exterior : scheme.Wall(fp.regions[key.region]);

            // Solid walls overlap neighbours at the ends to close corners, but never intrude into openings.
            float ext = outside ? ExteriorSkin + WallHalf : WallHalf;
            float a0 = from - (key.kind == EdgeKind.Wall && !openBefore ? ext : 0);
            float a1 = to + (key.kind == EdgeKind.Wall && !openAfter ? ext : 0);

            // Exterior skin also covers the slab edge below this floor.
            float bottom = outside ? SlabBottom(fp.floor) : y0;

            void Piece(float ya, float yb, Color c, string name)
            {
                var min = vertical ? new Vector3(n0, ya, a0) : new Vector3(a0, ya, n0);
                var max = vertical ? new Vector3(n1, yb, a1) : new Vector3(a1, yb, n1);
                pal.Box(parent, min, max, c, name);
            }

            // Trim stands slightly proud of the face.
            float t0 = side == 0 ? n0 - 0.02f : n0, t1 = side == 1 ? n1 + 0.02f : n1;
            switch (key.kind)
            {
                case EdgeKind.Wall:
                    Piece(bottom, top, color, "Wall");
                    break;
                case EdgeKind.Door:
                case EdgeKind.Entrance:
                    Piece(y0 + DoorHeight, top, color, key.kind == EdgeKind.Door ? "DoorLintel" : "EntranceLintel");
                    if (outside && fp.floor > 0) Piece(bottom, y0, color, "SlabEdge");
                    DoorTrim(parent, vertical, t0, t1, a0, a1, y0);
                    break;
                case EdgeKind.Window:
                    Piece(bottom, y0 + SillHeight, color, "WindowSill");
                    Piece(y0 + DoorHeight, top, color, "WindowLintel");
                    if (side == 0)
                    {
                        // One glass pane per window, on the wall line.
                        var min = vertical ? new Vector3(line - 0.02f, y0 + SillHeight, a0) : new Vector3(a0, y0 + SillHeight, line - 0.02f);
                        var max = vertical ? new Vector3(line + 0.02f, y0 + DoorHeight, a1) : new Vector3(a1, y0 + DoorHeight, line + 0.02f);
                        var glass = pal.Box(parent, min, max, Color.white, "Glass");
                        if (pal.glass != null) glass.GetComponent<Renderer>().sharedMaterial = pal.glass;
                    }
                    WindowTrim(parent, vertical, t0, t1, a0, a1, y0);
                    break;
            }
        }

        void DoorTrim(Transform parent, bool vertical, float n0, float n1, float a0, float a1, float y0)
        {
            const float w = 0.07f;
            void Box(float b0, float b1, float ya, float yb)
            {
                var min = vertical ? new Vector3(n0, ya, b0) : new Vector3(b0, ya, n0);
                var max = vertical ? new Vector3(n1, yb, b1) : new Vector3(b1, yb, n1);
                pal.Box(parent, min, max, scheme.trim, "DoorTrim", collider: false);
            }
            Box(a0, a0 + w, y0, y0 + DoorHeight);
            Box(a1 - w, a1, y0, y0 + DoorHeight);
            Box(a0, a1, y0 + DoorHeight - w, y0 + DoorHeight);
        }

        void WindowTrim(Transform parent, bool vertical, float n0, float n1, float a0, float a1, float y0)
        {
            const float w = 0.06f;
            void Box(float b0, float b1, float ya, float yb)
            {
                var min = vertical ? new Vector3(n0, ya, b0) : new Vector3(b0, ya, n0);
                var max = vertical ? new Vector3(n1, yb, b1) : new Vector3(b1, yb, n1);
                pal.Box(parent, min, max, Color.white * 0.95f, "WindowFrame", collider: false);
            }
            Box(a0, a0 + w, y0 + SillHeight, y0 + DoorHeight);
            Box(a1 - w, a1, y0 + SillHeight, y0 + DoorHeight);
            Box(a0, a1, y0 + SillHeight, y0 + SillHeight + w);
            Box(a0, a1, y0 + DoorHeight - w, y0 + DoorHeight);
            // Mullion in the middle of wide windows.
            if (a1 - a0 > 1.5f) Box((a0 + a1) / 2 - w / 2, (a0 + a1) / 2 + w / 2, y0 + SillHeight, y0 + DoorHeight);
        }

        // ---- Stairs -------------------------------------------------------------------------

        /// A box in stairwell-local coordinates: u across the stairwell (0..3), v from its door end (0..7).
        void StairBox(Transform parent, float u0, float u1, float v0, float v1, float y0, float y1, Color c, string name)
        {
            var s = Plan.stairwell;
            float x0 = s.xMin + u0, x1 = s.xMin + u1;
            float z0, z1;
            if (Plan.stairsNorth) { z0 = s.yMin + v0; z1 = s.yMin + v1; }
            else { z0 = s.yMax - v1; z1 = s.yMax - v0; }
            pal.Box(parent, new Vector3(x0, y0, z0), new Vector3(x1, y1, z1), c, name);
        }

        /// Switchback stairs: flight A climbs half a storey away from the door to a mid landing,
        /// flight B climbs back to the next floor's landing by the door. A divider wall separates them.
        void BuildStairs(FloorPlan fp, Transform parent, float y0)
        {
            int f = fp.floor;
            float half = floorHeight / 2f;
            const int steps = 10;
            const float vStart = 1.5f, vEnd = 6.0f;
            float run = (vEnd - vStart) / steps, rise = half / steps;
            float top = y0 + floorHeight - SlabDepth;
            float lane = BuildingPlan.StairWidth / 2f;

            // Divider between the two flights.
            StairBox(parent, lane - 0.05f, lane + 0.05f, vStart, vEnd, y0, f == floors - 1 ? top : y0 + floorHeight, scheme.stairs, "StairDivider");

            if (f < floors - 1)
            {
                for (int i = 0; i < steps; i++)
                {
                    float stepTop = y0 + (i + 1) * rise;
                    StairBox(parent, WallHalf, lane - 0.05f, vStart + i * run, vStart + (i + 1) * run,
                        Mathf.Max(y0, stepTop - 0.3f), stepTop, scheme.stairs, "StepA");
                    float stepTopB = y0 + half + (i + 1) * rise;
                    StairBox(parent, lane + 0.05f, BuildingPlan.StairWidth - WallHalf, vEnd - (i + 1) * run, vEnd - i * run,
                        stepTopB - 0.3f, stepTopB, scheme.stairs, "StepB");
                }
                StairBox(parent, WallHalf, BuildingPlan.StairWidth - WallHalf, vEnd, BuildingPlan.StairLength - WallHalf,
                    y0 + half - 0.2f, y0 + half, scheme.stairs, "MidLanding");
                // Handrail on the divider, both sides.
                StairBox(parent, lane - 0.1f, lane - 0.05f, vStart, vEnd, y0 + 0.9f, y0 + half + 0.95f, scheme.trim, "Handrail");
            }
            else
            {
                // Top floor: guard rail where the hole meets the landing, on the side without arriving stairs.
                StairBox(parent, WallHalf, lane, vStart - 0.05f, vStart + 0.05f, y0, y0 + 1.0f, scheme.stairs, "GuardRail");
            }

            // Light at the landing, exit sign over the door on the corridor side.
            var s = Plan.stairwell;
            float vz = Plan.stairsNorth ? s.yMin + 0.8f : s.yMax - 0.8f;
            Furnisher.AddLight(parent, new Vector3(s.center.x, top - 0.3f, vz), 6f, 1.5f);
            float doorZ = Plan.stairsNorth ? s.yMin - WallHalf : s.yMax + WallHalf;
            float yaw = Plan.stairsNorth ? 180f : 0f;
            Decor.ExitSign(pal, parent, new Vector3(s.xMin + 1.5f, y0 + DoorHeight + 0.25f, doorZ), yaw);
        }

        // ---- Colours ------------------------------------------------------------------------

        /// Colour choices for one building: exterior, per-room paint and floor finishes.
        class Scheme
        {
            public Color exterior, roof, ceiling, sidewalk, ground, trim, stairs, hallWall, hallFloor;
            readonly Dictionary<int, Color> wall = new(), floor = new();
            readonly Rng rng;

            public Scheme(Rng rng)
            {
                this.rng = rng;
                exterior = rng.Pick(new Color(0.62f, 0.33f, 0.26f), new Color(0.78f, 0.72f, 0.6f), new Color(0.55f, 0.57f, 0.6f), new Color(0.85f, 0.82f, 0.75f));
                roof = new Color(0.35f, 0.35f, 0.37f);
                ceiling = new Color(0.92f, 0.92f, 0.9f);
                sidewalk = new Color(0.68f, 0.68f, 0.66f);
                ground = new Color(0.36f, 0.5f, 0.28f);
                trim = rng.Pick(new Color(0.45f, 0.3f, 0.18f), new Color(0.9f, 0.9f, 0.88f), new Color(0.25f, 0.22f, 0.2f));
                stairs = new Color(0.62f, 0.62f, 0.6f);
                hallWall = rng.Pick(new Color(0.88f, 0.84f, 0.74f), new Color(0.82f, 0.85f, 0.82f), new Color(0.9f, 0.88f, 0.84f));
                hallFloor = rng.Pick(new Color(0.45f, 0.18f, 0.16f), new Color(0.22f, 0.28f, 0.42f), new Color(0.4f, 0.4f, 0.42f), new Color(0.55f, 0.4f, 0.25f));
            }

            Color Jitter(Color c)
            {
                float k = rng.Range(0.94f, 1.04f);
                return new Color(c.r * k, c.g * k, c.b * k);
            }

            public Color Wall(Region r)
            {
                if (r.type == RegionType.Stairwell) return new Color(0.75f, 0.75f, 0.73f);
                if (r.IsCorridor) return hallWall;
                if (wall.TryGetValue(r.id, out var c)) return c;
                c = r.role switch
                {
                    RoomRole.Bedroom => rng.Pick(new Color(0.7f, 0.8f, 0.9f), new Color(0.78f, 0.88f, 0.76f), new Color(0.84f, 0.78f, 0.9f), new Color(0.95f, 0.85f, 0.8f)),
                    RoomRole.LivingRoom => rng.Pick(new Color(0.93f, 0.86f, 0.72f), new Color(0.85f, 0.75f, 0.62f), new Color(0.75f, 0.83f, 0.8f)),
                    RoomRole.Kitchen => rng.Pick(new Color(0.96f, 0.92f, 0.7f), new Color(0.92f, 0.94f, 0.88f)),
                    RoomRole.Bathroom => rng.Pick(new Color(0.75f, 0.9f, 0.92f), new Color(0.93f, 0.95f, 0.96f)),
                    RoomRole.Office => rng.Pick(new Color(0.85f, 0.86f, 0.88f), new Color(0.8f, 0.84f, 0.9f)),
                    RoomRole.DiningRoom => rng.Pick(new Color(0.78f, 0.82f, 0.68f), new Color(0.9f, 0.8f, 0.7f)),
                    _ => new Color(0.78f, 0.78f, 0.76f),
                };
                return wall[r.id] = Jitter(c);
            }

            public Color Floor(Region r)
            {
                if (r.type == RegionType.Stairwell) return stairs;
                if (r.IsCorridor) return hallFloor;
                if (floor.TryGetValue(r.id, out var c)) return c;
                var wood = rng.Pick(new Color(0.6f, 0.42f, 0.27f), new Color(0.72f, 0.55f, 0.36f), new Color(0.48f, 0.33f, 0.22f));
                c = r.role switch
                {
                    RoomRole.Kitchen or RoomRole.Bathroom or RoomRole.Laundry =>
                        rng.Pick(new Color(0.85f, 0.85f, 0.82f), new Color(0.6f, 0.62f, 0.64f), new Color(0.78f, 0.72f, 0.62f)),
                    RoomRole.Storage => new Color(0.55f, 0.55f, 0.53f),
                    RoomRole.Bedroom => rng.Chance(0.5f) ? wood : rng.Pick(new Color(0.55f, 0.6f, 0.68f), new Color(0.7f, 0.62f, 0.55f)),
                    _ => wood,
                };
                return floor[r.id] = c;
            }
        }
    }
}
