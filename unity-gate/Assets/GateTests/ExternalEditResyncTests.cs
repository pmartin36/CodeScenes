using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SceneBuilder.Core.Sync;
using SceneBuilder.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// Gate for SceneBuilderResync.ResyncRoute: recovering an external edit that fired no
// ObjectChangeEvent (a direct live-transform mutation or a direct File.WriteAllText, watcher/pump
// never invoked) by reading the checkpoint + sidecar + source from disk and a fresh cold snapshot.
// Follows AutoSceneToCodeTests'/AutoConflictTests' pattern: builder+sidecar live under the real
// SceneBuilderPaths.Builder/Sidecar dir so SceneBuilderRouter.Discover() finds them, scene saved
// under Assets/GateTests/, seeded via SceneBuilderBuild.Run (which writes the first checkpoint).
public class ExternalEditResyncTests
{
    private const string BuilderName = "ExternalEditResyncScene";
    private const string ScenePath = "Assets/GateTests/__ExternalEditResyncTemp.unity";

    private string _builderPath;
    private string _sidecarPath;
    private string _statePath;

    private static string Source(float ax, float ay, float az) => $@"
using SceneBuilder.Authoring;
public class ExternalEditResyncScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
        scene.Add(""Alpha"").Transform(pos: ({ax}f, {ay}f, {az}f));
        scene.Add(""Beta"").Transform(pos: (0f, 0f, 0f));
    }}
}}";

    private static GameObject FindRoot(Scene scene, string name) =>
        scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);

    [SetUp]
    public void SetUp()
    {
        SceneBuilderPaths.EnsureBuildersDirectory();
        _builderPath = SceneBuilderPaths.Builder(BuilderName);
        _sidecarPath = SceneBuilderPaths.Sidecar(BuilderName);
        _statePath = SceneBuilderPaths.StateForSidecar(_sidecarPath);

        SceneBuilderRouter.ResetForTests();
        SceneBuilderAutoSync.ResetForTests();
        SuppressionScope.ResetForTests();
    }

    [TearDown]
    public void TearDown()
    {
        SceneBuilderRouter.ResetForTests();
        SceneBuilderAutoSync.ResetForTests();
        SuppressionScope.ResetForTests();

        if (File.Exists(_builderPath)) File.Delete(_builderPath);
        if (File.Exists(_sidecarPath)) File.Delete(_sidecarPath);
        if (File.Exists(_statePath)) File.Delete(_statePath);

        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }
    }

    private Scene Build()
    {
        File.WriteAllText(_builderPath, Source(1f, 2f, 3f));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        SceneBuilderRouter.ResetForTests();
        return EditorSceneManager.GetActiveScene();
    }

    private BuilderRoute Route()
    {
        Assert.IsTrue(SceneBuilderRouter.TryRouteScene(EditorSceneManager.GetActiveScene(), out var route),
            "Precondition: the seeded scene must route to its own builder.");
        return route;
    }

    [Test]
    public void NoExternalChange_Resync_IsNoOp_NoWrite()
    {
        Build();
        var route = Route();
        var sourceBefore = File.ReadAllText(_builderPath);
        var sidecarBefore = File.ReadAllText(_sidecarPath);
        var stateBefore = File.ReadAllText(_statePath);

        var direction = SceneBuilderResync.ResyncRoute(route, EditorSceneManager.GetActiveScene());

        Assert.AreEqual(SyncDirection.NoOp, direction,
            "A resync immediately after a converged build, with no external change, must decide NoOp.");
        Assert.AreEqual(sourceBefore, File.ReadAllText(_builderPath), "NoOp must not rewrite the builder source.");
        Assert.AreEqual(sidecarBefore, File.ReadAllText(_sidecarPath), "NoOp must not rewrite the sidecar.");
        Assert.AreEqual(stateBefore, File.ReadAllText(_statePath), "NoOp must not rewrite the checkpoint.");
    }

    [Test]
    public void ExternalSceneEdit_NoEvent_Resync_ReconcilesSource()
    {
        var scene = Build();
        var route = Route();
        var alpha = FindRoot(scene, "Alpha");
        Assert.IsNotNull(alpha, "Alpha was not created by SceneBuilderBuild.Run");
        alpha.transform.position = new Vector3(9f, 9f, 9f);

        var direction = SceneBuilderResync.ResyncRoute(route, EditorSceneManager.GetActiveScene());

        Assert.AreEqual(SyncDirection.Reconcile, direction,
            "A live scene mutation with no ObjectChangeEvent pumped must resync as Reconcile.");
        StringAssert.Contains("(9f, 9f, 9f)", File.ReadAllText(_builderPath),
            "Reconcile must patch the builder source with the externally-moved value.");
    }

    [Test]
    public void AfterReconcile_SecondResync_IsNoOp()
    {
        var scene = Build();
        var route = Route();
        var alpha = FindRoot(scene, "Alpha");
        alpha.transform.position = new Vector3(9f, 9f, 9f);

        var first = SceneBuilderResync.ResyncRoute(route, EditorSceneManager.GetActiveScene());
        Assert.AreEqual(SyncDirection.Reconcile, first,
            "Precondition: the first resync must reconcile the external edit before checking the follow-up.");

        var second = SceneBuilderResync.ResyncRoute(route, EditorSceneManager.GetActiveScene());

        Assert.AreEqual(SyncDirection.NoOp, second,
            "A resync immediately after a Reconcile must decide NoOp — the checkpoint refreshed to the reconciled state.");
    }

    [Test]
    public void ExternalSourceEdit_NoEvent_Resync_Materializes()
    {
        var scene = Build();
        var route = Route();
        File.WriteAllText(_builderPath, Source(9f, 9f, 9f));

        var direction = SceneBuilderResync.ResyncRoute(route, EditorSceneManager.GetActiveScene());

        Assert.AreEqual(SyncDirection.Materialize, direction,
            "A direct file write to the builder source with no watcher pump must resync as Materialize.");
        var alpha = FindRoot(EditorSceneManager.GetActiveScene(), "Alpha");
        Assert.IsNotNull(alpha, "Alpha was not found after Materialize");
        Assert.AreEqual(new Vector3(9f, 9f, 9f), alpha.transform.position,
            "Materialize must move the live object to the externally-authored value.");
    }

    [Test]
    public void CorruptCheckpoint_Resync_LogsAndDoesNotThrow()
    {
        Build();
        Route();
        var sourceBefore = File.ReadAllText(_builderPath);
        var sidecarBefore = File.ReadAllText(_sidecarPath);

        File.WriteAllText(_statePath, "{ this is not valid checkpoint json");

        LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex(".*"));

        Assert.DoesNotThrow(() => SceneBuilderResync.ResyncActiveScene(),
            "A corrupt on-disk checkpoint must not propagate a throw into the focus-regain trigger.");

        Assert.AreEqual(sourceBefore, File.ReadAllText(_builderPath),
            "A contained throw must not have written the builder source.");
        Assert.AreEqual(sidecarBefore, File.ReadAllText(_sidecarPath),
            "A contained throw must not have written the sidecar.");
    }

    [Test]
    public void BothChanged_Resync_SurfacesConflict_WritesNeither()
    {
        var scene = Build();
        var route = Route();
        var editedSource = Source(9f, 9f, 9f);
        File.WriteAllText(_builderPath, editedSource);

        var beta = FindRoot(scene, "Beta");
        Assert.IsNotNull(beta, "Beta was not created by SceneBuilderBuild.Run");
        beta.transform.position = new Vector3(5f, 5f, 5f);

        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Conflict.*ExternalEditResyncScene"));

        var direction = SceneBuilderResync.ResyncRoute(route, EditorSceneManager.GetActiveScene());

        Assert.AreEqual(SyncDirection.Conflict, direction,
            "Both the source and the scene changing since the last checkpoint must surface a conflict, never last-write-wins.");
        Assert.AreEqual(editedSource, File.ReadAllText(_builderPath),
            "A surfaced conflict must not overwrite the externally-edited builder source.");
        Assert.AreEqual(new Vector3(5f, 5f, 5f), FindRoot(EditorSceneManager.GetActiveScene(), "Beta").transform.position,
            "A surfaced conflict must not overwrite the manually-edited live object.");
    }
}
