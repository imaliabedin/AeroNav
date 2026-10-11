using System.Collections.Generic;
using UnityEngine;

namespace Drone2.Building
{
    /// Wall sides of a region. Each side's props face into the region.
    public enum Side { South, North, West, East }

    /// A spot where a person can sit, found while furnishing.
    public struct SeatSpot
    {
        public Vector3 position; // on the seat surface
        public float yaw;        // facing direction
        public string kind;
    }

    /// Floor occupancy for one region, at quarter-metre resolution, in building-local metres.
    /// "solid" cells hold furniture. "clear" cells must stay walkable (doorways, the space in front of furniture).
    public class RegionSpace
    {
        public const float Res = 0.25f;
        public const float WallHalf = 0.1f;
        public static bool LogFailures;

        public readonly Region region;
        public readonly FloorPlan plan;
        public readonly Transform parent;
        public readonly float floorY;
        public readonly float ceilingY;
        public readonly Rect interior;
        public readonly List<SeatSpot> seats = new();
        public readonly List<Rect> windowZones = new();
        public readonly List<Rect> doorZones = new();

        readonly bool[,] solid;
        readonly bool[,] clear;
        readonly int nx, nz;

        public RegionSpace(Region region, FloorPlan plan, Transform parent, float floorY, float ceilingY)
        {
            this.region = region;
            this.plan = plan;
            this.parent = parent;
            this.floorY = floorY;
            this.ceilingY = ceilingY;
            var r = region.rect;
            interior = Rect.MinMaxRect(r.xMin + WallHalf, r.yMin + WallHalf, r.xMax - WallHalf, r.yMax - WallHalf);
            nx = Mathf.FloorToInt(interior.width / Res + 0.001f);
            nz = Mathf.FloorToInt(interior.height / Res + 0.001f);
            solid = new bool[nx, nz];
            clear = new bool[nx, nz];
            MarkOpenings();
        }

        // ---- Grid queries -------------------------------------------------------------------

        void CellRange(Rect r, out int x0, out int z0, out int x1, out int z1)
        {
            x0 = Mathf.FloorToInt((r.xMin - interior.xMin) / Res + 0.01f);
            z0 = Mathf.FloorToInt((r.yMin - interior.yMin) / Res + 0.01f);
            x1 = Mathf.CeilToInt((r.xMax - interior.xMin) / Res - 0.01f);
            z1 = Mathf.CeilToInt((r.yMax - interior.yMin) / Res - 0.01f);
        }

        public bool Inside(Rect r) =>
            r.xMin >= interior.xMin - 0.001f && r.yMin >= interior.yMin - 0.001f &&
            r.xMax <= interior.xMax + 0.001f && r.yMax <= interior.yMax + 0.001f;

        /// Shrinks a rectangle slightly so props that touch (but don't really overlap) are allowed.
        static Rect Shrink(Rect r) => Rect.MinMaxRect(r.xMin + 0.06f, r.yMin + 0.06f, r.xMax - 0.06f, r.yMax - 0.06f);

        public bool IsFree(Rect r, bool respectClear = true)
        {
            if (!Inside(r)) return false;
            CellRange(Shrink(r), out int x0, out int z0, out int x1, out int z1);
            for (int x = Mathf.Max(0, x0); x < Mathf.Min(nx, x1); x++)
                for (int z = Mathf.Max(0, z0); z < Mathf.Min(nz, z1); z++)
                    if (solid[x, z] || respectClear && clear[x, z]) return false;
            return true;
        }

        /// True when no furniture sits in the rectangle (clipped to the interior).
        public bool NoSolid(Rect r)
        {
            CellRange(Shrink(r), out int x0, out int z0, out int x1, out int z1);
            for (int x = Mathf.Max(0, x0); x < Mathf.Min(nx, x1); x++)
                for (int z = Mathf.Max(0, z0); z < Mathf.Min(nz, z1); z++)
                    if (solid[x, z]) return false;
            return true;
        }

        public void Mark(Rect r, bool asSolid)
        {
            // Round to the nearest cell so marks don't bleed into a neighbour's quarter-metre cell.
            int x0 = Mathf.RoundToInt((r.xMin - interior.xMin) / Res), x1 = Mathf.RoundToInt((r.xMax - interior.xMin) / Res);
            int z0 = Mathf.RoundToInt((r.yMin - interior.yMin) / Res), z1 = Mathf.RoundToInt((r.yMax - interior.yMin) / Res);
            for (int x = Mathf.Max(0, x0); x < Mathf.Min(nx, x1); x++)
                for (int z = Mathf.Max(0, z0); z < Mathf.Min(nz, z1); z++)
                    if (asSolid) solid[x, z] = true;
                    else clear[x, z] = true;
        }

        /// Keeps the floor in front of every door clear, and notes windows so tall furniture avoids them.
        void MarkOpenings()
        {
            var r = region.rect;
            for (int z = r.yMin; z < r.yMax; z++)
            {
                MarkOpening(plan.vEdges[r.xMin, z], Rect.MinMaxRect(r.xMin, z - 0.2f, r.xMin + 1.3f, z + 1.2f));
                MarkOpening(plan.vEdges[r.xMax, z], Rect.MinMaxRect(r.xMax - 1.3f, z - 0.2f, r.xMax, z + 1.2f));
            }
            for (int x = r.xMin; x < r.xMax; x++)
            {
                MarkOpening(plan.hEdges[x, r.yMin], Rect.MinMaxRect(x - 0.2f, r.yMin, x + 1.2f, r.yMin + 1.3f));
                MarkOpening(plan.hEdges[x, r.yMax], Rect.MinMaxRect(x - 0.2f, r.yMax - 1.3f, x + 1.2f, r.yMax));
            }
        }

        void MarkOpening(EdgeKind kind, Rect zone)
        {
            if (kind == EdgeKind.Door || kind == EdgeKind.Entrance)
            {
                Mark(zone, false);
                doorZones.Add(zone);
            }
            else if (kind == EdgeKind.Window) windowZones.Add(zone);
        }

        /// True when a person fits here: inside the room, off the furniture (unless ignoreSolid) and out of doorways.
        public bool IsPersonFree(Rect r, bool ignoreSolid = false)
        {
            if (!ignoreSolid && (!Inside(r) || !NoSolid(r))) return false;
            foreach (var d in doorZones) if (d.Overlaps(r)) return false;
            return true;
        }

        // ---- Wall geometry ------------------------------------------------------------------

        public float WallLength(Side s) => s is Side.South or Side.North ? interior.width : interior.height;

        public static float SideYaw(Side s) => s switch
        {
            Side.South => 0f,
            Side.North => 180f,
            Side.West => 90f,
            _ => 270f,
        };

        /// Footprint of an item of width w and depth d with its back on wall s, starting t metres along it.
        public Rect WallRect(Side s, float t, float w, float d) => s switch
        {
            Side.South => new Rect(interior.xMin + t, interior.yMin, w, d),
            Side.North => new Rect(interior.xMin + t, interior.yMax - d, w, d),
            Side.West => new Rect(interior.xMin, interior.yMin + t, d, w),
            _ => new Rect(interior.xMax - d, interior.yMin + t, d, w),
        };

        /// The strip of floor in front of a wall footprint.
        public Rect FrontRect(Side s, Rect foot, float depth) => s switch
        {
            Side.South => new Rect(foot.xMin, foot.yMax, foot.width, depth),
            Side.North => new Rect(foot.xMin, foot.yMin - depth, foot.width, depth),
            Side.West => new Rect(foot.xMax, foot.yMin, depth, foot.height),
            _ => new Rect(foot.xMin - depth, foot.yMin, depth, foot.height),
        };

        /// Point on the wall face t metres along side s, at height y above the floor, and the yaw facing out of it.
        public Vector3 WallPoint(Side s, float t, float y)
        {
            float fy = floorY + y;
            return s switch
            {
                Side.South => new Vector3(interior.xMin + t, fy, interior.yMin),
                Side.North => new Vector3(interior.xMin + t, fy, interior.yMax),
                Side.West => new Vector3(interior.xMin, fy, interior.yMin + t),
                _ => new Vector3(interior.xMax, fy, interior.yMin + t),
            };
        }

        /// The kind of the plan edge behind a point t metres along a wall.
        public EdgeKind EdgeBehind(Side s, float t)
        {
            var r = region.rect;
            switch (s)
            {
                case Side.South: return plan.hEdges[Mathf.Clamp(Mathf.FloorToInt(interior.xMin + t), r.xMin, r.xMax - 1), r.yMin];
                case Side.North: return plan.hEdges[Mathf.Clamp(Mathf.FloorToInt(interior.xMin + t), r.xMin, r.xMax - 1), r.yMax];
                case Side.West: return plan.vEdges[r.xMin, Mathf.Clamp(Mathf.FloorToInt(interior.yMin + t), r.yMin, r.yMax - 1)];
                default: return plan.vEdges[r.xMax, Mathf.Clamp(Mathf.FloorToInt(interior.yMin + t), r.yMin, r.yMax - 1)];
            }
        }

        /// True when the wall is solid (no door, window or open junction) from t0 to t1 along side s.
        public bool SolidWall(Side s, float t0, float t1)
        {
            for (float t = t0; t <= t1 + 0.001f; t += 0.25f)
                if (EdgeBehind(s, Mathf.Min(t, WallLength(s) - 0.01f)) != EdgeKind.Wall) return false;
            return true;
        }

        bool BlocksWindow(Rect foot, float height)
        {
            if (height < 1.0f) return false;
            foreach (var w in windowZones) if (w.Overlaps(foot)) return true;
            return false;
        }

        // ---- Placement ----------------------------------------------------------------------

        public Placed Put(Placed p, float x, float z, float yaw, float y = 0f)
        {
            p.pivot.localPosition = new Vector3(x, floorY + y, z);
            p.pivot.localRotation = Quaternion.Euler(0, yaw, 0);
            return p;
        }

        static Rect FootAt(Vector3 center, Vector3 size, float yaw)
        {
            bool turned = Mathf.Abs(Mathf.DeltaAngle(yaw, 90)) < 1 || Mathf.Abs(Mathf.DeltaAngle(yaw, 270)) < 1;
            float w = turned ? size.z : size.x, d = turned ? size.x : size.z;
            return new Rect(center.x - w / 2, center.z - d / 2, w, d);
        }

        public Rect Footprint(Placed p) => FootAt(p.Position, p.size, p.Yaw);

        /// Places a prop with its back against a wall. Tries the given sides (all, shuffled, if none) and
        /// every position along them; corner = only the two ends of the wall.
        public Placed AgainstWall(PropKit kit, Rng rng, string name, float clearance = 0.6f,
            Side[] sides = null, bool corner = false, bool solidWallOnly = false, bool markSolid = true)
        {
            var p = kit.Spawn(name, parent);
            if (p == null) return null;
            var order = new List<Side>(sides ?? new[] { Side.South, Side.North, Side.West, Side.East });
            if (sides == null) rng.Shuffle(order);
            float w = p.size.x, d = p.size.z;
            foreach (var s in order)
            {
                float len = WallLength(s);
                var ts = new List<float>();
                if (corner) { ts.Add(0); ts.Add(len - w); }
                else for (float t = 0; t <= len - w + 0.001f; t += Res) ts.Add(t);
                rng.Shuffle(ts);
                foreach (var t in ts)
                {
                    if (t < -0.001f) continue;
                    var foot = WallRect(s, t, w, d);
                    if (!IsFree(foot) || BlocksWindow(foot, p.size.y)) continue;
                    if (!NoSolid(FrontRect(s, foot, clearance))) continue;
                    if (solidWallOnly && !SolidWall(s, t, t + w)) continue;
                    Put(p, foot.center.x, foot.center.y, SideYaw(s));
                    if (markSolid) Mark(foot, true);
                    Mark(FrontRect(s, foot, clearance), false);
                    return p;
                }
            }
            if (LogFailures) Debug.Log($"PLACE FAIL {name} size {p.size} in {region.role} {region.rect} floor {region.floor}");
            Kill(p.pivot.gameObject);
            return null;
        }

        /// Places a prop near the middle of the room with a margin of free floor around it.
        public Placed Center(PropKit kit, Rng rng, string name, float margin = 0.6f, bool markSolid = true, bool respectClear = true)
        {
            var p = kit.Spawn(name, parent);
            if (p == null) return null;
            var c = interior.center;
            for (int i = 0; i < 40; i++)
            {
                float yaw = interior.width >= interior.height ? (rng.Chance(0.8f) ? 0 : 90) : (rng.Chance(0.8f) ? 90 : 0);
                float jitter = 0.25f * (i / 8);
                var pos = new Vector3(c.x + rng.Range(-jitter, jitter), 0, c.y + rng.Range(-jitter, jitter));
                pos.x = Mathf.Round(pos.x / Res) * Res;
                pos.z = Mathf.Round(pos.z / Res) * Res;
                var foot = FootAt(pos, p.size, yaw);
                var withMargin = new Rect(foot.xMin - margin, foot.yMin - margin, foot.width + 2 * margin, foot.height + 2 * margin);
                if (!IsFree(foot, respectClear) || !NoSolid(withMargin)) continue;
                Put(p, pos.x, pos.z, yaw);
                if (markSolid) Mark(foot, true);
                return p;
            }
            Kill(p.pivot.gameObject);
            return null;
        }

        /// Places a prop next to an anchor along the same wall, backs aligned.
        public Placed Beside(PropKit kit, Rng rng, Placed anchor, string name, float gap = 0.05f)
        {
            var p = kit.Spawn(name, parent);
            if (p == null) return null;
            var dirs = new List<float> { 1f, -1f };
            rng.Shuffle(dirs);
            foreach (var dir in dirs)
            {
                var back = anchor.Position - anchor.Forward * (anchor.size.z / 2);
                var pos = back + anchor.Right * dir * (anchor.size.x / 2 + gap + p.size.x / 2) + anchor.Forward * (p.size.z / 2);
                var foot = FootAt(pos, p.size, anchor.Yaw);
                if (!IsFree(foot)) continue;
                Put(p, pos.x, pos.z, anchor.Yaw);
                Mark(foot, true);
                return p;
            }
            Kill(p.pivot.gameObject);
            return null;
        }

        /// Finds a free stretch of wall `length` long for a row of furniture (corners preferred).
        public bool FindWallRun(Rng rng, float length, float depth, float clearance, out Side side, out float t)
        {
            var sides = new List<Side> { Side.South, Side.North, Side.West, Side.East };
            rng.Shuffle(sides);
            foreach (var s in sides)
            {
                float len = WallLength(s);
                if (len < length) continue;
                var ts = new List<float> { 0f, len - length };
                for (float x = Res; x < len - length; x += Res) ts.Add(x);
                foreach (var x in ts)
                {
                    var foot = WallRect(s, x, length, depth);
                    if (IsFree(foot) && NoSolid(FrontRect(s, foot, clearance)) && !BlocksWindow(foot, 1.5f))
                    {
                        side = s;
                        t = x;
                        return true;
                    }
                }
            }
            side = Side.South;
            t = 0;
            return false;
        }

        /// Places a prop against wall s at t metres along it, if the spot is free.
        public Placed AtWall(PropKit kit, string name, Side s, float t, float clearance)
        {
            var p = kit.Spawn(name, parent);
            if (p == null) return null;
            var foot = WallRect(s, t, p.size.x, p.size.z);
            if (!IsFree(foot)) { Kill(p.pivot.gameObject); return null; }
            Put(p, foot.center.x, foot.center.y, SideYaw(s));
            Mark(foot, true);
            Mark(FrontRect(s, foot, clearance), false);
            return p;
        }

        /// Places the next module of a row (kitchen counters) against the same wall, dir = +1/-1 along the anchor's right.
        public Placed NextAlongWall(PropKit kit, Placed prev, string name, float dir, Side side, float clearance)
        {
            var p = kit.Spawn(name, parent);
            if (p == null) return null;
            var back = prev.Position - prev.Forward * (prev.size.z / 2);
            var pos = back + prev.Right * dir * (prev.size.x / 2 + p.size.x / 2) + prev.Forward * (p.size.z / 2);
            var foot = FootAt(pos, p.size, prev.Yaw);
            if (!IsFree(foot) || !NoSolid(FrontRect(side, foot, clearance)))
            {
                if (LogFailures) Debug.Log($"PLACE FAIL run {name} free {IsFree(foot)} inside {Inside(foot)} foot {foot} interior {interior}");
                Kill(p.pivot.gameObject);
                return null;
            }
            Put(p, pos.x, pos.z, prev.Yaw);
            Mark(foot, true);
            Mark(FrontRect(side, foot, clearance), false);
            return p;
        }

        /// Places a prop in front of an anchor, facing the same way (yawOffset 180 to face it).
        public Placed InFront(PropKit kit, Placed anchor, string name, float distance, float yawOffset = 0f, bool markSolid = true)
        {
            var p = kit.Spawn(name, parent);
            if (p == null) return null;
            var pos = anchor.Position + anchor.Forward * (anchor.size.z / 2 + distance + p.size.z / 2);
            float yaw = anchor.Yaw + yawOffset;
            var foot = FootAt(pos, p.size, yaw);
            if (!IsFree(foot, false)) { Kill(p.pivot.gameObject); return null; }
            Put(p, pos.x, pos.z, yaw);
            if (markSolid) Mark(foot, true);
            return p;
        }

        /// Sets a prop on top of another one (lamp on a nightstand, screen on a desk).
        public Placed OnTop(PropKit kit, Placed anchor, string name, float along = 0f, float back = 0f, float yawOffset = 0f)
        {
            var p = kit.Spawn(name, anchor.pivot);
            if (p == null) return null;
            p.pivot.localPosition = new Vector3(along, anchor.size.y, -back);
            p.pivot.localRotation = Quaternion.Euler(0, yawOffset, 0);
            return p;
        }

        /// Puts chairs on the sides of a table, facing it.
        public int ChairsAround(PropKit kit, Rng rng, Placed table, string chair, int max)
        {
            int placed = 0;
            float hx = table.size.x / 2, hz = table.size.z / 2;
            var slots = new List<(Vector3 dir, float half, float offset)>();
            foreach (var sign in new[] { 1f, -1f })
            {
                // Long tables seat two along each long side.
                if (table.size.x > 1.4f)
                {
                    slots.Add((table.Forward * sign, hz, -0.25f * table.size.x));
                    slots.Add((table.Forward * sign, hz, 0.25f * table.size.x));
                }
                else slots.Add((table.Forward * sign, hz, 0f));
            }
            slots.Add((table.Right, hx, 0f));
            slots.Add((-table.Right, hx, 0f));

            for (int i = 0; i < slots.Count && placed < max; i++)
            {
                var (dir, half, offset) = slots[i];
                var p = kit.Spawn(chair, parent);
                if (p == null) return placed;
                var d = new Vector3(Mathf.Round(dir.x), 0, Mathf.Round(dir.z));
                var pos = table.Position + table.Right * offset + d * (half + p.size.z / 2 + 0.02f);
                float yaw = Quaternion.LookRotation(-d).eulerAngles.y;
                var foot = FootAt(pos, p.size, yaw);
                if (!IsFree(foot, false)) { Kill(p.pivot.gameObject); continue; }
                Put(p, pos.x, pos.z, yaw + rng.Range(-8f, 8f));
                Mark(foot, true);
                AddSeatFromTop(p, p.Position, yaw, "chair");
                placed++;
            }
            return placed;
        }

        // ---- Seats --------------------------------------------------------------------------

        /// Records a seat on top of a prop at a local point, finding the surface height by raycast.
        public void AddSeatFromTop(Placed p, Vector3 point, float yaw, string kind)
        {
            Physics.SyncTransforms();
            var world = parent.TransformPoint(new Vector3(point.x, floorY + 3f, point.z));
            float best = float.NegativeInfinity;
            foreach (var hit in Physics.RaycastAll(world, Vector3.down, 3.2f))
                if (hit.collider.transform.IsChildOf(p.pivot) && hit.point.y > best)
                    best = hit.point.y;
            if (float.IsNegativeInfinity(best)) return;
            var local = parent.InverseTransformPoint(new Vector3(world.x, best, world.z));
            seats.Add(new SeatSpot { position = local, yaw = yaw, kind = kind });
        }

        public static void Kill(Object o)
        {
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }
    }
}
