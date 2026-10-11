using Drone2.Building;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// Creates the BurningBuilding scene: sun, sky, viewer camera and the building generator.
static class BuildingSceneSetup
{
    const string ScenePath = "Assets/Scenes/BurningBuilding.unity";
    const string BaseMatPath = "Assets/Materials/BuildingBase.mat";
    const string GlassMatPath = "Assets/Materials/Glass.mat";
    const string SkyMatPath = "Assets/Materials/SmokySky.mat";
    const string ProfilePath = "Assets/Settings/FireMoodProfile.asset";

    [MenuItem("Drone 2/Create Building Scene")]
    public static void CreateScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.shadows = LightShadows.Soft;
        RenderSettings.sun = sun;

        var gen = new GameObject("Building Generator").AddComponent<BuildingGenerator>();
        gen.baseMaterial = BaseMaterial();
        gen.glassMaterial = GlassMaterial();

        var cam = new GameObject("Main Camera").AddComponent<Camera>();
        cam.tag = "MainCamera";
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 500f;
        cam.transform.position = new Vector3(-20f, 22f, -26f);
        cam.transform.rotation = Quaternion.Euler(35f, 38f, 0f);
        cam.gameObject.AddComponent<AudioListener>();
        cam.gameObject.AddComponent<BuildingViewer>().generator = gen;

        ApplyMood();
        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        Debug.Log("Created " + ScenePath);
    }

    /// Dusk under a smoke-darkened sky: a low, dim, orange sun, dark ambient light, brown haze,
    /// and grim post-processing (bloom on the flames, desaturated contrasty grade, vignette, grain).
    [MenuItem("Drone 2/Apply Fire Mood")]
    public static void ApplyMood()
    {
        var sun = RenderSettings.sun;
        if (sun == null)
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.type == LightType.Directional) sun = l;
        if (sun != null)
        {
            sun.intensity = 1.0f;
            sun.color = new Color(1f, 0.5f, 0.3f);
            sun.transform.rotation = Quaternion.Euler(14, -35, 0);
            RenderSettings.sun = sun;
        }

        RenderSettings.skybox = SkyMaterial();
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.34f, 0.26f, 0.23f);
        RenderSettings.ambientEquatorColor = new Color(0.22f, 0.17f, 0.14f);
        RenderSettings.ambientGroundColor = new Color(0.08f, 0.07f, 0.06f);
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Exponential;
        RenderSettings.fogDensity = 0.014f;
        RenderSettings.fogColor = new Color(0.16f, 0.11f, 0.09f);

        var volume = Object.FindFirstObjectByType<Volume>();
        if (volume == null) volume = new GameObject("Post Processing").AddComponent<Volume>();
        volume.isGlobal = true;
        volume.sharedProfile = MoodProfile();

        foreach (var cam in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
            cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;

        EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
    }

    static Material SkyMaterial()
    {
        var m = AssetDatabase.LoadAssetAtPath<Material>(SkyMatPath);
        if (m != null) return m;
        m = new Material(Shader.Find("Skybox/Procedural"));
        m.SetFloat("_SunDisk", 1);
        m.SetFloat("_SunSize", 0.03f);
        m.SetFloat("_AtmosphereThickness", 2.6f);
        m.SetColor("_SkyTint", new Color(0.32f, 0.22f, 0.2f));
        m.SetColor("_GroundColor", new Color(0.08f, 0.07f, 0.065f));
        m.SetFloat("_Exposure", 0.9f);
        AssetDatabase.CreateAsset(m, SkyMatPath);
        return m;
    }

    static VolumeProfile MoodProfile()
    {
        var p = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
        if (p != null) return p;
        p = ScriptableObject.CreateInstance<VolumeProfile>();
        AssetDatabase.CreateAsset(p, ProfilePath);

        var tone = p.Add<Tonemapping>(true);
        tone.mode.Override(TonemappingMode.ACES);
        var bloom = p.Add<Bloom>(true);
        bloom.threshold.Override(1f);
        bloom.intensity.Override(1.4f);
        bloom.scatter.Override(0.75f);
        bloom.tint.Override(new Color(1f, 0.7f, 0.5f));
        var grade = p.Add<ColorAdjustments>(true);
        grade.postExposure.Override(0.3f);
        grade.contrast.Override(20f);
        grade.saturation.Override(-30f);
        grade.colorFilter.Override(new Color(1f, 0.92f, 0.85f));
        var vignette = p.Add<Vignette>(true);
        vignette.intensity.Override(0.4f);
        vignette.smoothness.Override(0.5f);
        var grain = p.Add<FilmGrain>(true);
        grain.type.Override(FilmGrainLookup.Medium1);
        grain.intensity.Override(0.3f);
        grain.response.Override(0.8f);

        foreach (var c in p.components) AssetDatabase.AddObjectToAsset(c, p);
        AssetDatabase.SaveAssets();
        return p;
    }

    static Material BaseMaterial()
    {
        var m = AssetDatabase.LoadAssetAtPath<Material>(BaseMatPath);
        if (m != null) return m;
        m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        m.SetFloat("_Smoothness", 0.15f);
        m.enableInstancing = true;
        AssetDatabase.CreateAsset(m, BaseMatPath);
        return m;
    }

    static Material GlassMaterial()
    {
        var m = AssetDatabase.LoadAssetAtPath<Material>(GlassMatPath);
        if (m != null) return m;
        m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        m.SetColor("_BaseColor", new Color(0.7f, 0.85f, 0.95f, 0.25f));
        m.SetFloat("_Smoothness", 0.9f);
        m.SetFloat("_Surface", 1);
        m.SetFloat("_Blend", 0);
        m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_ZWrite", 0);
        m.SetOverrideTag("RenderType", "Transparent");
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.renderQueue = (int)RenderQueue.Transparent;
        AssetDatabase.CreateAsset(m, GlassMatPath);
        return m;
    }
}
