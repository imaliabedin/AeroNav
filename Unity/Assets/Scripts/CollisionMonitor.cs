using System;
using UnityEngine;

/// Safety check, separate from perception: after every move it tests whether the drone's body
/// touched a real collider (sweeping from the previous position, so fast moves can't skip through).
/// It records collisions and can stop the drone. It never tells the planner anything.
public class CollisionMonitor : MonoBehaviour
{
    public Drone drone;
    [Tooltip("Stop the drone on contact (on in Sonar mode; Full-Knowledge mode only records, to keep its old behavior).")]
    public bool haltOnCollision = true;
    public LayerMask layers = Physics.DefaultRaycastLayers & ~(1 << 2);   // not the drone itself

    public int Collisions { get; private set; }
    public string LastHit { get; private set; }
    public Vector3 LastHitPosition { get; private set; }
    public bool Colliding { get; private set; }
    public event Action<Collider> Collided;

    readonly Collider[] buffer = new Collider[8];
    Vector3 lastPos;
    bool tracking;

    void LateUpdate()
    {
        var sim = SimulationManager.Instance;
        bool active = drone != null && drone.Running && (sim == null || sim.Mode == SimulationManager.SimMode.Running);
        if (!active) { tracking = false; Colliding = false; return; }
        Check();
    }

    public void ResetCount()
    {
        Collisions = 0;
        LastHit = null;
        tracking = false;
        Colliding = false;
    }

    /// Checks the move since the last call. Returns true while the drone touches something.
    public bool Check()
    {
        var pos = drone.transform.position;
        if (!tracking) { lastPos = pos; tracking = true; }
        var half = Vector3.Scale(drone.transform.lossyScale, drone.GetComponent<BoxCollider>().size) * 0.5f;

        Collider hitCollider = null;
        Vector3? contact = null;
        var delta = pos - lastPos;
        if (delta.sqrMagnitude > 1e-8f
            && Physics.BoxCast(lastPos, half, delta.normalized, out var hit, drone.transform.rotation, delta.magnitude, layers, QueryTriggerInteraction.Ignore))
        {
            hitCollider = hit.collider;
            contact = lastPos + delta.normalized * hit.distance;   // where the body first touched
        }
        if (hitCollider == null
            && Physics.OverlapBoxNonAlloc(pos, half, buffer, drone.transform.rotation, layers, QueryTriggerInteraction.Ignore) > 0)
            hitCollider = buffer[0];
        lastPos = pos;

        bool now = hitCollider != null;
        if (now && !Colliding)
        {
            Collisions++;
            LastHit = hitCollider.name;
            LastHitPosition = pos;
            Debug.LogWarning($"[Collision] drone touched '{hitCollider.name}' at {contact ?? pos}" + (haltOnCollision ? " - stopped" : " - recorded only"));
            if (haltOnCollision)
            {
                drone.Halt();
                // A fast move can carry the body past a thin obstacle within one frame; stop it at the contact instead.
                if (contact.HasValue) drone.transform.position = contact.Value;
                LastHitPosition = drone.transform.position;
            }
            Collided?.Invoke(hitCollider);
        }
        Colliding = now;
        return now;
    }
}
