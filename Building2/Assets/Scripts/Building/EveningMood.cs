using UnityEngine;

namespace Drone2.Building
{
    /// Turns the scene into a sunset evening in Play mode: a low orange sun, a redder sky, dimmer ambient light,
    /// and dimmer ceiling lights, some of them flickering. Reapplied after every rebuild. Edit mode (and so the
    /// dataset generator, which has its own randomised version) is left alone.
    [RequireComponent(typeof(BuildingGenerator))]
    public class EveningMood : MonoBehaviour
    {
        [Tooltip("How far daylight is turned down: 0 = the scene as saved, 1 = no sun or ambient light.")]
        [Range(0f, 1f)] public float darkness = 0.55f;
        [Tooltip("Sun height above the horizon in degrees. Low = orange sunset sky.")]
        [Range(0f, 30f)] public float sunElevation = 5f;
        public float sunHeading = 300f;
        [Tooltip("Ceiling light brightness in buildings without fire (fire scenes dim their own).")]
        [Range(0f, 1f)] public float ceilingLightDim = 0.6f;
        [Range(0f, 1f)] public float flickerShare = 0.3f;

        BuildingGenerator gen;
        Material skyCopy;

        void Awake()
        {
            gen = GetComponent<BuildingGenerator>();
            gen.Generated += OnGenerated;
            ApplySky();
        }

        void OnDestroy()
        {
            if (gen != null) gen.Generated -= OnGenerated;
            if (skyCopy != null) Destroy(skyCopy);
        }

        void OnGenerated(BuildingGenerator g)
        {
            if (Application.isPlaying) ApplyLights();
        }

        /// Sun, sky and ambient: once per Play session (Play-mode changes to them are not saved).
        void ApplySky()
        {
            if (!Application.isPlaying) return;
            var sun = RenderSettings.sun;
            if (sun != null)
            {
                sun.intensity *= 1f - darkness;
                sun.color = Color.Lerp(sun.color, new Color(1f, 0.5f, 0.25f), 0.6f + 0.4f * darkness);
                sun.transform.rotation = Quaternion.Euler(sunElevation, sunHeading, 0f);
            }
            var sky = RenderSettings.skybox;
            if (sky != null && sky.shader.name == "Skybox/Procedural")
            {
                skyCopy = new Material(sky);   // never change the material asset
                skyCopy.SetFloat("_AtmosphereThickness", 1.9f);
                skyCopy.SetFloat("_Exposure", sky.GetFloat("_Exposure") * Mathf.Lerp(1f, 0.6f, darkness));
                skyCopy.SetColor("_SkyTint", new Color(0.55f, 0.45f, 0.45f));
                skyCopy.SetColor("_GroundColor", new Color(0.18f, 0.14f, 0.13f));
                RenderSettings.skybox = skyCopy;
            }
            var dusk = new Color(0.75f, 0.8f, 1f);
            float amb = 1f - 0.8f * darkness;
            RenderSettings.ambientSkyColor *= dusk * amb;
            RenderSettings.ambientEquatorColor *= dusk * amb;
            RenderSettings.ambientGroundColor *= amb;
            DynamicGI.UpdateEnvironment();
        }

        /// Ceiling lights of the current building: dimmed (unless the fire already did) and some flickering.
        void ApplyLights()
        {
            var rng = new System.Random(gen.seed);
            foreach (var light in gen.Root.GetComponentsInChildren<Light>(true))
            {
                if (light.name != "CeilingLight" || !light.enabled) continue;
                if (light.GetComponent<LightFlicker>() != null) continue;   // the fire's failing wiring
                if (!gen.fireAndSmoke) light.intensity *= ceilingLightDim;
                if (rng.NextDouble() < flickerShare)
                    light.gameObject.AddComponent<LightFlicker>().phase = (float)rng.NextDouble() * 100f;
            }
        }
    }
}
