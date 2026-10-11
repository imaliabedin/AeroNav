using System.Collections.Generic;
using Drone2.Building;
using UnityEngine;

/// Spreads the building's fire over time. The building's own fires (from FireAndSmoke) are the seeds and
/// the look: new fires are copies of them, so flames, embers, smoke plumes and fire light match exactly.
/// Fire moves cell by cell (1 m) through rooms fastest, more slowly along corridors, only through open
/// edges and doorways (never through walls), occasionally to the floor above, and readily up the stairwell.
public class FireSpread
{
    [System.Serializable]
    public class Settings
    {
        [Tooltip("Seconds between spread steps.")]
        public float interval = 2f;
        [Tooltip("Chance per step that a burning cell sets a neighbouring room cell alight.")]
        public float roomChance = 0.03f;
        [Tooltip("The same into a corridor (less to burn there).")]
        public float corridorChance = 0.012f;
        [Tooltip("Chance per step that a burning cell sets the cell above it alight (through the floor).")]
        public float upChance = 0.0008f;
        [Tooltip("The same inside the stairwell, which acts like a chimney.")]
        public float stairUpChance = 0.02f;
        [Tooltip("At most this many fire effects are added (each spawns particles and maybe a light).")]
        public int maxEffects = 160;
    }

    readonly BuildingGenerator gen;
    readonly SearchCoordinator coord;
    readonly Settings s;
    readonly System.Random rng;
    readonly bool[,,] burning;          // [floor, x, z] plan cells
    readonly List<Vector3Int> front = new();
    readonly HashSet<Vector3Int> effectBlocks = new();
    readonly List<GameObject> templates = new(), litTemplates = new();
    float timer;
    int effects, lights;

    public int Burning { get; private set; }
    public int Cells { get; }
    public float Share => Cells == 0 ? 0f : Burning / (float)Cells;
    public bool IsBurning(int f, int x, int z) =>
        f >= 0 && f < gen.floors && x >= 0 && z >= 0 && x < gen.width && z < gen.depth && burning[f, x, z];

    public FireSpread(BuildingGenerator gen, SearchCoordinator coord, Settings settings, int seed)
    {
        this.gen = gen;
        this.coord = coord;
        s = settings;
        rng = new System.Random(seed);
        burning = new bool[gen.floors, gen.width, gen.depth];
        Cells = gen.floors * gen.width * gen.depth;

        foreach (var t in gen.Root.GetComponentsInChildren<Transform>(true))
        {
            if (t.name != "Fire") continue;
            (t.GetComponentInChildren<Light>(true) != null ? litTemplates : templates).Add(t.gameObject);
            var c = PlanCell(t.position);
            if (c.HasValue) Ignite(c.Value, effect: false);
        }
        if (templates.Count == 0) templates.AddRange(litTemplates);
    }

    /// Plan cell (floor, x, z) holding a world point, if inside the building.
    Vector3Int? PlanCell(Vector3 world)
    {
        var l = gen.Root.InverseTransformPoint(world);
        int f = Mathf.Clamp(Mathf.FloorToInt((l.y + 0.5f) / gen.floorHeight), 0, gen.floors - 1);
        int x = Mathf.FloorToInt(l.x), z = Mathf.FloorToInt(l.z);
        if (x < 0 || z < 0 || x >= gen.width || z >= gen.depth) return null;
        return new Vector3Int(x, f, z);
    }

    public void Step(float dt)
    {
        if ((timer += dt) < s.interval) return;
        timer = 0f;
        var plan = gen.Plan;
        var next = new List<Vector3Int>();
        var still = new List<Vector3Int>();
        foreach (var c in front)
        {
            bool canSpread = false;
            var fp = plan.floors[c.y];
            foreach (var (dx, dz) in Sides)
            {
                int nx = c.x + dx, nz = c.z + dz;
                if (nx < 0 || nz < 0 || nx >= gen.width || nz >= gen.depth || burning[c.y, nx, nz]) continue;
                if (!Open(fp, c.x, c.z, dx, dz)) continue;
                canSpread = true;
                var region = fp.RegionAt(nx, nz);
                float p = region != null && region.IsRoom ? s.roomChance : s.corridorChance;
                if (rng.NextDouble() < p) next.Add(new Vector3Int(nx, c.y, nz));
            }
            if (c.y + 1 < gen.floors && !burning[c.y + 1, c.x, c.z])
            {
                canSpread = true;
                bool stairs = plan.stairwell.Contains(new Vector2Int(c.x, c.z));
                if (rng.NextDouble() < (stairs ? s.stairUpChance : s.upChance)) next.Add(new Vector3Int(c.x, c.y + 1, c.z));
            }
            if (canSpread) still.Add(c);
        }
        front.Clear();
        front.AddRange(still);
        foreach (var c in next)
            if (!burning[c.y, c.x, c.z]) Ignite(c, effect: true);
    }

    static readonly (int, int)[] Sides = { (1, 0), (-1, 0), (0, 1), (0, -1) };

    /// Fire crosses an edge only where there is no wall: open floor, doorways and entrances.
    static bool Open(FloorPlan fp, int x, int z, int dx, int dz)
    {
        var kind = dx != 0 ? fp.GetEdge(true, dx > 0 ? x + 1 : x, z) : fp.GetEdge(false, x, dz > 0 ? z + 1 : z);
        return kind is EdgeKind.None or EdgeKind.Door or EdgeKind.Entrance;
    }

    void Ignite(Vector3Int c, bool effect)
    {
        burning[c.y, c.x, c.z] = true;
        Burning++;
        front.Add(c);
        if (!effect) return;
        // One fire effect per 2 x 2 m block keeps the look dense without thousands of particle systems.
        var block = new Vector3Int(c.x / 2, c.y, c.z / 2);
        if (effects >= s.maxEffects || !effectBlocks.Add(block)) return;
        var at = gen.Root.TransformPoint(new Vector3(c.x + 0.5f, c.y * gen.floorHeight, c.z + 0.5f));
        Spawn(at, c.y, 1f);
    }

    /// Fills every floor with fire (the end): ignites all cells and adds big fires throughout.
    public void Engulf(int extraEffects)
    {
        int budget = effects + extraEffects;
        for (int f = 0; f < gen.floors; f++)
            for (int x = 0; x < gen.width; x++)
                for (int z = 0; z < gen.depth; z++)
                    if (!burning[f, x, z]) { burning[f, x, z] = true; Burning++; }
        for (int i = 0; effects < budget && i < extraEffects * 3; i++)
        {
            int f = rng.Next(gen.floors), x = rng.Next(1, gen.width - 1), z = rng.Next(1, gen.depth - 1);
            var at = gen.Root.TransformPoint(new Vector3(x + 0.5f, f * gen.floorHeight, z + 0.5f));
            Spawn(at, f, 1.5f);
        }
    }

    /// A copy of one of the building's fires standing on the floor at `at`.
    public void Spawn(Vector3 at, int floor, float scale)
    {
        // Every fourth copy keeps its light, so the spreading fire lights the rooms without hundreds of lights.
        bool lit = litTemplates.Count > 0 && lights * 4 <= effects;
        var pool = lit ? litTemplates : templates;
        if (pool.Count == 0) return;
        var src = pool[rng.Next(pool.Count)];
        var fire = Object.Instantiate(src, gen.floorRoots[floor], true);
        fire.name = "Fire";
        fire.transform.position = at;
        fire.transform.localScale = src.transform.localScale * scale;
        fire.transform.rotation = Quaternion.Euler(0f, rng.Next(360), 0f);
        foreach (var scorch in fire.GetComponentsInChildren<Transform>(true))
            if (scorch.name == "Scorch") scorch.gameObject.SetActive(false);
        // The copied smoke cap sits at the template's ceiling height; put it under this floor's ceiling.
        float ceiling = gen.Root.position.y + (floor + 1) * gen.floorHeight - 0.25f;
        foreach (var t in fire.GetComponentsInChildren<Transform>(true))
            if (t.name == "SmokeCeiling") t.position = new Vector3(t.position.x, ceiling, t.position.z);
        if (!lit)
            foreach (var l in fire.GetComponentsInChildren<Light>(true))
                if (Application.isPlaying) Object.Destroy(l.gameObject); else Object.DestroyImmediate(l.gameObject);
        else lights++;
        foreach (var ps in fire.GetComponentsInChildren<ParticleSystem>(true))
        {
            ps.Clear(true);
            if (Application.isPlaying) ps.Play(false);
            else ps.Simulate(2f, false, true);
        }
        effects++;
        coord.AddFire(at, floor);
    }
}
