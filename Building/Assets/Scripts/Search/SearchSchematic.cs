using System.Collections.Generic;
using Drone2.Building;
using UnityEngine;
using UnityEngine.Rendering;

/// See-through schematic of the whole building for the side panel: the floors stacked with gaps
/// (an exploded view), low translucent walls, every search spot tinted by the drone that must search it
/// (faint) or that has searched it (bright), and each drone as a large marker with its planned route.
/// It lives far from the building on its own layer, so only the schematic camera sees it.
public class SearchSchematic
{
    const float FloorGap = 11f;
    const float WallHeight = 0.9f;
    static readonly Vector3 Offset = new Vector3(400f, 0f, 0f);
    static readonly Color Background = new Color(0.06f, 0.07f, 0.1f);

    readonly SearchCoordinator coord;
    readonly SearchGrid grid;
    readonly BuildingGenerator gen;
    readonly Camera cam;
    readonly GameObject root;
    readonly Mesh tiles, dynamic;
    readonly List<Vector3Int> tileCells = new();
    readonly List<Color> tileColors = new();
    readonly List<Transform> markers = new();
    readonly List<Vector3> lineVerts = new();
    readonly List<Color> lineColors = new();
    readonly Material fill, overlay;

    public SearchSchematic(SearchCoordinator coord, BuildingGenerator gen, Camera cam)
    {
        this.coord = coord;
        this.gen = gen;
        this.cam = cam;
        grid = coord.Grid;
        fill = SonarVisualizer.OverlayMaterial(false);
        overlay = SonarVisualizer.OverlayMaterial(true);
        root = new GameObject("Schematic") { layer = SearchCoordinator.SchematicLayer };
        root.transform.position = Offset;

        BuildStatic();
        tiles = BuildTiles();
        dynamic = NewMesh("Schematic routes", overlay);
        foreach (var d in coord.drones) markers.Add(Marker(d.color, 2.2f, 2.8f, "Drone marker"));
        foreach (var fire in coord.Fires)
        {
            var m = Marker(new Color(1f, 0.12f, 0.08f, 0.95f), 0.8f, 1f, "Fire");
            m.position = Point(fire.position - grid.origin, fire.floor) + Vector3.up * 0.3f;
        }

        cam.orthographic = true;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Background;
        cam.cullingMask = 1 << SearchCoordinator.SchematicLayer;
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 400f;
        // Mostly from the building's short side and fairly low, so the stack of floors fills a tall panel.
        cam.transform.rotation = Quaternion.Euler(26f, -72f, 0f);
        Refresh();
    }

    public void Destroy()
    {
        if (Application.isPlaying) Object.Destroy(root); else Object.DestroyImmediate(root);
    }

    /// Point in the schematic for a building-local position on (possibly fractional) floor f.
    Vector3 Point(Vector3 local, float f) => Offset + new Vector3(local.x, f * FloorGap, local.z);

    /// Floor labels for the HUD, left of each floor.
    public IEnumerable<(Vector3, string)> Labels()
    {
        for (int f = 0; f < grid.floors; f++)
            yield return (Point(new Vector3(-2.2f, 0f, gen.depth * 0.5f), f), $"{f + 1}F");
    }

    /// Fits the stack of floors into the camera's viewport, leaving room for the header and footer.
    public void FitCamera()
    {
        var center = Point(new Vector3(gen.width / 2f, 0f, gen.depth / 2f), (grid.floors - 1) / 2f);
        var inv = Quaternion.Inverse(cam.transform.rotation);
        float hx = 0f, hy = 0f;
        for (int i = 0; i < 8; i++)
        {
            var p = Point(new Vector3((i & 1) * gen.width, 0f, ((i >> 1) & 1) * gen.depth), (i >> 2) * (grid.floors - 1))
                    + Vector3.up * ((i >> 2) * WallHeight);
            var q = inv * (p - center);
            hx = Mathf.Max(hx, Mathf.Abs(q.x));
            hy = Mathf.Max(hy, Mathf.Abs(q.y));
        }
        float size = Mathf.Max(hy, hx / Mathf.Max(0.1f, cam.aspect)) * 1.12f;
        cam.orthographicSize = size * 1.25f;   // header and footer take about a fifth of the panel
        cam.transform.position = center - cam.transform.forward * 150f + cam.transform.up * (size * 0.08f);
    }

    // ---- Static parts ---------------------------------------------------------------------------------

    void BuildStatic()
    {
        var v = new List<Vector3>();
        var c = new List<Color>();
        var tris = new List<int>();
        var lv = new List<Vector3>();
        var lc = new List<Color>();
        var exterior = new Color(0.85f, 0.9f, 1f, 0.3f);
        var interior = new Color(0.85f, 0.9f, 1f, 0.17f);
        var window = new Color(0.45f, 0.8f, 1f, 0.25f);
        var top = new Color(0.9f, 0.95f, 1f, 0.6f);

        for (int f = 0; f < grid.floors; f++)
        {
            var fp = gen.Plan.floors[f];
            for (int x = 0; x <= fp.width; x++)
                for (int z = 0; z < fp.depth; z++)
                    Wall(fp, true, x, z, f);
            for (int x = 0; x < fp.width; x++)
                for (int z = 0; z <= fp.depth; z++)
                    Wall(fp, false, x, z, f);

            // Floor outline and stair core.
            Rect(lv, lc, new Rect(0, 0, gen.width, gen.depth), f, 0.02f, new Color(0.8f, 0.85f, 0.95f, 0.5f));
            var s = grid.stairwell;
            Rect(lv, lc, new Rect(s.x, s.y, s.width, s.height), f, 0.04f, new Color(1f, 0.85f, 0.2f, 0.8f));
            if (f < grid.floors - 1)
                for (int k = 0; k < 4; k++)
                {
                    var p = new Vector3(s.x + (k & 1) * s.width, 0f, s.y + (k >> 1) * s.height);
                    lv.Add(Point(p, f)); lv.Add(Point(p, f + 1));
                    lc.Add(new Color(1f, 0.85f, 0.2f, 0.35f)); lc.Add(new Color(1f, 0.85f, 0.2f, 0.35f));
                }
        }

        var walls = NewMesh("Schematic walls", fill);
        walls.SetVertices(v);
        walls.SetColors(c);
        walls.SetTriangles(tris, 0);
        walls.RecalculateBounds();

        var outline = NewMesh("Schematic outlines", fill);
        outline.SetVertices(lv);
        outline.SetColors(lc);
        outline.SetIndices(Sequence(lv.Count), MeshTopology.Lines, 0);
        outline.RecalculateBounds();

        void Wall(FloorPlan fp, bool vertical, int x, int z, int f)
        {
            var kind = fp.GetEdge(vertical, x, z);
            if (kind is EdgeKind.None or EdgeKind.Door or EdgeKind.Entrance) return;
            var (a, b) = fp.EdgeSides(vertical, x, z);
            var col = kind == EdgeKind.Window ? window : a == null || b == null ? exterior : interior;
            var p0 = new Vector3(x, 0f, z);
            var p1 = vertical ? new Vector3(x, 0f, z + 1) : new Vector3(x + 1, 0f, z);
            int i = v.Count;
            v.Add(Point(p0, f)); v.Add(Point(p1, f));
            v.Add(Point(p1, f) + Vector3.up * WallHeight); v.Add(Point(p0, f) + Vector3.up * WallHeight);
            for (int k = 0; k < 4; k++) c.Add(col);
            tris.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
            lv.Add(v[i + 3]); lv.Add(v[i + 2]);
            lc.Add(top); lc.Add(top);
        }
    }

    void Rect(List<Vector3> lv, List<Color> lc, Rect r, int f, float lift, Color col)
    {
        var corners = new[]
        {
            new Vector3(r.xMin, lift, r.yMin), new Vector3(r.xMax, lift, r.yMin),
            new Vector3(r.xMax, lift, r.yMax), new Vector3(r.xMin, lift, r.yMax),
        };
        for (int k = 0; k < 4; k++)
        {
            lv.Add(Point(corners[k], f)); lv.Add(Point(corners[(k + 1) % 4], f));
            lc.Add(col); lc.Add(col);
        }
    }

    Mesh BuildTiles()
    {
        var v = new List<Vector3>();
        var tris = new List<int>();
        const float h = SearchGrid.Cell * 0.5f;   // tiles touch: gaps between them shimmer at this scale
        for (int f = 0; f < grid.floors; f++)
            for (int x = 0; x < grid.nx; x++)
                for (int z = 0; z < grid.nz; z++)
                {
                    if (grid.state[f, x, z] == SearchGrid.Spot.None) continue;
                    var p = Point(new Vector3(x * SearchGrid.Cell, 0.01f, z * SearchGrid.Cell), f);
                    int i = v.Count;
                    v.Add(p + new Vector3(-h, 0, -h)); v.Add(p + new Vector3(h, 0, -h));
                    v.Add(p + new Vector3(h, 0, h)); v.Add(p + new Vector3(-h, 0, h));
                    tris.AddRange(new[] { i, i + 2, i + 1, i, i + 3, i + 2 });
                    tileCells.Add(new Vector3Int(x, f, z));
                    for (int k = 0; k < 4; k++) tileColors.Add(Color.clear);
                }
        var m = NewMesh("Schematic spots", fill);
        m.indexFormat = IndexFormat.UInt32;
        m.SetVertices(v);
        m.SetColors(tileColors);
        m.SetTriangles(tris, 0);
        m.RecalculateBounds();
        return m;
    }

    Transform Marker(Color col, float width, float height, string name)
    {
        var m = new Mesh { name = name };
        float w = width / 2f, h = height / 2f;
        m.SetVertices(new List<Vector3>
        {
            new(0, h, 0), new(0, -h, 0), new(w, 0, 0), new(-w, 0, 0), new(0, 0, w), new(0, 0, -w),
        });
        var dark = new Color(col.r * 0.55f, col.g * 0.55f, col.b * 0.55f, col.a);
        m.SetColors(new List<Color> { col, dark, col, dark, col, dark });
        m.SetTriangles(new[] { 0, 2, 4, 0, 4, 3, 0, 3, 5, 0, 5, 2, 1, 4, 2, 1, 3, 4, 1, 5, 3, 1, 2, 5 }, 0);
        m.RecalculateBounds();
        var go = new GameObject(name) { layer = SearchCoordinator.SchematicLayer };
        go.transform.SetParent(root.transform, true);
        go.AddComponent<MeshFilter>().sharedMesh = m;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = overlay;
        r.shadowCastingMode = ShadowCastingMode.Off;
        return go.transform;
    }

    Mesh NewMesh(string name, Material mat)
    {
        var go = new GameObject(name) { layer = SearchCoordinator.SchematicLayer };
        go.transform.SetParent(root.transform, false);
        go.transform.position = Vector3.zero;   // vertices are in world space
        var m = new Mesh { name = name };
        go.AddComponent<MeshFilter>().sharedMesh = m;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        return m;
    }

    static List<int> Sequence(int n)
    {
        var l = new List<int>(n);
        for (int i = 0; i < n; i++) l.Add(i);
        return l;
    }

    // ---- Live parts -----------------------------------------------------------------------------------

    /// Updates the spot colours, drone markers, routes and found people.
    public void Refresh()
    {
        for (int i = 0; i < tileCells.Count; i++)
        {
            var t = tileCells[i];
            var col = SpotColor(grid.state[t.y, t.x, t.z], grid.owner[t.y, t.x, t.z], grid.seenBy[t.y, t.x, t.z]);
            for (int k = 0; k < 4; k++) tileColors[i * 4 + k] = col;
        }
        tiles.SetColors(tileColors);

        lineVerts.Clear();
        lineColors.Clear();
        for (int i = 0; i < coord.drones.Count; i++)
        {
            var d = coord.drones[i];
            var local = d.transform.position - grid.origin;
            float f = grid.FloorAt(d.transform.position.y);
            var plane = Point(local, f);
            markers[i].position = plane + Vector3.up * 1.8f;
            markers[i].rotation = Quaternion.Euler(0f, d.transform.eulerAngles.y, 0f);

            var col = d.color;
            Line(plane, plane + Vector3.up * 1.8f, col);
            Circle(plane + Vector3.up * 0.03f, 1.4f, col);
            Circle(plane + Vector3.up * 0.03f, 2.1f, new Color(col.r, col.g, col.b, 0.4f));
            var fwd = d.transform.forward;
            fwd.y = 0f;
            Line(markers[i].position, markers[i].position + fwd.normalized * 2.2f, Color.white);

            // Remaining route.
            var from = plane + Vector3.up * 0.05f;
            var route = new Color(col.r, col.g, col.b, 0.9f);
            for (int k = d.RouteIndex; k < d.Route.Count; k++)
            {
                var w = d.Route[k];
                var to = Point(w - grid.origin, grid.FloorAt(w.y)) + Vector3.up * 0.05f;
                Line(from, to, route);
                from = to;
            }
        }

        foreach (var p in gen.people)
        {
            if (!coord.PersonFound(p)) continue;
            int f = grid.StoreyOf(p.Bounds.min.y + 0.1f);
            var at = Point(p.Bounds.center - grid.origin, f) + Vector3.up * 0.25f;
            var red = new Color(1f, 0.25f, 0.3f, 1f);
            Line(at + new Vector3(-0.4f, 0, -0.4f), at + new Vector3(0.4f, 0, 0.4f), red);
            Line(at + new Vector3(-0.4f, 0, 0.4f), at + new Vector3(0.4f, 0, -0.4f), red);
            Line(at, at + Vector3.up * 0.9f, red);
            Circle(at, 0.55f, red);
        }

        dynamic.Clear();
        dynamic.SetVertices(lineVerts);
        dynamic.SetColors(lineColors);
        dynamic.SetIndices(Sequence(lineVerts.Count), MeshTopology.Lines, 0);
        dynamic.bounds = new Bounds(Point(new Vector3(gen.width / 2f, 0, gen.depth / 2f), grid.floors / 2f), Vector3.one * 200f);
    }

    Color SpotColor(SearchGrid.Spot s, int owner, int seenBy)
    {
        Color DroneColor(int i) => i >= 0 && i < coord.drones.Count ? coord.drones[i].color : Color.gray;
        switch (s)
        {
            case SearchGrid.Spot.Seen:
            {
                var c = DroneColor(seenBy);
                c.a = 0.62f;
                return c;
            }
            case SearchGrid.Spot.Unseen:
            {
                var c = DroneColor(owner);
                c.a = 0.13f;
                return c;
            }
            case SearchGrid.Spot.Unreachable:
                return new Color(0.7f, 0.7f, 0.7f, 0.08f);
            default:
                return Color.clear;
        }
    }

    void Line(Vector3 a, Vector3 b, Color c)
    {
        lineVerts.Add(a); lineColors.Add(c);
        lineVerts.Add(b); lineColors.Add(c);
    }

    void Circle(Vector3 center, float r, Color c)
    {
        const int n = 32;
        var prevPoint = center + new Vector3(r, 0, 0);
        for (int i = 1; i <= n; i++)
        {
            float a = i * Mathf.PI * 2f / n;
            var p = center + new Vector3(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r);
            Line(prevPoint, p, c);
            prevPoint = p;
        }
    }
}
