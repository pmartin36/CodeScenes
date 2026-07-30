using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;

// m-ui-recttransform b3-t5: the mandatory Unity-boundary coverage for the SourcePatchApplier
// guard that stops a RemoveStatement anchored on a CLOSURE-AUTHORED node/component from
// rewriting anything more than that call: a real Inspector delete against closure-authored
// source rewrites only that call and never its enclosing statement. Harness follows
// RoundTripStructuralTests.cs exactly: a temp builder .cs + temp sidecar in a system temp dir
// (never the real Assets/SceneBuilder/DemoScene.cs), an EmptyScene per test, and [TearDown]
// cleanup.
public class ClosureAuthoredDeleteSyncTests
{
    private const string ScenePath = "Assets/GateTests/__ClosureAuthoredDeleteTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    // Wrap a Build-body fragment in a minimal ISceneDefinition the Core Roslyn parser understands.
    private static string Source(string body) => $@"
using UnityEngine;
using UnityEngine.UI;
using SceneBuilder.Authoring;
public class ClosureAuthoredDeleteScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    private static GameObject Find(Scene scene, string path)
    {
        var parts = path.Split('/');
        var root = scene.GetRootGameObjects().FirstOrDefault(go => go.name == parts[0]);
        var t = root == null ? null : root.transform;
        for (int i = 1; t != null && i < parts.Length; i++)
        {
            t = t.Find(parts[i]);
        }

        return t == null ? null : t.gameObject;
    }

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_cads_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "ClosureAuthoredDeleteScene.cs");
        _sidecarPath = Path.Combine(_dir, "ClosureAuthoredDeleteScene.sbmap.json");
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

    // Deleting a closure-authored UI CHILD in the Inspector must rewrite only that
    // child's own creation call out of the parent's configure closure — never destroy the parent
    // Panel's own statement.
    [Test]
    public void SceneToCode_DeletedClosureAuthoredUiChild_KeepsTheParentPanel()
    {
        File.WriteAllText(_builderPath, Source(
            "        var canvas = scene.Add(\"Canvas\").Component<Canvas>(c => c.Set(\"m_RenderMode\", 0));\n" +
            "        canvas.Add(\"Panel\", p => p.Add(\"Btn\").RectTransform(anchoredPos: (10f, 20f), sizeDelta: (64f, 24f)));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var live = EditorSceneManager.GetActiveScene();
        var btn = Find(live, "Canvas/Panel/Btn");
        Assert.IsNotNull(btn, "Canvas/Panel/Btn was not created by SceneBuilderBuild.Run");

        Object.DestroyImmediate(btn);

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite a destroyed closure-authored UI child");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("Add(\"Panel\"", rewritten,
            "Builder source lost the parent Panel statement it must have kept.\n" + rewritten);
        StringAssert.DoesNotContain("Add(\"Btn\")", rewritten,
            "Builder source still references the deleted child.\n" + rewritten);

        var second = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(second.Changed, "Second sync was not a no-op after the closure-authored delete.");
        Assert.AreEqual(0, second.PatchEdits, "Second sync produced edits — the delete did not converge.");

        // The user-visible consequence: a re-Build from the rewritten source must still create Panel.
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsNotNull(Find(EditorSceneManager.GetActiveScene(), "Canvas/Panel"),
            "Panel is missing from a re-Build of the rewritten source — the parent was destroyed along with the child.");
    }

    // Deleting ONE component chained inside an expression-lambda configure closure
    // must leave every sibling component the user never touched — including one authored on the
    // SAME chain.
    [Test]
    public void SceneToCode_DeletedComponentFromChainedClosure_KeepsTheSiblingComponent()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Rig\", x => x.Component<UnityEngine.MeshFilter>().Component<UnityEngine.BoxCollider>());"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var live = EditorSceneManager.GetActiveScene();
        var rig = Find(live, "Rig");
        Assert.IsNotNull(rig, "Rig was not created by SceneBuilderBuild.Run");
        var boxCollider = rig.GetComponent<BoxCollider>();
        Assert.IsNotNull(boxCollider, "Authored BoxCollider was not materialized on Rig");
        Assert.IsNotNull(rig.GetComponent<MeshFilter>(), "Authored MeshFilter was not materialized on Rig");

        Object.DestroyImmediate(boxCollider);

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite a destroyed chained-closure component");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("Component<UnityEngine.MeshFilter>", rewritten,
            "Builder source lost the sibling MeshFilter it must have kept.\n" + rewritten);
        StringAssert.DoesNotContain("Component<UnityEngine.BoxCollider>", rewritten,
            "Builder source still declares the removed BoxCollider.\n" + rewritten);
        StringAssert.Contains("Add(\"Rig\"", rewritten,
            "Builder source lost the Rig statement itself.\n" + rewritten);

        var second = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(second.Changed, "Second sync was not a no-op after the chained-component delete.");
        Assert.AreEqual(0, second.PatchEdits, "Second sync produced edits — the delete did not converge.");

        // The user-visible consequence: a re-Build from the rewritten source must still carry MeshFilter.
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        var rebuilt = Find(EditorSceneManager.GetActiveScene(), "Rig");
        Assert.IsNotNull(rebuilt, "Rig is missing from a re-Build of the rewritten source.");
        Assert.IsNotNull(rebuilt.GetComponent<MeshFilter>(),
            "MeshFilter is missing from a re-Build of the rewritten source — the sibling component was lost.");
    }
}
