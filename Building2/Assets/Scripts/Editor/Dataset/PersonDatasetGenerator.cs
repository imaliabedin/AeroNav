using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Drone2.Building;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// Settings for one dataset run (see Tools/yolo/README.md).
[Serializable]
public class PersonDatasetSettings
{
    public string outputDir = "Datasets/people";   // relative to the project folder, or absolute
    [Tooltip("Upper bound on buildings. With a time limit set this is effectively unbounded.")]
    public int environments = 100000;
    public int startIndex = 0;
    public int baseSeed = 1000;
    [Tooltip("Stop after the building that is running when this many minutes have passed. 0 = no limit.")]
    public float maxMinutes = 45f;

    [Header("Shots per environment")]
    public int shotsPerEnv = 15;
    [Range(0f, 1f)] public float emptyFraction = 0.2f;
    [Tooltip("Random camera poses tried per building before giving up on filling its quota.")]
    public int maxAttemptsPerEnv = 1500;
    [Tooltip("Share of poses placed in a room that has a person in it (the spot inside it stays random). " +
             "The rest go anywhere. People are rare, so pure random poses almost never see one.")]
    [Range(0f, 1f)] public float personRoomBias = 0.5f;

    [Header("Camera")]
    public int width = 640, height = 640;
    [Tooltip("Vertical field of view (equal to horizontal for a square image).")]
    public float verticalFov = 70f;
    [Tooltip("Height above the storey floor.")]
    public float cameraHeight = 1.5f;
    [Tooltip("Uniform ± jitter on the height, per shot.")]
    public float heightJitter = 0.15f;
    [Tooltip("Downward tilt.")]
    public float pitch = 30f;
    [Tooltip("Uniform ± jitter on the tilt in degrees, per shot.")]
    public float pitchJitter = 5f;
    [Tooltip("Uniform ± roll in degrees, per shot.")]
    public float rollJitter = 3f;
    [Tooltip("Keep the camera this far from the room's walls.")]
    public float wallMargin = 0.3f;
    [Tooltip("Free radius around the camera (no furniture, people or walls).")]
    public float clearance = 0.25f;
    [Range(50, 100)] public int jpgQuality = 95;

    [Header("Building variety")]
    [Range(1, 6)] public int minFloors = 1;
    [Range(1, 6)] public int maxFloors = 6;
    [Range(0f, 1f)] public float fireChance = 0.5f;
    [Tooltip("People per building (dataset only; the game's own range is unchanged). Rooms only, never hallways.")]
    public int minPeople = 3;
    public int maxPeople = 10;

    [Header("Evening")]
    [Tooltip("Per building, how far daylight is turned down: 0 = the scene as saved, 1 = no sun or ambient light.")]
    public Vector2 eveningDarkness = new Vector2(0.4f, 0.7f);
    [Tooltip("Sun height above the horizon in degrees, per building. Low = orange sunset sky.")]
    public Vector2 sunsetElevation = new Vector2(1f, 8f);
    [Tooltip("Ceiling light brightness multiplier in scenes without fire (fire scenes already dim their own).")]
    public Vector2 ceilingLightDim = new Vector2(0.45f, 0.8f);
    [Tooltip("Share of ceiling lights that flicker. Each shot catches them at a random moment, sometimes mid-dropout.")]
    [Range(0f, 1f)] public float flickerShare = 0.3f;

    [Header("Drone headlight")]
    [Tooltip("Fire scenes keep the game's failing lights, so a spotlight on the camera lights what is near it.")]
    public bool headlightInFire = true;
    [Tooltip("Also use the headlight in scenes without fire.")]
    public bool headlightAlways = true;
    [Tooltip("Random intensity range, drawn per building.")]
    public Vector2 headlightIntensity = new Vector2(1.5f, 3f);
    public float headlightRange = 12f;
    public float headlightAngle = 80f;

    [Header("Label filters")]
    [Tooltip("Share of the whole body that must be visible (not occluded and inside the frame).")]
    [Range(0f, 1f)] public float minVisibleFraction = 0.15f;
    [Tooltip("Minimum box width and height in pixels.")]
    public int minBoxPixels = 8;

    [Header("Debug")]
    [Tooltip("Every Nth image also goes to _preview/ with its boxes drawn (green = label, grey = seen but not labelled). 0 = off.")]
    public int previewEvery = 5;

    public int EmptyQuota => Mathf.RoundToInt(shotsPerEnv * emptyFraction);
    public int PersonQuota => shotsPerEnv - EmptyQuota;

    /// Empty images allowed so far, so they stay at emptyFraction even when a building runs short of person images.
    public int EmptyAllowed(int personImages) => emptyFraction >= 1f ? EmptyQuota
        : Mathf.Min(EmptyQuota, Mathf.FloorToInt(personImages * emptyFraction / (1f - emptyFraction) + 0.5f));

    public string OutputPath => Path.IsPathRooted(outputDir)
        ? outputDir
        : Path.GetFullPath(Path.Combine(Application.dataPath, "..", outputDir));
}

/// Generates a YOLO (Ultralytics detect, usable by YOLO-World) person dataset from the burning-building scene.
/// For every environment it builds a new random building, then photographs it from random poses: a random free
/// spot in a room or hallway, about cameraHeight above that storey's floor, tilted about pitch degrees down with a
/// little roll, random heading. Fire scenes keep the game's failing lights; a camera-mounted headlight lights them.
/// Boxes come from an id mask, so they cover only the visible part of each person. Smoke and flames are ignored
/// for labelling (people in smoke are labelled). Runs incrementally in the Editor or synchronously from the
/// command line, and resumes by skipping environments that already have a .done marker.
public static class PersonDatasetGenerator
{
    public const string Version = "1.1";
    public const string ScenePath = "Assets/Scenes/BurningBuilding.unity";
    public const string ClassName = "person";

    static Job job;
    static int progressId;

    public static bool IsRunning => job != null;
    public static string Status { get; private set; } = "Idle";

    // ---------- Editor (incremental) ----------

    public static void Start(PersonDatasetSettings settings)
    {
        if (job != null) { Debug.LogWarning("[Dataset] Already running."); return; }
        if (EditorApplication.isPlaying) { Debug.LogWarning("[Dataset] Exit Play mode first."); return; }
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        if (UnityEngine.Object.FindAnyObjectByType<BuildingGenerator>() == null) OpenScene();

        try
        {
            job = new Job(settings);
            job.Begin();
        }
        catch (Exception e)
        {
            Debug.LogError("[Dataset] Could not start: " + e.Message);
            job?.End(false);
            job = null;
            return;
        }

        progressId = Progress.Start("Person dataset",
            (settings.maxMinutes > 0 ? $"{settings.maxMinutes:0} min" : $"{job.TargetImages} images") + $" → {settings.OutputPath}");
        Progress.RegisterCancelCallback(progressId, () => { Stop(); return true; });
        EditorApplication.update += Tick;
    }

    public static void Stop()
    {
        if (job != null) job.stopRequested = true;
    }

    static void Tick()
    {
        bool more;
        try
        {
            more = !job.stopRequested && job.Step();
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            more = false;
        }
        Status = job.StatusText;
        if (Progress.Exists(progressId))
            Progress.Report(progressId, job.Progress01, job.StatusText);
        if (more) return;

        EditorApplication.update -= Tick;
        bool cancelled = job.stopRequested;
        job.End(true);
        Status = job.StatusText;
        if (Progress.Exists(progressId))
            Progress.Finish(progressId, cancelled ? Progress.Status.Canceled : Progress.Status.Succeeded);
        job = null;
    }

    // ---------- Command line (synchronous) ----------

    /// Unity.exe -batchmode -projectPath "C:\Unity Projects\Drone 2" -executeMethod PersonDatasetGenerator.RunFromCommandLine
    ///     [-minutes 45] [-envs 100000] [-start 0] [-seed 1000] [-shots 15] [-out Datasets/people]
    /// Needs graphics (do not pass -nographics).
    public static void RunFromCommandLine()
    {
        var s = new PersonDatasetSettings();
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "-envs": s.environments = int.Parse(args[i + 1]); break;
                case "-start": s.startIndex = int.Parse(args[i + 1]); break;
                case "-seed": s.baseSeed = int.Parse(args[i + 1]); break;
                case "-shots": s.shotsPerEnv = int.Parse(args[i + 1]); break;
                case "-minutes": s.maxMinutes = float.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture); break;
                case "-out": s.outputDir = args[i + 1]; break;
            }
        }

        int exitCode = 0;
        try
        {
            OpenScene();
            var j = new Job(s);
            j.Begin();
            int lastLogged = -1;
            while (j.Step())
                if (j.DoneImages % 20 == 0 && j.DoneImages != lastLogged)
                {
                    lastLogged = j.DoneImages;
                    Debug.Log("[Dataset] " + j.StatusText);
                }
            j.End(false);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            exitCode = 1;
        }
        if (Application.isBatchMode) EditorApplication.Exit(exitCode);
    }

    static void OpenScene()
    {
        if (!File.Exists(ScenePath)) BuildingSceneSetup.CreateScene();
        EditorSceneManager.OpenScene(ScenePath);
    }

    // ---------- the job ----------

    class Job
    {
        public bool stopRequested;
        public int TargetImages => envs.Count * s.shotsPerEnv;
        public int DoneImages { get; private set; }
        public string StatusText { get; private set; } = "Starting";
        public bool TimeUp => s.maxMinutes > 0 && Elapsed >= s.maxMinutes * 60;
        double Elapsed => EditorApplication.timeSinceStartup - startTime;

        /// Time share when there is a time limit, image share otherwise.
        public float Progress01 => s.maxMinutes > 0
            ? Mathf.Clamp01((float)(Elapsed / (s.maxMinutes * 60)))
            : Mathf.Clamp01((float)DoneImages / Mathf.Max(1, TargetImages));

        readonly PersonDatasetSettings s;
        string root, scenePath;
        BuildingGenerator gen;
        List<int> envs;
        int envCursor;
        bool envReady, warmedUp, timeLimitHit;
        int built;
        double startTime;

        GameObject camGo;
        Camera cam;
        Light headlight;

        // Lighting as saved in the scene, so each building's evening starts from the same place.
        Light sun;
        float sunIntensity0;
        Color sunColor0, ambSky0, ambEquator0, ambGround0;
        Quaternion sunRotation0;
        float darkness, skyExposure0;
        Material sky0, skyCopy;
        readonly List<(Light light, float baseIntensity)> flickers = new List<(Light, float)>();
        UniversalAdditionalCameraData camData;
        RenderTexture rgbRT, maskRT;
        Texture2D rgbTex, maskTex;
        Shader idShader;
        Material blackMat;
        readonly List<Material> idMats = new List<Material>();

        // per environment
        int envSeed, attempts, personShots, emptyShots;
        bool fire, useHeadlight;
        float headlightIntensity, shotHeight, shotPitch, shotRoll;
        System.Random rng;
        readonly List<RegionSpace> spaces = new List<RegionSpace>();
        readonly List<float> cumulativeArea = new List<float>();
        readonly List<RegionSpace> personSpaces = new List<RegionSpace>();
        readonly List<TrappedPerson> persons = new List<TrappedPerson>();
        readonly Dictionary<Renderer, int> personIndex = new Dictionary<Renderer, int>();
        Renderer[] sceneRenderers;

        public Job(PersonDatasetSettings settings) { s = settings; }

        public void Begin()
        {
            if (s.minFloors > s.maxFloors) throw new Exception("minFloors is larger than maxFloors.");
            if (s.minPeople > s.maxPeople) throw new Exception("minPeople is larger than maxPeople.");
            if (s.shotsPerEnv <= 0) throw new Exception("shotsPerEnv must be positive.");
            gen = UnityEngine.Object.FindAnyObjectByType<BuildingGenerator>();
            if (gen == null) throw new Exception($"Open {ScenePath} (it needs a BuildingGenerator).");
            scenePath = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;

            root = s.OutputPath;
            foreach (var split in new[] { "train", "val", "test" })
                foreach (var kind in new[] { "images", "labels", "meta" })
                    Directory.CreateDirectory(Path.Combine(root, kind, split));

            // Resume: skip environments whose .done marker exists (written after their last image).
            envs = new List<int>();
            for (int e = s.startIndex; e < s.startIndex + s.environments; e++)
                if (!File.Exists(DonePath(e))) envs.Add(e);

            camGo = new GameObject("Dataset Capture Camera") { hideFlags = HideFlags.HideAndDontSave };
            cam = camGo.AddComponent<Camera>();
            cam.enabled = false;
            cam.fieldOfView = s.verticalFov;
            cam.aspect = (float)s.width / s.height;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 200f;
            camData = cam.GetUniversalAdditionalCameraData();

            // Drone headlight: sits at the lens, so its shadows would be hidden from the camera anyway.
            var lightGo = new GameObject("Dataset Headlight") { hideFlags = HideFlags.HideAndDontSave };
            lightGo.transform.SetParent(camGo.transform, false);
            headlight = lightGo.AddComponent<Light>();
            headlight.type = LightType.Spot;
            headlight.color = new Color(1f, 0.96f, 0.88f);
            headlight.range = s.headlightRange;
            headlight.spotAngle = s.headlightAngle;
            headlight.innerSpotAngle = s.headlightAngle * 0.5f;
            headlight.shadows = LightShadows.None;
            headlight.enabled = false;

            sun = RenderSettings.sun;
            if (sun != null)
            {
                sunIntensity0 = sun.intensity;
                sunColor0 = sun.color;
                sunRotation0 = sun.transform.rotation;
            }
            ambSky0 = RenderSettings.ambientSkyColor;
            ambEquator0 = RenderSettings.ambientEquatorColor;
            ambGround0 = RenderSettings.ambientGroundColor;
            // Work on a copy of a procedural sky so the material asset is never changed.
            sky0 = RenderSettings.skybox;
            if (sky0 != null && sky0.shader.name == "Skybox/Procedural")
            {
                skyCopy = new Material(sky0) { hideFlags = HideFlags.HideAndDontSave };
                skyExposure0 = sky0.GetFloat("_Exposure");
                RenderSettings.skybox = skyCopy;
            }

            rgbRT = new RenderTexture(s.width, s.height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 1 };
            maskRT = new RenderTexture(s.width, s.height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { antiAliasing = 1, filterMode = FilterMode.Point };
            rgbRT.Create();
            maskRT.Create();
            rgbTex = new Texture2D(s.width, s.height, TextureFormat.RGB24, false, false);
            maskTex = new Texture2D(s.width, s.height, TextureFormat.RGBA32, false, true);

            idShader = Shader.Find("Hidden/DatasetGen/Id");
            if (idShader == null) throw new Exception("Shader Hidden/DatasetGen/Id not found (Assets/Shaders/DatasetId.shader).");
            blackMat = IdMaterial(0);

            WriteDataYaml();
            startTime = EditorApplication.timeSinceStartup;
            StatusText = envs.Count == 0 ? "Nothing to do: all environments already exist" : "Starting";
            Debug.Log($"[Dataset] {envs.Count} environments to generate ({s.environments - envs.Count} already done) → {root}");
        }

        /// One candidate camera pose per call. Returns false when finished.
        public bool Step()
        {
            if (envCursor >= envs.Count) return false;
            int env = envs[envCursor];
            if (!envReady)
            {
                SetupEnvironment(env);
                envReady = true;
            }

            TryCandidate(env);

            if ((personShots >= s.PersonQuota && emptyShots >= s.EmptyQuota) || attempts >= s.maxAttemptsPerEnv)
            {
                FinishEnvironment(env);
                envCursor++;
                envReady = false;
                // Stop only between buildings, so every building on disk is complete and marked done.
                if (TimeUp)
                {
                    timeLimitHit = true;
                    envCursor = envs.Count;
                }
            }

            double elapsed = Elapsed;
            double perImage = DoneImages > 0 ? elapsed / DoneImages : 0;
            string left = s.maxMinutes > 0
                ? $"{Math.Max(0, s.maxMinutes - elapsed / 60):0} min left"
                : $"~{perImage * (TargetImages - DoneImages) / 60:0} min left";
            StatusText = $"env {env} ({built} built)  {DoneImages} images" +
                         (DoneImages > 0 ? $"  ({perImage:0.00} s/img, {left})" : "");
            return envCursor < envs.Count;
        }

        public void End(bool reopenScene)
        {
            RestoreSceneLighting();
            if (camGo) UnityEngine.Object.DestroyImmediate(camGo);
            foreach (var rt in new[] { rgbRT, maskRT }) if (rt) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
            foreach (var t in new[] { rgbTex, maskTex }) if (t) UnityEngine.Object.DestroyImmediate(t);
            foreach (var m in idMats) if (m) UnityEngine.Object.DestroyImmediate(m);

            if (root != null) WriteManifest();
            double minutes = Elapsed / 60;
            StatusText = $"{(stopRequested ? "Stopped" : timeLimitHit ? "Time limit reached" : "Done")}: " +
                         $"{DoneImages} images from {built} buildings in {minutes:0.0} min written to {root}";
            Debug.Log("[Dataset] " + StatusText);

            // Throw away the generated building: put the user's saved scene back.
            if (reopenScene && !string.IsNullOrEmpty(scenePath))
                EditorSceneManager.OpenScene(scenePath);
        }

        // ---------- environment ----------

        void SetupEnvironment(int env)
        {
            envSeed = s.baseSeed + env;
            rng = new System.Random(envSeed * 7919 + 17);
            attempts = personShots = emptyShots = 0;

            fire = rng.NextDouble() < s.fireChance;
            gen.seed = envSeed;
            gen.floors = rng.Next(s.minFloors, s.maxFloors + 1);
            gen.fireAndSmoke = fire;
            gen.minPeople = s.minPeople;   // the scene is reloaded from disk afterwards, so the game keeps its own range
            gen.maxPeople = s.maxPeople;
            gen.Generate();   // fast-forwards the smoke and flames in edit mode
            Physics.SyncTransforms();

            // Fire scenes keep the game's failing lights; the headlight is what lights the near field.
            useHeadlight = fire ? s.headlightInFire : s.headlightAlways;
            headlightIntensity = useHeadlight ? Mathf.Lerp(s.headlightIntensity.x, s.headlightIntensity.y, (float)rng.NextDouble()) : 0f;
            headlight.intensity = headlightIntensity;
            ApplyEvening();

            spaces.Clear();
            cumulativeArea.Clear();
            float total = 0f;
            foreach (var sp in gen.roomSpaces.Concat(gen.hallSpaces))
            {
                float w = sp.interior.width - 2 * s.wallMargin, d = sp.interior.height - 2 * s.wallMargin;
                if (w <= 0f || d <= 0f) continue;
                total += w * d;
                spaces.Add(sp);
                cumulativeArea.Add(total);
            }

            persons.Clear();
            persons.AddRange(gen.people);
            personSpaces.Clear();
            personSpaces.AddRange(spaces.Where(sp => persons.Any(p => p.floor == sp.region.floor && p.roomId == sp.region.id)));
            personIndex.Clear();
            for (int i = 0; i < persons.Count; i++)
                foreach (var r in persons[i].GetComponentsInChildren<Renderer>())
                    personIndex[r] = i + 1;
            if (persons.Count > 510) throw new Exception("More than 510 people in one building; the id mask can't encode them.");

            var all = new HashSet<Renderer>(UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude));
            all.UnionWith(gen.Root.GetComponentsInChildren<Renderer>());
            sceneRenderers = all.ToArray();

            // The first render after a rebuild (and the first batch-mode render) can come out unlit.
            if (!warmedUp || Application.isBatchMode)
            {
                camGo.transform.SetPositionAndRotation(gen.transform.position + new Vector3(-20, 15, -25), Quaternion.Euler(25, 40, 0));
                RenderRgb();
                warmedUp = true;
            }
        }

        /// Evening: a low, dim, warm sun and darker ambient light; ceiling lights dimmed (fire scenes are already
        /// dimmed by FireAndSmoke) and some of them flickering.
        void ApplyEvening()
        {
            darkness = Mathf.Lerp(s.eveningDarkness.x, s.eveningDarkness.y, (float)rng.NextDouble());
            if (sun != null)
            {
                sun.intensity = sunIntensity0 * (1f - darkness);
                sun.color = Color.Lerp(sunColor0, new Color(1f, 0.5f, 0.25f), 0.6f + 0.4f * darkness);
                // Sunset: the sun just above the horizon, from any direction, so windows on every side get the glow.
                float elevation = Mathf.Lerp(s.sunsetElevation.x, s.sunsetElevation.y, (float)rng.NextDouble());
                sun.transform.rotation = Quaternion.Euler(elevation, (float)rng.NextDouble() * 360f, 0f);
            }
            if (skyCopy != null)
            {
                // Thicker atmosphere = redder, more orange sky near the low sun.
                skyCopy.SetFloat("_AtmosphereThickness", Mathf.Lerp(1.4f, 2.4f, (float)rng.NextDouble()));
                skyCopy.SetFloat("_Exposure", skyExposure0 * Mathf.Lerp(1f, 0.6f, darkness));
                skyCopy.SetColor("_SkyTint", new Color(0.55f, 0.45f, 0.45f));
                skyCopy.SetColor("_GroundColor", new Color(0.18f, 0.14f, 0.13f));
            }
            var dusk = new Color(0.75f, 0.8f, 1f);   // ambient goes slightly blue as it fades
            float amb = 1f - 0.8f * darkness;
            RenderSettings.ambientSkyColor = ambSky0 * dusk * amb;
            RenderSettings.ambientEquatorColor = ambEquator0 * dusk * amb;
            RenderSettings.ambientGroundColor = ambGround0 * amb;

            flickers.Clear();
            foreach (var light in gen.Root.GetComponentsInChildren<Light>(true))
            {
                if (light.name != "CeilingLight" || !light.enabled) continue;
                if (!fire) light.intensity *= Mathf.Lerp(s.ceilingLightDim.x, s.ceilingLightDim.y, (float)rng.NextDouble());
                // Fire scenes mark their stuttering lights with LightFlicker, which only animates in play mode.
                if (light.GetComponent<LightFlicker>() != null || rng.NextDouble() < s.flickerShare)
                    flickers.Add((light, light.intensity));
            }
        }

        /// Freezes each flickering light at a random moment: mostly near full, sometimes caught in a dropout.
        void StutterLights()
        {
            foreach (var (light, baseIntensity) in flickers)
            {
                if (light == null) continue;
                float k = rng.NextDouble() < 0.3 ? Mathf.Lerp(0.05f, 0.35f, (float)rng.NextDouble())
                                                 : Mathf.Lerp(0.8f, 1.05f, (float)rng.NextDouble());
                light.intensity = baseIntensity * k;
            }
        }

        /// Light falls off with the square of distance, so a wall right in front of the lens would blow out to white.
        /// Turn the headlight down when the nearest surface ahead is close (like a camera's auto-exposure would).
        void AimHeadlight()
        {
            if (!useHeadlight) return;
            const float fullAt = 3f;   // metres; at this distance or farther the headlight is at full strength
            var t = camGo.transform;
            float nearest = fullAt;
            foreach (var dir in new[] { t.forward, t.forward + 0.35f * t.right, t.forward - 0.35f * t.right,
                                        t.forward + 0.35f * t.up, t.forward - 0.35f * t.up })
                if (Physics.Raycast(t.position, dir.normalized, out var hit, fullAt, ~0, QueryTriggerInteraction.Ignore))
                    nearest = Mathf.Min(nearest, hit.distance);
            float k = nearest / fullAt;
            headlight.intensity = headlightIntensity * Mathf.Clamp(k * k, 0.08f, 1f);
        }

        /// Share of pixels in the last RGB render that are near white.
        float BlownOutShare()
        {
            var px = rgbTex.GetPixelData<byte>(0);   // RGB24
            int blown = 0, n = px.Length / 3;
            for (int i = 0; i < px.Length; i += 3)
                if (px[i] > 235 && px[i + 1] > 235 && px[i + 2] > 235) blown++;
            return (float)blown / n;
        }

        void RestoreSceneLighting()
        {
            if (sun != null)
            {
                sun.intensity = sunIntensity0;
                sun.color = sunColor0;
                sun.transform.rotation = sunRotation0;
            }
            RenderSettings.ambientSkyColor = ambSky0;
            RenderSettings.ambientEquatorColor = ambEquator0;
            RenderSettings.ambientGroundColor = ambGround0;
            if (skyCopy != null)
            {
                RenderSettings.skybox = sky0;
                UnityEngine.Object.DestroyImmediate(skyCopy);
                skyCopy = null;
            }
        }

        void FinishEnvironment(int env)
        {
            var info = new EnvMeta
            {
                env = env, seed = envSeed, floors = gen.floors, fire = fire, headlight = useHeadlight, people = persons.Count,
                attempts = attempts, personImages = personShots, emptyImages = emptyShots,
            };
            if (personShots < s.PersonQuota)
                Debug.LogWarning($"[Dataset] env {env}: only {personShots} / {s.PersonQuota} person images after {attempts} poses.");
            WriteAtomic(DonePath(env), Encoding.UTF8.GetBytes(JsonUtility.ToJson(info, true)));
            built++;
        }

        // ---------- one candidate pose ----------

        void TryCandidate(int env)
        {
            attempts++;
            if (!PickPose(out var space)) return;

            bool wantPerson = personShots < s.PersonQuota, wantEmpty = emptyShots < s.EmptyAllowed(personShots);
            var people = new List<PersonMeta>();
            var labels = new StringBuilder();
            int labeled = 0;

            if (AnyPersonInView())
            {
                // Mask (1): everything except smoke, flames and glass, so walls and furniture hide people.
                // Mask (2): people only, to measure how much of each person is hidden.
                var visible = new PixelStats[persons.Count + 1];
                var unoccluded = new PixelStats[persons.Count + 1];
                RenderMask(peopleOnly: false, visible);
                RenderMask(peopleOnly: true, unoccluded);
                labeled = BuildLabels(visible, unoccluded, people, labels);
            }

            if (labeled > 0 ? !wantPerson : !wantEmpty) return;

            int index = personShots + emptyShots;
            if (labeled > 0) personShots++;
            else emptyShots++;

            string split = SplitOf(env), stem = Stem(env, index);
            StutterLights();
            AimHeadlight();
            RenderRgb();
            // Auto-exposure stand-in: a surface right in front of the headlight blows out to white, so turn it down and retake.
            for (int retake = 0; retake < 4 && useHeadlight && BlownOutShare() > 0.04f; retake++)
            {
                headlight.intensity *= 0.5f;
                RenderRgb();
            }
            WriteAtomic(Path.Combine(root, "images", split, stem + ".jpg"), rgbTex.EncodeToJPG(s.jpgQuality));

            float fy = s.height * 0.5f / Mathf.Tan(s.verticalFov * 0.5f * Mathf.Deg2Rad);
            var region = space.region;
            var meta = new ImageMeta
            {
                env = env, seed = envSeed, view = index, split = split, image = stem + ".jpg",
                width = s.width, height = s.height, verticalFov = s.verticalFov,
                fx = fy, fy = fy, cx = s.width * 0.5f, cy = s.height * 0.5f,
                cameraPosition = camGo.transform.position, cameraRotation = camGo.transform.rotation,
                cameraYaw = camGo.transform.eulerAngles.y, cameraPitch = shotPitch, cameraRoll = shotRoll, cameraHeight = shotHeight,
                headlight = useHeadlight, headlightIntensity = useHeadlight ? headlight.intensity : 0f, eveningDarkness = darkness,
                floor = region.floor, floorY = space.parent.TransformPoint(new Vector3(0, space.floorY, 0)).y,
                spaceId = region.id, spaceKind = region.IsRoom ? "room" : "hallway", roomRole = region.IsRoom ? region.role.ToString() : "",
                fire = fire, floors = gen.floors, people = people,
            };
            WriteAtomic(Path.Combine(root, "meta", split, stem + ".json"), Encoding.UTF8.GetBytes(JsonUtility.ToJson(meta, true)));
            WriteAtomic(Path.Combine(root, "labels", split, stem + ".txt"), Encoding.UTF8.GetBytes(labels.ToString()));
            if (s.previewEvery > 0 && DoneImages % s.previewEvery == 0) WritePreview(stem, people);
            DoneImages++;
        }

        void WritePreview(string stem, List<PersonMeta> people)
        {
            var px = rgbTex.GetPixels32();
            int w = s.width, h = s.height;
            void Rect(float[] b, Color32 c)
            {
                int x1 = Mathf.Clamp((int)b[0], 0, w - 1), x2 = Mathf.Clamp((int)b[2] - 1, 0, w - 1);
                int y1 = Mathf.Clamp(h - (int)b[3], 0, h - 1), y2 = Mathf.Clamp(h - 1 - (int)b[1], 0, h - 1); // flip to texture rows
                for (int t = 0; t < 2; t++)
                {
                    for (int x = x1; x <= x2; x++) { px[Mathf.Min(y1 + t, h - 1) * w + x] = c; px[Mathf.Max(y2 - t, 0) * w + x] = c; }
                    for (int y = y1; y <= y2; y++) { px[y * w + Mathf.Min(x1 + t, w - 1)] = c; px[y * w + Mathf.Max(x2 - t, 0)] = c; }
                }
            }
            foreach (var p in people)
                if (p.bbox.Length == 4) Rect(p.bbox, p.labeled ? new Color32(0, 255, 120, 255) : new Color32(160, 160, 160, 255));
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.SetPixels32(px);
            tex.Apply(false);
            Directory.CreateDirectory(Path.Combine(root, "_preview"));
            File.WriteAllBytes(Path.Combine(root, "_preview", stem + ".jpg"), tex.EncodeToJPG(90));
            UnityEngine.Object.DestroyImmediate(tex);
        }

        /// A random free spot in a room or hallway, about cameraHeight above its floor, tilted about pitch degrees down
        /// with a little roll and a random heading. The space is one with a person in it (personRoomBias of the time)
        /// or any space weighted by area.
        bool PickPose(out RegionSpace space)
        {
            space = null;
            if (spaces.Count == 0) return false;
            bool personRoom = personSpaces.Count > 0 && rng.NextDouble() < s.personRoomBias;
            for (int tries = 0; tries < 30; tries++)
            {
                RegionSpace sp;
                if (personRoom) sp = personSpaces[rng.Next(personSpaces.Count)];
                else
                {
                    float pick = (float)rng.NextDouble() * cumulativeArea[cumulativeArea.Count - 1];
                    sp = spaces[Mathf.Max(0, cumulativeArea.FindIndex(a => a >= pick))];
                }
                var r = sp.interior;
                float x = Mathf.Lerp(r.xMin + s.wallMargin, r.xMax - s.wallMargin, (float)rng.NextDouble());
                float z = Mathf.Lerp(r.yMin + s.wallMargin, r.yMax - s.wallMargin, (float)rng.NextDouble());
                float height = s.cameraHeight + Jitter(s.heightJitter);
                var pos = sp.parent.TransformPoint(new Vector3(x, sp.floorY + height, z));
                if (Physics.CheckSphere(pos, s.clearance, ~0, QueryTriggerInteraction.Ignore)) continue;

                float yaw = (float)rng.NextDouble() * 360f;
                shotHeight = height;
                shotPitch = s.pitch + Jitter(s.pitchJitter);
                shotRoll = Jitter(s.rollJitter);
                camGo.transform.SetPositionAndRotation(pos, Quaternion.Euler(shotPitch, yaw, shotRoll));
                space = sp;
                return true;
            }
            return false;
        }

        /// Uniform in [-amount, amount].
        float Jitter(float amount) => amount <= 0f ? 0f : ((float)rng.NextDouble() * 2f - 1f) * amount;

        bool AnyPersonInView()
        {
            var planes = GeometryUtility.CalculateFrustumPlanes(cam);
            foreach (var p in persons)
                if (GeometryUtility.TestPlanesAABB(planes, p.Bounds)) return true;
            return false;
        }

        int BuildLabels(PixelStats[] visible, PixelStats[] unoccluded, List<PersonMeta> people, StringBuilder labels)
        {
            int labeled = 0;
            for (int i = 0; i < persons.Count; i++)
            {
                var p = persons[i];
                var vis = visible[i + 1];
                var full = unoccluded[i + 1];
                float inFrame = InFrameFraction(p, out bool projected);
                if (!projected || (full.count == 0 && inFrame <= 0f)) continue; // not in this photo at all

                // Share of the whole body that shows: unoccluded share of the in-frame part times the in-frame share.
                float unoccludedShare = full.count > 0 ? (float)vis.count / full.count : 0f;
                var pm = new PersonMeta
                {
                    id = p.id, pose = p.pose.ToString(), floor = p.floor, roomId = p.roomId, roomRole = p.roomRole.ToString(),
                    worldCenter = p.Bounds.center,
                    visiblePixels = vis.count, unoccludedPixels = full.count,
                    unoccludedFraction = unoccludedShare, inFrameFraction = inFrame,
                    visibleFraction = unoccludedShare * inFrame,
                };
                int bw = 0, bh = 0;
                if (vis.count > 0)
                {
                    pm.bbox = new[] { (float)vis.minX, vis.minY, vis.maxX + 1, vis.maxY + 1 };
                    bw = vis.maxX + 1 - vis.minX;
                    bh = vis.maxY + 1 - vis.minY;
                }

                if (vis.count == 0) pm.reason = "hidden";
                else if (pm.visibleFraction < s.minVisibleFraction) pm.reason = inFrame < 0.5f && unoccludedShare >= s.minVisibleFraction ? "cut off by frame" : "occluded";
                else if (bw < s.minBoxPixels || bh < s.minBoxPixels) pm.reason = "too small";
                else
                {
                    pm.labeled = true;
                    labeled++;
                    float x1 = vis.minX, y1 = vis.minY, x2 = vis.maxX + 1, y2 = vis.maxY + 1;
                    labels.Append("0 ")
                          .Append(F((x1 + x2) * 0.5f / s.width)).Append(' ')
                          .Append(F((y1 + y2) * 0.5f / s.height)).Append(' ')
                          .Append(F((x2 - x1) / s.width)).Append(' ')
                          .Append(F((y2 - y1) / s.height)).Append('\n');
                }
                people.Add(pm);
            }
            return labeled;
        }

        // ---------- rendering ----------

        void RenderRgb()
        {
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.allowHDR = true;
            cam.allowMSAA = true;
            camData.renderPostProcessing = true;
            camData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            camData.antialiasingQuality = AntialiasingQuality.High;
            headlight.enabled = useHeadlight;
            try { Render(rgbRT); }
            finally { headlight.enabled = false; }   // never part of the id masks
            ReadInto(rgbRT, rgbTex);
        }

        struct PixelStats { public int count, minX, minY, maxX, maxY; }

        bool IsIgnoredInMask(Renderer r) =>
            r is ParticleSystemRenderer || (gen.glassMaterial != null && r.sharedMaterial == gen.glassMaterial);

        void RenderMask(bool peopleOnly, PixelStats[] stats)
        {
            var renderers = sceneRenderers;
            var savedMats = new Material[renderers.Length][];
            var savedEnabled = new bool[renderers.Length];
            try
            {
                for (int i = 0; i < renderers.Length; i++)
                {
                    var r = renderers[i];
                    if (r == null) continue;
                    savedMats[i] = r.sharedMaterials;
                    savedEnabled[i] = r.enabled;
                    bool isPerson = personIndex.TryGetValue(r, out int id);
                    if ((peopleOnly && !isPerson) || IsIgnoredInMask(r)) { r.enabled = false; continue; }
                    var mat = isPerson ? IdMaterial(id) : blackMat;
                    var mats = new Material[Mathf.Max(1, savedMats[i].Length)];
                    for (int m = 0; m < mats.Length; m++) mats[m] = mat;
                    r.sharedMaterials = mats;
                }

                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.allowHDR = false;
                // MSAA would blend id colors along edges and decode as other people's ids.
                cam.allowMSAA = false;
                camData.renderPostProcessing = false;
                camData.antialiasing = AntialiasingMode.None;
                Render(maskRT);
                ReadInto(maskRT, maskTex);
            }
            finally
            {
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (renderers[i] == null || savedMats[i] == null) continue;
                    renderers[i].sharedMaterials = savedMats[i];
                    renderers[i].enabled = savedEnabled[i];
                }
            }

            for (int i = 0; i < stats.Length; i++) stats[i] = new PixelStats { minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1 };
            var px = maskTex.GetPixelData<Color32>(0);
            int w = s.width, h = s.height;
            for (int row = 0; row < h; row++)
            {
                int y = h - 1 - row; // texture rows start at the bottom, image rows at the top
                int o = row * w;
                for (int x = 0; x < w; x++)
                {
                    var c = px[o + x];
                    if (c.r < 16 && c.g < 16 && c.b < 16) continue;
                    if (!IsPureLevel(c.r) || !IsPureLevel(c.g) || !IsPureLevel(c.b)) continue; // blended edge pixel
                    int id = Level(c.r) + 8 * Level(c.g) + 64 * Level(c.b);
                    if (id <= 0 || id >= stats.Length) { unknownIdPixels++; continue; }
                    ref var st = ref stats[id];
                    st.count++;
                    if (x < st.minX) st.minX = x;
                    if (x > st.maxX) st.maxX = x;
                    if (y < st.minY) st.minY = y;
                    if (y > st.maxY) st.maxY = y;
                }
            }
            if (unknownIdPixels > 0 && !warnedUnknown)
            {
                warnedUnknown = true;
                Debug.LogWarning($"[Dataset] {unknownIdPixels} mask pixels decoded to unknown ids (color round-trip issue?)");
            }
        }

        int unknownIdPixels;
        bool warnedUnknown;

        static int Level(byte v) => Mathf.RoundToInt(v / 255f * 7f);

        /// True if the channel sits on one of the 8 exact id levels (anything else is a blended edge pixel).
        static bool IsPureLevel(byte v)
        {
            float x = v / 255f * 7f;
            return Mathf.Abs(x - Mathf.Round(x)) < 0.15f;
        }

        Material IdMaterial(int id)
        {
            while (idMats.Count <= id)
            {
                int k = idMats.Count;
                var m = new Material(idShader) { hideFlags = HideFlags.HideAndDontSave };
                m.SetVector("_IdColor", new Vector4((k % 8) / 7f, (k / 8 % 8) / 7f, (k / 64 % 8) / 7f, 1f));
                idMats.Add(m);
            }
            return idMats[id];
        }

        /// Share of the person's projected mesh (vertex bounding box) that lands inside the photo.
        float InFrameFraction(TrappedPerson person, out bool projected)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            projected = false;
            foreach (var mf in person.GetComponentsInChildren<MeshFilter>())
            {
                var mesh = mf.sharedMesh;
                if (mesh == null) continue;
                Vector3[] verts;
                try { verts = mesh.isReadable ? mesh.vertices : null; } catch { verts = null; }
                if (verts == null) verts = Corners(mesh.bounds);
                var m = mf.transform.localToWorldMatrix;
                foreach (var v in verts)
                {
                    var vp = cam.WorldToViewportPoint(m.MultiplyPoint3x4(v));
                    if (vp.z <= 0f) continue;
                    projected = true;
                    float x = vp.x * s.width, y = (1f - vp.y) * s.height;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            if (!projected) return 0f;
            float area = Mathf.Max(1e-3f, (maxX - minX) * (maxY - minY));
            float cw = Mathf.Max(0f, Mathf.Min(maxX, s.width) - Mathf.Max(minX, 0f));
            float ch = Mathf.Max(0f, Mathf.Min(maxY, s.height) - Mathf.Max(minY, 0f));
            return Mathf.Clamp01(cw * ch / area);
        }

        static Vector3[] Corners(Bounds b)
        {
            var c = new Vector3[8];
            for (int i = 0; i < 8; i++)
                c[i] = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
            return c;
        }

        void Render(RenderTexture target)
        {
            var request = new RenderPipeline.StandardRequest { destination = target };
            if (RenderPipeline.SupportsRenderRequest(cam, request))
                RenderPipeline.SubmitRenderRequest(cam, request);
            else
            {
                cam.targetTexture = target;
                cam.Render();
                cam.targetTexture = null;
            }
        }

        static void ReadInto(RenderTexture rt, Texture2D tex)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
            tex.Apply(false);
            RenderTexture.active = prev;
        }

        // ---------- files ----------

        string DonePath(int env) => Path.Combine(root ?? s.OutputPath, "meta", SplitOf(env), $"env{env:00000}.done");

        void WriteDataYaml()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Drone 2 person detector (generated by PersonDatasetGenerator " + Version + ")");
            sb.AppendLine("path: " + root.Replace('\\', '/'));
            sb.AppendLine("train: images/train");
            sb.AppendLine("val: images/val");
            sb.AppendLine("test: images/test");
            sb.AppendLine("names:");
            sb.AppendLine("  0: " + ClassName);
            File.WriteAllText(Path.Combine(root, "data.yaml"), sb.ToString());
        }

        void WriteManifest()
        {
            var m = new Manifest
            {
                generatorVersion = Version,
                updated = DateTime.Now.ToString("s"),
                settings = s,
                classes = new[] { ClassName },
            };
            foreach (var split in new[] { "train", "val", "test" })
            {
                var c = new SplitCounts { split = split };
                var dir = Path.Combine(root, "labels", split);
                if (Directory.Exists(dir))
                    foreach (var f in Directory.GetFiles(dir, "*.txt"))
                    {
                        c.images++;
                        int lines = File.ReadLines(f).Count(l => l.Trim().Length > 0);
                        c.objects += lines;
                        if (lines == 0) c.backgroundImages++;
                    }
                var metaDir = Path.Combine(root, "meta", split);
                if (Directory.Exists(metaDir)) c.environments = Directory.GetFiles(metaDir, "*.done").Length;
                m.splits.Add(c);
            }
            File.WriteAllText(Path.Combine(root, "dataset.json"), JsonUtility.ToJson(m, true));
        }

        static void WriteAtomic(string path, byte[] data)
        {
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        static string F(float v) => Mathf.Clamp01(v).ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture);
    }

    // ---------- shared helpers ----------

    public static string Stem(int env, int view) => $"env{env:00000}_v{view:00}";

    /// Deterministic 80 / 15 / 5 split by environment, so all views of a building share a split.
    public static string SplitOf(int env)
    {
        uint h = (uint)env * 2654435761u;
        h ^= h >> 16;
        int bucket = (int)(h % 100);
        return bucket < 80 ? "train" : bucket < 95 ? "val" : "test";
    }

    // ---------- JSON shapes ----------

    [Serializable]
    class PersonMeta
    {
        public int id;
        public string pose, roomRole;
        public int floor, roomId;
        public Vector3 worldCenter;
        public bool labeled;
        public string reason = "";
        public float[] bbox = new float[0];   // x1, y1, x2, y2 in pixels (visible part), empty if not visible
        public int visiblePixels, unoccludedPixels;
        public float visibleFraction, unoccludedFraction, inFrameFraction;
    }

    [Serializable]
    class ImageMeta
    {
        public int env, seed, view;
        public string split, image;
        public int width, height;
        public float verticalFov, fx, fy, cx, cy;
        public Vector3 cameraPosition;
        public Quaternion cameraRotation;
        public float cameraYaw, cameraPitch, cameraRoll, cameraHeight;
        public int floor, spaceId, floors;
        public float floorY;
        public string spaceKind, roomRole;
        public bool fire, headlight;
        public float headlightIntensity, eveningDarkness;
        public List<PersonMeta> people = new List<PersonMeta>();
    }

    [Serializable]
    class EnvMeta
    {
        public int env, seed, floors, people, attempts, personImages, emptyImages;
        public bool fire, headlight;
    }

    [Serializable]
    class SplitCounts
    {
        public string split;
        public int environments, images, objects, backgroundImages;
    }

    [Serializable]
    class Manifest
    {
        public string generatorVersion, updated;
        public string[] classes;
        public PersonDatasetSettings settings;
        public List<SplitCounts> splits = new List<SplitCounts>();
    }
}
