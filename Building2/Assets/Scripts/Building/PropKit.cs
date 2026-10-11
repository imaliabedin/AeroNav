using System.Collections.Generic;
using UnityEngine;

namespace Drone2.Building
{
    /// A spawned prop: a pivot at the centre of its footprint on the floor, front facing the pivot's +Z.
    public class Placed
    {
        public Transform pivot;
        /// Unrotated size: x = width, y = height, z = depth (front to back).
        public Vector3 size;

        public Vector3 Position => pivot.localPosition;
        public Vector3 Forward => pivot.localRotation * Vector3.forward;
        public Vector3 Right => pivot.localRotation * Vector3.right;
        public float Yaw => pivot.localEulerAngles.y;
    }

    /// Loads Kenney Furniture Kit models from Resources and normalises them: uniform scale,
    /// front along +Z, pivot at the bottom centre of the bounds, mesh colliders for drones.
    public class PropKit
    {
        /// Kenney furniture is modelled at about 5x real size; 0.2 brings a double bed to ~2.2 m.
        public float scale = 1f;
        /// Rotation applied to every model so its front faces +Z.
        public float yawFix;

        readonly Dictionary<string, GameObject> cache = new();

        GameObject Source(string name)
        {
            if (!cache.TryGetValue(name, out var go))
            {
                go = Resources.Load<GameObject>("Furniture/" + name);
                if (go == null) Debug.LogWarning($"Furniture model '{name}' not found in Resources/Furniture");
                cache[name] = go;
            }
            return go;
        }

        public Placed Spawn(string name, Transform parent)
        {
            var src = Source(name);
            if (src == null) return null;

            var pivot = new GameObject(name).transform;
            var model = Object.Instantiate(src, pivot);
            model.name = "model";
            model.transform.localRotation = Quaternion.Euler(0, yawFix, 0) * src.transform.localRotation;
            model.transform.localScale = src.transform.localScale * scale;

            var renderers = model.GetComponentsInChildren<Renderer>();
            var bounds = renderers.Length > 0 ? renderers[0].bounds : new Bounds();
            foreach (var r in renderers) bounds.Encapsulate(r.bounds);
            model.transform.localPosition = new Vector3(-bounds.center.x, -bounds.min.y, -bounds.center.z);

            foreach (var mf in model.GetComponentsInChildren<MeshFilter>())
                mf.gameObject.AddComponent<MeshCollider>().sharedMesh = mf.sharedMesh;

            pivot.SetParent(parent, false);
            return new Placed { pivot = pivot, size = bounds.size };
        }
    }

    /// Simple coloured-box helper for walls, floors and procedurally built decorations.
    public class Palette
    {
        readonly Material baseMaterial;
        readonly Dictionary<Color, Material> materials = new();
        public Material glass;

        public Palette(Material baseMaterial, Material glass)
        {
            this.baseMaterial = baseMaterial != null
                ? baseMaterial
                : new Material(Shader.Find("Universal Render Pipeline/Lit"));
            this.glass = glass;
        }

        public Material Get(Color c)
        {
            if (!materials.TryGetValue(c, out var m))
            {
                m = new Material(baseMaterial) { name = "Color " + ColorUtility.ToHtmlStringRGB(c) };
                m.SetColor("_BaseColor", c);
                m.color = c;
                materials[c] = m;
            }
            return m;
        }

        /// A box from min to max corner in the parent's local space.
        public GameObject Box(Transform parent, Vector3 min, Vector3 max, Color c, string name = "Box", bool collider = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            if (!collider) RegionSpace.Kill(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = (min + max) * 0.5f;
            go.transform.localScale = max - min;
            go.GetComponent<Renderer>().sharedMaterial = Get(c);
            return go;
        }

        /// A box centred at a point, in the parent's local space.
        public GameObject BoxAt(Transform parent, Vector3 center, Vector3 size, Color c, string name = "Box", bool collider = false)
            => Box(parent, center - size * 0.5f, center + size * 0.5f, c, name, collider);

        public GameObject Cylinder(Transform parent, Vector3 center, float radius, float height, Color c,
            Quaternion rotation, string name = "Cylinder", bool collider = false)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            RegionSpace.Kill(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = center;
            go.transform.localRotation = rotation;
            go.transform.localScale = new Vector3(radius * 2, height * 0.5f, radius * 2);
            go.GetComponent<Renderer>().sharedMaterial = Get(c);
            if (collider) go.AddComponent<CapsuleCollider>();
            return go;
        }
    }
}
