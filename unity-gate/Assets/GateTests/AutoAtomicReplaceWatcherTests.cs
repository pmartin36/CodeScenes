using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SceneBuilder.Editor;
using SceneBuilder.Editor.Licensing;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// Gate for the real FileSystemWatcher seeing an atomic-replace write (temp file + File.Replace onto
// the governing builder path) at parity with an in-place append, plus the focus-regain content-hash
// rescan backstop for a write the watcher still misses. Follows AutoCodeToSceneTests' harness for the
// builder/sidecar/saved-scene seed and AutoCodeToSceneSceneOpenRaceTests' Arm/Disarm + DisableWatcherForTests
// seams for isolating the real watcher from the deterministic rescan cases.
public class AutoAtomicReplaceWatcherTests
{
    private const string BuilderName = "AutoAtomicReplaceWatcherScene";
    private const string ScenePath = "Assets/GateTests/__AutoAtomicReplaceWatcherTemp.unity";

    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class AutoAtomicReplaceWatcherScene : ISceneDefinition
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

        var dir = Path.GetDirectoryName(_builderPath);
        if (dir != null && Directory.Exists(dir))
        {
            foreach (var tmp in Directory.GetFiles(dir, BuilderName + ".cs.tmp*"))
            {
                File.Delete(tmp);
            }
        }

        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }
    }

    private Scene Seed()
    {
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Alpha\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        SceneBuilderRouter.ResetForTests();
        return EditorSceneManager.GetActiveScene();
    }

    private string AtomicReplaceWith(string body)
    {
        var tmpPath = _builderPath + ".tmp" + Guid.NewGuid().ToString("N").Substring(0, 8);
        File.WriteAllText(tmpPath, Source(body));
        File.Replace(tmpPath, _builderPath, null);
        return tmpPath;
    }

    // Polls, pumping the real watcher's queued paths through PumpOnce each frame, until `converged`
    // is true or the timeout elapses. The real watcher's OS-level notification races the test thread,
    // so a single tick is never sufficient here.
    private static IEnumerator PollUntil(Func<bool> converged, float timeoutSeconds = 5f)
    {
        var deadline = Time.realtimeSinceStartup + timeoutSeconds;
        while (!converged() && Time.realtimeSinceStartup < deadline)
        {
            SceneBuilderAutoSync.PumpOnce(SceneBuilderAutoSync.Clock() + SceneBuilderAutoSync.SettleSeconds + 0.01);
            yield return null;
        }
    }

    // Baseline for the atomic-replace case below: a real in-place append, delivered by the actual
    // OS-level FileSystemWatcher, converges the scene with no direct pump call from the test.
    [UnityTest]
    public IEnumerator Append_RealWatcher_TriggersCodeToScene_SceneConverges()
    {
        var scene = Seed();
        Assert.IsNull(FindRoot(scene, "Beta"), "Precondition: Beta must not exist before the append.");

        SceneBuilderAutoSync.CodeToSceneExecutor = SceneBuilderAutoSync.ExecuteCodeToScene;

        File.WriteAllText(_builderPath, Source("        scene.Add(\"Alpha\");\n        scene.Add(\"Beta\");"));

        yield return PollUntil(() => FindRoot(EditorSceneManager.GetActiveScene(), "Beta") != null);

        Assert.IsNotNull(FindRoot(EditorSceneManager.GetActiveScene(), "Beta"),
            "A real in-place append delivered by the OS watcher must converge the scene with no manual pump call from the test.");
    }

    // The platform-claim arbiter: an atomic replace (write a temp file, then File.Replace onto the
    // builder path) must be seen by the real watcher at parity with the append case above.
    [UnityTest]
    public IEnumerator AtomicReplace_RealWatcher_TriggersCodeToScene_SceneConverges()
    {
        var scene = Seed();
        Assert.IsNull(FindRoot(scene, "Beta"), "Precondition: Beta must not exist before the atomic replace.");

        SceneBuilderAutoSync.CodeToSceneExecutor = SceneBuilderAutoSync.ExecuteCodeToScene;

        AtomicReplaceWith("        scene.Add(\"Alpha\");\n        scene.Add(\"Beta\");");

        yield return PollUntil(() => FindRoot(EditorSceneManager.GetActiveScene(), "Beta") != null);

        Assert.IsNotNull(FindRoot(EditorSceneManager.GetActiveScene(), "Beta"),
            "An atomic replace (temp file + File.Replace onto the builder path) must be seen by the real watcher and converge the scene, at parity with an in-place append.");
    }

    // A slipped write the watcher never saw (modeled here by disabling it) converges once the
    // focus-regain rescan runs: it re-enqueues the changed builder and the pump builds it into the
    // open scene, with no in-scene edit made by the test.
    [Test]
    public void FocusRescan_SlippedAtomicWrite_ConvergesActiveScene_NoInSceneEdit()
    {
        var scene = Seed();
        SceneBuilderAutoSync.DisableWatcherForTests = true;
        SceneBuilderAutoSync.Disarm();
        SceneBuilderAutoSync.Arm();
        SceneBuilderAutoSync.CodeToSceneExecutor = SceneBuilderAutoSync.ExecuteCodeToScene;

        AtomicReplaceWith("        scene.Add(\"Alpha\");\n        scene.Add(\"Beta\");");

        Assert.IsNull(FindRoot(scene, "Beta"),
            "Precondition: the watcher is disabled, so the slipped write must not have converged yet.");

        SceneBuilderAutoSync.RescanDiscoveredBuilders();
        SceneBuilderAutoSync.PumpOnce(SceneBuilderAutoSync.Clock() + SceneBuilderAutoSync.SettleSeconds + 0.01);

        Assert.IsNotNull(FindRoot(EditorSceneManager.GetActiveScene(), "Beta"),
            "A focus-regain rescan must re-enqueue a slipped atomic write and converge the active scene with no in-scene edit.");
    }

    // Once a rescan has converged a builder, a further rescan with no additional source change must
    // not re-enqueue it -- the stored content hash suppresses a re-fire on every focus.
    [Test]
    public void FocusRescan_UnchangedBuilder_DoesNotReFire()
    {
        Seed();
        SceneBuilderAutoSync.DisableWatcherForTests = true;
        SceneBuilderAutoSync.Disarm();
        SceneBuilderAutoSync.Arm();
        SceneBuilderAutoSync.CodeToSceneExecutor = SceneBuilderAutoSync.ExecuteCodeToScene;

        AtomicReplaceWith("        scene.Add(\"Alpha\");\n        scene.Add(\"Beta\");");

        SceneBuilderAutoSync.RescanDiscoveredBuilders();
        SceneBuilderAutoSync.PumpOnce(SceneBuilderAutoSync.Clock() + SceneBuilderAutoSync.SettleSeconds + 0.01);

        Assert.IsNotNull(FindRoot(EditorSceneManager.GetActiveScene(), "Beta"),
            "Precondition: the first rescan must converge the slipped write before checking the follow-up.");
        var cyclesAfterFirstRescan = SceneBuilderAutoSync.CodeToSceneCycleCount;

        SceneBuilderAutoSync.RescanDiscoveredBuilders();
        SceneBuilderAutoSync.PumpOnce(SceneBuilderAutoSync.Clock() + SceneBuilderAutoSync.SettleSeconds + 0.01);

        Assert.AreEqual(cyclesAfterFirstRescan, SceneBuilderAutoSync.CodeToSceneCycleCount,
            "A rescan over an unchanged builder must not re-enqueue it into another code->scene cycle.");
    }
}
