using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;

// Spec 46's combined accept-when: a scene carrying BOTH defects in one sync -- a MonoBehaviour
// `[SerializeField] private` field set from the scene, and a component field referencing a handle
// that would otherwise be declared later -- syncs to source that compiles with zero errors, and a
// further sync of the converged builder is byte-stable (no repeated rewrite).
public class SyncBackCompilesRoundTripTests
{
    private const string ScenePath = "Assets/GateTests/__SyncBackCompilesRoundTripTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using UnityEngine;
using SceneBuilder.Authoring;
public class SyncBackCompilesRoundTripScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    private static GameObject Find(Scene scene, string name) =>
        scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_sbcrt_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "SyncBackCompilesRoundTripScene.cs");
        _sidecarPath = Path.Combine(_dir, "SyncBackCompilesRoundTripScene.sbmap.json");
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

    private SceneBuilderSync.SyncResult Sync()
    {
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);
        return EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
    }

    // Both spec-46 defects, one sync: (a) an authored typed selector on a component's own private
    // serialized field, live-edited via SerializedObject, must downgrade to the string-key form;
    // (b) a scene-side sibling reorder that pushes an already-referenced handle's declaration
    // below its referrer must hoist the declaration back above it. A converged re-sync must then
    // be byte-stable.
    [Test]
    public void SceneToCode_PrivateFieldSetAndReorderedForwardReferenceInOneSync_CompilesThenByteStable()
    {
        File.WriteAllText(_builderPath, Source(
            "        var door = scene.Add(\"Door\");\n" +
            "        var opener = scene.Add(\"Opener\");\n" +
            "        opener.Component<DoorOpener>(c => c.Set(\"target\", door));\n" +
            "        scene.Add(\"Crate\").Component<GateFixtures.InaccessibleFieldFixtureBehaviour>(" +
            "r => r.Set(r2 => r2.m_Secret, 5f));"));
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var opener = Find(EditorSceneManager.GetActiveScene(), "Opener");
        var door = Find(EditorSceneManager.GetActiveScene(), "Door");
        var crate = Find(EditorSceneManager.GetActiveScene(), "Crate");
        Assert.IsNotNull(opener, "Opener was not created by SceneBuilderBuild.Run");
        Assert.IsNotNull(door, "Door was not created by SceneBuilderBuild.Run");
        Assert.IsNotNull(crate, "Crate was not created by SceneBuilderBuild.Run");
        Assert.AreEqual(door, opener.GetComponent<DoorOpener>().target, "The authored reference did not resolve on Build.");

        // Push Door -- already referenced by Opener's component -- past Opener in sibling order,
        // the shape that otherwise relocates its declaration below the referencing statement.
        door.transform.SetSiblingIndex(2);

        var behaviour = crate.GetComponent<GateFixtures.InaccessibleFieldFixtureBehaviour>();
        var so = new SerializedObject(behaviour);
        so.FindProperty("m_Secret").floatValue = 8f;
        so.ApplyModifiedProperties();

        var result = Sync();
        Assert.IsTrue(result.Changed, "Sync reported no change despite a sibling reorder and a live private-field edit.");

        var written = File.ReadAllText(_builderPath);
        var doorIndex = written.IndexOf("Add(\"Door\")", System.StringComparison.Ordinal);
        var doorOpenerComponentIndex = written.IndexOf("Component<DoorOpener>", System.StringComparison.Ordinal);
        Assert.IsTrue(doorIndex >= 0, "Door's declaration is missing from emitted source:\n" + written);
        Assert.IsTrue(doorIndex < doorOpenerComponentIndex,
            "Door's declaration must precede the referencing component:\n" + written);
        StringAssert.Contains("c.Set(\"target\", door)", written,
            "The forward reference did not fold onto the opener's component.\n" + written);
        StringAssert.DoesNotContain("r2.m_Secret", written,
            "Emitted source must never carry a typed selector naming an inaccessible member.\n" + written);
        StringAssert.Contains("m_Secret", written,
            "The private field's value patch must still land, in string-key form.\n" + written);

        var beforeThird = File.ReadAllText(_builderPath);
        var third = Sync();
        Assert.AreEqual(0, third.PatchEdits, "A sync of the converged, combined-defect builder must be a fixed point.");
        Assert.IsFalse(third.Changed, "A sync of the converged, combined-defect builder must not rewrite the source.");
        Assert.AreEqual(beforeThird, File.ReadAllText(_builderPath),
            "Re-syncing the converged builder must be byte-stable.");
    }
}
