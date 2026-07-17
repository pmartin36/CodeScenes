using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SceneBuilder.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Gate for b7-t2, the whole-loop integration (spec checklist #3 no-ping-pong, #11 toggle/manual):
// proves the CROSS-BUCKET wiring — a suppressed scene write drops its own echo, a registered source
// write drops its own watcher echo, the loop settles instead of oscillating, and the master toggle
// gates the whole loop while the retained manual menu items still work and the toggle persists.
// Drives the real production write paths (SceneBuilderBuild.Run / SceneBuilderAutoSync.ExecuteSceneToCode
// / BuildDemo / SyncDemo) directly, following AutoSceneToCodeTests'/AutoConflictTests' setup pattern —
// this file does NOT re-test b2-t1's own ref-count/time-bound internals (SuppressionScopeTests already
// does), only that the real write seams and the b4-t1 pump agree with each other.
public class AutoIntegrationTests
{
    private const string ScenePath = "Assets/GateTests/__AutoIntegrationTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class AutoIntegrationScene : ISceneDefinition
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
        _dir = Path.Combine(Path.GetTempPath(), "sb_integration_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "AutoIntegrationScene.cs");
        _sidecarPath = Path.Combine(_dir, "AutoIntegrationScene.sbmap.json");

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

    // (a1) checklist #3: a real auto-Build write happens inside SuppressionScope (b2-t1's own internal
    // scope, at SceneBuilderBuild.cs's write chokepoint). Wrapping that call in an OUTER scope and
    // simulating the ObjectChangeEvents echo it would raise (NotifySceneChanged), synchronously while
    // still inside the (ref-counted, still-open) suppression window, must never schedule a scene->code
    // cycle — the write-seam guard (b2-t1) and the pump's drop check (b4-t1) must agree.
    [Test]
    public void NoPingPong_AutoBuildSceneWrite_SuppressesSceneToCodeEcho()
    {
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Alpha\");"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();

        using (SuppressionScope.SuppressScene())
        {
            // Nests inside the Run's own internal SuppressionScope.SuppressScene() (ref-counted), so
            // suppression stays engaged for the whole block, matching the real synchronous-echo timing.
            SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

            var alpha = FindRoot(EditorSceneManager.GetActiveScene(), "Alpha");
            Assert.IsNotNull(alpha, "Precondition: the auto-Build must have created Alpha.");

            SceneBuilderAutoSync.NotifySceneChanged(new[] { alpha.GetEntityId() });
        }

        var now = 100.0;
        SceneBuilderAutoSync.Clock = () => now;
        now += SceneBuilderAutoSync.SettleSeconds + 0.01;
        SceneBuilderAutoSync.PumpOnce(now);

        Assert.AreEqual(0, SceneBuilderAutoSync.SceneToCodeCycleCount,
            "An auto-Build's own scene write, echoed while its scene-suppression scope is open, must " +
            "never schedule a scene->code cycle (checklist #3, no ping-pong).");
    }

    // (a2) checklist #3: a real scene->code write (ExecuteSceneToCode -> SceneBuilderSync.Run ->
    // WriteIfChanged) records (path, hash) in SuppressionScope's own-write registry. Notifying the
    // watcher's real signal (NotifySourceChanged) for that exact write must be recognized as our own
    // and never schedule a code->scene cycle.
    [Test]
    public void NoPingPong_AutoSyncSourceWrite_RegistryDropsCodeToSceneEcho()
    {
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Alpha\");"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var alpha = FindRoot(EditorSceneManager.GetActiveScene(), "Alpha");
        Assert.IsNotNull(alpha, "Alpha was not created by SceneBuilderBuild.Run.");

        alpha.transform.position = new Vector3(1f, 2f, 3f);
        SceneBuilderAutoSync.ExecuteSceneToCode(new[] { alpha.GetEntityId() });

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("(1f, 2f, 3f)", rewritten,
            "Precondition: the scene->code executor must actually have patched the source.\n" + rewritten);

        // The watcher's real signal for the write the executor just made.
        SceneBuilderAutoSync.NotifySourceChanged(_builderPath);

        var now = 100.0;
        SceneBuilderAutoSync.Clock = () => now;
        now += SceneBuilderAutoSync.SettleSeconds + 0.01;
        SceneBuilderAutoSync.PumpOnce(now);

        Assert.AreEqual(0, SceneBuilderAutoSync.CodeToSceneCycleCount,
            "A source write produced by the scene->code executor's own reconcile must be recognized " +
            "as an own-write via the registry and never schedule a code->scene cycle (checklist #3, no ping-pong).");
    }

    // (a3) checklist #3: after a genuine scene->code write, a second cold cycle with no further change
    // must be a byte-stable no-op — the loop settles in a bounded number of cycles, it does not oscillate.
    [Test]
    public void NoPingPong_LoopSettles_SecondSyncNoOp()
    {
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Alpha\");"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var alpha = FindRoot(EditorSceneManager.GetActiveScene(), "Alpha");
        Assert.IsNotNull(alpha, "Alpha was not created by SceneBuilderBuild.Run.");

        alpha.transform.position = new Vector3(4f, 5f, 6f);
        SceneBuilderAutoSync.ExecuteSceneToCode(new[] { alpha.GetEntityId() });

        var builderAfterFirst = File.ReadAllText(_builderPath);
        var sidecarAfterFirst = File.ReadAllText(_sidecarPath);

        SceneBuilderAutoSync.ExecuteSceneToCode(Array.Empty<EntityId>());

        Assert.AreEqual(builderAfterFirst, File.ReadAllText(_builderPath),
            "A second scene->code cycle with no further change must not rewrite the builder source " +
            "(bounded settle, no oscillation).");
        Assert.AreEqual(sidecarAfterFirst, File.ReadAllText(_sidecarPath),
            "A second scene->code cycle with no further change must not rewrite the sidecar " +
            "(bounded settle, no oscillation).");
    }

    // (b) checklist #11: the master toggle OFF disarms the WHOLE loop (a scene edit produces zero
    // cycles) while the retained CodeScenes/Build DemoScene and CodeScenes/Sync DemoScene menu items —
    // driven exactly as the menu would, via their public static entry points — still perform their
    // work against the real default DemoScene fixture. Toggling back ON persists across a re-read
    // (simulated restart, EditorPrefs is never cached). Backs up/restores every real path this test
    // touches (EditorPrefs key, SceneBuilders/DemoScene.*, Assets/SceneBuilder/), mirroring
    // AutoToggleTests' hermetic restore for real machine/project-global state.
    [Test]
    public void ToggleOff_DisarmsLoop_ManualMenuItemsStillWork_PersistsOn()
    {
        var prefKey = SceneBuilderAutoToggle.PrefKey;
        var hadKey = EditorPrefs.HasKey(prefKey);
        var originalValue = EditorPrefs.GetBool(prefKey, true);

        var demoBuilderPath = SceneBuilderPaths.Builder("DemoScene");
        var demoSidecarPath = SceneBuilderPaths.Sidecar("DemoScene");
        const string DemoScenePath = "Assets/SceneBuilder/DemoScene.unity";
        const string DemoSceneFolder = "Assets/SceneBuilder";

        var hadDemoBuilder = File.Exists(demoBuilderPath);
        var originalDemoBuilder = hadDemoBuilder ? File.ReadAllText(demoBuilderPath) : null;
        var hadDemoSidecar = File.Exists(demoSidecarPath);
        var originalDemoSidecar = hadDemoSidecar ? File.ReadAllText(demoSidecarPath) : null;
        var hadDemoScene = File.Exists(DemoScenePath);
        var hadDemoFolder = AssetDatabase.IsValidFolder(DemoSceneFolder);

        try
        {
            // --- toggle OFF disarms the whole loop ---
            SceneBuilderAutoToggle.Enabled = false;
            SceneBuilderAutoSync.ApplyToggleState();
            Assert.IsFalse(SceneBuilderAutoSync.IsArmed, "Toggle OFF must disarm the loop.");

            var go = new GameObject("Target");
            try
            {
                var now = 100.0;
                SceneBuilderAutoSync.Clock = () => now;
                SceneBuilderAutoSync.NotifySceneChanged(new[] { go.GetEntityId() });
                now += SceneBuilderAutoSync.SettleSeconds + 0.01;
                SceneBuilderAutoSync.PumpOnce(now);

                Assert.AreEqual(0, SceneBuilderAutoSync.SceneToCodeCycleCount,
                    "With auto toggled OFF, a scene edit must produce zero sync cycles.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }

            // --- retained manual menu items still work while disarmed ---
            if (!hadDemoFolder)
            {
                AssetDatabase.CreateFolder("Assets", "SceneBuilder");
            }
            SceneBuilderPaths.EnsureBuildersDirectory();
            File.WriteAllText(demoBuilderPath, Source("        scene.Add(\"ManualObject\");"));

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            SceneBuilderBuild.BuildDemo();

            var manual = FindRoot(EditorSceneManager.GetActiveScene(), "ManualObject");
            Assert.IsNotNull(manual,
                "CodeScenes/Build DemoScene must still materialize the object while auto is toggled OFF.");

            manual.transform.position = new Vector3(9f, 8f, 7f);
            SceneBuilderSync.SyncDemo();

            var syncedSource = File.ReadAllText(demoBuilderPath);
            StringAssert.Contains("(9f, 8f, 7f)", syncedSource,
                "CodeScenes/Sync DemoScene must still patch the source while auto is toggled OFF.\n" + syncedSource);

            // --- toggle back ON persists across a re-read (simulated restart) ---
            SceneBuilderAutoToggle.Enabled = true;
            Assert.IsTrue(SceneBuilderAutoToggle.Enabled,
                "Toggling auto back ON must persist across a re-read of the EditorPrefs key (simulated restart).");
        }
        finally
        {
            if (hadKey)
            {
                EditorPrefs.SetBool(prefKey, originalValue);
            }
            else
            {
                EditorPrefs.DeleteKey(prefKey);
            }

            if (hadDemoBuilder)
            {
                File.WriteAllText(demoBuilderPath, originalDemoBuilder);
            }
            else if (File.Exists(demoBuilderPath))
            {
                File.Delete(demoBuilderPath);
            }

            if (hadDemoSidecar)
            {
                File.WriteAllText(demoSidecarPath, originalDemoSidecar);
            }
            else if (File.Exists(demoSidecarPath))
            {
                File.Delete(demoSidecarPath);
            }

            if (!hadDemoScene && File.Exists(DemoScenePath))
            {
                AssetDatabase.DeleteAsset(DemoScenePath);
            }

            if (!hadDemoFolder && AssetDatabase.IsValidFolder(DemoSceneFolder))
            {
                AssetDatabase.DeleteAsset(DemoSceneFolder);
            }

            SceneBuilderAutoSync.ApplyToggleState();
        }
    }
}
