using System.Collections.Generic;
using UnityEngine;

/// Brings the city backdrop to life at play time: cars driving around the blocks in their lane and people
/// walking the sidewalks. Both are solid (the sonar hears them, the collision check feels them) and sit on the
/// TransparentFX layer like the buildings, so the builder's clicks go through them. Street layout matches
/// Drone Sim/Build City Backdrop (10 m streets on a 44 m pitch, 3 m sidewalks).
public class CityLife : MonoBehaviour
{
    // Street center lines (from CityBackdrop).
    static readonly float[] XStreets = { -184, -140, -96, -52, -8, 88, 132, 176, 220, 264 };
    static readonly float[] ZStreets = { -140, -96, -52, -8, 68, 112, 156, 200, 244 };
    const float Street = 10f, Walk = 3f, RoadTop = -0.5f, WalkTop = -0.42f;
    static readonly Vector2 LotCenter = new Vector2(40f, 30f);

    /// The city's built-up area (x, z): from the first to the last street, sidewalks included.
    public static Rect StreetArea => Rect.MinMaxRect(XStreets[0] - Street, ZStreets[0] - Street,
                                                    XStreets[XStreets.Length - 1] + Street, ZStreets[ZStreets.Length - 1] + Street);

    /// True if a ground point (x, z) is on a street's carriageway (where traffic drives).
    public static bool IsRoad(float x, float z)
    {
        foreach (var s in XStreets) if (Mathf.Abs(x - s) <= Street * 0.5f) return true;
        foreach (var s in ZStreets) if (Mathf.Abs(z - s) <= Street * 0.5f) return true;
        return false;
    }

    /// True for the moving traffic and pedestrians this script creates (they block the view only temporarily).
    public static bool IsMover(Transform t) =>
        t != null && (t.root.name.StartsWith("Traffic ") || t.root.name == "Pedestrian");

    public int carsPerBlock = 5;       // evenly spaced around each block's lane
    public int people = 200;           // 1200 made the WebGL build run out of memory at startup (Chrome "OOM")
    public float carSpeed = 8f;        // m/s
    public float walkSpeed = 1.3f;     // m/s
    public float range = 170f;         // only blocks this close to the lot get traffic
    [Tooltip("Height cap for traffic (m); off by default: the drone's band is scaled to the scene, so full-size cars fit under it.")]
    public float maxCarHeight = 100f;

    class Mover
    {
        public Transform t;
        public Rect loop;          // rectangle the mover goes around (x/z)
        public float corner;       // corner radius
        public float s;            // distance travelled along the loop
        public float speed;
        public float y;
        public bool clockwise;
        public Transform legL, legR;
    }

    readonly List<Mover> movers = new List<Mover>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Attach()
    {
        if (GameObject.Find(CityColliders.BackdropName) == null || SimulationManager.Instance == null) return;
        new GameObject("City life").AddComponent<CityLife>();
    }

    void Start()
    {
        var sim = SimulationManager.Instance;
        var catalog = sim.map.props ? sim.map.props.catalog : null;
        var bodyMat = sim.map.wallMaterial;
        // People at the same scale as the scene's (oversized) cars, so they look right next to them.
        var nav = sim.drone ? sim.drone.GetComponent<SonarNavigator>() : null;
        float personScale = nav ? nav.SceneScale : 1f;
        var rng = new System.Random(4242);

        // Blocks (between neighbouring streets) near the lot, plus the ring road around the lot itself.
        var blocks = new List<Rect>();
        for (int i = 0; i + 1 < XStreets.Length; i++)
            for (int j = 0; j + 1 < ZStreets.Length; j++)
            {
                var r = Rect.MinMaxRect(XStreets[i], ZStreets[j], XStreets[i + 1], ZStreets[j + 1]);
                if (Vector2.Distance(r.center, LotCenter) < range) blocks.Add(r);
            }
        if (blocks.Count == 0) return;

        // Cars: carsPerBlock per block, driving clockwise around it in the lane 2.5 m in from the street's center
        // line (right-hand traffic: the block is on the car's right; the far lane belongs to the next block, so every
        // street has two-way traffic). Cars on one block share a speed and are evenly spaced, and every loop only
        // turns right around its own block, so no two cars ever meet.
        string[] carNames = { "Sedan", "SUV", "Taxi", "Police car", "Van" };
        int carCount = 0;
        foreach (var block in blocks)
        {
            if (catalog == null) break;
            var loop = Inset(block, 2.5f);
            float per = Perimeter(loop, 6f), speed = carSpeed * (0.8f + 0.4f * (float)rng.NextDouble()), offset = (float)rng.NextDouble() * per;
            for (int k = 0; k < carsPerBlock; k++)
            {
                var entry = catalog.Get(catalog.IndexOf(carNames[rng.Next(carNames.Length)]));
                if (entry == null || entry.prefab == null) continue;
                var car = PropSet.Build(entry);
                car.name = "Traffic " + entry.name;
                // The car models scaled to real lengths come out 2.3-2.55 m tall; real cars are 1.5-2 m, which is
                // what lets a drone cruise at 8-10 ft over traffic. Cap the height at maxCarHeight (uniform scale).
                if (PropSet.RendererBounds(car, out var cb) && cb.size.y > maxCarHeight)
                    car.transform.localScale *= maxCarHeight / cb.size.y;
                MakeSolid(car, true);
                movers.Add(new Mover { t = car.transform, loop = loop, corner = 6f, s = offset + k * per / carsPerBlock,
                                       speed = speed, y = RoadTop, clockwise = true });
                carCount++;
            }
        }

        // People: on the building side of each block's sidewalk, clear of the street lamps (0.6 m from the curb) and
        // street trees (1.5 m). One lane per walking direction (2.2 m / 2.6 m in from the curb), one speed per lane,
        // people spread out along it, so nobody walks through anybody. Not on the lot's own sidewalk (barrier cubes).
        var lanes = new List<(Rect loop, bool cw, float speed)>();
        foreach (var block in blocks)
        {
            if (block.Contains(LotCenter)) continue;
            lanes.Add((Inset(block, Street * 0.5f + 2.2f), true, walkSpeed * (0.85f + 0.3f * (float)rng.NextDouble())));
            lanes.Add((Inset(block, Street * 0.5f + 2.6f), false, walkSpeed * (0.85f + 0.3f * (float)rng.NextDouble())));
        }
        int perLane = lanes.Count == 0 ? 0 : Mathf.CeilToInt(people / (float)lanes.Count);
        int peopleCount = 0;
        foreach (var lane in lanes)
        {
            float per = Perimeter(lane.loop, 1.5f), offset = (float)rng.NextDouble() * per;
            for (int k = 0; k < perLane && peopleCount < people; k++)
            {
                var person = Person(bodyMat, rng, out var legL, out var legR);
                person.transform.localScale = Vector3.one * personScale;
                float jitter = (float)rng.NextDouble() * 0.5f * per / perLane;   // spaced, but not in lockstep
                movers.Add(new Mover { t = person.transform, loop = lane.loop, corner = 1.5f, s = offset + k * per / perLane + jitter,
                                       speed = lane.speed, y = WalkTop, clockwise = lane.cw, legL = legL, legR = legR });
                peopleCount++;
            }
        }
        Debug.Log($"[CityLife] {movers.Count} movers ({carCount} cars, {peopleCount} people) around {blocks.Count} blocks");
    }

    void Update()
    {
        float dt = Time.deltaTime;
        foreach (var m in movers)
        {
            m.s += m.speed * dt;
            float per = Perimeter(m.loop, m.corner);
            m.s %= per;
            var (p, dir) = OnLoop(m.loop, m.corner, m.clockwise ? per - m.s : m.s);
            if (m.clockwise) dir = -dir;
            m.t.position = new Vector3(p.x, m.y, p.y);
            if (dir.sqrMagnitude > 1e-6f) m.t.rotation = Quaternion.LookRotation(new Vector3(dir.x, 0f, dir.y));
            if (m.legL)   // walking: swing the legs and bob a little
            {
                float ph = m.s * 4f;
                m.legL.localRotation = Quaternion.Euler(Mathf.Sin(ph) * 28f, 0f, 0f);
                m.legR.localRotation = Quaternion.Euler(-Mathf.Sin(ph) * 28f, 0f, 0f);
                m.t.position += Vector3.up * Mathf.Abs(Mathf.Sin(ph)) * 0.04f;
            }
        }
    }

    // ---------- geometry: a rectangle with rounded corners, walked counter-clockwise from its bottom-left ----------

    static Rect Inset(Rect r, float d) => Rect.MinMaxRect(r.xMin + d, r.yMin + d, r.xMax - d, r.yMax - d);

    static float Perimeter(Rect r, float c) => 2f * (r.width - 2f * c) + 2f * (r.height - 2f * c) + 2f * Mathf.PI * c;

    /// Point and travel direction at distance s along the rounded rectangle.
    static (Vector2, Vector2) OnLoop(Rect r, float c, float s)
    {
        float w = r.width - 2f * c, h = r.height - 2f * c, arc = Mathf.PI * c * 0.5f;
        float[] lengths = { w, arc, h, arc, w, arc, h, arc };
        Vector2[] starts = { new Vector2(r.xMin + c, r.yMin), Vector2.zero, new Vector2(r.xMax, r.yMin + c), Vector2.zero,
                             new Vector2(r.xMax - c, r.yMax), Vector2.zero, new Vector2(r.xMin, r.yMax - c), Vector2.zero };
        Vector2[] dirs = { Vector2.right, Vector2.zero, Vector2.up, Vector2.zero, Vector2.left, Vector2.zero, Vector2.down, Vector2.zero };
        Vector2[] centers = { Vector2.zero, new Vector2(r.xMax - c, r.yMin + c), Vector2.zero, new Vector2(r.xMax - c, r.yMax - c),
                              Vector2.zero, new Vector2(r.xMin + c, r.yMax - c), Vector2.zero, new Vector2(r.xMin + c, r.yMin + c) };
        float[] startAngles = { 0f, -90f, 0f, 0f, 0f, 90f, 0f, 180f };
        for (int k = 0; k < 8; k++)
        {
            if (s > lengths[k] && k < 7) { s -= lengths[k]; continue; }
            if (k % 2 == 0) return (starts[k] + dirs[k] * s, dirs[k]);
            float a = (startAngles[k] + s / c * Mathf.Rad2Deg) * Mathf.Deg2Rad;
            var p = centers[k] + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * c;
            return (p, new Vector2(-Mathf.Sin(a), Mathf.Cos(a)));
        }
        return (starts[0], dirs[0]);
    }

    // ---------- building the movers ----------

    /// Solid for the sonar and collision check, moved kinematically, ignored by builder clicks.
    static void MakeSolid(GameObject go, bool box)
    {
        if (box && PropSet.RendererBounds(go, out var b))
        {
            var col = go.AddComponent<BoxCollider>();
            col.center = go.transform.InverseTransformPoint(b.center);
            col.size = b.size;
        }
        var rb = go.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;
        foreach (var t in go.GetComponentsInChildren<Transform>()) t.gameObject.layer = CityColliders.Layer;
    }

    static GameObject Person(Material baseMat, System.Random rng, out Transform legL, out Transform legR)
    {
        Color[] shirts = { new Color(0.85f, 0.25f, 0.2f), new Color(0.2f, 0.45f, 0.85f), new Color(0.95f, 0.8f, 0.25f),
                           new Color(0.3f, 0.65f, 0.35f), new Color(0.9f, 0.9f, 0.92f), new Color(0.35f, 0.3f, 0.45f) };
        Color[] skins = { new Color(0.96f, 0.8f, 0.66f), new Color(0.8f, 0.6f, 0.45f), new Color(0.55f, 0.38f, 0.26f), new Color(0.36f, 0.24f, 0.16f) };
        var shirt = Tinted(baseMat, shirts[rng.Next(shirts.Length)]);
        var skin = Tinted(baseMat, skins[rng.Next(skins.Length)]);
        var pants = Tinted(baseMat, new Color(0.18f, 0.2f, 0.28f));

        var root = new GameObject("Pedestrian");
        Part(root.transform, PrimitiveType.Capsule, new Vector3(0f, 1.15f, 0f), new Vector3(0.42f, 0.38f, 0.3f), shirt);   // torso
        Part(root.transform, PrimitiveType.Sphere, new Vector3(0f, 1.62f, 0f), Vector3.one * 0.24f, skin);                // head
        legL = Leg(root.transform, -0.1f, pants);
        legR = Leg(root.transform, 0.1f, pants);
        var col = root.AddComponent<CapsuleCollider>();
        col.center = new Vector3(0f, 0.85f, 0f);
        col.height = 1.75f;
        col.radius = 0.25f;
        MakeSolid(root, false);
        return root;
    }

    static Transform Leg(Transform parent, float x, Material m)
    {
        var hip = new GameObject("Hip").transform;
        hip.SetParent(parent, false);
        hip.localPosition = new Vector3(x, 0.8f, 0f);
        Part(hip, PrimitiveType.Capsule, new Vector3(0f, -0.4f, 0f), new Vector3(0.14f, 0.4f, 0.14f), m);
        return hip;
    }

    static void Part(Transform parent, PrimitiveType type, Vector3 pos, Vector3 scale, Material m)
    {
        var go = GameObject.CreatePrimitive(type);
        Destroy(go.GetComponent<Collider>());   // the person has one capsule collider on its root
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        go.GetComponent<Renderer>().sharedMaterial = m;
    }

    static readonly Dictionary<Color, Material> tints = new Dictionary<Color, Material>();
    static Material Tinted(Material baseMat, Color c)
    {
        if (tints.TryGetValue(c, out var m) && m) return m;
        m = new Material(baseMat);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        tints[c] = m;
        return m;
    }
}
