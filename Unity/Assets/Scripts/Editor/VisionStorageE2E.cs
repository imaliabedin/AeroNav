using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// End-to-end check of frame storage: a real DroneCamera frame sent with FrameMeta through
/// TargetDetector's client must come back with an imageId. Needs the vision server running.
///   Unity -batchmode -projectPath Unity -executeMethod VisionStorageE2E.RunBatch
public static class VisionStorageE2E
{
    public static void RunBatch()
    {
        try { Run(); }
        catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
    }

    static void Run()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/ParkingLot.unity", OpenSceneMode.Single);
        // FindObjectsByType has no fixed order; sort so every run photographs the same phone.
        var phone = UnityEngine.Object.FindObjectsByType<Prop>(FindObjectsSortMode.None)
            .Where(p => p.entryName == "Phone")
            .Select(p => p.transform.position)
            .OrderBy(p => p.x).ThenBy(p => p.z)
            .First();

        var rig = new GameObject("Vision Storage Rig") { layer = 2, hideFlags = HideFlags.DontSave };
        var cam = rig.AddComponent<DroneCamera>();
        cam.EnsureCamera();
        rig.transform.position = new Vector3(phone.x, cam.groundY + 2.44f, phone.z) - cam.mountOffset;

        var meta = new FrameMeta
        {
            sessionId = $"e2e-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
            position = rig.transform.position,
            altitude = cam.Altitude,
            capturedAtUtc = DateTime.UtcNow,
        };
        byte[] jpg = cam.CaptureJpg();
        UnityEngine.Object.DestroyImmediate(rig);

        // Same frame twice from the same spot: the second must be reported as a duplicate of the first.
        var first = TargetDetector.BuildRequest("http://127.0.0.1:8765", TargetCategory.Phone, 0.3f, jpg, 10f, meta);
        first.SendWebRequest();
        UnityEngine.Networking.UnityWebRequest second = null;

        EditorApplication.update += Poll;
        void Poll()
        {
            if (!first.isDone) return;
            if (second == null)
            {
                second = TargetDetector.BuildRequest("http://127.0.0.1:8765", TargetCategory.Phone, 0.3f, jpg, 10f, meta);
                second.SendWebRequest();
                return;
            }
            if (!second.isDone) return;
            EditorApplication.update -= Poll;

            var a = TargetDetector.ParseResponse(first, TargetCategory.Phone);
            var b = TargetDetector.ParseResponse(second, TargetCategory.Phone);
            foreach (var (name, r) in new[] { ("first", a), ("second", b) })
                Debug.Log($"[VisionStorageE2E] {name}: session={r.sessionId} imageId={r.imageId} duplicate={r.duplicate} " +
                          $"capturedAt={r.capturedAt} receivedAt={r.receivedAt} detected={r.detected} conf={r.confidence:0.000} " +
                          $"error='{r.error}' storageError='{r.storageError}'");
            bool ok = string.IsNullOrEmpty(a.error) && !string.IsNullOrEmpty(a.imageId) && !a.duplicate
                      && b.duplicate && b.imageId == a.imageId && a.sessionId == meta.sessionId
                      && string.IsNullOrEmpty(a.storageError) && string.IsNullOrEmpty(b.storageError);
            Debug.Log($"[VisionStorageE2E] {(ok ? "PASS" : "FAIL")}");
            first.Dispose();
            second.Dispose();
            EditorApplication.Exit(ok ? 0 : 1);
        }
    }
}
