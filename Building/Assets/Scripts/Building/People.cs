using System.Collections.Generic;
using UnityEngine;

namespace Drone2.Building
{
    /// Builds blocky low-poly people from boxes on a simple joint hierarchy, and poses them.
    /// A person's root sits on the floor; front is +Z, right is +X.
    public static class People
    {
        static readonly Color[] SkinTones =
        {
            new(0.98f, 0.82f, 0.68f), new(0.91f, 0.72f, 0.56f), new(0.78f, 0.57f, 0.42f),
            new(0.6f, 0.42f, 0.3f), new(0.42f, 0.28f, 0.2f), new(0.95f, 0.78f, 0.62f),
        };
        static readonly Color[] HairColors =
        {
            new(0.08f, 0.06f, 0.05f), new(0.3f, 0.18f, 0.1f), new(0.55f, 0.38f, 0.2f),
            new(0.85f, 0.7f, 0.4f), new(0.6f, 0.6f, 0.6f), new(0.6f, 0.25f, 0.12f),
        };
        static readonly Color[] PantsColors =
        {
            new(0.15f, 0.22f, 0.4f), new(0.25f, 0.25f, 0.28f), new(0.4f, 0.3f, 0.2f),
            new(0.1f, 0.1f, 0.12f), new(0.45f, 0.42f, 0.35f), new(0.3f, 0.4f, 0.55f),
        };

        const float StandHips = 0.99f;

        class Rig
        {
            public Transform root, hips;
            public readonly Dictionary<string, Transform> joints = new();
        }

        static Transform Joint(Rig rig, Transform parent, string name, Vector3 at)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = at;
            rig.joints[name] = t;
            return t;
        }

        static void Part(Palette pal, Transform joint, Vector3 center, Vector3 size, Color c, string name)
            => pal.BoxAt(joint, center, size, c, name);

        static Rig Build(Palette pal, Rng rng, Transform parent, string name)
        {
            var skin = rng.Pick(SkinTones);
            var hair = rng.Pick(HairColors);
            var shirt = Color.HSVToRGB(rng.Range(0f, 1f), rng.Range(0.35f, 0.8f), rng.Range(0.45f, 0.9f));
            var pants = rng.Pick(PantsColors);
            var shoes = new Color(0.12f, 0.1f, 0.1f);
            bool shortSleeves = rng.Chance(0.4f);
            bool longHair = rng.Chance(0.4f);

            var rig = new Rig { root = new GameObject(name).transform };
            rig.root.SetParent(parent, false);
            var hips = rig.hips = Joint(rig, rig.root, "Hips", new Vector3(0, StandHips, 0));
            Part(pal, hips, new Vector3(0, -0.02f, 0), new Vector3(0.34f, 0.16f, 0.2f), pants, "Pelvis");

            var spine = Joint(rig, hips, "Spine", new Vector3(0, 0.06f, 0));
            Part(pal, spine, new Vector3(0, 0.24f, 0), new Vector3(0.38f, 0.44f, 0.22f), shirt, "Torso");

            var neck = Joint(rig, spine, "Neck", new Vector3(0, 0.48f, 0));
            Part(pal, neck, new Vector3(0, 0.03f, 0), new Vector3(0.1f, 0.08f, 0.1f), skin, "NeckPart");
            Part(pal, neck, new Vector3(0, 0.17f, 0.01f), new Vector3(0.22f, 0.25f, 0.24f), skin, "Head");
            Part(pal, neck, new Vector3(0, 0.285f, -0.005f), new Vector3(0.235f, 0.06f, 0.255f), hair, "Hair");
            Part(pal, neck, new Vector3(0, longHair ? 0.12f : 0.19f, -0.115f), new Vector3(0.235f, longHair ? 0.3f : 0.16f, 0.04f), hair, "HairBack");
            Part(pal, neck, new Vector3(-0.05f, 0.19f, 0.131f), new Vector3(0.035f, 0.035f, 0.005f), new Color(0.1f, 0.1f, 0.1f), "EyeL");
            Part(pal, neck, new Vector3(0.05f, 0.19f, 0.131f), new Vector3(0.035f, 0.035f, 0.005f), new Color(0.1f, 0.1f, 0.1f), "EyeR");

            foreach (var (side, sx) in new[] { ("L", -1f), ("R", 1f) })
            {
                var shoulder = Joint(rig, spine, "Shoulder" + side, new Vector3(0.245f * sx, 0.43f, 0));
                Part(pal, shoulder, new Vector3(0, -0.14f, 0), new Vector3(0.1f, 0.3f, 0.11f), shortSleeves ? skin : shirt, "UpperArm");
                if (shortSleeves) Part(pal, shoulder, new Vector3(0, -0.05f, 0), new Vector3(0.11f, 0.12f, 0.12f), shirt, "Sleeve");
                var elbow = Joint(rig, shoulder, "Elbow" + side, new Vector3(0, -0.29f, 0));
                Part(pal, elbow, new Vector3(0, -0.13f, 0), new Vector3(0.085f, 0.27f, 0.09f), shortSleeves ? skin : shirt, "Forearm");
                Part(pal, elbow, new Vector3(0, -0.31f, 0), new Vector3(0.08f, 0.1f, 0.05f), skin, "Hand");

                var hip = Joint(rig, hips, "Hip" + side, new Vector3(0.1f * sx, -0.08f, 0));
                Part(pal, hip, new Vector3(0, -0.21f, 0), new Vector3(0.15f, 0.42f, 0.16f), pants, "Thigh");
                var knee = Joint(rig, hip, "Knee" + side, new Vector3(0, -0.42f, 0));
                Part(pal, knee, new Vector3(0, -0.2f, 0), new Vector3(0.12f, 0.4f, 0.13f), pants, "Shin");
                var ankle = Joint(rig, knee, "Ankle" + side, new Vector3(0, -0.41f, 0));
                Part(pal, ankle, new Vector3(0, -0.04f, 0.05f), new Vector3(0.11f, 0.08f, 0.25f), shoes, "Foot");
            }
            return rig;
        }

        static void Rot(Rig rig, string joint, float x, float y = 0, float z = 0)
            => rig.joints[joint].localRotation = Quaternion.Euler(x, y, z);

        static void Both(Rig rig, string joint, float x, float zOut = 0)
        {
            Rot(rig, joint + "L", x, 0, -zOut);
            Rot(rig, joint + "R", x, 0, zOut);
        }

        /// Poses the rig. seatHeight is only used by the sitting poses.
        static void ApplyPose(Rig rig, Rng rng, PersonPose pose, float seatHeight)
        {
            var hips = rig.hips;
            switch (pose)
            {
                case PersonPose.Standing:
                    Both(rig, "Shoulder", rng.Range(-8f, 5f), 6);
                    Both(rig, "Elbow", -10);
                    if (rng.Chance(0.5f)) { Both(rig, "Shoulder", -45, -25); Both(rig, "Elbow", -100); } // hugging self
                    Rot(rig, "Neck", rng.Range(-5f, 15f), rng.Range(-20f, 20f));
                    break;

                case PersonPose.Waving:
                    Rot(rig, "ShoulderR", 0, 0, 160);
                    Rot(rig, "ElbowR", 0, 0, 25);
                    Rot(rig, "ShoulderL", -10, 0, -6);
                    Rot(rig, "Neck", -10);
                    break;

                case PersonPose.SittingOnFurniture:
                case PersonPose.SittingHeadInHands:
                {
                    hips.localPosition = new Vector3(0, seatHeight + 0.1f, 0);
                    float drop = seatHeight - 0.06f;
                    float phi = drop >= 0.41f ? 0f : Mathf.Acos(Mathf.Clamp01(drop / 0.41f)) * Mathf.Rad2Deg;
                    Both(rig, "Hip", -90, 4);
                    Both(rig, "Knee", 90 - phi);
                    Both(rig, "Ankle", phi);
                    if (pose == PersonPose.SittingOnFurniture)
                    {
                        Both(rig, "Shoulder", -25, 4);
                        Both(rig, "Elbow", -55);
                        Rot(rig, "Neck", rng.Range(0f, 20f));
                    }
                    else
                    {
                        Rot(rig, "Spine", 32);
                        Both(rig, "Shoulder", -65, -8);
                        Both(rig, "Elbow", -115);
                        Rot(rig, "Neck", 30);
                    }
                    break;
                }

                case PersonPose.SittingAgainstWall:
                    hips.localPosition = new Vector3(0, 0.1f, 0);
                    Rot(rig, "Spine", -8);
                    Both(rig, "Hip", -90, 5);
                    if (rng.Chance(0.5f)) { Rot(rig, "HipL", -135, 0, -5); Rot(rig, "KneeL", 110); Rot(rig, "AnkleL", 25); }
                    Both(rig, "Shoulder", -15, 10);
                    Both(rig, "Elbow", -30);
                    Rot(rig, "Neck", rng.Range(10f, 30f), rng.Range(-15f, 15f));
                    break;

                case PersonPose.HuddledKneesUp:
                    hips.localPosition = new Vector3(0, 0.14f, 0);
                    Rot(rig, "Spine", 25);
                    Both(rig, "Hip", -143, 3);
                    Both(rig, "Knee", 114);
                    Both(rig, "Ankle", 29);
                    Both(rig, "Shoulder", -50, -10);
                    Both(rig, "Elbow", -70);
                    Rot(rig, "Neck", 35);
                    break;

                case PersonPose.CrouchingInCorner:
                    hips.localPosition = new Vector3(0, 0.55f, -0.05f);
                    Rot(rig, "Spine", 35);
                    Both(rig, "Hip", -74, 8);
                    Both(rig, "Knee", 122);
                    Both(rig, "Ankle", -48);
                    if (rng.Chance(0.5f)) { Both(rig, "Shoulder", -150, -15); Both(rig, "Elbow", -100); } // covering head
                    else { Both(rig, "Shoulder", -70, -8); Both(rig, "Elbow", -60); }
                    Rot(rig, "Neck", 20);
                    break;

                case PersonPose.LyingOnBack:
                    hips.localPosition = new Vector3(0, 0.11f, -0.05f);
                    hips.localRotation = Quaternion.Euler(-90, 0, 0);
                    Both(rig, "Shoulder", 0, 25);
                    Both(rig, "Elbow", -15);
                    if (rng.Chance(0.5f)) { Rot(rig, "HipR", -40); Rot(rig, "KneeR", 75); }
                    Rot(rig, "Neck", 0, rng.Range(-40f, 40f));
                    break;

                case PersonPose.LyingFaceDown:
                    hips.localPosition = new Vector3(0, 0.11f, 0.05f);
                    hips.localRotation = Quaternion.Euler(90, 0, 0);
                    Rot(rig, "ShoulderL", -160, 0, -15);
                    Rot(rig, "ShoulderR", rng.Range(-30f, 0f), 0, 20);
                    Rot(rig, "ElbowL", -30);
                    Both(rig, "Ankle", 60);
                    Rot(rig, "Neck", -20, rng.Chance(0.5f) ? 70 : -70);
                    break;

                case PersonPose.Kneeling:
                    hips.localPosition = new Vector3(0, 0.565f, 0);
                    Rot(rig, "Spine", 10);
                    Both(rig, "Hip", 0, 3);
                    Both(rig, "Knee", 90);
                    Both(rig, "Ankle", 90);
                    Both(rig, "Shoulder", -35, -12);
                    Both(rig, "Elbow", -125);
                    Rot(rig, "Neck", 15);
                    break;
            }
        }

        public static TrappedPerson Spawn(Palette pal, Rng rng, Transform parent, int id, PersonPose pose,
            Vector3 position, float yaw, float seatHeight = 0.45f)
        {
            var rig = Build(pal, rng, parent, $"Person {id} ({pose})");
            float scale = rng.Range(0.9f, 1.02f);
            rig.root.localScale = Vector3.one * scale;
            ApplyPose(rig, rng, pose, seatHeight / scale);
            rig.root.localPosition = position;
            rig.root.localRotation = Quaternion.Euler(0, yaw, 0);

            var person = rig.root.gameObject.AddComponent<TrappedPerson>();
            person.id = id;
            person.pose = pose;

            // One box collider around the posed body, in root space.
            var bounds = new Bounds();
            bool first = true;
            foreach (var r in rig.root.GetComponentsInChildren<Renderer>())
            {
                var b = r.bounds;
                foreach (var corner in Corners(b))
                {
                    var local = rig.root.InverseTransformPoint(corner);
                    if (first) { bounds = new Bounds(local, Vector3.zero); first = false; }
                    else bounds.Encapsulate(local);
                }
            }
            var col = rig.root.gameObject.AddComponent<BoxCollider>();
            col.center = bounds.center;
            col.size = bounds.size;
            return person;
        }

        static IEnumerable<Vector3> Corners(Bounds b)
        {
            for (int i = 0; i < 8; i++)
                yield return new Vector3(
                    (i & 1) == 0 ? b.min.x : b.max.x,
                    (i & 2) == 0 ? b.min.y : b.max.y,
                    (i & 4) == 0 ? b.min.z : b.max.z);
        }

        // ---- Placement ----------------------------------------------------------------------

        struct Option
        {
            public PersonPose pose;
            public Vector3 position;
            public float yaw, seatHeight, weight;
            public Rect footprint;
            public int seatIndex;
        }

        /// Places `count` people in rooms only (never hallways), spread over different rooms where possible,
        /// each in a pose that suits a free spot in the room.
        public static List<TrappedPerson> Populate(Palette pal, Rng rng, List<RegionSpace> rooms, int count)
        {
            var people = new List<TrappedPerson>();
            if (rooms.Count == 0) return people;
            var order = new List<RegionSpace>(rooms);
            rng.Shuffle(order);
            var usedSeats = new HashSet<(RegionSpace, int)>();
            var poseCounts = new Dictionary<PersonPose, int>();

            for (int i = 0, attempts = 0; people.Count < count && attempts < count * 6; attempts++, i++)
            {
                var space = order[i % order.Count];
                var options = Options(space, rng, usedSeats);
                if (options.Count == 0) continue;
                // Favour poses not used yet so the group looks varied.
                var weights = options.ConvertAll(o =>
                    o.weight / (1 + 3 * (poseCounts.TryGetValue(o.pose, out var n) ? n : 0)));
                var pick = options[rng.Weighted(weights)];

                var person = Spawn(pal, rng, space.parent, people.Count + 1, pick.pose,
                    new Vector3(pick.position.x, space.floorY, pick.position.z), pick.yaw, pick.seatHeight);
                person.floor = space.region.floor;
                person.roomId = space.region.id;
                person.roomRole = space.region.role;
                space.Mark(pick.footprint, true);
                if (pick.seatIndex >= 0) usedSeats.Add((space, pick.seatIndex));
                poseCounts[pick.pose] = (poseCounts.TryGetValue(pick.pose, out var c) ? c : 0) + 1;
                people.Add(person);
            }
            return people;
        }

        static List<Option> Options(RegionSpace s, Rng rng, HashSet<(RegionSpace, int)> usedSeats)
        {
            var list = new List<Option>();
            var r = s.interior;
            var center = new Vector3(r.center.x, 0, r.center.y);

            // On beds, sofas and chairs.
            for (int i = 0; i < s.seats.Count; i++)
            {
                if (usedSeats.Contains((s, i))) continue;
                var seat = s.seats[i];
                if (seat.kind == "toilet") continue;
                float h = seat.position.y - s.floorY;
                if (h < 0.25f || h > 0.75f) continue;
                var fwd = Quaternion.Euler(0, seat.yaw, 0) * Vector3.forward;
                var feet = seat.position + fwd * 0.45f;
                var foot = new Rect(feet.x - 0.3f, feet.z - 0.3f, 0.6f, 0.6f);
                if (!s.IsPersonFree(foot, ignoreSolid: true)) continue;
                var pose = rng.Chance(0.35f) ? PersonPose.SittingHeadInHands : PersonPose.SittingOnFurniture;
                list.Add(new Option
                {
                    pose = pose, position = seat.position, yaw = seat.yaw, seatHeight = h,
                    weight = seat.kind == "bed" ? 3f : 2f, footprint = foot, seatIndex = i,
                });
            }

            // Corners: crouching, facing into the room.
            foreach (var (cx, cz) in new[] { (r.xMin, r.yMin), (r.xMax, r.yMin), (r.xMin, r.yMax), (r.xMax, r.yMax) })
            {
                float dx = cx == r.xMin ? 1 : -1, dz = cz == r.yMin ? 1 : -1;
                var foot = Rect.MinMaxRect(Mathf.Min(cx, cx + dx * 0.9f), Mathf.Min(cz, cz + dz * 0.9f),
                    Mathf.Max(cx, cx + dx * 0.9f), Mathf.Max(cz, cz + dz * 0.9f));
                if (!s.IsPersonFree(foot)) continue;
                var pos = new Vector3(cx + dx * 0.38f, 0, cz + dz * 0.38f);
                float yaw = Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
                list.Add(new Option { pose = PersonPose.CrouchingInCorner, position = pos, yaw = yaw, weight = 3f, footprint = foot, seatIndex = -1 });
                list.Add(new Option { pose = PersonPose.HuddledKneesUp, position = pos, yaw = yaw, weight = 1.5f, footprint = foot, seatIndex = -1 });
            }

            // Along walls: sitting or huddled with the back to the wall.
            foreach (Side side in System.Enum.GetValues(typeof(Side)))
            {
                float len = s.WallLength(side);
                for (int k = 0; k < 4; k++)
                {
                    float t = rng.Range(0.4f, len - 0.4f);
                    var foot = s.WallRect(side, t - 0.35f, 0.7f, 1.2f);
                    if (!s.IsPersonFree(foot)) continue;
                    var wall = s.WallPoint(side, t, 0);
                    float yaw = RegionSpace.SideYaw(side);
                    var pos = wall + Quaternion.Euler(0, yaw, 0) * Vector3.forward * 0.2f;
                    bool tight = !s.IsPersonFree(s.WallRect(side, t - 0.35f, 0.7f, 1.3f));
                    list.Add(new Option
                    {
                        pose = tight ? PersonPose.HuddledKneesUp : PersonPose.SittingAgainstWall,
                        position = pos, yaw = yaw, weight = 1.5f, footprint = foot, seatIndex = -1,
                    });
                    break;
                }
            }

            // Open floor: standing, waving, kneeling, lying.
            for (int k = 0; k < 12; k++)
            {
                var p = new Vector3(rng.Range(r.xMin + 0.5f, r.xMax - 0.5f), 0, rng.Range(r.yMin + 0.5f, r.yMax - 0.5f));
                var foot = new Rect(p.x - 0.4f, p.z - 0.4f, 0.8f, 0.8f);
                if (!s.IsPersonFree(foot)) continue;
                var toCenter = center - p;
                float yaw = toCenter.sqrMagnitude > 0.01f ? Mathf.Atan2(toCenter.x, toCenter.z) * Mathf.Rad2Deg : rng.Range(0f, 360f);
                yaw += rng.Range(-60f, 60f);
                list.Add(new Option { pose = PersonPose.Standing, position = p, yaw = yaw, weight = 1.5f, footprint = foot, seatIndex = -1 });
                list.Add(new Option { pose = PersonPose.Waving, position = p, yaw = yaw, weight = 0.8f, footprint = foot, seatIndex = -1 });
                list.Add(new Option { pose = PersonPose.Kneeling, position = p, yaw = yaw, weight = 1f, footprint = foot, seatIndex = -1 });
                break;
            }
            for (int k = 0; k < 16; k++)
            {
                bool alongZ = rng.Chance(0.5f);
                var p = new Vector3(rng.Range(r.xMin + 0.5f, r.xMax - 0.5f), 0, rng.Range(r.yMin + 0.5f, r.yMax - 0.5f));
                var foot = alongZ ? new Rect(p.x - 0.4f, p.z - 1.0f, 0.8f, 2.0f) : new Rect(p.x - 1.0f, p.z - 0.4f, 2.0f, 0.8f);
                if (!s.IsPersonFree(foot)) continue;
                float yaw = (alongZ ? 0 : 90) + (rng.Chance(0.5f) ? 180 : 0);
                list.Add(new Option
                {
                    pose = rng.Chance(0.5f) ? PersonPose.LyingOnBack : PersonPose.LyingFaceDown,
                    position = p, yaw = yaw, weight = 1.2f, footprint = foot, seatIndex = -1,
                });
                break;
            }
            return list;
        }
    }
}
