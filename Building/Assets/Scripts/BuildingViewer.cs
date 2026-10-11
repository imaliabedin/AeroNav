using Drone2.Building;
using UnityEngine;
using UnityEngine.InputSystem;

/// Free-fly camera for inspecting the generated building, plus a small help overlay.
/// Right mouse: look. WASD: move. Q/E: down/up. Shift: fast.
/// 1-9: cut away floors above that one. 0: show everything. R: rebuild with a new seed.
/// P: jump to the next trapped person.
public class BuildingViewer : MonoBehaviour
{
    public BuildingGenerator generator;
    public float speed = 8f;
    public float lookSensitivity = 0.15f;

    const int MinStoreys = 1, MaxStoreys = 6;

    float yaw, pitch;
    int floorView = -1;
    int personIndex = -1;

    void Start()
    {
        var e = transform.eulerAngles;
        yaw = e.y;
        pitch = e.x > 180 ? e.x - 360 : e.x;
    }

    void Update()
    {
        var kb = Keyboard.current;
        var mouse = Mouse.current;
        if (kb == null || mouse == null) return;

        if (mouse.rightButton.isPressed)
        {
            var d = mouse.delta.ReadValue();
            yaw += d.x * lookSensitivity;
            pitch = Mathf.Clamp(pitch - d.y * lookSensitivity, -89f, 89f);
            transform.rotation = Quaternion.Euler(pitch, yaw, 0);
        }

        var move = Vector3.zero;
        if (kb.wKey.isPressed) move += Vector3.forward;
        if (kb.sKey.isPressed) move += Vector3.back;
        if (kb.aKey.isPressed) move += Vector3.left;
        if (kb.dKey.isPressed) move += Vector3.right;
        var vertical = (kb.eKey.isPressed ? 1f : 0f) - (kb.qKey.isPressed ? 1f : 0f);
        float s = speed * (kb.leftShiftKey.isPressed ? 3f : 1f) * Time.unscaledDeltaTime;
        transform.position += transform.rotation * move * s + Vector3.up * vertical * s;

        if (generator == null) return;
        if (kb.rKey.wasPressedThisFrame)
        {
            generator.GenerateNewSeed();
            generator.ShowFloorsUpTo(floorView);
            personIndex = -1;
        }
        if (kb.digit0Key.wasPressedThisFrame) SetFloorView(-1);
        for (int i = 1; i <= 9; i++)
            if (kb[(Key)((int)Key.Digit1 + i - 1)].wasPressedThisFrame && i <= generator.floors)
                SetFloorView(i - 1);
        if (kb.pKey.wasPressedThisFrame && generator.people.Count > 0)
        {
            personIndex = (personIndex + 1) % generator.people.Count;
            var person = generator.people[personIndex];
            SetFloorView(person.floor);
            var target = person.Bounds.center;
            transform.position = target + new Vector3(-2.5f, 3.5f, -2.5f);
            transform.LookAt(target);
            var e = transform.eulerAngles;
            yaw = e.y;
            pitch = e.x > 180 ? e.x - 360 : e.x;
        }
    }

    void SetFloorView(int floor)
    {
        floorView = floor;
        generator.ShowFloorsUpTo(floor);
    }

    /// Rebuilds the same building (same seed) with a different number of storeys.
    void SetStoreys(int count)
    {
        count = Mathf.Clamp(count, MinStoreys, MaxStoreys);
        if (count == generator.floors) return;
        generator.floors = count;
        generator.Generate();
        floorView = -1;
        personIndex = -1;
    }

    void OnGUI()
    {
        if (generator == null) return;
        StoreyPanel();
        var view = floorView < 0 ? "all" : $"1-{floorView + 1}";
        var person = personIndex >= 0 && personIndex < generator.people.Count ? generator.people[personIndex] : null;
        var text =
            $"Seed {generator.seed}   Floors shown: {view}   People: {generator.people.Count}\n" +
            "RMB look · WASD move · Q/E down/up · Shift fast\n" +
            "1-9 cut away upper floors · 0 show all · P next person · R new building";
        if (person != null)
            text += $"\nPerson {person.id}: {person.pose} in {person.roomRole} (floor {person.floor + 1})";
        GUI.Box(new Rect(10, 10, 470, person != null ? 80 : 62), "");
        GUI.Label(new Rect(18, 14, 460, 80), text);
    }

    // Kept on the left: the Game view drops clicks along its right edge.
    void StoreyPanel()
    {
        const float x = 10, y = 100, w = 300;
        GUI.Box(new Rect(x, y, w, 96), "");
        GUI.Label(new Rect(x + 8, y + 4, w - 16, 22), $"Storeys: {generator.floors}");
        for (int n = MinStoreys; n <= MaxStoreys; n++)
        {
            var prev = GUI.backgroundColor;
            if (n == generator.floors) GUI.backgroundColor = new Color(1f, 0.7f, 0.25f);
            if (GUI.Button(new Rect(x + 8 + (n - MinStoreys) * 47, y + 28, 43, 28), n.ToString())) SetStoreys(n);
            GUI.backgroundColor = prev;
        }
        bool fx = GUI.Toggle(new Rect(x + 8, y + 66, w - 16, 22), generator.fireAndSmoke, " Fire and smoke");
        if (fx != generator.fireAndSmoke)
        {
            generator.fireAndSmoke = fx;
            generator.Generate();
            floorView = -1;
            personIndex = -1;
        }
    }
}

