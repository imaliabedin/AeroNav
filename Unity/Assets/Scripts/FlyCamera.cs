using UnityEngine;
using UnityEngine.InputSystem;

/// Free-fly camera: hold RMB to look, WASD to move, Q/E down/up, Shift for speed,
/// scroll to dolly. If `follow` is set, it hovers above that target instead. Runs on unscaled time, so it
/// behaves the same at any simulation speed (Time.timeScale).
public class FlyCamera : MonoBehaviour
{
    public float moveSpeed = 8f;
    public float fastMultiplier = 3f;
    public float lookSensitivity = 0.15f;
    public Transform follow;
    public Vector3 followOffset = new Vector3(0f, 7f, -3f);

    float yaw, pitch;

    void Start()
    {
        var e = transform.eulerAngles;
        yaw = e.y;
        pitch = e.x > 180f ? e.x - 360f : e.x;
    }

    /// Place the camera above-and-in-front of a box so all of it is in view.
    public void Frame(Vector3 center, Vector3 size)
    {
        // Width counts for less than depth: the view is wider than it is tall.
        float extent = Mathf.Max(size.x * 0.75f, size.z, size.y * 1.5f);
        transform.position = center + new Vector3(0f, 1.3f * extent, -0.65f * extent);   // ~63° down: a bit more ground
        transform.LookAt(center + new Vector3(0f, 0f, 0.05f * extent));
        var e = transform.eulerAngles;
        yaw = e.y;
        pitch = e.x > 180f ? e.x - 360f : e.x;
    }

    void LateUpdate()
    {
        var kb = Keyboard.current;
        var mouse = Mouse.current;
        if (kb == null || mouse == null) return;

        if (follow != null)
        {
            var target = follow.position + followOffset;
            transform.position = Vector3.Lerp(transform.position, target, 1f - Mathf.Exp(-8f * Time.unscaledDeltaTime));
            transform.rotation = Quaternion.Slerp(transform.rotation,
                Quaternion.LookRotation(follow.position - transform.position), 1f - Mathf.Exp(-8f * Time.unscaledDeltaTime));
            var r = transform.eulerAngles;
            yaw = r.y;
            pitch = r.x > 180f ? r.x - 360f : r.x;
            return;
        }

        if (mouse.rightButton.isPressed)
        {
            var delta = mouse.delta.ReadValue();
            yaw += delta.x * lookSensitivity;
            pitch = Mathf.Clamp(pitch - delta.y * lookSensitivity, -89f, 89f);
            transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
        }

        var move = Vector3.zero;
        if (kb.wKey.isPressed) move += transform.forward;
        if (kb.sKey.isPressed) move -= transform.forward;
        if (kb.dKey.isPressed) move += transform.right;
        if (kb.aKey.isPressed) move -= transform.right;
        if (kb.eKey.isPressed) move += Vector3.up;
        if (kb.qKey.isPressed) move -= Vector3.up;

        float speed = moveSpeed * (kb.shiftKey.isPressed ? fastMultiplier : 1f);
        transform.position += move * speed * Time.unscaledDeltaTime;

        float scroll = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(scroll) > 0.01f)
            transform.position += transform.forward * Mathf.Sign(scroll) * speed * 0.15f;
    }
}
