using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// Sonar-only demo: opens ParkingLot, enters Play mode, turns off the vision camera/detector,
/// drops obstacles the drone has never seen onto its route (Play mode only, nothing is saved),
/// and launches the drone in Sonar mode with the camera following it.
///   Menu: Drone Sim/Sonar Demo     or   Unity -projectPath Unity -executeMethod SonarDemo.Run
/// Set SONAR_DEMO_FRAMES=<folder> to save a screenshot every second.
[InitializeOnLoad]
public static class SonarDemo
{
    const string Pending = "SonarDemo.Pending";
    static double startAt, nextShot;
    static int shot;
    static bool launched;

    static SonarDemo() => EditorApplication.playModeStateChanged += OnPlayMode;

    [MenuItem("Drone Sim/Sonar Demo")]
    public static void Run()
    {
        if (EditorApplication.isPlaying) return;
        EditorSceneManager.OpenScene("Assets/Scenes/ParkingLot.unity", OpenSceneMode.Single);
        SessionState.SetBool(Pending, true);   // survives the domain reload when Play mode starts
        EditorApplication.EnterPlaymode();
    }

    static void OnPlayMode(PlayModeStateChange change)
    {
        if (change == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
        {
            SessionState.SetBool(Pending, false);
            startAt = EditorApplication.timeSinceStartup + 1.0;
            launched = false;
            EditorApplication.update += Step;
        }
        if (change == PlayModeStateChange.ExitingPlayMode) EditorApplication.update -= Step;
    }

    static void Step()
    {
        if (!EditorApplication.isPlaying) { EditorApplication.update -= Step; return; }
        var sim = SimulationManager.Instance;
        if (sim == null || EditorApplication.timeSinceStartup < startAt) return;

        if (!launched)
        {
            launched = true;
            var drone = sim.drone;

            // Sonar only: remove the vision camera and detector for this demo.
            foreach (var c in drone.GetComponents<TargetDetector>()) Object.Destroy(c);
            foreach (var c in drone.GetComponents<DroneCamera>()) Object.Destroy(c);
            sim.detector = null;

            var nav = drone.GetComponent<SonarNavigator>();
            nav.mode = SonarNavigator.Mode.Sonar;
            nav.ApplyMode();
            var vis = drone.GetComponent<SonarVisualizer>();
            vis.view = SonarVisualizer.View.EchoesAndRays;

            // Obstacles the drone's planner has never been told about (shared with the web build).
            WebAutopilot.PlaceSurpriseObstacles(sim.map, drone.GetComponent<SonarNavigator>().BandLevels.x);

            sim.flyCamera.followOffset = new Vector3(0f, 8f, -6f);
            sim.flyCamera.follow = drone.transform;
            sim.SetSpeed(6f);
            sim.StartSim();
            nextShot = EditorApplication.timeSinceStartup + 0.5;
            Debug.Log("[SonarDemo] launched: Sonar mode only; wall at x=12, pole fence at x=30, wire at x=45");
        }

        var dir = System.Environment.GetEnvironmentVariable("SONAR_DEMO_FRAMES");
        if (!string.IsNullOrEmpty(dir) && EditorApplication.timeSinceStartup >= nextShot && shot < 120)
        {
            Directory.CreateDirectory(dir);
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"frame_{shot++:000}.png"));
            nextShot = EditorApplication.timeSinceStartup + 1.0;
            var d = sim.drone;
            Debug.Log($"[SonarDemo] t={shot}s mode={sim.Mode} drone={d.Status} pos={d.transform.position} moves={d.Steps} replans={d.Replans}");
        }
    }
}
