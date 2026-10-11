using UnityEngine;

// Spins a propeller, smoothly speeding up when turned on
// and slowing down when turned off, like a real motor.
// Attach to each Prop_ parent object.
public class PropellorSpin : MonoBehaviour
{
    // Full spin speed in degrees per second. Negative = opposite direction.
    public float maxSpeed = 720f;

    // How quickly the speed changes, in degrees per second, per second.
    // Example: maxSpeed 720 with acceleration 360 = 2 seconds to full speed.
    public float acceleration = 360f;

    // Turn the motor on or off. Other scripts can change this later
    // (for example, when the drone takes off or lands).
    public bool spinning = true;

    // The speed right now. "private" = hidden from the Inspector and
    // other scripts, just like private fields in Java.
    private float currentSpeed = 0f;

    void Update()
    {
        // Where we're heading: full speed if on, zero if off.
        // (condition ? a : b) works exactly like Java's ternary operator.
        float targetSpeed = spinning ? maxSpeed : 0f;

        // Move currentSpeed a small step toward targetSpeed each frame,
        // never overshooting it. This creates the smooth ramp up/down.
        currentSpeed = Mathf.MoveTowards(
            currentSpeed, targetSpeed, acceleration * Time.deltaTime);

        // Rotate by the current speed (scaled by frame time, as before).
        transform.Rotate(0f, currentSpeed * Time.deltaTime, 0f);
    }
}