using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SceneBuilder.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// Gate for the b5-t2 code->scene executor (spec checklist #4): a real external write to the
// governing builder drives an in-place Build; a byte-identical re-emit through WriteIfChanged
// must not fire the watcher / must not run a build; a parse error logs LOCATED and leaves the
// scene untouched. Follows AutoSceneToCodeTests' setup pattern: SceneBuilderBuild.Run seeds an
// initial builder + sidecar + saved scene, then the test drives an external edit and asserts
// propagation via SceneBuilderAutoSync.ExecuteCodeToScene directly (real GameObject/scene asset).
public class AutoCodeToSceneTests
{
    private const string ScenePath = "Assets/GateTests/__AutoCodeToSceneTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class AutoCodeToSceneScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    private static GameObject FindRoot(Scene scene, string name) =>
        scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_c2a_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "AutoCodeToSceneScene.cs");
        _sidecarPath = Path.Combine(_dir, "AutoCodeToSceneScene.sbmap.json");

        SceneBuilderAutoSync.ResetForTests();
        SuppressionScope.ResetForTests();
    }

    [TearDown]
    public void TearDown()
    {
        SceneBuilderAutoSync.ResetForTests();
        SuppressionScope.ResetForTests();

        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }
    }

    // (a) checklist #4, positive half: an EXTERNAL edit to the governing builder must drive an
    // in-place Build — the live scene AND the saved scene asset must gain the new object.
    [Test]
    public void CodeToScene_ExternalBuilderEdit_BuildsSceneInPlace()
    {
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Alpha\");"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        Assert.IsNull(FindRoot(EditorSceneManager.GetActiveScene(), "Beta"),
            "Precondition: Beta must not exist before the external edit.");

        // A real external write — the file watcher's raw signal, not routed through WriteIfChanged.
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Alpha\");\n        scene.Add(\"Beta\");"));

        SceneBuilderAutoSync.ExecuteCodeToScene(new[] { _builderPath });

        Assert.IsNotNull(FindRoot(EditorSceneManager.GetActiveScene(), "Beta"),
            "ExecuteCodeToScene must build the external edit into the live scene in place.");
        StringAssert.Contains("Beta", File.ReadAllText(ScenePath),
            "ExecuteCodeToScene must save the scene asset on disk with the new object (non-destructive in-place Build).");
    }

    // (b) checklist #4, no-op half: a byte-identical re-emit through WriteIfChanged must not be
    // treated as a real external write, and a cycle whose only path is an own-write must not run a
    // build (proves the pre-existing WriteIfChanged + own-write-registry guard, not new mechanism).
    [Test]
    public void CodeToScene_ByteIdenticalReEmit_DoesNotFireBuild()
    {
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Alpha\");"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var contents = File.ReadAllText(_builderPath);

        // Re-emitting byte-identical content through the write-if-changed path must be a no-op:
        // no write, so no mtime bump, so the watcher never observes a change to notify on.
        var wroteAgain = SceneBuilderPaths.WriteIfChanged(_builderPath, contents);
        Assert.IsFalse(wroteAgain, "WriteIfChanged must not rewrite byte-identical content.");

        // And even if the pump were notified of this own-write path, it must be dropped before
        // reaching a debounce cycle at all — CodeToSceneCycleCount must stay at zero.
        SceneBuilderAutoSync.NotifySourceChanged(_builderPath);
        Assert.AreEqual(0, SceneBuilderAutoSync.CodeToSceneCycleCount,
            "A byte-identical own-write must never accumulate into a code->scene debounce cycle.");
    }

    // A parse error must log LOCATED (file/line/message) and leave BOTH the live scene and the
    // saved scene asset untouched — never a silent no-op-with-broken-state (§7 fail-loud).
    [Test]
    public void CodeToScene_ParseError_LogsLocated_SceneUntouched()
    {
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Alpha\");"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var sceneTextBefore = File.ReadAllText(ScenePath);
        var rootsBefore = EditorSceneManager.GetActiveScene().GetRootGameObjects().Select(go => go.name).ToArray();

        // An unresolvable receiver — a structural parse error, not a semantic/resolution one.
        File.WriteAllText(_builderPath, Source("        foo.Bar();"));

        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(
            System.Text.RegularExpressions.Regex.Escape(_builderPath) + ".*line"));

        SceneBuilderAutoSync.ExecuteCodeToScene(new[] { _builderPath });

        Assert.AreEqual(sceneTextBefore, File.ReadAllText(ScenePath),
            "A parse error must leave the saved scene asset untouched.");
        var rootsAfter = EditorSceneManager.GetActiveScene().GetRootGameObjects().Select(go => go.name).ToArray();
        CollectionAssert.AreEquivalent(rootsBefore, rootsAfter,
            "A parse error must leave the live scene's root objects untouched.");
    }

    // The pump's production default must be wired to the real executor, not left null — the whole
    // point of the auto-sync loop is that the happy path needs no manual wiring.
    [Test]
    public void AutoSync_WireDefaultExecutors_WiresCodeToSceneExecutor()
    {
        SceneBuilderAutoSync.WireDefaultExecutors();

        Assert.IsNotNull(SceneBuilderAutoSync.CodeToSceneExecutor,
            "WireDefaultExecutors must assign a real code->scene executor so the auto-sync pump is wired to production logic by default.");

        Action<System.Collections.Generic.IReadOnlyCollection<string>> expected = SceneBuilderAutoSync.ExecuteCodeToScene;
        Assert.AreEqual(expected, SceneBuilderAutoSync.CodeToSceneExecutor,
            "The default-wired code->scene executor must be ExecuteCodeToScene.");
    }
}
