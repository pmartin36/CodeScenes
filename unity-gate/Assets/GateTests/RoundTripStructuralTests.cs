using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;

// M2 (structural + flags) bidirectional round-trip gate tests. Each test DRIVES a real change on
// one side (a live EditMode scene edit, or an authored builder-source edit) through the programmatic
// Build/Sync APIs (SceneBuilderBuild.Run / SceneBuilderSync.Run) and ASSERTS propagation on the
// other side. Follows RoundTripTransformTests exactly: a temp builder .cs + temp sidecar in a
// system temp dir (never the real Assets/SceneBuilder/DemoScene.cs), an EmptyScene created per
// test, and [TearDown] cleanup. Covers create / delete / reparent / rename (both directions) and
// the four GameObject flags (both directions).
public class RoundTripStructuralTests
{
    // Scene must save under Assets (EditorSceneManager.SaveScene is project-relative); the builder
    // + sidecar live in a system temp dir so Unity never tries to import/compile the builder .cs.
    private const string ScenePath = "Assets/GateTests/__RoundTripStructuralTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    // Wrap a Build-body fragment in a minimal ISceneDefinition the Core Roslyn parser understands.
    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class RoundTripScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    private static GameObject FindRoot(Scene scene, string name)
    {
        return scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);
    }

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_rts_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "RoundTripScene.cs");
        _sidecarPath = Path.Combine(_dir, "RoundTripScene.sbmap.json");
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

    // 1. Create (scene->code): a GameObject created in the editor under a managed parent appends an
    //    Add(...) statement for it (the handle-less parent gets a var handle introduced).
    [Test]
    public void SceneToCode_CreatedChildObject_AppendsAddStatement()
    {
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Weapon\");"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var weapon = FindRoot(EditorSceneManager.GetActiveScene(), "Weapon");
        Assert.IsNotNull(weapon, "Weapon was not created by SceneBuilderBuild.Run");

        // Create a NEW GameObject in the scene, parented under the managed Weapon.
        var scope = new GameObject("Scope");
        scope.transform.SetParent(weapon.transform);

        var result = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite a newly-created GameObject");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("Add(\"Scope\")", rewritten,
            "Builder source did not gain an Add(\"Scope\") statement for the scene-created object.\n" + rewritten);
    }

    // 2. Delete (scene->code): a GameObject destroyed in the editor removes its statement; the
    //    survivor remains.
    [Test]
    public void SceneToCode_DeletedObject_RemovesItsStatement()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Keeper\");\n" +
            "        scene.Add(\"Goner\");"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var goner = FindRoot(EditorSceneManager.GetActiveScene(), "Goner");
        Assert.IsNotNull(goner, "Goner was not created by SceneBuilderBuild.Run");
        Object.DestroyImmediate(goner);

        var result = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite a destroyed GameObject");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.DoesNotContain("Add(\"Goner\")", rewritten,
            "Builder source still references the deleted object.\n" + rewritten);
        StringAssert.Contains("Add(\"Keeper\")", rewritten,
            "Builder source lost the surviving object.\n" + rewritten);
    }

    // 3. Reparent (scene->code): moving one root under another (transform.SetParent) rewrites the
    //    child's statement to hang off the parent's handle.
    [Test]
    public void SceneToCode_ReparentedObject_RewritesReceiverToParentHandle()
    {
        // Alpha authored WITH a handle so the reparent is expressible via the existing API.
        File.WriteAllText(_builderPath, Source(
            "        var alpha = scene.Add(\"Alpha\");\n" +
            "        scene.Add(\"Beta\");"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var live = EditorSceneManager.GetActiveScene();
        var alpha = FindRoot(live, "Alpha");
        var beta = FindRoot(live, "Beta");
        Assert.IsNotNull(alpha, "Alpha was not created by SceneBuilderBuild.Run");
        Assert.IsNotNull(beta, "Beta was not created by SceneBuilderBuild.Run");

        beta.transform.SetParent(alpha.transform);

        var result = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite a reparent");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("alpha.Add(\"Beta\")", rewritten,
            "Builder source did not reparent Beta under Alpha's handle.\n" + rewritten);
    }

    // 4. Rename with identity preserved (code->scene) — the regression that bit the user. Renaming a
    //    handle-less object's Add("...") argument and rebuilding the SAME scene renames in place: no
    //    duplicate, no orphan, SAME underlying object (identity preserved, not destroy+recreate).
    [Test]
    public void CodeToScene_RenamedHandlelessObject_PreservesIdentityNoDuplicate()
    {
        File.WriteAllText(_builderPath, Source("        scene.Add(\"ManHade\");"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var original = FindRoot(EditorSceneManager.GetActiveScene(), "ManHade");
        Assert.IsNotNull(original, "ManHade was not created by the first build");
        var originalEntityId = original.GetEntityId();

        // Rewrite the SAME slot to a new name and rebuild the SAME scene.
        File.WriteAllText(_builderPath, Source("        scene.Add(\"GanPaid\");"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var live = EditorSceneManager.GetActiveScene();
        Assert.IsNull(FindRoot(live, "ManHade"), "Old-named object still exists — rename left an orphan.");

        var managed = live.GetRootGameObjects().Where(go => go.name == "GanPaid").ToArray();
        Assert.AreEqual(1, managed.Length, "Expected exactly one renamed object — got a duplicate or none.");
        Assert.AreEqual(originalEntityId, managed[0].GetEntityId(),
            "Renamed object is a different instance — identity was NOT preserved (destroy+recreate).");
    }

    // 5. Rename (scene->code): renaming the real GameObject rewrites the Add("...") name argument.
    [Test]
    public void SceneToCode_RenamedObject_UpdatesNameArgument()
    {
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Original\");"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var go = FindRoot(EditorSceneManager.GetActiveScene(), "Original");
        Assert.IsNotNull(go, "Original was not created by SceneBuilderBuild.Run");
        go.name = "Renamed";

        var result = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite a rename");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("Add(\"Renamed\")", rewritten,
            "Builder source did not pick up the renamed object.\n" + rewritten);
        StringAssert.DoesNotContain("Add(\"Original\")", rewritten,
            "Builder source still carries the old name.\n" + rewritten);
    }

    // 6. Flags (scene->code): setting tag / layer / active / static on the real GameObject introduces
    //    the corresponding .Tag/.Layer/.Active/.Static calls onto its statement.
    [Test]
    public void SceneToCode_FlagsChanged_IntroducesFlagCalls()
    {
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Flagged\");"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var go = FindRoot(EditorSceneManager.GetActiveScene(), "Flagged");
        Assert.IsNotNull(go, "Flagged was not created by SceneBuilderBuild.Run");

        go.tag = "Player";   // builtin tag — no TagManager edit required
        go.layer = 5;        // "UI" — an existing layer in the gate project's TagManager
        go.SetActive(false);
        go.isStatic = true;

        var result = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite four flag edits");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains(".Tag(\"Player\")", rewritten, "Missing .Tag call.\n" + rewritten);
        StringAssert.Contains(".Layer(5)", rewritten, "Missing .Layer call.\n" + rewritten);
        StringAssert.Contains(".Active(false)", rewritten, "Missing .Active call.\n" + rewritten);
        StringAssert.Contains(".Static()", rewritten, "Missing .Static call.\n" + rewritten);
    }

    // 7. Flags (code->scene): authoring .Active(false) materializes an inactive GameObject.
    [Test]
    public void CodeToScene_ActiveFalse_MaterializesInactiveGameObject()
    {
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Sleeper\").Active(false);"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        // GameObject.Find only returns active objects, so look through the scene roots directly.
        var sleeper = FindRoot(EditorSceneManager.GetActiveScene(), "Sleeper");
        Assert.IsNotNull(sleeper, "Sleeper was not created by SceneBuilderBuild.Run");
        Assert.IsFalse(sleeper.activeSelf,
            "Authored .Active(false) did not materialize an inactive GameObject.");
    }
}
