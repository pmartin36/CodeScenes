using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;

// C9: a code-authored NodeHandle.None clears the LIVE reference property (both a native PPtr field,
// Button.m_TargetGraphic, and a MonoBehaviour public GameObject field, DoorOpener.target), for both
// authoring spellings (raw serialized path and typed member selector), and the clear is a fixed
// point — the following sync makes zero source edits and the builder file still reads
// NodeHandle.None afterward. Also proves a converged source whose reference field is already
// assigned in the live scene rebuilds with zero plan ops, since a settled scene must not be
// rewritten on every build.
public class RoundTripObjectRefClearTests
{
    private const string ScenePath = "Assets/GateTests/__RoundTripObjectRefClearTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using UnityEngine.UI;
using SceneBuilder.Authoring;
public class RoundTripObjectRefClearScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    private static GameObject FindRoot(Scene scene, string name)
    {
        foreach (var go in scene.GetRootGameObjects())
        {
            if (go.name == name)
            {
                return go;
            }
        }
        return null;
    }

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_rtorc_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "RoundTripObjectRefClearScene.cs");
        _sidecarPath = Path.Combine(_dir, "RoundTripObjectRefClearScene.sbmap.json");
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

    // Builds a scene with the reference field ASSIGNED, converges the source, then replaces the
    // authored setter's argument with NodeHandle.None and rebuilds — asserting the live property is
    // null afterward. `setterArgumentSuffix` is the exact `, <arg>)` tail of the authored setter call
    // (unique enough not to collide with the object's own declaration line). `spellingMarker` pins
    // the authoring spelling (raw path or typed selector) that survives convergence and actually
    // reaches the clearing build — required, not defaulted, so a case cannot silently skip the check.
    private void AssertClearNullsLiveProperty(
        string body, string rootName, string setterArgumentSuffix, string spellingMarker, System.Func<GameObject, bool> isNull)
    {
        File.WriteAllText(_builderPath, Source(body));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var target = FindRoot(EditorSceneManager.GetActiveScene(), rootName);
        Assert.IsNotNull(target, rootName + " was not created by SceneBuilderBuild.Run");
        Assert.IsFalse(isNull(target), "Precondition failed: the reference field was not assigned by the initial build.");

        EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        var convergenceCheck = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.AreEqual(0, convergenceCheck.PatchEdits, "Source did not converge to a stable baseline before the clear was authored.");

        var converged = File.ReadAllText(_builderPath);
        StringAssert.Contains(setterArgumentSuffix, converged,
            "Converged source does not contain the expected setter argument to replace.\n" + converged);
        StringAssert.Contains(spellingMarker, converged,
            "Converged source does not contain the expected authoring spelling.\n" + converged);
        var cleared = converged.Replace(setterArgumentSuffix, ", NodeHandle.None)");
        Assert.AreNotEqual(converged, cleared, "Replacing the setter argument did not change the source text.");
        File.WriteAllText(_builderPath, cleared);

        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        var rebuilt = FindRoot(EditorSceneManager.GetActiveScene(), rootName);
        Assert.IsNotNull(rebuilt, rootName + " was not found in the scene after the clearing rebuild.");
        Assert.IsTrue(isNull(rebuilt),
            "The live reference property was not null after authoring NodeHandle.None and rebuilding.");
    }

    [Test]
    public void Clear_ButtonTargetGraphic_RawPath_NullsTheLiveProperty()
    {
        AssertClearNullsLiveProperty(
            "        var panel = scene.Add(\"Panel\");\n" +
            "        panel.Component<Image>();\n" +
            "        var btn = scene.Add(\"Btn\");\n" +
            "        btn.Component<Button>(c => c.Set(\"m_TargetGraphic\", panel));",
            "Btn",
            ", panel)",
            "\"m_TargetGraphic\"",
            go => go.GetComponent<Button>().targetGraphic == null);
    }

    [Test]
    public void Clear_ButtonTargetGraphic_TypedSelector_NullsTheLiveProperty()
    {
        AssertClearNullsLiveProperty(
            "        var panel = scene.Add(\"Panel\");\n" +
            "        panel.Component<Image>();\n" +
            "        var btn = scene.Add(\"Btn\");\n" +
            "        btn.Component<Button>(c => c.Set(x => x.targetGraphic, panel));",
            "Btn",
            ", panel)",
            "x => x.targetGraphic",
            go => go.GetComponent<Button>().targetGraphic == null);
    }

    [Test]
    public void Clear_MonoBehaviourGameObjectField_RawPath_NullsTheLiveProperty()
    {
        AssertClearNullsLiveProperty(
            "        var door = scene.Add(\"Door\");\n" +
            "        var opener = scene.Add(\"Opener\");\n" +
            "        opener.Component<DoorOpener>(c => c.Set(\"target\", door));",
            "Opener",
            ", door)",
            "\"target\"",
            go => go.GetComponent<DoorOpener>().target == null);
    }

    [Test]
    public void Clear_MonoBehaviourGameObjectField_TypedSelector_NullsTheLiveProperty()
    {
        AssertClearNullsLiveProperty(
            "        var door = scene.Add(\"Door\");\n" +
            "        var opener = scene.Add(\"Opener\");\n" +
            "        opener.Component<DoorOpener>(c => c.Set(x => x.target, door));",
            "Opener",
            ", door)",
            "x => x.target",
            go => go.GetComponent<DoorOpener>().target == null);
    }

    // Both reference kinds cleared in ONE scene: after the clearing rebuild, a following sync makes
    // zero source edits and the builder file is byte-identical, still authoring NodeHandle.None.
    [Test]
    public void ClearedSource_NextSync_IsAByteLevelFixedPoint()
    {
        File.WriteAllText(_builderPath, Source(
            "        var panel = scene.Add(\"Panel\");\n" +
            "        panel.Component<Image>();\n" +
            "        var btn = scene.Add(\"Btn\");\n" +
            "        btn.Component<Button>(c => c.Set(x => x.targetGraphic, panel));\n" +
            "        var door = scene.Add(\"Door\");\n" +
            "        var opener = scene.Add(\"Opener\");\n" +
            "        opener.Component<DoorOpener>(c => c.Set(x => x.target, door));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var btn = FindRoot(EditorSceneManager.GetActiveScene(), "Btn");
        var opener = FindRoot(EditorSceneManager.GetActiveScene(), "Opener");
        Assert.IsNotNull(btn, "Btn was not created by SceneBuilderBuild.Run");
        Assert.IsNotNull(opener, "Opener was not created by SceneBuilderBuild.Run");
        Assert.IsNotNull(btn.GetComponent<Button>().targetGraphic, "Precondition failed: targetGraphic was not assigned.");
        Assert.IsNotNull(opener.GetComponent<DoorOpener>().target, "Precondition failed: target was not assigned.");

        EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        var convergenceCheck = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.AreEqual(0, convergenceCheck.PatchEdits, "Source did not converge to a stable baseline before the clears were authored.");

        var converged = File.ReadAllText(_builderPath);
        var cleared = converged.Replace(", panel)", ", NodeHandle.None)").Replace(", door)", ", NodeHandle.None)");
        Assert.AreNotEqual(converged, cleared, "Replacing both setter arguments did not change the source text.");
        File.WriteAllText(_builderPath, cleared);

        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var beforeSyncBytes = File.ReadAllBytes(_builderPath);
        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        var afterSyncBytes = File.ReadAllBytes(_builderPath);

        Assert.AreEqual(0, result.PatchEdits,
            "The sync following a settled clear produced " + result.PatchEdits + " patch edit(s); it must be a no-op.");
        CollectionAssert.AreEqual(beforeSyncBytes, afterSyncBytes,
            "The builder source was not byte-identical after a sync that should have been a no-op.");

        var finalText = File.ReadAllText(_builderPath);
        Assert.AreEqual(2, System.Text.RegularExpressions.Regex.Matches(finalText, "NodeHandle\\.None").Count,
            "Both authored NodeHandle.None clears must still be present in the source.\n" + finalText);
    }

    // D1: rebuilding from a converged source whose reference field is already assigned in the live
    // scene must not rewrite it. A build path blind to an assigned scene reference treats the field
    // as unknown and emits a redundant SetReference forever.
    [Test]
    public void Rebuild_FromConvergedSourceWithAssignedReference_EmitsZeroPlanOps()
    {
        File.WriteAllText(_builderPath, Source(
            "        var door = scene.Add(\"Door\");\n" +
            "        var opener = scene.Add(\"Opener\");\n" +
            "        opener.Component<DoorOpener>(c => c.Set(x => x.target, door));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var rebuild = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        Assert.AreEqual(0, rebuild.PlanOpCount,
            "Rebuilding from a converged source whose reference field is already assigned in the live scene "
            + "emitted " + rebuild.PlanOpCount + " plan op(s); a settled scene must not be rewritten on every build.");
    }
}
