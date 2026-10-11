using UnityEditor;
using UnityEngine;

/// Drone 2/Person Dataset…: settings, start/stop and live status for PersonDatasetGenerator.
public class PersonDatasetWindow : EditorWindow
{
    [SerializeField] PersonDatasetSettings settings = new PersonDatasetSettings();
    SerializedObject so;
    Vector2 scroll;

    [MenuItem("Drone 2/Person Dataset…")]
    static void Open() => GetWindow<PersonDatasetWindow>("Person Dataset");

    [MenuItem("Drone 2/Open Person Dataset Folder")]
    static void OpenFolder()
    {
        var path = new PersonDatasetSettings().OutputPath;
        System.IO.Directory.CreateDirectory(path);
        EditorUtility.RevealInFinder(path);
    }

    void OnEnable() => so = new SerializedObject(this);

    void OnGUI()
    {
        so.Update();
        scroll = EditorGUILayout.BeginScrollView(scroll);
        EditorGUILayout.HelpBox(
            "Each environment: build a new random building, then photograph it from random spots in rooms and hallways " +
            "(camera height above the floor, tilted down). Writes YOLO labels for the visible part of every person.\n" +
            "Output goes outside Assets. Finished environments are skipped, so a stopped run can be resumed.",
            MessageType.Info);

        using (new EditorGUI.DisabledScope(PersonDatasetGenerator.IsRunning))
            EditorGUILayout.PropertyField(so.FindProperty("settings"), true);
        so.ApplyModifiedProperties();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Output", settings.OutputPath, EditorStyles.wordWrappedMiniLabel);
        EditorGUILayout.LabelField("Status", PersonDatasetGenerator.Status, EditorStyles.wordWrappedLabel);

        EditorGUILayout.Space();
        if (PersonDatasetGenerator.IsRunning)
        {
            if (GUILayout.Button("Stop", GUILayout.Height(28))) PersonDatasetGenerator.Stop();
        }
        else if (GUILayout.Button(settings.maxMinutes > 0
                     ? $"Generate for {settings.maxMinutes:0.#} min ({settings.shotsPerEnv} shots per building)"
                     : $"Generate {settings.environments} environments (~{settings.environments * settings.shotsPerEnv} images)",
                     GUILayout.Height(28)))
        {
            PersonDatasetGenerator.Start(settings);
        }
        EditorGUILayout.EndScrollView();
    }

    void Update()
    {
        if (PersonDatasetGenerator.IsRunning) Repaint();
    }
}
