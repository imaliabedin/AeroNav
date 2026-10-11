using UnityEngine;
using UnityEngine.InputSystem;

/// Build-mode editing: Minecraft-style cube placement onto the hovered face, per-wall
/// invisibility, and placing decorative objects from the Objects panel.
[RequireComponent(typeof(Camera))]
public class MapBuilder : MonoBehaviour
{
    public enum Tool { Wall, Win, DroneStart, Invisible, Object }

    public CellMap map;
    public PropSet props;
    public Material ghostMaterial;
    public Tool tool = Tool.Wall;
    public float reach = 200f;

    [Header("Objects")]
    public int selectedProp;
    public float propYaw;

    public string ToolName => tool switch
    {
        Tool.DroneStart => "Drone start",
        Tool.Invisible => "Invisible toggle",
        Tool.Object => SelectedEntry != null ? SelectedEntry.name : "Object",
        _ => tool + " cube"
    };

    PropCatalog.Entry SelectedEntry => props && props.catalog ? props.catalog.Get(selectedProp) : null;

    Camera cam;
    GameObject ghost;
    GameObject propGhost;
    int propGhostIndex = -1;
    bool panelOpen = true;
    Rect panelRect;

    void Awake()
    {
        cam = GetComponent<Camera>();
        ghost = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ghost.name = "Build Ghost";
        Destroy(ghost.GetComponent<Collider>());
        ghost.GetComponent<Renderer>().sharedMaterial = ghostMaterial;
        ghost.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        ghost.SetActive(false);
    }

    void OnDisable() => HideGhosts();

    void HideGhosts()
    {
        if (ghost) ghost.SetActive(false);
        if (propGhost) propGhost.SetActive(false);
    }

    void Update()
    {
        var kb = Keyboard.current;
        var mouse = Mouse.current;
        if (kb == null || mouse == null) return;

        if (kb.digit1Key.wasPressedThisFrame) tool = Tool.Wall;
        if (kb.digit2Key.wasPressedThisFrame) tool = Tool.Win;
        if (kb.digit3Key.wasPressedThisFrame) tool = Tool.DroneStart;
        if (kb.digit4Key.wasPressedThisFrame) tool = Tool.Invisible;
        if (kb.backspaceKey.wasPressedThisFrame) map.ClearAll();
        if (kb.f5Key.wasPressedThisFrame) map.Save();
        if (kb.f9Key.wasPressedThisFrame && map.Load()) ResetDrone();

        if (tool == Tool.Object)
        {
            float step = kb.shiftKey.isPressed ? 90f : 15f;
            if (kb.zKey.wasPressedThisFrame) propYaw = Mathf.Repeat(propYaw - step, 360f);
            if (kb.xKey.wasPressedThisFrame) propYaw = Mathf.Repeat(propYaw + step, 360f);
        }

        HideGhosts();
        var mousePos = mouse.position.ReadValue();
        if (IsPointerOverUI(mousePos)) return;
        var ray = cam.ScreenPointToRay(mousePos);
        bool click = mouse.leftButton.wasPressedThisFrame;

        if (kb.ctrlKey.isPressed)
            UpdateRemove(ray, click);
        else if (tool == Tool.Object)
            UpdateObjectPlacement(ray, click);
        else
            UpdateCubeTools(ray, click);
    }

    // Ctrl+LMB: removes whatever cube or object is nearest under the mouse.
    void UpdateRemove(Ray ray, bool click)
    {
        if (!FindSurface(ray, out _, out var cell, out var prop)) return;

        ghost.SetActive(true);
        if (prop != null && PropSet.RendererBounds(prop.gameObject, out var b))
        {
            ghost.transform.position = b.center;
            ghost.transform.localScale = b.size * 1.05f;
        }
        else
        {
            ghost.transform.position = cell.Coord;
            ghost.transform.localScale = Vector3.one * 1.05f;
        }

        if (!click) return;
        if (prop != null) props.Remove(prop);
        else map.Remove(cell.Coord);
    }

    void UpdateObjectPlacement(Ray ray, bool click)
    {
        if (SelectedEntry == null || SelectedEntry.prefab == null) return;

        Vector3 point;
        // Aim through invisible walls: they aren't drawn during a run, so objects on them would float.
        if (FindSurface(ray, out var hit, out _, out _, skipInvisible: true)) point = hit.point;
        else if (!GroundPoint(ray, out point)) return;

        EnsurePropGhost();
        propGhost.SetActive(true);
        propGhost.transform.SetPositionAndRotation(point, Quaternion.Euler(0f, propYaw, 0f));

        if (click) props.Place(selectedProp, point, propYaw);
    }

    void UpdateCubeTools(Ray ray, bool click)
    {
        if (!FindTarget(ray, out var placeCell, out var hoveredCell, out bool hitCube)) return;

        if (tool == Tool.Invisible)
        {
            var target = hitCube ? map.Get(hoveredCell) : null;
            if (target == null || target.type != CellType.Wall) return;
            ghost.SetActive(true);
            ghost.transform.position = hoveredCell;
            ghost.transform.localScale = Vector3.one * 1.05f;
            if (click) map.SetInvisible(hoveredCell, !target.invisible);
            return;
        }

        ghost.SetActive(true);
        ghost.transform.position = placeCell;
        ghost.transform.localScale = Vector3.one * (tool == Tool.DroneStart ? 0.4f : 0.9f);

        if (!click) return;
        switch (tool)
        {
            case Tool.Wall:
            case Tool.Win:
                if (placeCell != map.droneStart)
                    map.Place(placeCell, tool == Tool.Win ? CellType.Win : CellType.Wall);
                break;
            case Tool.DroneStart:
                if (map.Get(placeCell) == null)
                {
                    map.droneStart = placeCell;
                    ResetDrone();
                }
                break;
        }
    }

    /// Nearest *visible* cube under the mouse (objects and hidden cubes are skipped),
    /// falling back to the ground plane under the floor layer.
    bool FindTarget(Ray ray, out Vector3Int placeCell, out Vector3Int hoveredCell, out bool hitCube)
    {
        placeCell = hoveredCell = default;
        hitCube = false;

        foreach (var h in SortedHits(ray))
        {
            var cell = h.collider.GetComponent<Cell>();
            if (cell == null || !cell.GetComponent<Renderer>().enabled) continue;
            hoveredCell = cell.Coord;
            placeCell = cell.Coord + Vector3Int.RoundToInt(h.normal);
            hitCube = true;
            return true;
        }

        if (GroundPoint(ray, out var p))
        {
            placeCell = Vector3Int.RoundToInt(p + Vector3.up * 0.5f);
            return true;
        }
        return false;
    }

    /// Nearest visible cube or placed object under the mouse.
    bool FindSurface(Ray ray, out RaycastHit hit, out Cell cell, out Prop prop, bool skipInvisible = false)
    {
        foreach (var h in SortedHits(ray))
        {
            cell = h.collider.GetComponent<Cell>();
            prop = cell == null ? h.collider.GetComponentInParent<Prop>() : null;
            if (cell != null && (!cell.GetComponent<Renderer>().enabled || (skipInvisible && cell.invisible))) continue;
            if (cell == null && prop == null) continue;
            hit = h;
            return true;
        }
        hit = default;
        cell = null;
        prop = null;
        return false;
    }

    RaycastHit[] SortedHits(Ray ray)
    {
        // Not layer 2 (the drone) nor 1 (city buildings, see CityColliders): clicks go through them to the ground.
        var hits = Physics.RaycastAll(ray, reach, ~((1 << 2) | (1 << 1)), QueryTriggerInteraction.Collide);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        return hits;
    }

    bool GroundPoint(Ray ray, out Vector3 point)
    {
        var ground = new Plane(Vector3.up, new Vector3(0, -0.5f, 0));
        point = default;
        if (!ground.Raycast(ray, out float t) || t >= reach) return false;
        point = ray.GetPoint(t);
        return true;
    }

    void EnsurePropGhost()
    {
        if (propGhost != null && propGhostIndex == selectedProp) return;
        if (propGhost != null) Destroy(propGhost);

        propGhost = PropSet.Build(SelectedEntry);
        propGhost.name = "Object Ghost";
        propGhostIndex = selectedProp;
        foreach (var c in propGhost.GetComponentsInChildren<Collider>()) Destroy(c);
        foreach (var r in propGhost.GetComponentsInChildren<Renderer>())
        {
            var mats = new Material[r.sharedMaterials.Length];
            for (int i = 0; i < mats.Length; i++) mats[i] = ghostMaterial;
            r.sharedMaterials = mats;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }
    }

    // ---------- Objects panel (left side under the HUD, build mode only; OnGUI stops while disabled) ----------
    // Not on the right edge: the Editor's Game view swallows clicks along its right border.

    GUIStyle label;

    bool IsPointerOverUI(Vector2 mouseScreenPos)
    {
        var gui = new Vector2(mouseScreenPos.x, Screen.height - mouseScreenPos.y); // IMGUI is top-left origin
        var sim = SimulationManager.Instance;
        return panelRect.Contains(gui) || (sim != null && sim.HudRect.Contains(gui));
    }

    void OnGUI()
    {
        if (SimulationManager.HideHud) { panelRect = Rect.zero; return; }   // the website shows its own tools
        if (label == null)
        {
            label = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true };
            label.normal.textColor = Color.white;
        }

        var sim = SimulationManager.Instance;
        float top = sim != null && sim.HudRect.height > 0 ? sim.HudRect.yMax + 8 : 10;

        if (!panelOpen)
        {
            panelRect = new Rect(10, top, 90, 26);
            if (GUI.Button(panelRect, "Objects  +")) panelOpen = true;
            return;
        }

        var catalog = props ? props.catalog : null;
        int count = catalog ? catalog.Count : 0;
        const float w = 230f, rowH = 26f, gap = 4f;
        int rows = (count + 1) / 2;
        float h = 34 + rows * (rowH + gap) + 58;
        panelRect = new Rect(10, top, w, h);
        var prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.65f);
        GUI.DrawTexture(panelRect, Texture2D.whiteTexture);
        GUI.color = prev;

        float x = panelRect.x + 10, y = panelRect.y + 6, inner = w - 20;
        GUI.Label(new Rect(x, y, inner, 22), "<color=#F0F0F0><b>OBJECTS</b>  click to place</color>", label);
        if (GUI.Button(new Rect(panelRect.xMax - 30, y + 1, 22, 20), "–")) panelOpen = false;
        y += 28;

        float bw = (inner - gap) * 0.5f;
        var prevBg = GUI.backgroundColor;
        for (int i = 0; i < count; i++)
        {
            var r = new Rect(x + (i % 2) * (bw + gap), y + (i / 2) * (rowH + gap), bw, rowH);
            bool selected = tool == Tool.Object && selectedProp == i;
            GUI.backgroundColor = selected ? new Color(1f, 0.85f, 0.1f) : prevBg;
            if (GUI.Button(r, catalog.Get(i).name))
            {
                tool = Tool.Object;
                selectedProp = i;
            }
        }
        GUI.backgroundColor = prevBg;
        y += rows * (rowH + gap) + 2;

        GUI.Label(new Rect(x, y, inner, 22), $"<color=#F0F0F0>Rotation <b>{propYaw:0}°</b>   Z / X  (Shift 90°)</color>", label);
        y += 24;
        if (GUI.Button(new Rect(x, y, inner, 24), "Clear objects") && props) props.Clear();
    }

    void ResetDrone()
    {
        var sim = SimulationManager.Instance;
        if (sim == null) return;
        sim.drone.ResetTo(map.droneStart);
    }
}
