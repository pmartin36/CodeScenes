using System;
using System.IO;
using NUnit.Framework;
using SceneBuilder.Editor;
using SceneBuilder.Editor.Licensing;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Gate for the prefab auto-sync lanes wired onto SceneBuilderAutoSync's existing debounce pump: a
// watched Prefabs/*.cs edit debounces into PrefabBuildSyncTarget.Build (code->prefab, edit-in-place),
// a Prefab Mode edit debounces into PrefabBuildSyncTarget.Sync (prefab->code), both lanes stay
// independent of the scene<->code lanes, both are gated by the same IsArmed master toggle, and a
// reconcile's own .cs write never loops back into a rebuild.
public class PrefabAutoSyncTests
{
    private const string BuilderName = "PrefabAutoSyncWidget";
    private const string FixturesDir = "Assets/GateTests/Fixtures_PrefabAutoSync";
    private const string PrefabPath = FixturesDir + "/" + BuilderName + ".prefab";
    private const string DefaultPathPrefab = "Assets/SceneBuilder/" + BuilderName + ".prefab";

    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class PrefabAutoSyncWidget : IPrefabDefinition
{{
    public void Build(PrefabRoot root)
    {{
{body}
    }}
}}";

    [SetUp]
    public void SetUp()
    {
        LicenseGate.ResetToDefault();
        SceneBuilderPaths.EnsurePrefabBuildersDirectory();
        _builderPath = SceneBuilderPaths.PrefabBuilder(BuilderName);
        _sidecarPath = SceneBuilderPaths.PrefabSidecar(BuilderName);

        SceneBuilderRouter.ResetForTests();
        SceneBuilderAutoSync.ResetForTests();
        SuppressionScope.ResetForTests();

        if (!AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "Fixtures_PrefabAutoSync");
        }
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

        if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null)
        {
            AssetDatabase.DeleteAsset(PrefabPath);
        }

        if (AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.DeleteAsset(FixturesDir);
        }

        // A CREATE build with no sidecar falls back to the router's default convention path
        // (Assets/SceneBuilder/<Name>.prefab); several cases in this file exercise that path.
        if (AssetDatabase.LoadAssetAtPath<GameObject>(DefaultPathPrefab) != null)
        {
            AssetDatabase.DeleteAsset(DefaultPathPrefab);
        }
    }

    // Simulates a Prefab-Mode edit: load the persisted contents, mutate them in place, save back onto
    // the same asset path, unload, then re-import so a following read sees the change.
    private static void ApplyPrefabModeEdit(Action<GameObject> mutate)
    {
        var contents = PrefabUtility.LoadPrefabContents(PrefabPath);
        mutate(contents);
        PrefabUtility.SaveAsPrefabAsset(contents, PrefabPath);
        PrefabUtility.UnloadPrefabContents(contents);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(PrefabPath, ImportAssetOptions.ForceUpdate);
    }

    private static void WithToggleOff(Action body)
    {
        var hadKey = EditorPrefs.HasKey(SceneBuilderAutoToggle.PrefKey);
        var originalValue = EditorPrefs.GetBool(SceneBuilderAutoToggle.PrefKey, true);
        try
        {
            SceneBuilderAutoToggle.Enabled = false;
            SceneBuilderAutoSync.ApplyToggleState();
            Assert.IsFalse(SceneBuilderAutoSync.IsArmed,
                "ApplyToggleState() with the master toggle OFF must disarm the loop.");
            body();
        }
        finally
        {
            if (hadKey) EditorPrefs.SetBool(SceneBuilderAutoToggle.PrefKey, originalValue);
            else EditorPrefs.DeleteKey(SceneBuilderAutoToggle.PrefKey);
        }
    }

    [Test]
    public void AutoCodeToPrefab_ExternalBuilderEdit_UpdatesPrefabInPlace_SiblingFileIdStable()
    {
        File.WriteAllText(_builderPath, Source(
            "        root.Add(\"" + BuilderName + "\", w => { w.Add(\"A\"); w.Add(\"B\"); });"));

        var create = PrefabBuildSyncTarget.Build(_builderPath, PrefabPath, _sidecarPath);
        Assert.IsEmpty(create.Diagnostics, "Create build reported diagnostics");

        var rootBefore = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        var aBefore = rootBefore.transform.Find("A").gameObject;
        Assert.IsTrue(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(aBefore, out _, out long aFileIdBefore),
            "Could not resolve sibling A's local fileID after the create build");

        // A real external write to the builder .cs, not routed through WriteIfChanged.
        File.WriteAllText(_builderPath, Source(
            "        root.Add(\"" + BuilderName + "\", w => { w.Add(\"A\"); w.Add(\"B\").Transform(pos: (7f, 0f, 0f)); });"));

        SceneBuilderAutoSync.ExecutePrefabCodeToAsset(new[] { _builderPath });

        var rootAfter = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        var aAfter = rootAfter.transform.Find("A").gameObject;
        Assert.IsTrue(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(aAfter, out _, out long aFileIdAfter),
            "Could not resolve sibling A's local fileID after ExecutePrefabCodeToAsset");
        Assert.AreEqual(aFileIdBefore, aFileIdAfter,
            "Unchanged sibling A's local fileID changed — the prefab was rebuilt fresh instead of edited in place");

        var bAfter = rootAfter.transform.Find("B").gameObject;
        Assert.AreEqual(new Vector3(7f, 0f, 0f), bAfter.transform.localPosition,
            "ExecutePrefabCodeToAsset did not materialize the external builder edit onto the prefab");
    }

    [Test]
    public void AutoCodeToPrefab_ArmedPump_RunsExactlyOneCycle()
    {
        File.WriteAllText(_builderPath, Source(
            "        root.Add(\"" + BuilderName + "\", w => { w.Add(\"A\"); });"));
        SceneBuilderRouter.ResetForTests();

        var now = 100.0;
        SceneBuilderAutoSync.Clock = () => now;

        SceneBuilderAutoSync.NotifyPrefabSourceChanged(_builderPath);
        now += SceneBuilderAutoSync.SettleSeconds + 0.01;
        SceneBuilderAutoSync.PumpOnce(now);

        Assert.AreEqual(1, SceneBuilderAutoSync.PrefabCodeToAssetCycleCount,
            "A prefab builder .cs change, once the settle window passes, must run exactly one prefab code->asset cycle with no menu click.");
    }

    [Test]
    public void AutoCodeToPrefab_ToggleOff_FiresNothing()
    {
        File.WriteAllText(_builderPath, Source(
            "        root.Add(\"" + BuilderName + "\", w => { w.Add(\"A\"); });"));
        SceneBuilderRouter.ResetForTests();

        WithToggleOff(() =>
        {
            var now = 100.0;
            SceneBuilderAutoSync.Clock = () => now;
            SceneBuilderAutoSync.NotifyPrefabSourceChanged(_builderPath);
            now += SceneBuilderAutoSync.SettleSeconds + 0.01;
            SceneBuilderAutoSync.PumpOnce(now);

            Assert.AreEqual(0, SceneBuilderAutoSync.PrefabCodeToAssetCycleCount,
                "With auto toggled OFF, a prefab builder .cs change must not schedule a cycle.");
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath),
                "With auto toggled OFF, no prefab must be written.");
        });
    }

    [Test]
    public void WatcherDrain_RoutesPrefabCsPath_ToPrefabLane_NotSceneLane()
    {
        File.WriteAllText(_builderPath, Source(
            "        root.Add(\"" + BuilderName + "\", w => { w.Add(\"A\"); });"));
        SceneBuilderRouter.ResetForTests();

        var now = 100.0;
        SceneBuilderAutoSync.Clock = () => now;

        // EnqueueWatcherPath only queues; DrainWatcherPaths (which arms the deadline via Clock())
        // runs at the top of PumpOnce. The first pump drains + arms the deadline; the second, after
        // advancing past the settle window, is the one that fires the cycle.
        SceneBuilderAutoSync.EnqueueWatcherPath(_builderPath, WatcherChangeTypes.Changed);
        SceneBuilderAutoSync.PumpOnce(now);
        now += SceneBuilderAutoSync.SettleSeconds + 0.01;
        SceneBuilderAutoSync.PumpOnce(now);

        Assert.AreEqual(1, SceneBuilderAutoSync.PrefabCodeToAssetCycleCount,
            "A watcher event on a Prefabs/*.cs path must route to the prefab code->asset lane.");
        Assert.AreEqual(0, SceneBuilderAutoSync.CodeToSceneCycleCount,
            "A watcher event on a Prefabs/*.cs path must never route into the scene code->scene lane.");
    }

    [Test]
    public void AutoPrefabToCode_PrefabModeEdit_RewritesBuilder()
    {
        File.WriteAllText(_builderPath, Source(
            "        root.Add(\"" + BuilderName + "\", w => { w.Add(\"A\"); });"));

        var create = PrefabBuildSyncTarget.Build(_builderPath, PrefabPath, _sidecarPath);
        Assert.IsEmpty(create.Diagnostics, "Create build reported diagnostics");
        SceneBuilderRouter.ResetForTests();
        Assert.IsTrue(SceneBuilderRouter.TryRoutePrefabAsset(PrefabPath, out var route),
            "Precondition: the just-built prefab must be discoverable by its route.");

        ApplyPrefabModeEdit(contents =>
        {
            var a = contents.transform.Find("A");
            a.localPosition = new Vector3(9f, 0f, 0f);
        });

        SceneBuilderAutoSync.InPrefabModeProbe = () => true;
        SceneBuilderAutoSync.ActivePrefabRouteProbe = () => route;

        var now = 100.0;
        SceneBuilderAutoSync.Clock = () => now;
        SceneBuilderAutoSync.RouteEditorChange(Array.Empty<EntityId>());
        now += SceneBuilderAutoSync.SettleSeconds + 0.01;
        SceneBuilderAutoSync.PumpOnce(now);

        Assert.AreEqual(1, SceneBuilderAutoSync.PrefabAssetToCodeCycleCount,
            "A Prefab Mode edit, once the settle window passes, must run exactly one prefab asset->code cycle with no menu click.");
        StringAssert.Contains("9f, 0f, 0f", File.ReadAllText(_builderPath),
            "The builder .cs was not rewritten with the Prefab Mode field edit.");
    }

    [Test]
    public void ProductionDefault_InPrefabMode_DetectsLiveStageAndRoute()
    {
        File.WriteAllText(_builderPath, Source(
            "        root.Add(\"" + BuilderName + "\", w => { w.Add(\"A\"); });"));

        var create = PrefabBuildSyncTarget.Build(_builderPath, PrefabPath, _sidecarPath);
        Assert.IsEmpty(create.Diagnostics, "Create build reported diagnostics");
        SceneBuilderRouter.ResetForTests();
        Assert.IsTrue(SceneBuilderRouter.TryRoutePrefabAsset(PrefabPath, out var route),
            "Precondition: the just-built prefab must be discoverable by its route.");

        // No probe injection here: InPrefabModeProbe/ActivePrefabRouteProbe are exercised at their
        // production defaults, wired via WireDefaultExecutors/ResetForTests, not overridden per-case.
        try
        {
            PrefabStageUtility.OpenPrefab(PrefabPath);

            Assert.IsTrue(SceneBuilderAutoSync.InPrefabModeProbe(),
                "With a real Prefab Mode stage open on a built prefab, the production probe must detect it without test injection.");

            var detectedRoute = SceneBuilderAutoSync.ActivePrefabRouteProbe();
            Assert.IsNotNull(detectedRoute,
                "The production route probe must resolve the open stage's asset path to its builder route.");
            Assert.AreEqual(route.BuilderName, detectedRoute.Value.BuilderName,
                "The production route probe resolved the wrong builder for the open Prefab Mode stage.");
        }
        finally
        {
            StageUtility.GoToMainStage();
        }

        Assert.IsFalse(SceneBuilderAutoSync.InPrefabModeProbe(),
            "After returning to the main stage, the production probe must no longer report Prefab Mode.");
    }

    [Test]
    public void AutoPrefabToCode_ToggleOff_FiresNothing()
    {
        File.WriteAllText(_builderPath, Source(
            "        root.Add(\"" + BuilderName + "\", w => { w.Add(\"A\"); });"));

        var create = PrefabBuildSyncTarget.Build(_builderPath, PrefabPath, _sidecarPath);
        Assert.IsEmpty(create.Diagnostics, "Create build reported diagnostics");
        SceneBuilderRouter.ResetForTests();
        Assert.IsTrue(SceneBuilderRouter.TryRoutePrefabAsset(PrefabPath, out var route),
            "Precondition: the just-built prefab must be discoverable by its route.");

        var beforeText = File.ReadAllText(_builderPath);

        WithToggleOff(() =>
        {
            SceneBuilderAutoSync.InPrefabModeProbe = () => true;
            SceneBuilderAutoSync.ActivePrefabRouteProbe = () => route;

            var now = 100.0;
            SceneBuilderAutoSync.Clock = () => now;
            SceneBuilderAutoSync.RouteEditorChange(Array.Empty<EntityId>());
            now += SceneBuilderAutoSync.SettleSeconds + 0.01;
            SceneBuilderAutoSync.PumpOnce(now);

            Assert.AreEqual(0, SceneBuilderAutoSync.PrefabAssetToCodeCycleCount,
                "With auto toggled OFF, a Prefab Mode edit must not schedule a cycle.");
            Assert.AreEqual(beforeText, File.ReadAllText(_builderPath),
                "With auto toggled OFF, the builder .cs must be left untouched.");
        });
    }

    [Test]
    public void AutoPrefabToCode_OwnWrite_DoesNotLoopBackToBuild()
    {
        File.WriteAllText(_builderPath, Source(
            "        root.Add(\"" + BuilderName + "\", w => { w.Add(\"A\"); });"));
        SceneBuilderRouter.ResetForTests();

        // Simulates a reconcile's own write to the builder .cs — WriteIfChanged records (path, hash)
        // in SuppressionScope's own-write registry, the same loop-break the scene source lane uses.
        SceneBuilderPaths.WriteIfChanged(_builderPath, Source(
            "        root.Add(\"" + BuilderName + "\", w => { w.Add(\"A\"); w.Add(\"B\"); });"));

        var now = 100.0;
        SceneBuilderAutoSync.Clock = () => now;
        SceneBuilderAutoSync.NotifyPrefabSourceChanged(_builderPath);
        now += SceneBuilderAutoSync.SettleSeconds + 0.01;
        SceneBuilderAutoSync.PumpOnce(now);

        Assert.AreEqual(0, SceneBuilderAutoSync.PrefabCodeToAssetCycleCount,
            "A prefab builder .cs write already recorded in SuppressionScope's own-write registry must be dropped, never scheduling a prefab code->asset cycle.");
    }
}
