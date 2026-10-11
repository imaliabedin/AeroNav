using System.Collections.Generic;
using UnityEngine;

/// The drone: plans a route to the win cube with A* over the cell map, then flies it
/// one cell at a time. It wins when it arrives inside the (see-through) win cube.
[RequireComponent(typeof(Rigidbody), typeof(BoxCollider))]
public class Drone : MonoBehaviour
{
    public enum State { Idle, Following, Arrived, NoPath }

    [Header("Motion")]
    public float cellsPerSecond = 4f;

    [Header("Visuals")]
    public bool showPath = true;
    public Material pathMaterial;
    public Material trailMaterial;

    [Header("Sonar mode (set by SonarNavigator; defaults = Full-Knowledge)")]
    /// Planner's obstacle test in Sonar mode (the sonar belief). Null = Full-Knowledge planning from the map.
    public System.Func<Vector3Int, bool> plannerBlocked;
    /// Known outer bounds for Sonar-mode planning.
    public BoundsInt? plannerBounds;
    /// Upper limit on cellsPerSecond (Sonar mode caps speed to what the sensor can react to).
    public float speedCap = float.PositiveInfinity;
    /// Raised at the start of Run(), before planning (lets the sonar take its first readings).
    public event System.Action PlanningStarted;

    [Header("Natural motion")]
    [Tooltip("Accelerate and brake like a real drone: slow down before turns and the goal instead of moving at constant speed.")]
    public bool naturalMotion = true;
    public float acceleration = 6f;     // cells/s^2, also used for braking
    public float cornerSpeed = 2.2f;    // cells/s through a 90-degree turn (on the rounded arc)
    [Tooltip("Corners are flown as smooth arcs of this radius (cells) instead of sharp turns. The arc stays inside the free corner cell.")]
    public float cornerRadius = 0.45f;
    [Tooltip("When a replan finds no path (e.g. a stray echo briefly blocks the way), hover and retry for this long before giving up.")]
    public float noPathRetrySeconds = 3f;
    public float CurrentSpeed { get; private set; }
    public Vector3 Velocity { get; private set; }

    public State Status { get; private set; } = State.Idle;
    public bool Running { get; private set; }
    public AStarPlanner.Result Plan { get; private set; }
    public int Steps => movesTotal;
    public int PathSteps => Plan != null && Plan.found ? Plan.path.Count - 1 : 0;
    public int PathIndex => pathIndex;
    public int Replans { get; private set; }

    LineRenderer pathLine;
    TrailRenderer trail;
    int pathIndex;              // index into Plan.path of the cell we're currently at
    int movesTotal;             // cells flown this run (equals pathIndex unless the drone replanned)
    Vector3 track;              // position along the grid path; the transform shows it with rounded corners
    bool waitingForPath;        // hovering after a failed replan, retrying
    float noPathWait, retryTimer;
    bool moving;
    bool retreating;            // heading back to Plan.path[pathIndex] because the next cell became blocked
    bool replanRequested;
    Vector3 moveTarget;

    float HalfSize => transform.localScale.x * 0.5f;

    void Awake()
    {
        var rb = GetComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;
        gameObject.layer = 2; // Ignore Raycast: keeps the drone out of the builder's mouse rays

        var lineGo = new GameObject("Planned Path");
        lineGo.transform.SetParent(transform, false);
        pathLine = lineGo.AddComponent<LineRenderer>();
        pathLine.useWorldSpace = true;
        pathLine.widthMultiplier = 0.06f;
        pathLine.sharedMaterial = pathMaterial;
        pathLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        pathLine.positionCount = 0;

        trail = gameObject.AddComponent<TrailRenderer>();
        trail.time = 100000f;
        trail.minVertexDistance = 0.1f;
        trail.widthMultiplier = 0.08f;
        trail.sharedMaterial = trailMaterial;
        trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    /// Teleport to a cell and forget any plan.
    public void ResetTo(Vector3Int cell)
    {
        Running = false;
        moving = false;
        retreating = false;
        replanRequested = false;
        Status = State.Idle;
        Plan = null;
        pathIndex = 0;
        movesTotal = 0;
        Replans = 0;
        waitingForPath = false;
        noPathWait = 0f;
        transform.position = cell;
        track = cell;
        if (trail != null) trail.Clear();
        if (pathLine != null) pathLine.positionCount = 0;
    }

    /// Plan with A* from the current cell, then start flying.
    public void Run()
    {
        PlanningStarted?.Invoke();
        track = transform.position;
        var start = Vector3Int.RoundToInt(track);
        Plan = AStarPlanner.FindPath(CellMap.Instance, start, 200000, plannerBlocked, plannerBounds);
        pathIndex = 0;
        movesTotal = 0;
        Replans = 0;
        CurrentSpeed = 0f;
        waitingForPath = false;
        noPathWait = 0f;
        moving = false;
        retreating = false;
        replanRequested = false;

        if (!Plan.found)
        {
            Status = State.NoPath;
            Running = false;
            pathLine.positionCount = 0;
            Debug.Log($"[Drone] NO PATH ({Plan.failReason}) after expanding {Plan.expanded} nodes");
            if (SimulationManager.Instance != null) SimulationManager.Instance.OnDroneGaveUp();
            return;
        }

        Debug.Log($"[Drone] A* path: {PathSteps} moves, {Plan.expanded} nodes expanded, {Plan.milliseconds:0.00} ms");
        pathLine.positionCount = Plan.path.Count;
        for (int i = 0; i < Plan.path.Count; i++) pathLine.SetPosition(i, Plan.path[i]);
        Status = State.Following;
        Running = true;
    }

    public void Halt() { Running = false; }

    /// Sonar mode: the remaining path crosses a newly blocked cell. The drone replans when it next
    /// reaches a cell center; if the cell it is flying into is blocked, it turns back first.
    public void RequestReplan()
    {
        if (!Running || Plan == null) return;
        replanRequested = true;
        if (moving && !retreating && plannerBlocked != null && plannerBlocked(Vector3Int.RoundToInt(moveTarget)))
        {
            moveTarget = Plan.path[pathIndex];
            retreating = true;
        }
    }

    /// Plans again from the current cell with the same planner inputs. False if no path is left.
    bool Replan()
    {
        var start = Vector3Int.RoundToInt(track);
        Plan = AStarPlanner.FindPath(CellMap.Instance, start, 200000, plannerBlocked, plannerBounds);
        pathIndex = 0;
        Replans++;
        if (!Plan.found && noPathWait < noPathRetrySeconds)
        {
            waitingForPath = true;     // hover and retry (see Advance)
            pathLine.positionCount = 0;
            return false;
        }
        waitingForPath = false;
        noPathWait = 0f;
        if (!Plan.found)
        {
            Status = State.NoPath;
            Running = false;
            pathLine.positionCount = 0;
            Debug.Log($"[Drone] NO PATH after replan ({Plan.failReason})");
            if (SimulationManager.Instance != null) SimulationManager.Instance.OnDroneGaveUp();
            return false;
        }
        pathLine.positionCount = Plan.path.Count;
        for (int i = 0; i < Plan.path.Count; i++) pathLine.SetPosition(i, Plan.path[i]);
        return true;
    }

    void Update()
    {
        if (pathLine != null) pathLine.enabled = showPath;

        var sim = SimulationManager.Instance;
        if (!Running || (sim != null && sim.Paused)) return;
        Advance(Time.deltaTime);
    }

    /// Moves along the plan for `dt` seconds (called from Update; tests call it directly).
    public void Advance(float dt)
    {
        var before = transform.position;
        if (waitingForPath)
        {
            // Hovering: keep sensing (the sonar runs on its own) and retry the plan ten times a second.
            noPathWait += dt;
            retryTimer -= dt;
            if (retryTimer <= 0f) { retryTimer = 0.1f; Replan(); }
            if (waitingForPath || !Running) { CurrentSpeed = 0f; Velocity = Vector3.zero; return; }
        }
        float cruise = Mathf.Min(cellsPerSecond, speedCap);
        float speed = cruise;
        if (naturalMotion)
        {
            CurrentSpeed = Mathf.MoveTowards(CurrentSpeed, Mathf.Min(cruise, SpeedLimitAhead()), acceleration * dt);
            speed = Mathf.Max(CurrentSpeed, 0.25f);   // keep creeping so it never stalls between cells
        }
        // Spend this frame's movement budget; at high speed several cells may be crossed per frame.
        float budget = speed * dt;
        while (budget > 0f && Running)
        {
            if (!moving)
            {
                if (replanRequested)
                {
                    replanRequested = false;
                    if (!Replan()) break;
                }
                if (pathIndex >= Plan.path.Count - 1) { Arrive(); break; }
                moveTarget = Plan.path[pathIndex + 1];
                moving = true;
            }

            float remaining = Vector3.Distance(track, moveTarget);
            float step = Mathf.Min(budget, remaining);
            track = Vector3.MoveTowards(track, moveTarget, step);
            budget -= step;

            if (Vector3.Distance(track, moveTarget) < 1e-4f)
            {
                track = moveTarget;
                if (!naturalMotion) trail.AddPosition(moveTarget); // sharp corners only when not flying rounded arcs
                moving = false;
                movesTotal++;
                if (retreating) retreating = false;   // back on Plan.path[pathIndex]
                else pathIndex++;
            }
        }
        transform.position = naturalMotion ? Rounded(track) : track;
        Velocity = dt > 0f ? (transform.position - before) / dt : Vector3.zero;
    }

    /// The point on the flown curve for a position on the grid path: near a corner (within cornerRadius) the
    /// path is replaced by a quadratic Bezier arc from the incoming to the outgoing segment.
    Vector3 Rounded(Vector3 p)
    {
        if (Plan == null || !Plan.found || retreating || cornerRadius <= 0f) return p;
        var path = Plan.path;
        int i = pathIndex;
        float r = cornerRadius;
        if (moving && i + 2 < path.Count)                       // a corner ahead, at node i+1
        {
            Vector3 b = path[i + 1];
            float d = Vector3.Distance(p, b);
            if (d < r && IsCorner(path[i], b, path[i + 2], out var u, out var v)) return Arc(b, u, v, r, (r - d) / (2f * r));
        }
        if (i >= 1 && i + 1 < path.Count)                       // a corner just passed, at node i
        {
            Vector3 a = path[i];
            float d = Vector3.Distance(p, a);
            if (d < r && IsCorner(path[i - 1], a, path[i + 1], out var u, out var v)) return Arc(a, u, v, r, 0.5f + d / (2f * r));
        }
        return p;
    }

    static bool IsCorner(Vector3 prev, Vector3 node, Vector3 next, out Vector3 u, out Vector3 v)
    {
        u = (node - prev).normalized;
        v = (next - node).normalized;
        float dot = Vector3.Dot(u, v);
        return dot < 0.99f && dot > -0.5f;   // a real turn, but not a turn-back
    }

    static Vector3 Arc(Vector3 corner, Vector3 u, Vector3 v, float r, float t)
    {
        Vector3 p0 = corner - u * r, p2 = corner + v * r;
        return (1f - t) * (1f - t) * p0 + 2f * (1f - t) * t * corner + t * t * p2;
    }

    /// Fastest speed that still lets the drone brake (at `acceleration`) to the right speed for the next
    /// turn, or to a stop at the goal or a turn-back.
    float SpeedLimitAhead()
    {
        if (Plan == null || !Plan.found || Plan.path.Count < 2) return 0f;
        int last = Plan.path.Count - 1;
        Vector3 pos = track;
        Vector3 target = moving ? moveTarget : (Vector3)Plan.path[Mathf.Min(pathIndex + 1, last)];
        float dist = Vector3.Distance(pos, target);
        if (retreating) return Mathf.Sqrt(2f * acceleration * dist);          // turning back: stop, then go
        Vector3 dir = dist > 1e-4f ? (target - pos) / dist : Vector3.zero;
        for (int j = moving ? pathIndex + 1 : Mathf.Min(pathIndex + 1, last); j < last; j++)
        {
            Vector3 seg = (Vector3)(Plan.path[j + 1] - Plan.path[j]);
            if (dir == Vector3.zero) dir = seg;
            if (Vector3.Angle(dir, seg) < 1f) { dist += 1f; continue; }
            float turnSpeed = Vector3.Angle(dir, seg) > 135f ? 0.3f : cornerSpeed;
            return Mathf.Sqrt(turnSpeed * turnSpeed + 2f * acceleration * dist);
        }
        return Mathf.Sqrt(2f * acceleration * dist) + 0.5f;                   // ease in to the goal
    }

    /// End of the path: confirm we're physically inside a win cube.
    void Arrive()
    {
        Running = false;
        foreach (var c in Physics.OverlapBox(transform.position, Vector3.one * HalfSize, Quaternion.identity, ~0, QueryTriggerInteraction.Collide))
        {
            var cell = c.GetComponent<Cell>();
            if (cell != null && cell.type == CellType.Win)
            {
                Status = State.Arrived;
                Debug.Log($"[Drone] WIN after {Steps} moves (A* expanded {Plan.expanded} nodes)");
                if (SimulationManager.Instance != null) SimulationManager.Instance.OnDroneWon();
                return;
            }
        }
        Status = State.NoPath;
        Debug.LogWarning("[Drone] Reached end of path but no win cube here");
        if (SimulationManager.Instance != null) SimulationManager.Instance.OnDroneGaveUp();
    }

    // Scene view: cells A* expanded (closed set) and the chosen path.
    void OnDrawGizmos()
    {
        if (Plan == null) return;
        Gizmos.color = new Color(0.3f, 0.6f, 1f, 0.35f);
        foreach (var c in Plan.closed) Gizmos.DrawCube(c, Vector3.one * 0.25f);
        Gizmos.color = Color.yellow;
        for (int i = 1; i < Plan.path.Count; i++) Gizmos.DrawLine(Plan.path[i - 1], Plan.path[i]);
    }
}
