using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using SceneBuilder.Editor;
using SceneBuilder.Editor.Licensing;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// Gate for the code->scene scene-open race: a source edit whose routed scene is not yet open must
// be DEFERRED (left in the pump's pending set, deadline re-armed) rather than dropped, so the next
// due tick after the scene opens still builds it. Drives the real pump (PumpOnce + NotifySourceChanged)
// with the real ExecuteCodeToScene executor wired in, following AutoCodeToSceneTests' harness for the
// builder/sidecar/scene seed and MultiSceneRoutingTests' pattern for opening/closing the routed scene.
public class AutoCodeToSceneSceneOpenRaceTests
{
    private const string BuilderName = "AutoCodeToSceneRaceScene";
    private const string ScenePath = "Assets/GateTests/__AutoCodeToSceneRaceTemp.unity";

    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class AutoCodeToSceneRaceScene : ISceneDefinition
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
        LicenseGate.ResetToDefault();
        SceneBuilderPaths.EnsureBuildersDirectory();
        _builderPath = SceneBuilderPaths.Builder(BuilderName);
        _sidecarPath = SceneBuilderPaths.Sidecar(BuilderName);

        SceneBuilderRouter.ResetForTests();
        SceneBuilderAutoSync.ResetForTests();
        SuppressionScope.ResetForTests();

        // This file drives the pending-set LOGIC via the injectable Clock + explicit PumpOnce ticks
        // against a REAL routable builder path. The real OS-level FileSystemWatcher also watches that
        // same path and would race the deterministic ticks below, so it is disabled for this file;
        // the pump, its executors and the deferral logic under test are otherwise fully armed.
        SceneBuilderAutoSync.DisableWatcherForTests = true;
        SceneBuilderAutoSync.Disarm();
        SceneBuilderAutoSync.Arm();

        SceneBuilderAutoSync.CodeToSceneExecutor = SceneBuilderAutoSync.ExecuteCodeToScene;

        File.WriteAllText(_builderPath, Source("        scene.Add(\"Alpha\");"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var seedScene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, seedScene);
        SceneBuilderRouter.ResetForTests();

        // Replace the active scene so the routed target scene (ScenePath) is no longer open/loaded —
        // the precondition every case in this file starts from.
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    [TearDown]
    public void TearDown()
    {
        LicenseEnforcement.Register();
        SceneBuilderRouter.ResetForTests();
        SceneBuilderAutoSync.ResetForTests();
        SuppressionScope.ResetForTests();

        if (File.Exists(_builderPath)) File.Delete(_builderPath);
        if (File.Exists(_sidecarPath)) File.Delete(_sidecarPath);

        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }
    }

    // A code->scene cycle whose routed scene is not open must defer the path (stay in the pump's
    // pending set) instead of dropping it — the target scene never sees the edit until it opens.
    [Test]
    public void CodeToScene_TargetSceneNotOpen_DefersPathInsteadOfDropping()
    {
        Assert.IsTrue(SceneBuilderRouter.TryRouteBuilderFile(_builderPath, out var route),
            "Precondition: the builder must route.");
        Assert.IsFalse(SceneBuilderRouter.TryGetOpenScene(route, out _),
            "Precondition: the routed scene must not be open.");

        File.WriteAllText(_builderPath, Source("        scene.Add(\"Alpha\");\n        scene.Add(\"Beta\");"));

        var now = 100.0;
        SceneBuilderAutoSync.Clock = () => now;
        SceneBuilderAutoSync.NotifySourceChanged(_builderPath);
        now += SceneBuilderAutoSync.SettleSeconds + 0.01;
        SceneBuilderAutoSync.PumpOnce(now);

        Assert.IsTrue(SceneBuilderAutoSync.IsSourcePathPending(_builderPath),
            "A code->scene cycle whose routed scene is not open must defer the path (stay pending), never drop it.");
    }

    // Once the deferred path's scene opens, a further due tick must build the edit into it and drain
    // the path from the pending set — the deferral converges, it does not hang forever.
    [Test]
    public void CodeToScene_DeferredThenSceneOpens_ConvergesAndClearsPending()
    {
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Alpha\");\n        scene.Add(\"Beta\");"));

        var now = 100.0;
        SceneBuilderAutoSync.Clock = () => now;
        SceneBuilderAutoSync.NotifySourceChanged(_builderPath);
        now += SceneBuilderAutoSync.SettleSeconds + 0.01;
        SceneBuilderAutoSync.PumpOnce(now);

        StringAssert.DoesNotContain("Beta", File.ReadAllText(ScenePath),
            "Precondition: a deferred cycle must not have written the edit to the scene asset yet.");

        var opened = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        now += SceneBuilderAutoSync.SettleSeconds + 0.01;
        SceneBuilderAutoSync.PumpOnce(now);

        Assert.IsNotNull(FindRoot(opened, "Beta"),
            "Once the routed scene is open, a further due tick must build the deferred edit into the scene.");
        Assert.IsFalse(SceneBuilderAutoSync.IsSourcePathPending(_builderPath),
            "A converged build must drain the path from the pending set.");
    }

    // A retry ceiling bounds the deferral to exactly CodeToSceneDeferralCeiling deferred ticks: each
    // of those ticks still leaves the path pending (stays deferred); only the NEXT miss past the
    // ceiling drops the path and logs it as abandoned rather than retrying forever.
    [Test]
    public void CodeToScene_RetryCeiling_DropsAfterMaxAttempts_NeverPending()
    {
        var prevIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        try
        {
            SceneBuilderAutoSync.CodeToSceneDeferralCeiling = 2;

            File.WriteAllText(_builderPath, Source("        scene.Add(\"Alpha\");\n        scene.Add(\"Beta\");"));

            var now = 100.0;
            SceneBuilderAutoSync.Clock = () => now;
            SceneBuilderAutoSync.NotifySourceChanged(_builderPath);

            now += SceneBuilderAutoSync.SettleSeconds + 0.01;
            SceneBuilderAutoSync.PumpOnce(now); // deferral 1 of 2 — below the ceiling

            Assert.IsTrue(SceneBuilderAutoSync.IsSourcePathPending(_builderPath),
                "The first deferral, below the ceiling of 2, must leave the path pending.");

            now += SceneBuilderAutoSync.SettleSeconds + 0.01;
            SceneBuilderAutoSync.PumpOnce(now); // deferral 2 of 2 — AT the ceiling, one more miss abandons it

            Assert.IsTrue(SceneBuilderAutoSync.IsSourcePathPending(_builderPath),
                "The deferral that reaches the ceiling of 2 must still leave the path pending — the ceiling counts completed deferrals, and only the miss AFTER it abandons the path.");

            now += SceneBuilderAutoSync.SettleSeconds + 0.01;
            LogAssert.Expect(LogType.Error, new Regex("abandoned"));
            SceneBuilderAutoSync.PumpOnce(now); // 3rd miss — ceiling exceeded, abandon

            Assert.IsFalse(SceneBuilderAutoSync.IsSourcePathPending(_builderPath),
                "Once a miss exceeds the retry ceiling the path must be dropped, never left pending forever.");
        }
        finally
        {
            LogAssert.ignoreFailingMessages = prevIgnore;
        }
    }

    // A successful build resets the path's deferral counter — a later edit to the same builder
    // against a still-closed scene gets a fresh set of retries, not treated as already exhausted.
    [Test]
    public void CodeToScene_CounterResetsOnSuccess_LaterEditDefersFreshNotAbandoned()
    {
        SceneBuilderAutoSync.CodeToSceneDeferralCeiling = 1;

        File.WriteAllText(_builderPath, Source("        scene.Add(\"Alpha\");\n        scene.Add(\"Beta\");"));

        var now = 100.0;
        SceneBuilderAutoSync.Clock = () => now;
        SceneBuilderAutoSync.NotifySourceChanged(_builderPath);
        now += SceneBuilderAutoSync.SettleSeconds + 0.01;
        SceneBuilderAutoSync.PumpOnce(now); // the sole allowed deferral at ceiling = 1

        Assert.IsTrue(SceneBuilderAutoSync.IsSourcePathPending(_builderPath),
            "Precondition: the first deferral (below the ceiling of 1) must leave the path pending.");

        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        now += SceneBuilderAutoSync.SettleSeconds + 0.01;
        SceneBuilderAutoSync.PumpOnce(now);

        Assert.IsFalse(SceneBuilderAutoSync.IsSourcePathPending(_builderPath),
            "Precondition: the converged build must drain the path from the pending set.");

        // Close the scene again and make a further external edit — with the counter reset, this
        // must defer fresh (one allowed retry again), not be treated as already at the ceiling.
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Alpha\");\n        scene.Add(\"Beta\");\n        scene.Add(\"Gamma\");"));
        SceneBuilderAutoSync.NotifySourceChanged(_builderPath);
        now += SceneBuilderAutoSync.SettleSeconds + 0.01;
        SceneBuilderAutoSync.PumpOnce(now);

        Assert.IsTrue(SceneBuilderAutoSync.IsSourcePathPending(_builderPath),
            "A later edit after a converged build must defer fresh (counter reset on success), not be abandoned as if still at the old ceiling.");
    }
}
