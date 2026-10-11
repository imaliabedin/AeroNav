using UnityEngine;

namespace Drone2.Building
{
    /// Small low-poly props the furniture kit lacks, built from boxes and cylinders.
    /// Wall items are built with their back on the wall plane and their face toward local +Z.
    public static class Decor
    {
        static readonly Color White = new(0.93f, 0.93f, 0.9f);
        static readonly Color Dark = new(0.15f, 0.15f, 0.17f);
        static readonly Color FrameWood = new(0.35f, 0.22f, 0.13f);
        static readonly Color Red = new(0.78f, 0.1f, 0.08f);

        static Transform Root(Transform parent, string name, Vector3 at, float yaw)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = at;
            t.localRotation = Quaternion.Euler(0, yaw, 0);
            return t;
        }

        public static Transform Outlet(Palette pal, Transform parent, Vector3 at, float yaw)
        {
            var t = Root(parent, "Outlet", at, yaw);
            pal.BoxAt(t, new Vector3(0, 0, 0.005f), new Vector3(0.08f, 0.12f, 0.01f), White, "Plate");
            pal.BoxAt(t, new Vector3(0, 0.025f, 0.011f), new Vector3(0.03f, 0.015f, 0.004f), Dark, "Socket");
            pal.BoxAt(t, new Vector3(0, -0.025f, 0.011f), new Vector3(0.03f, 0.015f, 0.004f), Dark, "Socket");
            return t;
        }

        public static Transform LightSwitch(Palette pal, Transform parent, Vector3 at, float yaw)
        {
            var t = Root(parent, "LightSwitch", at, yaw);
            pal.BoxAt(t, new Vector3(0, 0, 0.005f), new Vector3(0.08f, 0.12f, 0.01f), White, "Plate");
            pal.BoxAt(t, new Vector3(0, 0.01f, 0.014f), new Vector3(0.02f, 0.04f, 0.012f), new Color(0.8f, 0.8f, 0.78f), "Toggle");
            return t;
        }

        public static Transform Painting(Palette pal, Rng rng, Transform parent, Vector3 at, float yaw)
        {
            var t = Root(parent, "Painting", at, yaw);
            float w = rng.Range(0.5f, 1.0f), h = rng.Range(0.4f, 0.7f);
            pal.BoxAt(t, new Vector3(0, 0, 0.02f), new Vector3(w, h, 0.04f), FrameWood, "Frame");
            var bg = Color.HSVToRGB(rng.Range(0f, 1f), rng.Range(0.1f, 0.4f), rng.Range(0.6f, 0.95f));
            pal.BoxAt(t, new Vector3(0, 0, 0.041f), new Vector3(w - 0.08f, h - 0.08f, 0.004f), bg, "Canvas");
            // A few flat shapes for "art".
            int shapes = rng.Range(2, 4);
            for (int i = 0; i < shapes; i++)
            {
                var c = Color.HSVToRGB(rng.Range(0f, 1f), rng.Range(0.4f, 0.8f), rng.Range(0.4f, 0.9f));
                var size = new Vector3(rng.Range(0.1f, w * 0.5f), rng.Range(0.08f, h * 0.5f), 0.004f);
                var pos = new Vector3(rng.Range(-(w - 0.1f - size.x) / 2, (w - 0.1f - size.x) / 2),
                    rng.Range(-(h - 0.1f - size.y) / 2, (h - 0.1f - size.y) / 2), 0.044f + i * 0.001f);
                pal.BoxAt(t, pos, size, c, "Shape");
            }
            return t;
        }

        public static Transform Clock(Palette pal, Rng rng, Transform parent, Vector3 at, float yaw)
        {
            var t = Root(parent, "WallClock", at, yaw);
            var face = Quaternion.Euler(90, 0, 0);
            pal.Cylinder(t, new Vector3(0, 0, 0.025f), 0.17f, 0.05f, rng.Chance(0.5f) ? Dark : FrameWood, face, "Rim");
            pal.Cylinder(t, new Vector3(0, 0, 0.051f), 0.145f, 0.004f, White, face, "Face");
            // Hands at a random time.
            float hour = rng.Range(0f, 360f), minute = rng.Range(0f, 360f);
            var hourHand = new GameObject("HourHand").transform;
            hourHand.SetParent(t, false);
            hourHand.localPosition = new Vector3(0, 0, 0.056f);
            hourHand.localRotation = Quaternion.Euler(0, 0, -hour);
            pal.BoxAt(hourHand, new Vector3(0, 0.04f, 0), new Vector3(0.015f, 0.08f, 0.004f), Dark, "Hand");
            var minuteHand = new GameObject("MinuteHand").transform;
            minuteHand.SetParent(t, false);
            minuteHand.localPosition = new Vector3(0, 0, 0.06f);
            minuteHand.localRotation = Quaternion.Euler(0, 0, -minute);
            pal.BoxAt(minuteHand, new Vector3(0, 0.06f, 0), new Vector3(0.01f, 0.12f, 0.004f), Dark, "Hand");
            return t;
        }

        /// Fire extinguisher hanging on a wall bracket; `at` is the bracket height on the wall.
        public static Transform Extinguisher(Palette pal, Transform parent, Vector3 at, float yaw)
        {
            var t = Root(parent, "FireExtinguisher", at, yaw);
            pal.BoxAt(t, new Vector3(0, 0, 0.01f), new Vector3(0.12f, 0.06f, 0.02f), Dark, "Bracket");
            pal.Cylinder(t, new Vector3(0, -0.2f, 0.1f), 0.075f, 0.5f, Red, Quaternion.identity, "Body", true);
            pal.Cylinder(t, new Vector3(0, 0.08f, 0.1f), 0.03f, 0.06f, Dark, Quaternion.identity, "Valve");
            pal.BoxAt(t, new Vector3(0, 0.12f, 0.12f), new Vector3(0.03f, 0.02f, 0.12f), Dark, "Handle");
            pal.BoxAt(t, new Vector3(0.06f, -0.05f, 0.14f), new Vector3(0.02f, 0.3f, 0.02f), Dark, "Hose");
            return t;
        }

        public static Transform FireAlarm(Palette pal, Transform parent, Vector3 at, float yaw)
        {
            var t = Root(parent, "FireAlarmPull", at, yaw);
            pal.BoxAt(t, new Vector3(0, 0, 0.025f), new Vector3(0.12f, 0.16f, 0.05f), Red, "Body");
            pal.BoxAt(t, new Vector3(0, -0.01f, 0.052f), new Vector3(0.07f, 0.03f, 0.01f), White, "Handle");
            return t;
        }

        public static Transform ExitSign(Palette pal, Transform parent, Vector3 at, float yaw)
        {
            var t = Root(parent, "ExitSign", at, yaw);
            pal.BoxAt(t, new Vector3(0, 0, 0.04f), new Vector3(0.4f, 0.18f, 0.08f), new Color(0.1f, 0.6f, 0.25f), "Sign");
            pal.BoxAt(t, new Vector3(0, 0, 0.081f), new Vector3(0.28f, 0.07f, 0.004f), White, "Text");
            return t;
        }

        /// Ceiling smoke detector; `at` is on the ceiling surface.
        public static Transform SmokeDetector(Palette pal, Transform parent, Vector3 at)
        {
            var t = Root(parent, "SmokeDetector", at, 0);
            pal.Cylinder(t, new Vector3(0, -0.02f, 0), 0.07f, 0.04f, White, Quaternion.identity, "Body");
            pal.Cylinder(t, new Vector3(0, -0.041f, 0), 0.01f, 0.004f, Red, Quaternion.identity, "Led");
            return t;
        }

        public static Transform Thermostat(Palette pal, Transform parent, Vector3 at, float yaw)
        {
            var t = Root(parent, "Thermostat", at, yaw);
            pal.BoxAt(t, new Vector3(0, 0, 0.015f), new Vector3(0.12f, 0.12f, 0.03f), White, "Body");
            pal.BoxAt(t, new Vector3(0, 0.01f, 0.031f), new Vector3(0.08f, 0.04f, 0.003f), new Color(0.3f, 0.5f, 0.6f), "Screen");
            return t;
        }
    }
}
