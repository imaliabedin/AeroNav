using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using Drone2.Building;
using UnityEngine;

/// Builds the Rescue Mission (the two-drone search scene, DroneSearchDemo.unity) for the website (WebGL) into
/// <repo>/rescue-sim/, which rescue.html loads.
///   Menu: Drone 2/Build Web (Rescue Mission)
///   Unity -batchmode -projectPath Building2 -executeMethod RescueWebBuild.BuildBatch
/// Compression is off so any simple static server can serve it. Changed player settings are put back afterwards.
public static class RescueWebBuild
{
    const string Scene = "Assets/Scenes/DroneSearchDemo.unity";
    const string ShaderDir = "Assets/Resources/RescueShaders";
    // Shaders the building and drones look up by name at run time (WebGL strips shaders nothing references).
    static readonly string[] RuntimeShaders =
    {
        "Hidden/Internal-Colored", "Skybox/Procedural", "Universal Render Pipeline/Lit", "Universal Render Pipeline/Particles/Unlit",
    };

    static string OutDir => Path.GetFullPath(Path.Combine(Application.dataPath, "../../rescue-sim"));

    [MenuItem("Drone 2/Build Web (Rescue Mission)")]
    public static void BuildMenu() => Build();

    public static void BuildBatch() => EditorApplication.Exit(Build() ? 0 : 1);

    static bool Build()
    {
        if (!File.Exists(Scene)) DroneSearchDemo.CreateScene();
        KeepRuntimeShaders();
        KeepRuntimeVariants();

        var compression = PlayerSettings.WebGL.compressionFormat;
        var fallback = PlayerSettings.WebGL.decompressionFallback;
        var template = PlayerSettings.WebGL.template;
        try
        {
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
            PlayerSettings.WebGL.decompressionFallback = false;
            PlayerSettings.WebGL.template = "APPLICATION:Minimal";
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { Scene },
                locationPathName = OutDir,
                target = BuildTarget.WebGL,
                options = BuildOptions.None,
            });
            var s = report.summary;
            Debug.Log($"[RescueWebBuild] {s.result}: {s.totalErrors} errors, {s.totalWarnings} warnings, " +
                      $"{s.totalSize / 1e6:0.0} MB, {s.totalTime.TotalMinutes:0.0} min -> {OutDir}");
            return s.result == BuildResult.Succeeded;
        }
        finally
        {
            PlayerSettings.WebGL.compressionFormat = compression;
            PlayerSettings.WebGL.decompressionFallback = fallback;
            PlayerSettings.WebGL.template = template;
            AssetDatabase.SaveAssets();
        }
    }

    /// The building makes its fire, smoke and furniture materials at run time and sets their shader keywords (e.g.
    /// transparent particles). WebGL only includes shader variants that some saved material uses, so without this the
    /// smoke renders as hard opaque quads in the browser. Generates the building once in the editor (scene not saved),
    /// and saves a copy of each distinct run-time material (shader + keywords + queue) in Resources.
    static void KeepRuntimeVariants()
    {
        var dir = ShaderDir + "/Variants";
        if (Directory.Exists(dir)) AssetDatabase.DeleteAsset(dir);
        Directory.CreateDirectory(dir);
        AssetDatabase.Refresh();
        EditorSceneManager.OpenScene(Scene);
        var gen = Object.FindFirstObjectByType<BuildingGenerator>();
        gen.fireAndSmoke = true;
        gen.Generate();
        var seen = new System.Collections.Generic.HashSet<string>();
        int n = 0;
        var renderers = Resources.FindObjectsOfTypeAll<Renderer>();   // includes the generator's hidden objects
        Debug.Log($"[RescueWebBuild] generated building: {renderers.Length} renderers");
        foreach (var r in renderers)
        {
            if (EditorUtility.IsPersistent(r)) continue;            // prefabs/assets, not scene objects
            var mats = new System.Collections.Generic.List<Material>(r.sharedMaterials);
            if (r is ParticleSystemRenderer pr && pr.trailMaterial != null) mats.Add(pr.trailMaterial);
            foreach (var m in mats)
            {
                if (m == null || m.shader == null || AssetDatabase.Contains(m)) continue;
                var kw = m.shaderKeywords; System.Array.Sort(kw);
                string key = m.shader.name + "|" + string.Join(",", kw) + "|" + m.renderQueue;
                if (!seen.Add(key)) continue;
                var copy = new Material(m) { name = "Variant" + n };
                foreach (var tex in copy.GetTexturePropertyNames()) copy.SetTexture(tex, null);   // run-time textures aren't assets
                AssetDatabase.CreateAsset(copy, $"{dir}/Variant{n++}.mat");
            }
        }
        AssetDatabase.SaveAssets();
        EditorSceneManager.OpenScene(Scene);           // drop the generated building (never saved)
        Debug.Log($"[RescueWebBuild] kept {n} run-time material variants");
    }

    /// One material per run-time shader in Resources, so the build includes the shader.
    static void KeepRuntimeShaders()
    {
        Directory.CreateDirectory(ShaderDir);
        foreach (var name in RuntimeShaders)
        {
            var path = $"{ShaderDir}/{name.Replace('/', '_').Replace(' ', '_')}.mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) continue;
            var shader = Shader.Find(name);
            if (shader == null) { Debug.LogWarning("[RescueWebBuild] shader not found: " + name); continue; }
            AssetDatabase.CreateAsset(new Material(shader), path);
        }
        AssetDatabase.SaveAssets();
    }
}
