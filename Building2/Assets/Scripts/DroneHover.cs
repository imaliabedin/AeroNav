using Drone2.Building;
using UnityEngine;

/// Keeps the drone hovering in the main hallway of one floor of the generated building.
/// After every (re)build it picks a clear spot near the middle of the corridor, away from any fire,
/// then bobs and sways gently in place. Optionally keeps a camera framing it from down the hallway.
public class DroneHover : MonoBehaviour
{
    public BuildingGenerator generator;
    [Tooltip("0 = first (ground) floor.")]
    public int floor = 0;
    [Tooltip("Height above the floor, in metres.")]
    public float hoverHeight = 1.4f;
    public float bobAmplitude = 0.06f;
    public float bobPeriod = 2.4f;
    public float swayDegrees = 2.5f;

    [Tooltip("Optional camera that watches the drone from further along the hallway.")]
    public Camera showcaseCamera;
    public float cameraDistance = 1.5f;

    const float Clearance = 0.55f;   // free radius around the drone
    const float FireGap = 2.5f;      // keep at least this far from a fire

    Vector3 home;
    Quaternion facing;
    float seed;
    bool placed;

    void OnEnable() { if (generator != null) generator.Generated += Place; }
    void OnDisable() { if (generator != null) generator.Generated -= Place; }

    /// Moves the drone (and the showcase camera) into the hallway of the freshly generated building.
    public void Place(BuildingGenerator gen)
    {
        var corridor = gen.Plan.mainCorridor;
        int f = Mathf.Clamp(floor, 0, gen.floors - 1);
        float y = f * gen.floorHeight + hoverHeight;
        Physics.SyncTransforms();

        var fires = new System.Collections.Generic.List<Vector3>();
        foreach (var t in gen.floorRoots[f].GetComponentsInChildren<Transform>())
            if (t.name == "Fire") fires.Add(t.position);

        var along = gen.Root.right;
        // Try spots from the middle of the corridor outwards, half a metre apart.
        for (int i = 0; i < corridor.width * 2; i++)
        {
            float x = corridor.center.x + (i % 2 == 0 ? 1 : -1) * (i / 2) * 0.5f;
            var p = gen.Root.TransformPoint(new Vector3(x, y, corridor.center.y));
            if (Physics.CheckSphere(p, Clearance, ~0, QueryTriggerInteraction.Ignore)) continue;
            if (fires.Exists(fp => Vector3.Distance(fp, p) < FireGap)) continue;
            foreach (var dir in new[] { along, -along })
            {
                var cam = p + dir * cameraDistance + Vector3.up * 0.25f;
                if (showcaseCamera != null &&
                    (Physics.CheckSphere(cam, 0.2f, ~0, QueryTriggerInteraction.Ignore) ||
                     Physics.Linecast(cam, p, ~0, QueryTriggerInteraction.Ignore)))
                    continue;
                home = p;
                // Face down the hallway, turned partly toward the camera so its front camera shows.
                facing = Quaternion.LookRotation(Quaternion.AngleAxis(35f, Vector3.up) * dir);
                seed = Random.value * 100f;
                placed = true;
                if (showcaseCamera != null) showcaseCamera.transform.position = cam;
                Pose(0f);
                Debug.Log($"Drone hovering in floor {f + 1} hallway at {home}");
                return;
            }
        }
        Debug.LogWarning($"DroneHover: no clear spot found in the floor {f + 1} hallway");
    }

    void Update()
    {
        if (placed) Pose(Time.time);
    }

    /// Hover motion at time t: a slow bob plus small pitch and roll wobbles.
    public void Pose(float t)
    {
        float bob = Mathf.Sin(2f * Mathf.PI * t / bobPeriod) * bobAmplitude;
        float pitch = (Mathf.PerlinNoise(seed, t * 0.4f) - 0.5f) * 2f * swayDegrees;
        float roll = (Mathf.PerlinNoise(seed + 7f, t * 0.4f) - 0.5f) * 2f * swayDegrees;
        transform.SetPositionAndRotation(home + Vector3.up * bob, facing * Quaternion.Euler(pitch, 0f, roll));
        if (showcaseCamera != null) showcaseCamera.transform.LookAt(transform.position);
    }
}
