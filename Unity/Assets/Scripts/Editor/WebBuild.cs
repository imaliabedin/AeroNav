using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// Builds the Drone City scene for the website (WebGL) into <repo>/drone-city/, which city.html loads.
///   Menu: Drone Sim/Build Web (Drone City)
///   Unity -batchmode -projectPath Unity -executeMethod WebBuild.BuildDroneCity
/// Compression is off so any simple static server (python3 -m http.server) can serve it.
/// The WebGL player settings it changes are put back afterwards.
public static class WebBuild
{
    const string Scene = "Assets/Scenes/DroneCity.unity";
    const string LineMaterial = "Assets/Resources/SonarLines.mat";

    static string OutDir => Path.GetFullPath(Path.Combine(Application.dataPath, "../../drone-city"));

    [MenuItem("Drone Sim/Build Web (Drone City)")]
    public static void BuildDroneCityMenu() => Build();

    public static void BuildDroneCity() => EditorApplication.Exit(Build() ? 0 : 1);

    static bool Build()
    {
        EnsureLineMaterial();

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
            Debug.Log($"[WebBuild] {s.result}: {s.totalErrors} errors, {s.totalWarnings} warnings, " +
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

    /// The sonar overlay draws with this material; keeping it in Resources makes the build include its shader.
    static void EnsureLineMaterial()
    {
        if (AssetDatabase.LoadAssetAtPath<Material>(LineMaterial) != null) return;
        if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
        AssetDatabase.CreateAsset(new Material(Shader.Find("Hidden/Internal-Colored")), LineMaterial);
        AssetDatabase.SaveAssets();
    }
}
