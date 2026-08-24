using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using SceneBuilder.Editor;

// Spec 46 defect-1, live: an authored typed selector naming a component's OWN private serialized
// field (`r => r.m_Secret`) never compiles. Sync must downgrade it to the string-key form the same
// value patch already writes, not leave it and let BuilderCompileCheck fail the whole file.
public class NonPublicFieldSyncCompileTests
{
    private const string ScenePath = "Assets/GateTests/__NonPublicFieldSyncCompileTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class NonPublicFieldSyncScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    private static GameObject FindRoot(UnityEngine.SceneManagement.Scene scene, string name) =>
        scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_npfsc_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "NonPublicFieldSyncScene.cs");
        _sidecarPath = Path.Combine(_dir, "NonPublicFieldSyncScene.sbmap.json");
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }
    }

    // DELIVERABLE: a scene edit to a `[SerializeField] private` field, authored with the typed
    // selector form, syncs to builder source that compiles with zero errors -- and carries no
    // remaining typed selector for that field anywhere in the file.
    [Test]
    public void SceneToCode_PrivateFieldEditedViaAuthoredTypedSelector_SyncedSourceCompiles()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Crate\").Component<GateFixtures.InaccessibleFieldFixtureBehaviour>(" +
            "r => r.Set(r2 => r2.m_Secret, 5f));"));
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var crate = FindRoot(EditorSceneManager.GetActiveScene(), "Crate");
        var behaviour = crate.GetComponent<GateFixtures.InaccessibleFieldFixtureBehaviour>();
        var so = new SerializedObject(behaviour);
        so.FindProperty("m_Secret").floatValue = 8f;
        so.ApplyModifiedProperties();

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite a live private-field edit.");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.DoesNotContain("r2.m_Secret", rewritten,
            "Emitted source must never carry a typed selector naming an inaccessible member.\n" + rewritten);
    }
}
