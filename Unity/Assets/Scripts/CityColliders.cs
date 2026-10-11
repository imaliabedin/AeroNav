using UnityEngine;

/// The city backdrop (built by Drone Sim/Build City Backdrop) is decorative: it has no colliders, so the
/// sonar could not hear its buildings and the collision check could not feel them. At play time this adds
/// a box collider to every building, tree and street-furniture part tall enough to matter, so the city is
/// solid. They go on the TransparentFX layer, which the sonar and collision check see but the builder's
/// mouse clicks ignore (so targets can't be dropped on rooftops).
public static class CityColliders
{
    public const string BackdropName = "Backdrop";
    public const int Layer = 1;   // TransparentFX

    /// The city's footprint (all its renderers), or null if the scene has no backdrop.
    public static Bounds? Area { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void OnLoad() => Add();

    /// Adds the colliders (safe to call again). Returns how many were added.
    public static int Add()
    {
        Area = null;
        var root = GameObject.Find(BackdropName);
        if (root == null) return 0;
        int added = 0;
        Bounds area = default;
        bool any = false;
        foreach (var r in root.GetComponentsInChildren<MeshRenderer>())
        {
            var b = r.bounds;
            if (!any) { area = b; any = true; } else area.Encapsulate(b);
            if (b.size.y < 0.3f || b.max.y < 0f) continue;   // roads, sidewalks, lawns: the ground plane covers them
            var go = r.gameObject;
            if (go.GetComponent<Collider>() == null && go.GetComponent<MeshFilter>() != null)
            {
                go.AddComponent<BoxCollider>();   // fits the mesh bounds
                added++;
            }
            go.layer = Layer;
        }
        if (any) Area = area;
        Physics.SyncTransforms();
        Debug.Log($"[CityColliders] {added} city parts made solid");
        return added;
    }
}
