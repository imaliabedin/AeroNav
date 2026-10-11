using System.Collections.Generic;
using Drone2.Building;
using UnityEngine;

/// The end when time runs out: fire engulfs every floor, then the building pancakes from the top down.
/// The roof and each floor drop under gravity with a little tumble, coming to rest on the floor below them,
/// and the drones fall with the debris. Stepped by the coordinator, so it also runs in batch recordings.
public class BuildingCollapse
{
    public const float EngulfSeconds = 3f;
    const float PileHeight = 0.6f;   // what is left of a storey once crushed

    class Piece
    {
        public Transform tr;
        public float delay, restY;
        public Vector3 velocity, spin;
    }

    readonly BuildingGenerator gen;
    readonly FireSpread fire;
    readonly List<Piece> pieces = new();
    readonly System.Random rng;
    float t;
    bool falling;

    public float Seconds => t;
    public bool Falling => falling;
    /// The drone feeds cut out once the fire is everywhere.
    public bool SignalLost => t >= EngulfSeconds * 0.7f;

    public BuildingCollapse(BuildingGenerator gen, FireSpread fire, IEnumerable<SearchDrone> drones, int seed)
    {
        this.gen = gen;
        this.fire = fire;
        rng = new System.Random(seed);
        fire.Engulf(120);

        float h = gen.floorHeight - PileHeight;
        int n = gen.floors;
        if (gen.RoofRoot != null) Add(gen.RoofRoot, 0f, -n * h);
        for (int f = n - 1; f >= 1; f--) Add(gen.floorRoots[f], (n - f) * 0.3f, -f * h);
        if (n > 0) Add(gen.floorRoots[0], n * 0.3f, -0.4f);   // the ground floor just sags
        foreach (var d in drones)
            pieces.Add(new Piece { tr = d.transform, delay = Rand(0f, 0.6f), restY = gen.Root.position.y + 0.15f, spin = RandSpin(220f) });
    }

    void Add(Transform tr, float delay, float drop) =>
        pieces.Add(new Piece { tr = tr, delay = delay, restY = tr.position.y + drop, spin = RandSpin(5f) });

    float Rand(float a, float b) => a + (float)rng.NextDouble() * (b - a);
    Vector3 RandSpin(float max) => new(Rand(-max, max), Rand(-max, max) * 0.3f, Rand(-max, max));

    public void Step(float dt)
    {
        t += dt;
        if (!falling && t >= EngulfSeconds)
        {
            falling = true;
            // Dust and smoke billowing out around the base as it comes down.
            for (int i = 0; i < 14; i++)
            {
                float a = i / 14f * Mathf.PI * 2f;
                var local = new Vector3(gen.width / 2f + Mathf.Cos(a) * gen.width * 0.55f, 0f, gen.depth / 2f + Mathf.Sin(a) * gen.depth * 0.55f);
                fire.Spawn(gen.Root.TransformPoint(local), 0, 2.2f);
            }
        }
        if (!falling) return;

        float since = t - EngulfSeconds;
        foreach (var p in pieces)
        {
            if (p.tr == null || since < p.delay || p.velocity == Vector3.positiveInfinity) continue;
            p.velocity += Physics.gravity * dt;
            var pos = p.tr.position + p.velocity * dt;
            p.tr.Rotate(p.spin * dt, Space.World);
            if (pos.y <= p.restY)
            {
                pos.y = p.restY;
                p.velocity = Vector3.zero;
                p.spin *= 0.2f;
            }
            p.tr.position = pos;
        }
    }
}
