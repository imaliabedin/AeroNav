using UnityEngine;

/// Downward-facing camera mounted under the drone. It renders into its own RenderTexture
/// (never the screen), and <see cref="CaptureJpg"/> reads real frames from it for the detector.
/// Image top = the drone's +Z (forward) direction.
public class DroneCamera : MonoBehaviour
{
    public int width = 640;
    public int height = 640;
    [Tooltip("Vertical field of view. 60° at 2.44 m sees about 2.8 x 2.8 m of ground.")]
    public float fieldOfView = 60f;
    [Tooltip("Camera position relative to the drone, in world meters (not scaled with the drone).")]
    public Vector3 mountOffset = new Vector3(0f, -0.12f, 0f);
    [Tooltip("World height of the ground surface, used only to report altitude in the HUD.")]
    public float groundY = -0.5f;

    public Camera Cam { get; private set; }
    public RenderTexture Target { get; private set; }
    public float Altitude => Cam ? Cam.transform.position.y - groundY : 0f;

    Texture2D readback;

    void Awake() => EnsureCamera();

    /// Creates the camera on first use. Safe to call in edit mode (used by the capture test).
    public void EnsureCamera()
    {
        if (Cam != null) return;

        var go = new GameObject("Downward Camera");
        go.transform.SetParent(transform, false);
        // A world-space offset, so it is unaffected by the drone's 0.4 scale.
        go.transform.position = transform.position + mountOffset;
        go.transform.rotation = Quaternion.LookRotation(Vector3.down, transform.forward);

        Cam = go.AddComponent<Camera>();
        Cam.fieldOfView = fieldOfView;
        Cam.nearClipPlane = 0.05f;
        Cam.farClipPlane = 100f;
        Cam.clearFlags = CameraClearFlags.Skybox;
        // Layer 2 (Ignore Raycast) holds the drone body, its visual, path line and trail: don't film ourselves.
        Cam.cullingMask = ~(1 << 2);
        Cam.depth = -10;

        Target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { name = "DroneCameraRT" };
        Target.Create();
        Cam.targetTexture = Target;
        readback = new Texture2D(width, height, TextureFormat.RGB24, false);

        // The drone's runtime path line is created on the default layer; hide it from the camera.
        foreach (var lr in GetComponentsInChildren<LineRenderer>(true)) lr.gameObject.layer = 2;
    }

    /// Renders the camera now and returns the frame as JPEG bytes.
    public byte[] CaptureJpg(int quality = 90)
    {
        EnsureCamera();
        Cam.Render();
        var prev = RenderTexture.active;
        RenderTexture.active = Target;
        readback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
        readback.Apply(false);
        RenderTexture.active = prev;
        return readback.EncodeToJPG(quality);
    }

    void OnDestroy()
    {
        if (Cam != null) Cam.targetTexture = null;   // detach before releasing
        if (Target != null) { Target.Release(); Destroy(Target); }
        if (readback != null) Destroy(readback);
    }
}
