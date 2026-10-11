using UnityEngine;

/// Makes the drone's model fly like a quadcopter: it turns to face where it is going, pitches nose-down
/// to accelerate and nose-up to brake, and banks into turns. Only the model ("Drone Visual") rotates;
/// the drone's collider and sensors stay level, so navigation and sonar are unaffected.
public class DroneMotionVisual : MonoBehaviour
{
    public Drone drone;
    public Transform visual;
    [Tooltip("Largest tilt in degrees.")]
    public float maxTilt = 22f;
    [Tooltip("Degrees of forward tilt per cell/s of speed (a drone leans to hold speed against drag).")]
    public float tiltPerSpeed = 2.2f;
    [Tooltip("Degrees of tilt per cell/s^2 of acceleration or braking.")]
    public float tiltPerAccel = 1.6f;
    public float turnRate = 5f;
    public float smoothing = 8f;
    [Tooltip("World height of the ground; at the lowest grid level the model is lowered to sit on it.")]
    public float groundY = -0.5f;

    Quaternion baseRotation;
    Vector3 basePosition;
    float bottomBelowRoot;   // how far the model's lowest point is below the drone's center
    Vector3 lastVelocity, accel;
    float yaw, pitch, roll;

    void Start()
    {
        if (!drone) drone = GetComponent<Drone>();
        if (!visual) visual = transform.Find("Drone Visual");
        if (visual)
        {
            baseRotation = visual.localRotation;
            basePosition = visual.localPosition;
            bool any = false;
            float minY = 0f;
            foreach (var r in visual.GetComponentsInChildren<Renderer>())
            {
                minY = any ? Mathf.Min(minY, r.bounds.min.y) : r.bounds.min.y;
                any = true;
            }
            bottomBelowRoot = any ? transform.position.y - minY : 0.2f;
        }
    }

    void LateUpdate()
    {
        if (!drone || !visual) return;
        float dt = Mathf.Max(Time.deltaTime, 1e-4f);
        var v = drone.Running ? drone.Velocity : Vector3.zero;
        accel = Vector3.Lerp(accel, (v - lastVelocity) / dt, 1f - Mathf.Exp(-6f * dt));
        lastVelocity = v;

        var flat = new Vector3(v.x, 0f, v.z);
        if (flat.magnitude > 0.2f)
            yaw = Mathf.LerpAngle(yaw, Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg, 1f - Mathf.Exp(-turnRate * dt));
        var forward = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
        var right = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;

        float targetPitch = Mathf.Clamp(tiltPerSpeed * Vector3.Dot(flat, forward) + tiltPerAccel * Vector3.Dot(accel, forward), -maxTilt, maxTilt);
        float targetRoll = Mathf.Clamp(-tiltPerAccel * Vector3.Dot(accel, right), -maxTilt, maxTilt);
        float k = 1f - Mathf.Exp(-smoothing * dt);
        pitch = Mathf.Lerp(pitch, targetPitch, k);
        roll = Mathf.Lerp(roll, targetRoll, k);

        // Rotate about the drone's own center: yaw first, then lean forward (pitch) and sideways (roll).
        visual.localRotation = Quaternion.Euler(0f, yaw, 0f) * Quaternion.Euler(pitch, 0f, roll) * baseRotation;

        // Parked or landing at the lowest level (drone center half a cell above the ground): rest the model on the
        // ground; the offset fades out over the first metre of climb.
        float height = transform.position.y - groundY;
        float sink = Mathf.Max(0f, height - bottomBelowRoot) * Mathf.Clamp01(1.5f - height);
        visual.localPosition = basePosition - Vector3.up * sink / Mathf.Max(transform.lossyScale.y, 1e-4f);
    }
}
