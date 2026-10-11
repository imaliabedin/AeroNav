using System.Collections.Generic;
using UnityEngine;

/// Re-rolls the parked cars and dropped items in every quadrant of the parking lot (R key).
/// Everything else (trees, lights, cones, cubes) is left alone. Cars and items are scenery only.
public class LotRandomizer : MonoBehaviour
{
    public PropSet props;

    [Tooltip("Catalog names treated as parked cars.")]
    public string[] carNames = { "Sedan", "SUV", "Taxi", "Police car", "Van" };
    [Tooltip("Catalog names treated as dropped items.")]
    public string[] itemNames = { "Phone", "Keys", "Wallet", "Laptop", "Purse", "Backpack", "Headphones", "Umbrella", "Water bottle", "Soda can", "Coffee cup" };

    [Header("Lot layout (1 unit = 1 m, quadrant-local)")]
    public Vector2Int quadrants = new Vector2Int(2, 2);
    public Vector2 quadSize = new Vector2(40f, 30f);
    public int baysPerRow = 12;
    public float bayWidth = 2.7f, bayDepth = 5.5f;
    public float firstBayX = 3.8f;
    [Tooltip("Gap between the quadrant's south/north edge and its bay rows.")]
    public float rowMargin = 1.5f;

    [Header("Variety")]
    [Range(0f, 1f)] public float minEmptyBays = 0.25f;
    [Range(0f, 1f)] public float maxEmptyBays = 0.6f;
    public int minItemsPerQuadrant = 2, maxItemsPerQuadrant = 5;

    public void Randomize() => Randomize(Random.Range(0, int.MaxValue));

    public void Randomize(int seed)
    {
        if (props == null || props.catalog == null) return;

        // Remove the current cars and items
        var cars = Ids(carNames);
        var items = Ids(itemNames);
        var names = new HashSet<string>(carNames);
        names.UnionWith(itemNames);
        foreach (var p in props.GetComponentsInChildren<Prop>())
            if (names.Contains(p.entryName)) props.Remove(p);
        if (cars.Count == 0) return;

        var rng = new System.Random(seed);
        for (int qz = 0; qz < quadrants.y; qz++)
            for (int qx = 0; qx < quadrants.x; qx++)
                FillQuadrant(new Vector3(qx * quadSize.x, 0f, qz * quadSize.y), cars, items, rng);
    }

    void FillQuadrant(Vector3 o, List<int> cars, List<int> items, System.Random rng)
    {
        // Each quadrant fills a different share of its bays
        double empty = minEmptyBays + rng.NextDouble() * (maxEmptyBays - minEmptyBays);
        var parked = new List<Vector3>();
        foreach (float z0 in new[] { rowMargin, quadSize.y - rowMargin - bayDepth })
            for (int i = 0; i < baysPerRow; i++)
            {
                if (rng.NextDouble() < empty) continue;
                var center = o + new Vector3(firstBayX + (i + 0.5f) * bayWidth, -0.5f, z0 + bayDepth * 0.5f);
                float yaw = (rng.Next(2) == 0 ? 0f : 180f) + (float)(rng.NextDouble() * 6 - 3);
                props.Place(cars[rng.Next(cars.Count)], center, yaw);
                parked.Add(center);
            }

        // Dropped items beside random parked cars, on the aisle side of the bay
        if (parked.Count == 0 || items.Count == 0) return;
        int drops = rng.Next(minItemsPerQuadrant, maxItemsPerQuadrant + 1);
        for (int d = 0; d < drops; d++)
        {
            var car = parked[rng.Next(parked.Count)];
            float side = car.z - o.z < quadSize.y * 0.5f ? 1f : -1f;
            var pos = car + new Vector3((float)(rng.NextDouble() * 2 - 1) * 1.2f, 0f,
                side * (bayDepth * 0.5f + 0.3f + (float)rng.NextDouble() * 0.8f));
            props.Place(items[rng.Next(items.Count)], pos, rng.Next(360));
        }
    }

    List<int> Ids(string[] names)
    {
        var ids = new List<int>();
        foreach (var n in names)
        {
            int i = props.catalog.IndexOf(n);
            if (i >= 0) ids.Add(i);
        }
        return ids;
    }
}
