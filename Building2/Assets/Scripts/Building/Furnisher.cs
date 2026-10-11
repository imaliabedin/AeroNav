using System.Collections.Generic;
using UnityEngine;

namespace Drone2.Building
{
    /// Furnishes rooms by role and decorates hallways. Big pieces go against walls, the floor in front
    /// of doors stays clear, and the middle of each room stays mostly open.
    public class Furnisher
    {
        readonly PropKit kit;
        readonly Palette pal;
        readonly Rng rng;

        public Furnisher(PropKit kit, Palette pal, Rng rng)
        {
            this.kit = kit;
            this.pal = pal;
            this.rng = rng;
        }

        static Side SideOfYaw(float yaw)
        {
            int q = Mathf.RoundToInt(Mathf.Repeat(yaw, 360) / 90f) % 4;
            return q switch { 0 => Side.South, 1 => Side.West, 2 => Side.North, _ => Side.East };
        }

        static Side Opposite(Side s) => s switch
        {
            Side.South => Side.North,
            Side.North => Side.South,
            Side.West => Side.East,
            _ => Side.West,
        };

        string Pick(params string[] names) => rng.Pick(names);

        // ---- Rooms --------------------------------------------------------------------------

        public void FurnishRoom(RegionSpace s)
        {
            switch (s.region.role)
            {
                case RoomRole.Bedroom: Bedroom(s); break;
                case RoomRole.LivingRoom: LivingRoom(s); break;
                case RoomRole.Kitchen: Kitchen(s); break;
                case RoomRole.Bathroom: Bathroom(s); break;
                case RoomRole.Office: Office(s); break;
                case RoomRole.DiningRoom: DiningRoom(s); break;
                case RoomRole.Storage: Storage(s); break;
                case RoomRole.Laundry: Laundry(s); break;
            }
            Fillers(s);
            RoomFixtures(s);
        }

        /// One or two extra pieces in bigger rooms so they don't look empty.
        void Fillers(RegionSpace s)
        {
            string[] options = s.region.role switch
            {
                RoomRole.Bedroom => new[] { "pottedPlant", "lampSquareFloor", "bookcaseOpenLow", "loungeChair", "sideTable", "cardboardBoxClosed" },
                RoomRole.LivingRoom => new[] { "loungeChairRelax", "pottedPlant", "sideTable", "bookcaseOpenLow", "speaker" },
                RoomRole.Kitchen => new[] { "pottedPlant", "kitchenCabinetDrawer", "plantSmall1" },
                RoomRole.Office => new[] { "bookcaseClosedWide", "pottedPlant", "sideTableDrawers", "cardboardBoxClosed", "loungeChair" },
                RoomRole.DiningRoom => new[] { "pottedPlant", "bookcaseOpenLow", "sideTable" },
                RoomRole.Storage => new[] { "cardboardBoxClosed", "bookcaseOpen", "cardboardBoxOpen" },
                RoomRole.Laundry => new[] { "cardboardBoxOpen", "coatRackStanding", "trashcan" },
                _ => null,
            };
            if (options == null) return;
            float area = s.interior.width * s.interior.height;
            int extras = area > 40 ? 3 : area > 28 ? 2 : area > 18 ? 1 : 0;
            for (int i = 0; i < extras; i++)
            {
                var p = s.AgainstWall(kit, rng, rng.Pick(options), 0.6f);
                if (p != null && p.pivot.name.StartsWith("lounge"))
                    s.AddSeatFromTop(p, p.Position + p.Forward * 0.1f, p.Yaw, "armchair");
            }
        }

        void Bedroom(RegionSpace s)
        {
            bool big = s.interior.width * s.interior.height > 14;
            var bed = s.AgainstWall(kit, rng, big ? Pick("bedDouble", "bedDouble", "bedSingle") : "bedSingle",
                          0.7f, solidWallOnly: true)
                      ?? s.AgainstWall(kit, rng, "bedSingle", 0.5f);
            if (bed != null)
            {
                foreach (var dir in new[] { 1f, -1f })
                    s.AddSeatFromTop(bed, bed.Position + bed.Right * dir * (bed.size.x / 2 - 0.3f) + bed.Forward * 0.25f,
                        bed.Yaw + 90f * dir, "bed");
                s.AddSeatFromTop(bed, bed.Position + bed.Forward * (bed.size.z / 2 - 0.3f), bed.Yaw, "bed");

                var stand = s.Beside(kit, rng, bed, Pick("cabinetBed", "cabinetBedDrawer", "cabinetBedDrawerTable"));
                if (stand != null) s.OnTop(kit, stand, Pick("lampRoundTable", "lampSquareTable"), 0, 0.05f);
                if (big && rng.Chance(0.6f)) s.Beside(kit, rng, bed, "cabinetBed");

                if (rng.Chance(0.7f))
                {
                    // Rug under the foot of the bed, sticking out into the room.
                    var rug = kit.Spawn(Pick("rugRectangle", "rugRounded"), s.parent);
                    var at = bed.Position + bed.Forward * (bed.size.z * 0.4f);
                    if (rug != null) s.Put(rug, at.x, at.z, bed.Yaw + 90, 0.002f);
                }
            }

            s.AgainstWall(kit, rng, Pick("bookcaseClosedDoors", "bookcaseClosed"), 0.7f, solidWallOnly: true);
            if (big && rng.Chance(0.5f)) Desk(s, laptop: true);
            if (rng.Chance(0.5f)) s.AgainstWall(kit, rng, Pick("lampRoundFloor", "lampSquareFloor"), 0.3f, corner: true);
            if (rng.Chance(0.4f)) s.AgainstWall(kit, rng, "pottedPlant", 0.3f, corner: true);
        }

        void LivingRoom(RegionSpace s)
        {
            var sofa = s.AgainstWall(kit, rng, Pick("loungeSofa", "loungeSofaLong", "loungeDesignSofa"), 1.5f, solidWallOnly: true)
                       ?? s.AgainstWall(kit, rng, "loungeSofa", 1.0f);
            if (sofa != null)
            {
                int seats = sofa.size.x > 1.8f ? 3 : 2;
                for (int i = 0; i < seats; i++)
                {
                    float along = (i - (seats - 1) / 2f) * (sofa.size.x / (seats + 0.5f));
                    s.AddSeatFromTop(sofa, sofa.Position + sofa.Right * along + sofa.Forward * 0.1f, sofa.Yaw, "sofa");
                }
                var table = s.InFront(kit, sofa, Pick("tableCoffee", "tableCoffeeGlass", "tableCoffeeSquare"), 0.4f);
                if (table != null && rng.Chance(0.7f))
                {
                    var rug = kit.Spawn(Pick("rugRectangle", "rugRound", "rugRounded"), s.parent);
                    if (rug != null) s.Put(rug, table.Position.x, table.Position.z, table.Yaw, 0.002f);
                }

                var tvSide = Opposite(SideOfYaw(sofa.Yaw));
                var tv = s.AgainstWall(kit, rng, Pick("cabinetTelevision", "cabinetTelevisionDoors"), 0.8f, new[] { tvSide })
                         ?? s.AgainstWall(kit, rng, "cabinetTelevision", 0.8f);
                if (tv != null)
                {
                    s.OnTop(kit, tv, Pick("televisionModern", "televisionModern", "televisionVintage"));
                    if (rng.Chance(0.5f)) s.Beside(kit, rng, tv, "speaker");
                }
            }

            var chair = s.AgainstWall(kit, rng, Pick("loungeChair", "loungeChairRelax", "loungeDesignChair"), 0.8f, corner: true);
            if (chair != null) s.AddSeatFromTop(chair, chair.Position + chair.Forward * 0.1f, chair.Yaw, "armchair");
            if (rng.Chance(0.6f)) s.AgainstWall(kit, rng, Pick("bookcaseOpen", "bookcaseClosedWide"), 0.7f, solidWallOnly: true);
            if (rng.Chance(0.7f)) s.AgainstWall(kit, rng, Pick("lampRoundFloor", "lampSquareFloor"), 0.3f, corner: true);
            if (rng.Chance(0.6f)) s.AgainstWall(kit, rng, "pottedPlant", 0.3f, corner: true);
        }

        void Kitchen(RegionSpace s)
        {
            CounterRun(s);
            var table = s.Center(kit, rng, Pick("table", "tableRound", "tableCloth"), 0.8f);
            if (table != null) s.ChairsAround(kit, rng, table, Pick("chair", "chairCushion", "chairModernCushion"), rng.Range(2, 5));
            s.AgainstWall(kit, rng, "trashcan", 0.3f, corner: true);
            if (rng.Chance(0.4f)) s.AgainstWall(kit, rng, "plantSmall2", 0.3f, corner: true);
        }

        /// A row of kitchen modules along one wall, starting from a corner, with uppers and appliances.
        void CounterRun(RegionSpace s)
        {
            var modules = new List<string>
            {
                "kitchenCabinet", "kitchenSink", "kitchenCabinetDrawer",
                Pick("kitchenStove", "kitchenStoveElectric"), "kitchenCabinet", "kitchenCabinetDrawer", "kitchenCabinet",
            };
            // Find a clear stretch of wall for the fridge plus a few modules, then fill it from its start.
            if (!s.FindWallRun(rng, 3.5f, 0.9f, 0.9f, out var sideOfFridge, out float t)
                && !s.FindWallRun(rng, 2.6f, 0.9f, 0.9f, out sideOfFridge, out t)) return;
            var first = s.AtWall(kit, Pick("kitchenFridge", "kitchenFridgeLarge"), sideOfFridge, t, 0.9f);
            if (first == null) return;
            // Along increasing t: the prop's right is +t on the south and east walls, -t on the others.
            float dir = sideOfFridge is Side.South or Side.East ? 1f : -1f;

            var last = first;
            var counters = new List<Placed>();
            foreach (var name in modules)
            {
                var next = s.NextAlongWall(kit, last, name, dir, sideOfFridge, 0.9f);
                if (next == null) break;
                last = next;
                if (name.StartsWith("kitchenStove"))
                {
                    var hood = kit.Spawn("hoodModern", s.parent);
                    if (hood != null) s.Put(hood, next.Position.x, next.Position.z, next.Yaw, 1.55f)
                        .pivot.localPosition -= next.Forward * ((next.size.z - hood.size.z) / 2);
                }
                else
                {
                    counters.Add(next);
                    var upper = kit.Spawn("kitchenCabinetUpper", s.parent);
                    if (upper != null) s.Put(upper, next.Position.x, next.Position.z, next.Yaw, 1.5f)
                        .pivot.localPosition -= next.Forward * ((next.size.z - upper.size.z) / 2);
                }
            }

            var appliances = new List<string> { "kitchenMicrowave", "kitchenCoffeeMachine", "toaster", "kitchenBlender" };
            rng.Shuffle(appliances);
            int n = 0;
            foreach (var c in counters)
            {
                if (n >= 2 || c.pivot.name == "kitchenSink") continue;
                if (!rng.Chance(0.6f)) continue;
                s.OnTop(kit, c, appliances[n++], 0, 0.05f);
            }
        }

        void Bathroom(RegionSpace s)
        {
            // Biggest piece first, in a corner.
            if (s.AgainstWall(kit, rng, "bathtub", 0.6f, corner: true) == null
                && s.AgainstWall(kit, rng, Pick("shower", "showerRound"), 0.5f, corner: true) == null)
                s.AgainstWall(kit, rng, "shower", 0.4f);

            var toilet = s.AgainstWall(kit, rng, Pick("toilet", "toiletSquare"), 0.6f, solidWallOnly: true);
            if (toilet != null) s.AddSeatFromTop(toilet, toilet.Position + toilet.Forward * 0.1f, toilet.Yaw, "toilet");

            var sink = s.AgainstWall(kit, rng, Pick("bathroomSink", "bathroomSinkSquare"), 0.6f, solidWallOnly: true);
            if (sink != null)
            {
                var mirror = kit.Spawn("bathroomMirror", s.parent);
                if (mirror != null)
                    s.Put(mirror, sink.Position.x, sink.Position.z, sink.Yaw, 1.25f)
                        .pivot.localPosition -= sink.Forward * ((sink.size.z - mirror.size.z) / 2);
            }

            if (rng.Chance(0.5f)) s.AgainstWall(kit, rng, "bathroomCabinetDrawer", 0.5f);
            s.AgainstWall(kit, rng, "trashcan", 0.2f, corner: true);
            if (rng.Chance(0.6f))
            {
                var mat = kit.Spawn("rugDoormat", s.parent);
                var c = s.interior.center;
                if (mat != null) s.Put(mat, c.x, c.y, rng.Chance(0.5f) ? 0 : 90, 0.002f);
            }
        }

        void Desk(RegionSpace s, bool laptop)
        {
            var desk = s.AgainstWall(kit, rng, "desk", 1.0f, solidWallOnly: true);
            if (desk == null) return;
            if (laptop) s.OnTop(kit, desk, "laptop", 0, 0.1f);
            else
            {
                s.OnTop(kit, desk, "computerScreen", 0, desk.size.z * 0.25f);
                s.OnTop(kit, desk, "computerKeyboard", 0, -desk.size.z * 0.15f);
                s.OnTop(kit, desk, "computerMouse", desk.size.x * 0.3f, -desk.size.z * 0.15f);
            }
            var chair = s.InFront(kit, desk, "chairDesk", -0.15f, 180f);
            if (chair != null) s.AddSeatFromTop(chair, chair.Position, chair.Yaw, "deskChair");
        }

        void Office(RegionSpace s)
        {
            Desk(s, laptop: false);
            if (s.interior.width * s.interior.height > 20 && rng.Chance(0.6f)) Desk(s, laptop: rng.Chance(0.5f));
            s.AgainstWall(kit, rng, Pick("bookcaseClosed", "bookcaseOpen", "bookcaseClosedWide"), 0.7f, solidWallOnly: true);
            if (rng.Chance(0.5f)) s.AgainstWall(kit, rng, Pick("bookcaseOpen", "bookcaseClosedDoors"), 0.7f, solidWallOnly: true);
            if (rng.Chance(0.6f)) s.AgainstWall(kit, rng, "pottedPlant", 0.3f, corner: true);
            if (rng.Chance(0.5f)) s.AgainstWall(kit, rng, "lampSquareFloor", 0.3f, corner: true);
            s.AgainstWall(kit, rng, "trashcan", 0.2f, corner: true);
        }

        void DiningRoom(RegionSpace s)
        {
            var table = s.Center(kit, rng, Pick("table", "tableCloth", "tableCross", "tableCrossCloth", "tableGlass"), 0.9f);
            if (table != null)
            {
                var rug = kit.Spawn("rugRectangle", s.parent);
                if (rug != null) s.Put(rug, table.Position.x, table.Position.z, table.Yaw, 0.002f);
                s.ChairsAround(kit, rng, table, Pick("chair", "chairCushion", "chairModernCushion", "chairRounded"), rng.Range(4, 7));
            }
            s.AgainstWall(kit, rng, Pick("bookcaseClosedWide", "kitchenCabinetDrawer"), 0.7f, solidWallOnly: true);
            if (rng.Chance(0.7f)) s.AgainstWall(kit, rng, "pottedPlant", 0.3f, corner: true);
            if (rng.Chance(0.5f)) s.AgainstWall(kit, rng, "lampRoundFloor", 0.3f, corner: true);
        }

        void Storage(RegionSpace s)
        {
            for (int i = 0; i < 2; i++) s.AgainstWall(kit, rng, Pick("bookcaseOpen", "bookcaseOpenLow"), 0.6f);
            int boxes = rng.Range(3, 7);
            for (int i = 0; i < boxes; i++)
            {
                var box = s.AgainstWall(kit, rng, Pick("cardboardBoxClosed", "cardboardBoxOpen", "cardboardBoxClosed"), 0.4f);
                if (box != null && rng.Chance(0.4f)) s.OnTop(kit, box, "cardboardBoxClosed", 0, 0, rng.Range(-20f, 20f));
            }
            if (rng.Chance(0.5f)) s.AgainstWall(kit, rng, "coatRackStanding", 0.3f, corner: true);
        }

        void Laundry(RegionSpace s)
        {
            if (rng.Chance(0.4f)) s.AgainstWall(kit, rng, "washerDryerStacked", 0.8f, solidWallOnly: true);
            else
            {
                var washer = s.AgainstWall(kit, rng, "washer", 0.8f, solidWallOnly: true);
                if (washer != null) s.Beside(kit, rng, washer, "dryer");
            }
            s.AgainstWall(kit, rng, "bookcaseOpenLow", 0.5f);
            for (int i = 0; i < 2; i++) s.AgainstWall(kit, rng, "cardboardBoxOpen", 0.3f);
            s.AgainstWall(kit, rng, "trashcan", 0.2f, corner: true);
        }

        /// Ceiling lamp and light, smoke detector, outlets, a light switch by the door, maybe a painting.
        void RoomFixtures(RegionSpace s)
        {
            var c = s.interior.center;
            var lamp = kit.Spawn("lampSquareCeiling", s.parent);
            if (lamp != null) s.Put(lamp, c.x, c.y, 0, s.ceilingY - s.floorY - lamp.size.y);
            AddLight(s.parent, new Vector3(c.x, s.ceilingY - 0.45f, c.y), Mathf.Max(s.interior.width, s.interior.height) + 2f, 2.2f);
            Decor.SmokeDetector(pal, s.parent, new Vector3(c.x + 0.6f, s.ceilingY, c.y));

            WallItems(s, rng.Range(1, 3), 0.3f, (at, yaw) => Decor.Outlet(pal, s.parent, at, yaw), 0.3f);
            if (s.region.role is not (RoomRole.Bathroom or RoomRole.Storage or RoomRole.Laundry) && rng.Chance(0.75f))
                WallItems(s, 1, 1.6f, (at, yaw) => Decor.Painting(pal, rng, s.parent, at, yaw), 0.6f, tallClear: true);
            if (s.region.role is RoomRole.Kitchen or RoomRole.Office && rng.Chance(0.6f))
                WallItems(s, 1, 1.9f, (at, yaw) => Decor.Clock(pal, rng, s.parent, at, yaw), 0.3f, tallClear: true);
            SwitchesByDoors(s);
        }

        // ---- Hallways -----------------------------------------------------------------------

        public void DecorateHallway(RegionSpace s, bool hasStairDoor)
        {
            // Lights and smoke detectors along the corridor.
            var r = s.interior;
            bool alongX = r.width >= r.height;
            float len = alongX ? r.width : r.height;
            int lights = Mathf.Max(1, Mathf.RoundToInt(len / 5f));
            for (int i = 0; i < lights; i++)
            {
                float f = (i + 0.5f) / lights;
                var p = alongX ? new Vector3(r.xMin + f * r.width, 0, r.center.y) : new Vector3(r.center.x, 0, r.yMin + f * r.height);
                var lamp = kit.Spawn("lampSquareCeiling", s.parent);
                if (lamp != null) s.Put(lamp, p.x, p.z, alongX ? 0 : 90, s.ceilingY - s.floorY - lamp.size.y);
                AddLight(s.parent, new Vector3(p.x, s.ceilingY - 0.45f, p.z), 6f, 1.8f);
                if (i % 2 == 0) Decor.SmokeDetector(pal, s.parent, new Vector3(p.x + (alongX ? 1.2f : 0), s.ceilingY, p.z + (alongX ? 0 : 1.2f)));
            }

            // Floor-standing decorations against walls, keeping a clear lane down the middle.
            var floorItems = new[]
            {
                "pottedPlant", "pottedPlant", "bookcaseOpenLow", "bookcaseClosed", "sideTable",
                "sideTableDrawers", "coatRackStanding", "trashcan", "bench", "plantSmall1",
            };
            int count = Mathf.RoundToInt(len / 3.5f);
            for (int i = 0; i < count; i++)
            {
                var name = rng.Pick(floorItems);
                var item = s.AgainstWall(kit, rng, name, 1.2f, solidWallOnly: true, sides: LongSides(alongX));
                if (item == null) continue;
                if (item.size.z > 0.55f) { RegionSpace.Kill(item.pivot.gameObject); continue; }
                if (name.StartsWith("sideTable"))
                    s.OnTop(kit, item, rng.Pick("lampRoundTable", "plantSmall3", "radio", "books"), 0, 0);
                if (name == "bookcaseOpenLow" && rng.Chance(0.6f)) s.OnTop(kit, item, "plantSmall1", item.size.x * 0.25f, 0);
            }

            // Wall-mounted items.
            WallItems(s, Mathf.Max(1, Mathf.RoundToInt(len / 4f)), 0.3f, (at, yaw) => Decor.Outlet(pal, s.parent, at, yaw), 0.4f);
            WallItems(s, Mathf.Max(1, Mathf.RoundToInt(len / 6f)), 1.6f, (at, yaw) => Decor.Painting(pal, rng, s.parent, at, yaw), 0.6f, tallClear: true);
            if (rng.Chance(0.8f))
                WallItems(s, 1, 2.0f, (at, yaw) => Decor.Clock(pal, rng, s.parent, at, yaw), 0.3f, tallClear: true);
            WallItems(s, Mathf.Max(1, Mathf.RoundToInt(len / 10f)), 1.1f, (at, yaw) => Decor.Extinguisher(pal, s.parent, at, yaw), 0.4f);
            if (rng.Chance(0.6f))
                WallItems(s, Mathf.Max(1, Mathf.RoundToInt(len / 8f)), 2.0f, (at, yaw) =>
                {
                    var lamp = kit.Spawn("lampWall", s.parent);
                    if (lamp != null) { lamp.pivot.localPosition = at; lamp.pivot.localRotation = Quaternion.Euler(0, yaw, 0); }
                    return lamp?.pivot;
                }, 0.4f, tallClear: true);
            if (hasStairDoor) WallItems(s, 1, 1.3f, (at, yaw) => Decor.FireAlarm(pal, s.parent, at, yaw), 0.3f);
            if (rng.Chance(0.5f)) WallItems(s, 1, 1.5f, (at, yaw) => Decor.Thermostat(pal, s.parent, at, yaw), 0.3f);
        }

        static Side[] LongSides(bool alongX) => alongX ? new[] { Side.South, Side.North } : new[] { Side.West, Side.East };

        // ---- Wall-mounted helpers -----------------------------------------------------------

        readonly Dictionary<RegionSpace, List<(Side, float, float)>> usedWall = new();

        /// Mounts items on solid stretches of wall at height y. tallClear keeps them off walls behind tall furniture.
        void WallItems(RegionSpace s, int count, float y, System.Func<Vector3, float, Transform> make, float width, bool tallClear = false)
        {
            if (!usedWall.TryGetValue(s, out var used)) usedWall[s] = used = new List<(Side, float, float)>();
            var sides = new List<Side> { Side.South, Side.North, Side.West, Side.East };
            for (int n = 0; n < count; n++)
            {
                for (int attempt = 0; attempt < 30; attempt++)
                {
                    var side = rng.PickFrom(sides);
                    float len = s.WallLength(side);
                    if (len < width + 0.6f) continue;
                    float t = rng.Range(0.3f + width / 2, len - 0.3f - width / 2);
                    if (!s.SolidWall(side, t - width / 2, t + width / 2)) continue;
                    if (used.Exists(u => u.Item1 == side && Mathf.Abs(u.Item2 - t) < (u.Item3 + width) / 2 + 0.3f)) continue;
                    if (tallClear)
                    {
                        // Keep paintings and clocks off walls with tall furniture in front of them.
                        var strip = s.WallRect(side, t - width / 2, width, 0.3f);
                        if (!s.NoSolid(strip) && !TallFree(s, strip)) continue;
                    }
                    make(s.WallPoint(side, t, y), RegionSpace.SideYaw(side));
                    used.Add((side, t, width));
                    break;
                }
            }
        }

        static bool TallFree(RegionSpace s, Rect strip)
        {
            Physics.SyncTransforms();
            var c = s.parent.TransformPoint(new Vector3(strip.center.x, s.floorY + 1.3f, strip.center.y));
            var half = new Vector3(strip.width / 2 - 0.05f, 0.2f, strip.height / 2 - 0.05f);
            return !Physics.CheckBox(c, half, s.parent.rotation);
        }

        /// A light switch just inside each door of a room.
        void SwitchesByDoors(RegionSpace s)
        {
            foreach (Side side in System.Enum.GetValues(typeof(Side)))
            {
                float len = s.WallLength(side);
                for (float t = 0.05f; t < len; t += 0.25f)
                {
                    if (s.EdgeBehind(side, t) != EdgeKind.Door) continue;
                    // t is just inside the door's near edge; put the switch on the wall beside it.
                    float st = t - 0.25f;
                    if (st > 0.1f && s.SolidWall(side, st - 0.05f, st))
                        Decor.LightSwitch(pal, s.parent, s.WallPoint(side, st, 1.2f), RegionSpace.SideYaw(side));
                    return;
                }
            }
        }

        public static void AddLight(Transform parent, Vector3 at, float range, float intensity)
        {
            var go = new GameObject("CeilingLight");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = at;
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.range = range;
            light.intensity = intensity;
            light.color = new Color(1f, 0.93f, 0.82f);
            light.shadows = LightShadows.None;
        }
    }
}
