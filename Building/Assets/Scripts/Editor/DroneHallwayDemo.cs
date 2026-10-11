using System.IO;
using Drone2.Building;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// Demo of the drone hovering in the first-floor hallway. Builds its own scene so BurningBuilding stays as is.
/// Menu: Drone 2 > Drone Hallway Demo. Batch: -executeMethod DroneHallwayDemo.RenderFrames -out <dir> [-seed n].
static class DroneHallwayDemo
{
    const string ScenePath = "Assets/Scenes/DroneHallwayDemo.unity";
    const string DronePath = "Assets/Prefabs/Drone.prefab";

    [MenuItem("Drone 2/Drone Hallway Demo")]
    public static void Play()
    {
        if (!File.Exists(ScenePath)) CreateScene();
        EditorSceneManager.OpenScene(ScenePath);
        EditorApplication.EnterPlaymode();
    }

    [MenuItem("Drone 2/Recreate Drone Hallway Demo Scene")]
    public static void CreateScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // Same sun and sky as BuildingSceneSetup.
        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.intensity = 1.3f;
        sun.color = new Color(1f, 0.96f, 0.88f);
        sun.shadows = LightShadows.Soft;
        sun.transform.rotation = Quaternion.Euler(50, -35, 0);
        RenderSettings.skybox = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Skybox.mat");
        RenderSettings.sun = sun;
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.62f, 0.68f, 0.78f);
        RenderSettings.ambientEquatorColor = new Color(0.5f, 0.5f, 0.5f);
        RenderSettings.ambientGroundColor = new Color(0.32f, 0.3f, 0.28f);

        var gen = new GameObject("Building Generator").AddComponent<BuildingGenerator>();
        gen.baseMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/BuildingBase.mat");
        gen.glassMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/Glass.mat");

        var cam = new GameObject("Main Camera").AddComponent<Camera>();
        cam.tag = "MainCamera";
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 500f;
        cam.fieldOfView = 50f;
        cam.gameObject.AddComponent<AudioListener>();

        var drone = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(DronePath));
        var hover = drone.AddComponent<DroneHover>();
        hover.generator = gen;
        hover.showcaseCamera = cam;

        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log("Created " + ScenePath);
    }

    /// Renders the hover as numbered PNG frames, for checking without opening the Editor UI.
    public static void RenderFrames()
    {
        string outDir = Arg("-out", "DemoFrames");
        int frames = int.Parse(Arg("-frames", "60"));
        Directory.CreateDirectory(outDir);
        CreateScene();
        EditorSceneManager.OpenScene(ScenePath);
        var gen = Object.FindFirstObjectByType<BuildingGenerator>();
        var hover = Object.FindFirstObjectByType<DroneHover>();
        gen.seed = int.Parse(Arg("-seed", gen.seed.ToString()));
        gen.Generate();
        hover.Place(gen);

        var cam = hover.showcaseCamera;
        var props = hover.GetComponentsInChildren<PropellorSpin>();
        Shot(cam, Path.Combine(outDir, "warmup.png"), 320, 200);   // first batch render comes out unlit
        File.Delete(Path.Combine(outDir, "warmup.png"));
        const float dt = 1f / 15f;
        for (int i = 0; i < frames; i++)
        {
            hover.Pose(i * dt);
            foreach (var p in props) p.transform.Rotate(0f, p.maxSpeed * dt * 0.37f, 0f);
            Shot(cam, Path.Combine(outDir, $"frame_{i:D3}.png"), 960, 600);
        }
        Debug.Log($"DEMO rendered {frames} frames to {outDir} (seed {gen.seed})");
    }

    static string Arg(string name, string fallback)
    {
        var args = System.Environment.GetCommandLineArgs();
        int i = System.Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }

    static void Shot(Camera cam, string path, int w, int h)
    {
        var rt = new RenderTexture(w, h, 24);
        cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        tex.Apply();
        File.WriteAllBytes(path, tex.EncodeToPNG());
        cam.targetTexture = null;
        RenderTexture.active = null;
        Object.DestroyImmediate(rt);
        Object.DestroyImmediate(tex);
    }
}
