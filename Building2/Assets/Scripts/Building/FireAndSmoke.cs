using System.Collections.Generic;
using UnityEngine;

namespace Drone2.Building
{
    /// Flickers a flame tongue (and an optional light) with noise.
    public class FlameFlicker : MonoBehaviour
    {
        public float speed = 1f;
        public float phase;
        public Light flameLight;

        Vector3 baseScale;
        float baseIntensity;

        void Awake()
        {
            baseScale = transform.localScale;
            if (flameLight != null) baseIntensity = flameLight.intensity;
        }

        void Update()
        {
            float t = Time.time * speed * 2.3f;
            float tall = Mathf.PerlinNoise(t, phase);
            float wide = Mathf.PerlinNoise(phase + 7f, t * 0.8f);
            if (flameLight == null)
            {
                transform.localScale = new Vector3(baseScale.x * (0.9f + 0.25f * wide), baseScale.y * (0.7f + 0.6f * tall), baseScale.z * (0.9f + 0.25f * wide));
                transform.localRotation = Quaternion.Euler((wide - 0.5f) * 14f, transform.localEulerAngles.y, (tall - 0.5f) * 14f);
            }
            else flameLight.intensity = baseIntensity * (0.7f + 0.6f * tall);
        }
    }

    /// Makes a light stutter like failing wiring: steady most of the time, with brief dropouts.
    public class LightFlicker : MonoBehaviour
    {
        public float phase;
        Light lamp;
        float baseIntensity;

        void Awake()
        {
            lamp = GetComponent<Light>();
            baseIntensity = lamp.intensity;
        }

        void Update()
        {
            float n = Mathf.PerlinNoise(Time.time * 3.1f, phase);
            float stutter = n < 0.32f ? Mathf.PerlinNoise(Time.time * 37f, phase + 5f) * 0.25f : 1f;
            lamp.intensity = baseIntensity * stutter;
        }
    }

    /// Scatters low-poly fire and smoke through a generated building: burning furniture and floor fires
    /// in a few rooms, plumes that rise from each fire and pool under the ceiling, a thick hot smoke
    /// layer under every ceiling and a thinner haze filling the rooms (heaviest near the fire and on the
    /// floors above it), flames and smoke pouring out of windows, and plumes from the roof.
    /// Flames are layered, crisp-edged blob particles (red, orange, yellow core); smoke is soft billboard puffs.
    public class FireAndSmoke
    {
        const float SlabDepth = 0.2f;
        const float FlameGlow = 2.2f;   // HDR boost so flames bloom

        static readonly Color Red = new(0.9f, 0.18f, 0.04f);
        static readonly Color Orange = new(1f, 0.5f, 0.07f);
        static readonly Color Yellow = new(1f, 0.85f, 0.22f);
        static readonly Color Char = new(0.07f, 0.06f, 0.06f);
        static readonly Vector3 Wind = new(0.5f, 0f, 0.3f);

        static readonly Material[] flameMaterials = new Material[3];
        static Material smokeMaterial, emberMaterial;
        static Texture2D puffTexture, blobTexture;
        static Mesh cubeMesh;

        readonly BuildingGenerator g;
        readonly Palette pal;
        readonly Rng rng;
        readonly List<ParticleSystem> systems = new();
        readonly int origin;

        FireAndSmoke(BuildingGenerator g, Palette pal, Rng rng)
        {
            this.g = g;
            this.pal = pal;
            this.rng = rng;
            origin = rng.Range(0, g.floors);
        }

        /// Adds fire and smoke to an already furnished building.
        public static void Build(BuildingGenerator g, Palette pal, int seed)
        {
            var fx = new FireAndSmoke(g, pal, new Rng(unchecked(seed * 31 + 9157)));
            fx.Run();
        }

        // ---- Layout -------------------------------------------------------------------------

        float FireWeight(int f) => f == origin ? 1f : Mathf.Abs(f - origin) == 1 ? 0.55f : 0.25f;

        /// Smoke rises: thickest on the fire floor and above it, thin below.
        float SmokeWeight(int f) => f == origin ? 1f
            : f > origin ? Mathf.Max(0.5f, 1f - (f - origin) * 0.15f)
            : Mathf.Max(0.15f, 0.45f - (origin - f) * 0.15f);

        void Run()
        {
            var burning = new HashSet<RegionSpace>();
            for (int f = 0; f < g.floors && g.fireDensity > 0f; f++)
            {
                var rooms = g.roomSpaces.FindAll(s => s.region.floor == f);
                if (rooms.Count == 0) continue;
                int n = Mathf.RoundToInt(rooms.Count * g.fireDensity * FireWeight(f));
                if (f == origin) n = Mathf.Max(n, 1);
                n = Mathf.Min(n, Mathf.CeilToInt(rooms.Count / 2f));
                rng.Shuffle(rooms);
                // The first room on the fire's floor is one with a window, so the fire shows from outside too.
                if (f == origin)
                {
                    int w = rooms.FindIndex(r => ExteriorWindows(r.region).Count > 0);
                    if (w > 0) (rooms[0], rooms[w]) = (rooms[w], rooms[0]);
                }
                for (int i = 0; i < n; i++) burning.Add(rooms[i]);
            }

            foreach (var s in burning) BurnRoom(s);
            foreach (var h in g.hallSpaces) BurnHallway(h);

            foreach (var s in g.roomSpaces) RoomSmoke(s, burning.Contains(s));
            foreach (var h in g.hallSpaces) HallSmoke(h);
            StairSmoke();
            RoofSmoke(burning);
            DimHouseLights();

            // Systems are stopped while being configured; start them (or fast-forward them in the Editor).
            foreach (var ps in systems)
                if (Application.isPlaying) ps.Play(false);
                else ps.Simulate(12f, false, true);
        }

        /// The power is failing: ceiling lights are dimmed, some are dead and some stutter.
        void DimHouseLights()
        {
            foreach (var light in g.Root.GetComponentsInChildren<Light>(true))
            {
                if (light.name != "CeilingLight") continue;
                int f = Mathf.Clamp(Mathf.FloorToInt(light.transform.position.y / g.floorHeight), 0, g.floors - 1);
                if (rng.Chance(0.25f + 0.25f * SmokeWeight(f)))
                {
                    light.enabled = false;
                    continue;
                }
                light.intensity *= rng.Range(0.25f, 0.45f);
                light.color = new Color(0.85f, 0.9f, 1f);
                if (rng.Chance(0.45f)) light.gameObject.AddComponent<LightFlicker>().phase = rng.Range(0f, 100f);
            }
        }

        // ---- Fires --------------------------------------------------------------------------

        struct Spot
        {
            public Vector3 basePos;   // building-local, where the flames start
            public float radius, height;
            public Vector2 size;      // footprint of the flames (x, z)
            public bool onFurniture;
        }

        void BurnRoom(RegionSpace s)
        {
            int count = 1 + (s.interior.width * s.interior.height >= 22f ? 1 : 0) + (rng.Chance(0.2f) ? 1 : 0);
            var used = new List<Vector3>();
            var furniture = FurnitureSpots(s);
            rng.Shuffle(furniture);
            int placed = 0;
            foreach (var spot in furniture)
            {
                if (placed >= count) break;
                if (!FarFromOthers(spot.basePos, s, used)) continue;
                Ignite(s.parent, spot, rng.Range(0.9f, 1.3f), withLight: placed == 0);
                used.Add(spot.basePos);
                placed++;
            }
            for (int tries = 0; placed < count && tries < 20; tries++)
            {
                var p = new Vector3(rng.Range(s.interior.xMin + 0.7f, s.interior.xMax - 0.7f), s.floorY, rng.Range(s.interior.yMin + 0.7f, s.interior.yMax - 0.7f));
                var foot = new Rect(p.x - 0.4f, p.z - 0.4f, 0.8f, 0.8f);
                if (!s.Inside(foot) || !s.IsPersonFree(foot) || !FarFromOthers(p, s, used)) continue;
                Ignite(s.parent, new Spot { basePos = p, radius = rng.Range(0.3f, 0.5f), height = rng.Range(0.8f, 1.2f), size = new Vector2(rng.Range(0.5f, 0.9f), rng.Range(0.5f, 0.9f)) }, 1f, withLight: placed == 0);
                used.Add(p);
                placed++;
            }

            foreach (var (center, outward, width) in ExteriorWindows(s.region))
                if (rng.Chance(0.8f)) WindowFire(s, center, outward);
        }

        /// A few small fires on furniture along corridors (not in the middle, which stays flyable).
        void BurnHallway(RegionSpace h)
        {
            float p = g.fireDensity * FireWeight(h.region.floor) * 0.7f;
            if (!rng.Chance(p)) return;
            var furniture = FurnitureSpots(h);
            if (furniture.Count == 0) return;
            var spot = rng.PickFrom(furniture);
            spot.radius = Mathf.Min(spot.radius, 0.35f);
            spot.height = Mathf.Min(spot.height, 1.0f);
            spot.size = Vector2.Min(spot.size, new Vector2(0.8f, 0.8f));
            Ignite(h.parent, spot, 1f, withLight: true);
        }

        bool FarFromOthers(Vector3 local, RegionSpace s, List<Vector3> used)
        {
            foreach (var u in used) if ((u - local).sqrMagnitude < 1.5f * 1.5f) return false;
            var world = s.parent.TransformPoint(local);
            foreach (var person in g.people)
            {
                var pc = person.Bounds.center;
                if (Mathf.Abs(pc.y - world.y) < 2f && new Vector2(pc.x - world.x, pc.z - world.z).magnitude < 1.8f) return false;
            }
            return true;
        }

        /// Floor-standing furniture that could be set alight, with where its flames should start.
        List<Spot> FurnitureSpots(RegionSpace s)
        {
            var spots = new List<Spot>();
            foreach (Transform t in s.parent)
            {
                if (t.GetComponent<TrappedPerson>() != null || t.name.Contains("lant")) continue;
                var rs = t.GetComponentsInChildren<Renderer>();
                if (rs.Length == 0) continue;
                var b = rs[0].bounds;
                foreach (var r in rs) b.Encapsulate(r.bounds);
                var bottom = s.parent.InverseTransformPoint(new Vector3(b.center.x, b.min.y, b.center.z));
                var top = s.parent.InverseTransformPoint(new Vector3(b.center.x, b.max.y, b.center.z));
                if (bottom.y - s.floorY > 0.15f) continue;
                float h = top.y - bottom.y, foot = Mathf.Max(b.size.x, b.size.z);
                if (h < 0.3f || h > 1.25f || foot < 0.7f || foot > 2.6f) continue;   // flames sit on top, so nothing taller than a dresser
                spots.Add(new Spot
                {
                    basePos = new Vector3(top.x, Mathf.Min(top.y, s.floorY + 1.0f) - 0.05f, top.z),
                    radius = Mathf.Clamp(Mathf.Min(b.size.x, b.size.z) * 0.45f, 0.2f, 0.6f),
                    height = Mathf.Clamp(0.5f + h * 0.4f, 0.7f, 1.5f),
                    size = new Vector2(Mathf.Clamp(b.size.x * 0.85f, 0.35f, 2.2f), Mathf.Clamp(b.size.z * 0.85f, 0.35f, 2.2f)),
                    onFurniture = true,
                });
            }
            return spots;
        }

        void Ignite(Transform parent, Spot spot, float scale, bool withLight)
        {
            var root = new GameObject("Fire").transform;
            root.SetParent(parent, false);
            root.localPosition = spot.basePos;
            float r = spot.radius * scale, h = spot.height * scale;

            if (!spot.onFurniture)
                pal.BoxAt(root, new Vector3(0, 0.012f, 0), new Vector3(r * 3.2f, 0.024f, r * 3.2f), Char, "Scorch");

            // Wide furniture burns wide and a little lower; small things burn narrow and tall.
            var foot = spot.size * scale;
            float spread = Mathf.Max(foot.x, foot.y);
            Flame(root, Vector3.zero, foot, h * Mathf.Lerp(1.05f, 0.85f, Mathf.InverseLerp(0.6f, 2f, spread)), Quaternion.identity);

            var ps = NewSystem(root, "Embers", Vector3.zero, Quaternion.Euler(-90, 0, 0), Ember());
            ConfigureEmbers(ps, r, h);

            // Smoke rising from the flames, then pooling and spreading under the ceiling.
            float ceiling = CeilingAbove(parent, spot.basePos) - spot.basePos.y;
            float start = h * 0.85f, rise = Mathf.Max(0.4f, ceiling - start);
            var plume = Plume(root, new Vector3(0, start, 0), Quaternion.LookRotation(Vector3.up), rise / 1.0f + 6f, 1.0f,
                0.45f + r * 0.6f, 5f + 7f * g.smokeDensity, 0.5f, 12f, r * 0.5f, maxSize: 2.6f, dark: 0.2f, glow: true);
            CapAt(plume, root, ceiling - 0.05f);

            if (withLight) AddFireLight(root, new Vector3(0, h * 0.6f, 0), 5f + h * 2.5f, 3f);
        }

        /// Stops a plume's particles at a ceiling `localY` above `root`, so they spread out under it.
        static void CapAt(ParticleSystem ps, Transform root, float localY)
        {
            var plane = new GameObject("SmokeCeiling").transform;
            plane.SetParent(root, false);
            plane.localPosition = new Vector3(0, localY, 0);
            plane.localRotation = Quaternion.Euler(180f, 0, 0);   // normal points down, towards the smoke
            var col = ps.collision;
            col.enabled = true;
            col.type = ParticleSystemCollisionType.Planes;
            col.SetPlane(0, plane);
            col.bounce = 0f;
            col.dampen = 0.6f;
            col.lifetimeLoss = 0f;
            col.radiusScale = 0.35f;
        }

        float CeilingAbove(Transform parent, Vector3 local)
        {
            // Floors are g.floorHeight apart; the ceiling is under the next slab.
            int f = Mathf.Clamp(Mathf.FloorToInt((local.y + 0.01f) / g.floorHeight), 0, g.floors - 1);
            return f * g.floorHeight + g.floorHeight - SlabDepth;
        }


        /// A stylised flame: three nested layers of crisp, lumpy blobs (red outside, orange, a hot yellow core)
        /// rising from a `size` footprint, wavering, and shrinking to points by `height`.
        void Flame(Transform parent, Vector3 at, Vector2 size, float height, Quaternion tilt)
        {
            var t = new GameObject("Flame").transform;
            t.SetParent(parent, false);
            t.localPosition = at;
            t.localRotation = tilt;
            size = new Vector2(Mathf.Max(0.2f, size.x), Mathf.Max(0.2f, size.y));
            float blob = Mathf.Clamp(0.3f + 0.35f * Mathf.Min(size.x, size.y), 0.36f, 0.7f);
            float area = size.x * size.y;
            for (int layer = 0; layer < 3; layer++)
            {
                float footK = layer == 0 ? 0.95f : layer == 1 ? 0.7f : 0.4f;
                float sizeK = layer == 0 ? 1f : layer == 1 ? 0.8f : 0.55f;
                float reach = layer == 0 ? 1f : layer == 1 ? 0.8f : 0.55f;
                float life = layer == 0 ? 0.95f : layer == 1 ? 0.8f : 0.6f;

                var ps = NewSystem(t, layer == 0 ? "FlameOuter" : layer == 1 ? "FlameMid" : "FlameCore", Vector3.zero, Quaternion.Euler(-90, 0, 0), FlameMaterial(layer));
                var main = ps.main;
                main.startLifetime = new ParticleSystem.MinMaxCurve(life * 0.75f, life);
                main.startSpeed = new ParticleSystem.MinMaxCurve(height * reach / life * 0.8f, height * reach / life * 1.1f);
                main.startSize = new ParticleSystem.MinMaxCurve(blob * sizeK * 0.8f, blob * sizeK * 1.2f);
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                main.maxParticles = 300;
                var em = ps.emission;
                em.rateOverTime = (28f + 50f * area) * (layer == 2 ? 1.2f : 1f);
                var shape = ps.shape;
                shape.shapeType = ParticleSystemShapeType.Box;
                shape.scale = new Vector3(size.x * footK, size.y * footK, 0.05f);   // system is turned so +Z is up

                var sz = ps.sizeOverLifetime;
                sz.enabled = true;
                sz.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.55f), new Keyframe(0.2f, 1f), new Keyframe(1f, 0.08f)));
                var col = ps.colorOverLifetime;
                col.enabled = true;
                var grad = new Gradient();
                var end = layer == 0 ? new Color(0.55f, 0.3f, 0.3f) : Color.white;   // the outer layer's tips go dark crimson
                grad.SetKeys(
                    new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 0.5f), new GradientColorKey(end, 1f) },
                    new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.85f), new GradientAlphaKey(0f, 1f) });
                col.color = grad;
                var rot = ps.rotationOverLifetime;
                rot.enabled = true;
                rot.z = new ParticleSystem.MinMaxCurve(-1.5f, 1.5f);
                var noise = ps.noise;
                noise.enabled = true;
                noise.strength = 0.12f + 0.1f * height;
                noise.frequency = 2.2f;
                noise.scrollSpeed = 1.4f;
                noise.damping = true;
                noise.quality = ParticleSystemNoiseQuality.Medium;

                // Draw the blobs in towards a point above the flame as they rise, so each layer tapers to a tip.
                var vel = ps.velocityOverLifetime;
                vel.enabled = true;
                vel.x = vel.y = vel.z = new ParticleSystem.MinMaxCurve(0f);
                vel.orbitalOffsetX = vel.orbitalOffsetY = new ParticleSystem.MinMaxCurve(0f);
                vel.orbitalOffsetZ = new ParticleSystem.MinMaxCurve(height * 1.5f);
                vel.radial = new ParticleSystem.MinMaxCurve(-Mathf.Max(size.x, size.y) * footK * 0.5f / life);

                var rend = ps.GetComponent<ParticleSystemRenderer>();
                rend.sortMode = ParticleSystemSortMode.YoungestInFront;
                rend.sortingFudge = -layer;   // core drawn over mid, mid over outer
            }
        }

        void WindowFire(RegionSpace s, Vector3 center, Vector3 outward)
        {
            float y0 = s.region.floor * g.floorHeight;
            var parent = g.floorRoots[s.region.floor];
            var basePos = new Vector3(center.x, y0 + 0.9f, center.z) + outward * 0.06f;
            var root = new GameObject("WindowFire").transform;
            root.SetParent(parent, false);
            root.localPosition = basePos;
            // Flames fill the window and lick up and out of it.
            bool alongX = Mathf.Abs(outward.x) < 0.5f;
            Flame(root, Vector3.zero, alongX ? new Vector2(0.8f, 0.3f) : new Vector2(0.3f, 0.8f), rng.Range(1.1f, 1.4f),
                Quaternion.FromToRotation(Vector3.up, (Vector3.up + outward * 0.35f).normalized));

            // Soot stains above the window on the outer wall face (wall skin is 0.25 m thick).
            var face = center + outward * 0.262f;
            var wide = alongX ? new Vector3(1.4f, 0.85f, 0.01f) : new Vector3(0.01f, 0.85f, 1.4f);
            var narrow = alongX ? new Vector3(0.9f, 0.55f, 0.01f) : new Vector3(0.01f, 0.55f, 0.9f);
            pal.BoxAt(parent, new Vector3(face.x, y0 + 2.1f + 0.425f, face.z), wide, Char, "Soot");
            pal.BoxAt(parent, new Vector3(face.x, y0 + 2.1f + 0.85f + 0.275f, face.z), narrow, new Color(0.12f, 0.11f, 0.11f), "Soot");

            // Orange glow washing over the facade.
            AddFireLight(parent, new Vector3(center.x, y0 + 1.3f, center.z) + outward * 0.9f, 5f, 2.2f);

            // Thick black smoke pouring out of the top of the window and billowing up the facade.
            var dir = (outward * 0.5f + Vector3.up).normalized;
            Plume(parent, new Vector3(center.x, y0 + 1.95f, center.z) + outward * 0.35f, Quaternion.LookRotation(dir),
                6f, 1.4f, 1.1f, 8f + 10f * g.smokeDensity, 0.42f, 20f, 0.35f, maxSize: 5f, dark: 0.05f, wind: true);
        }

        // ---- Smoke --------------------------------------------------------------------------

        void RoomSmoke(RegionSpace s, bool burning)
        {
            float d = g.smokeDensity * SmokeWeight(s.region.floor) * (burning ? 1.5f : 0.55f);
            Layer(s.parent, s.interior, s.ceilingY, d);
        }

        void HallSmoke(RegionSpace h)
        {
            float d = g.smokeDensity * SmokeWeight(h.region.floor) * 1.3f;
            Layer(h.parent, h.interior, h.ceilingY, d);
        }

        void StairSmoke()
        {
            var st = g.Plan.stairwell;
            var rect = Rect.MinMaxRect(st.xMin, st.yMin, st.xMax, st.yMax);
            for (int f = 0; f < g.floors; f++)
            {
                float d = g.smokeDensity * SmokeWeight(f) * (f >= origin ? 0.8f : 0.3f);
                Layer(g.floorRoots[f], rect, f * g.floorHeight + g.floorHeight - SlabDepth, d);
            }
        }

        /// Hot smoke banked down from a ceiling, plus a thinner haze filling the space below it. Density 0..~1.5.
        void Layer(Transform parent, Rect area, float ceilingY, float density)
        {
            density = Mathf.Clamp(density, 0f, 1.5f);
            if (density < 0.08f) return;
            float k = Mathf.Min(1f, density);
            float w = Mathf.Max(0.2f, area.width - 0.6f), d = Mathf.Max(0.2f, area.height - 0.6f);
            float thick = Mathf.Lerp(0.35f, 1.0f, k);

            // The banked layer: big, dense, overlapping puffs right under the ceiling.
            var ps = NewSystem(parent, "SmokeLayer", new Vector3(area.center.x, ceilingY - 0.2f - thick * 0.5f, area.center.y), Quaternion.identity, Smoke());
            var main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(8f, 12f);
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(0.9f, 1.6f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.1f, 0.09f, 0.085f, 0.5f + 0.35f * k), new Color(0.21f, 0.19f, 0.18f, 0.45f + 0.35f * k));
            main.gravityModifier = -0.004f;
            main.maxParticles = 600;
            var em = ps.emission;
            em.rateOverTime = (w * d * 4f * density + 8f) / 10f;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(w, thick, d);
            Fade(ps, 0.8f, 1.25f);
            Drift(ps, 0.15f);

            // The haze: a few huge, faint puffs filling the room down to the floor.
            float floorY = ceilingY - (g.floorHeight - SlabDepth);
            float hazeTop = ceilingY - 0.2f - thick, hazeH = Mathf.Max(0.3f, hazeTop - floorY - 0.2f);
            var haze = NewSystem(parent, "SmokeHaze", new Vector3(area.center.x, floorY + 0.2f + hazeH * 0.5f, area.center.y), Quaternion.identity, Smoke());
            main = haze.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(10f, 14f);
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(2.4f, 3.6f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.32f, 0.28f, 0.25f, 0.12f + 0.12f * k), new Color(0.42f, 0.37f, 0.33f, 0.1f + 0.1f * k));
            main.maxParticles = 120;
            em = haze.emission;
            em.rateOverTime = (w * d * 0.6f * density + 3f) / 12f;
            shape = haze.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(w, hazeH, d);
            Fade(haze, 0.8f, 1.2f);
            Drift(haze, 0.1f);
        }

        /// A rising stream of smoke puffs that grows as it goes. `dark` is the grey of the darkest puffs;
        /// `wind` bends it downwind (for smoke outside); `glow` tints it with firelight as it leaves the flames.
        ParticleSystem Plume(Transform parent, Vector3 at, Quaternion rot, float lifetime, float speed, float size, float rate,
            float alpha, float coneAngle, float radius, float maxSize = 2.2f, float dark = 0.08f, bool wind = false, bool glow = false)
        {
            var ps = NewSystem(parent, "SmokePlume", at, rot, Smoke());
            var main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(lifetime * 0.8f, lifetime);
            main.startSpeed = new ParticleSystem.MinMaxCurve(speed * 0.8f, speed * 1.1f);
            main.startSize = new ParticleSystem.MinMaxCurve(size * 0.7f, size * 1.2f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(dark, dark * 0.95f, dark * 0.9f, alpha), new Color(dark * 2.4f, dark * 2.3f, dark * 2.2f, alpha * 0.85f));
            main.gravityModifier = -0.05f;   // hot smoke keeps climbing
            main.maxParticles = 400;
            var em = ps.emission;
            em.rateOverTime = rate;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = coneAngle;
            shape.radius = Mathf.Max(0.05f, radius);
            Fade(ps, 0.5f, maxSize / Mathf.Max(0.1f, size), glow ? new Color(1f, 0.62f, 0.38f) : Color.white);
            Drift(ps, 0.35f);
            if (wind)
            {
                var vel = ps.velocityOverLifetime;
                vel.enabled = true;
                vel.space = ParticleSystemSimulationSpace.World;
                vel.x = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0f, 1f, Wind.x * 2f));
                vel.y = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0f, 1f, 0f));
                vel.z = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0f, 1f, Wind.z * 2f));
            }
            return ps;
        }

        void RoofSmoke(HashSet<RegionSpace> burning)
        {
            if (g.RoofRoot == null || g.smokeDensity <= 0.05f) return;
            float y = g.floors * g.floorHeight;
            // The stairwell vents a towering column out of the access hut.
            var st = g.Plan.stairwell;
            Plume(g.RoofRoot, new Vector3(st.center.x, y + 2.6f, st.center.y), Quaternion.LookRotation(Vector3.up),
                10f, 2.2f, 1.8f, 8f + 10f * g.smokeDensity, 0.4f, 10f, 0.7f, maxSize: 8f, dark: 0.05f, wind: true);
            // Fires on the top floor push smoke through the roof above them.
            foreach (var s in burning)
                if (s.region.floor == g.floors - 1)
                    Plume(g.RoofRoot, new Vector3(s.interior.center.x, y + 0.1f, s.interior.center.y), Quaternion.LookRotation(Vector3.up),
                        9f, 2f, 1.6f, 7f + 9f * g.smokeDensity, 0.4f, 14f, 0.8f, maxSize: 7f, dark: 0.05f, wind: true);
        }

        // ---- Exterior windows ---------------------------------------------------------------

        /// Window cells of a region that face outside: centre (building-local, y unused) and outward direction.
        List<(Vector3 center, Vector3 outward, float width)> ExteriorWindows(Region r)
        {
            var list = new List<(Vector3, Vector3, float)>();
            var plan = g.Plan.floors[r.floor];
            var rc = r.rect;
            for (int z = rc.yMin; z < rc.yMax; z++)
            {
                if (plan.vEdges[rc.xMin, z] == EdgeKind.Window && plan.RegionAt(rc.xMin - 1, z) == null)
                    list.Add((new Vector3(rc.xMin, 0, z + 0.5f), Vector3.left, 1f));
                if (plan.vEdges[rc.xMax, z] == EdgeKind.Window && plan.RegionAt(rc.xMax, z) == null)
                    list.Add((new Vector3(rc.xMax, 0, z + 0.5f), Vector3.right, 1f));
            }
            for (int x = rc.xMin; x < rc.xMax; x++)
            {
                if (plan.hEdges[x, rc.yMin] == EdgeKind.Window && plan.RegionAt(x, rc.yMin - 1) == null)
                    list.Add((new Vector3(x + 0.5f, 0, rc.yMin), Vector3.back, 1f));
                if (plan.hEdges[x, rc.yMax] == EdgeKind.Window && plan.RegionAt(x, rc.yMax) == null)
                    list.Add((new Vector3(x + 0.5f, 0, rc.yMax), Vector3.forward, 1f));
            }
            return list;
        }

        // ---- Particle plumbing --------------------------------------------------------------

        ParticleSystem NewSystem(Transform parent, string name, Vector3 at, Quaternion rot, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = at;
            go.transform.localRotation = rot;
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.duration = 8f;
            main.loop = true;
            main.prewarm = true;
            main.playOnAwake = true;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;

            var rend = go.GetComponent<ParticleSystemRenderer>();
            rend.sortingFudge = 0;
            if (mat != emberMaterial)
            {
                // Camera-facing puffs and blobs.
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                rend.renderMode = ParticleSystemRenderMode.Billboard;
                rend.maxParticleSize = 3f;
            }
            else
            {
                main.startRotation3D = true;
                main.startRotationX = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                main.startRotationY = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                main.startRotationZ = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                rend.renderMode = ParticleSystemRenderMode.Mesh;
                rend.mesh = CubeMesh();
                rend.alignment = ParticleSystemRenderSpace.World;
            }
            rend.sharedMaterial = mat;
            rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            rend.receiveShadows = false;
            rend.sortMode = ParticleSystemSortMode.Distance;
            systems.Add(ps);
            return ps;
        }

        /// Alpha fades in and out; size grows from `from` to `to` times the start size. `birth` tints young particles.
        static void Fade(ParticleSystem ps, float from, float to, Color? birth = null)
        {
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(birth ?? Color.white, 0f), new GradientColorKey(Color.white, 0.4f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.2f), new GradientAlphaKey(0.8f, 0.65f), new GradientAlphaKey(0f, 1f) });
            col.color = grad;
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, from, 1f, to));
            var rot = ps.rotationOverLifetime;
            rot.enabled = true;
            rot.z = new ParticleSystem.MinMaxCurve(-0.5f, 0.5f);
        }

        static void Drift(ParticleSystem ps, float strength)
        {
            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = strength;
            noise.frequency = 0.3f;
            noise.scrollSpeed = 0.2f;
            noise.quality = ParticleSystemNoiseQuality.Low;
        }

        static void ConfigureEmbers(ParticleSystem ps, float radius, float height)
        {
            var main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.8f, 1.6f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.6f, 1.4f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.04f, 0.08f);
            main.startColor = new ParticleSystem.MinMaxGradient(Orange, Yellow);
            main.maxParticles = 20;
            main.gravityModifier = -0.05f;
            var em = ps.emission;
            em.rateOverTime = 3f + height * 2f;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 20f;
            shape.radius = radius * 0.8f;
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0f));
        }

        static void AddFireLight(Transform parent, Vector3 at, float range, float intensity)
        {
            var go = new GameObject("FireLight");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = at;
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.range = range;
            light.intensity = intensity;
            light.color = new Color(1f, 0.52f, 0.18f);
            light.shadows = LightShadows.None;
            var flicker = go.AddComponent<FlameFlicker>();
            flicker.flameLight = light;
            flicker.phase = at.x * 3.1f + at.z;
        }

        // ---- Shared assets ------------------------------------------------------------------

        static Mesh CubeMesh()
        {
            if (cubeMesh == null)
            {
                var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cubeMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
                RegionSpace.Kill(tmp);
            }
            return cubeMesh;
        }

        /// Layer 0 red (outer), 1 orange, 2 yellow (core); HDR so the brighter layers bloom.
        static Material FlameMaterial(int layer)
        {
            if (flameMaterials[layer] == null)
            {
                var c = layer == 0 ? new Color(1f, 0.2f, 0.03f) : layer == 1 ? new Color(1f, 0.42f, 0.04f) * 1.15f : new Color(1f, 0.82f, 0.35f) * 1.7f;
                var m = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit")) { name = "Flame" + layer, hideFlags = HideFlags.HideAndDontSave };
                m.SetColor("_BaseColor", new Color(c.r, c.g, c.b, 1f));
                m.SetTexture("_BaseMap", BlobTexture());
                m.SetFloat("_CameraFadingEnabled", 1);
                m.SetVector("_CameraFadeParams", new Vector4(0.1f, 1f / 0.4f, 0, 0));
                m.EnableKeyword("_FADING_ON");
                m.SetFloat("_Surface", 1);
                m.SetFloat("_Blend", 0);
                m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                m.SetFloat("_ZWrite", 0);
                m.SetOverrideTag("RenderType", "Transparent");
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                m.SetShaderPassEnabled("ShadowCaster", false);
                flameMaterials[layer] = m;
            }
            return flameMaterials[layer];
        }

        /// A flat, crisp-edged, lumpy blob, for the stylised flames.
        static Texture2D BlobTexture()
        {
            if (blobTexture != null) return blobTexture;
            const int N = 128;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, true) { name = "FlameBlob", wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float u = (x + 0.5f) / N * 2f - 1f, v = (y + 0.5f) / N * 2f - 1f;
                    float r = Mathf.Sqrt(u * u + v * v), ang = Mathf.Atan2(v, u);
                    float lumps = Mathf.PerlinNoise(Mathf.Cos(ang) * 1.6f + 5.3f, Mathf.Sin(ang) * 1.6f + 2.9f);
                    float edge = 0.62f + 0.3f * lumps;
                    float a = Mathf.Clamp01((edge - r) / 0.035f);
                    px[y * N + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            tex.SetPixels32(px);
            tex.Apply(true, false);
            blobTexture = tex;
            return tex;
        }

        static Material Ember()
        {
            if (emberMaterial == null)
            {
                emberMaterial = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit")) { name = "Ember", hideFlags = HideFlags.HideAndDontSave };
                emberMaterial.SetColor("_BaseColor", Color.white * FlameGlow * 1.5f);
            }
            return emberMaterial;
        }

        /// Soft-edged against walls and faded near the camera. Unlit: camera-facing puffs shade badly under point lights.
        static Material Smoke()
        {
            if (smokeMaterial == null)
            {
                var m = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit")) { name = "Smoke", hideFlags = HideFlags.HideAndDontSave };
                m.SetColor("_BaseColor", Color.white);
                m.SetTexture("_BaseMap", PuffTexture());
                m.SetFloat("_SoftParticlesEnabled", 1);
                m.SetVector("_SoftParticleFadeParams", new Vector4(0f, 1f / 0.8f, 0, 0));
                m.EnableKeyword("_SOFTPARTICLES_ON");
                m.SetFloat("_CameraFadingEnabled", 1);
                m.SetVector("_CameraFadeParams", new Vector4(0.3f, 1f / 1.2f, 0, 0));
                m.EnableKeyword("_FADING_ON");
                m.SetFloat("_Surface", 1);
                m.SetFloat("_Blend", 0);
                m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                m.SetFloat("_ZWrite", 0);
                m.SetOverrideTag("RenderType", "Transparent");
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                m.SetShaderPassEnabled("ShadowCaster", false);
                smokeMaterial = m;
            }
            return smokeMaterial;
        }

        /// A round, lumpy, soft-edged white puff; the particle colour tints it.
        static Texture2D PuffTexture()
        {
            if (puffTexture != null) return puffTexture;
            const int N = 128;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, true) { name = "SmokePuff", wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float u = (x + 0.5f) / N * 2f - 1f, v = (y + 0.5f) / N * 2f - 1f;
                    float r = Mathf.Sqrt(u * u + v * v);
                    float noise = 0f, amp = 0.5f, freq = 3f;
                    for (int o = 0; o < 4; o++, amp *= 0.5f, freq *= 2f)
                        noise += amp * Mathf.PerlinNoise(u * freq + 13.7f, v * freq + 4.1f);
                    float edge = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((r - 0.15f + (noise - 0.47f) * 0.35f) / 0.85f));
                    float a = edge * edge * Mathf.Lerp(0.55f, 1f, noise * 1.1f) * Mathf.Clamp01((1f - r) * 4f);
                    px[y * N + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(a) * 255f));
                }
            tex.SetPixels32(px);
            tex.Apply(true, false);
            puffTexture = tex;
            return tex;
        }
    }
}
