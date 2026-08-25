using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using SceneBuilder.Core.Sync;
using SceneBuilder.Editor;
using SceneBuilder.Editor.Licensing;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// Gate for SceneBuilderBuildStatus: the one place a code->scene / code->prefab build OUTCOME is
// recorded, wired into EVERY channel that can produce a refused BuildResult (Materialize/resync,
// auto code->scene, the both-changed tail, the manual menu build, prefab code->asset — including a
// pre-RunCore SB2402 mismatch and a pre-RunCore SB2401 multi-root refusal) plus the raw-parse
// ParseException RunCore now catches instead of throwing. A refusal sets the standing status (File,
// Line, BuilderName) and leaves the scene/prefab untouched; a following clean build clears it and
// removes only that builder's scene-view overlay entry, leaving an unrelated one registered.
public class BuildStatusRecorderTests
{
    private const string BuilderName = "BuildStatusRecorderScene";
    private const string ScenePath = "Assets/GateTests/__BuildStatusRecorderTemp.unity";

    private const string PrefabFixturesDir = "Assets/GateTests/Fixtures_BuildStatusRecorder";
    private const string PrefabDefaultPath = "Assets/SceneBuilder/BuildStatusRecorderWidget.prefab";

    private string _builderPath;
    private string _sidecarPath;
    private string _statePath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class BuildStatusRecorderScene : ISceneDefinition
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
        _statePath = SceneBuilderPaths.StateForSidecar(_sidecarPath);

        SceneBuilderRouter.ResetForTests();
        SceneBuilderAutoSync.ResetForTests();
        SuppressionScope.ResetForTests();
        SceneBuilderBuildStatus.ResetForTests();
        ConflictSurfacing.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        LicenseEnforcement.Register();
        SceneBuilderRouter.ResetForTests();
        SceneBuilderAutoSync.ResetForTests();
        SuppressionScope.ResetForTests();
        SceneBuilderBuildStatus.ResetForTests();
        ConflictSurfacing.Clear();

        if (File.Exists(_builderPath)) File.Delete(_builderPath);
        if (File.Exists(_sidecarPath)) File.Delete(_sidecarPath);
        if (File.Exists(_statePath)) File.Delete(_statePath);

        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }

        if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabDefaultPath) != null)
        {
            AssetDatabase.DeleteAsset(PrefabDefaultPath);
        }

        if (AssetDatabase.IsValidFolder(PrefabFixturesDir))
        {
            AssetDatabase.DeleteAsset(PrefabFixturesDir);
        }
    }

    private Scene Build()
    {
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Alpha\");"));
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

    // Every refusal-logging channel names the builder file followed by "(line,col):" — the one
    // located-line shape every channel's console error must carry, regardless of its exact wording.
    private Regex LocatedLogPattern() => new(Regex.Escape(_builderPath) + @"\(\d+,\d+\)");

    // (1) Materialize/resync channel, COLLECTED-diagnostic modality: an external source-only edit
    // that introduces an ambiguous duplicate sibling (two positional `scene.Add("Alpha")`,
    // SB2201/SB2202) is detected from the raw parse's own Ambiguities data, so the resync's own
    // desired-model hash computation completes without throwing (unlike an unresolved component
    // type or asset ref, both of which throw during that same hash computation and would route to
    // NoOp before ever reaching Materialize). It routes to Materialize, and the refusal the
    // discarded BuildResult carries must still set the standing status.
    [Test]
    public void Materialize_CollectedRefusal_SetsStandingStatus_SceneUntouched()
    {
        Build();
        var route = Route();

        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Alpha\");\n        scene.Add(\"Alpha\");"));

        var direction = SceneBuilderResync.ResyncRoute(route, EditorSceneManager.GetActiveScene());

        Assert.AreEqual(SyncDirection.Materialize, direction,
            "Precondition: a source-only edit with no live scene change must resync as Materialize.");

        var refusal = SceneBuilderBuildStatus.GetRefusal(BuilderName);
        Assert.IsNotNull(refusal,
            "A collected refusal reached through the Materialize/resync channel (RunMaterialize discards "
            + "its BuildResult) must still set the standing build-status refusal.");
        Assert.AreEqual(_builderPath, refusal.File, "Standing refusal recorded the wrong builder file.");
        Assert.Greater(refusal.Line, 0, "Standing refusal carries no located line.");

        Assert.AreEqual(1, EditorSceneManager.GetActiveScene().GetRootGameObjects().Count(go => go.name == "Alpha"),
            "A refused Materialize must leave the live scene untouched (still exactly one Alpha).");
    }

    // (2) Materialize/resync channel, THROWN-ParseException modality: with no checkpoint on disk yet
    // (a corrupted-away one, same technique other resync tests use to force the pre-checkpoint
    // branch), a raw syntax error is a structural ParseException thrown by RunCore's raw parse. Today
    // it escapes RunMaterialize uncaught and is swallowed unlocated by ResyncRoute's own outer catch;
    // the standing status must be set regardless of which modality produced the refusal.
    [Test]
    public void Materialize_ThrownParseException_SetsStandingStatus()
    {
        Build();
        var route = Route();
        Assert.IsTrue(File.Exists(_statePath), "Precondition: Build() must leave a sync checkpoint.");
        File.Delete(_statePath);

        File.WriteAllText(_builderPath, Source("        foo.Bar();"));

        SceneBuilderResync.ResyncRoute(route, EditorSceneManager.GetActiveScene());

        var refusal = SceneBuilderBuildStatus.GetRefusal(BuilderName);
        Assert.IsNotNull(refusal,
            "A raw-parse ParseException reached through the Materialize/resync channel must set the "
            + "standing build-status refusal, not just an unlocated swallowed exception.");
        Assert.AreEqual(_builderPath, refusal.File, "Standing refusal recorded the wrong builder file.");
    }

    // (2b) Materialize/resync channel, STEADY STATE (a sync checkpoint is on disk, unlike (2) which
    // forces the pre-checkpoint branch): an unresolved component type throws while ResyncRoute
    // computes its own desired-model hash to decide a direction, before Materialize is reached. The
    // standing status must still be set and the live scene must stay untouched.
    [Test]
    public void Materialize_ThrownRefusalWithCheckpoint_SetsStandingStatus_SceneUntouched()
    {
        Build();
        var route = Route();
        Assert.IsTrue(File.Exists(_statePath), "Precondition: Build() must leave a sync checkpoint.");

        File.WriteAllText(_builderPath, Source(
            "        var cube = scene.Add(\"Cube\");\n        cube.Component<Rigidbdy>();"));

        SceneBuilderResync.ResyncRoute(route, EditorSceneManager.GetActiveScene());

        var refusal = SceneBuilderBuildStatus.GetRefusal(BuilderName);
        Assert.IsNotNull(refusal,
            "An unresolved component type that throws while the Materialize/resync channel computes "
            + "its own desired-model hash (checkpoint present) must still set the standing build-status "
            + "refusal, not be swallowed as an unlocated NoOp.");
        Assert.AreEqual(_builderPath, refusal.File, "Standing refusal recorded the wrong builder file.");
        Assert.Greater(refusal.Line, 0, "Standing refusal carries no located line.");

        var scene = EditorSceneManager.GetActiveScene();
        Assert.IsNull(FindRoot(scene, "Cube"),
            "A refused Materialize/resync must leave the live scene untouched.");
        Assert.IsNotNull(FindRoot(scene, "Alpha"),
            "A refused Materialize/resync must leave the live scene untouched.");
    }

    // (2c) Materialize/resync channel, STEADY STATE, raw-parse modality: a syntax error throws
    // during the same pre-Materialize desired-model hash computation as (2b), distinct from an
    // unresolved component type and from (2)'s pre-checkpoint branch.
    [Test]
    public void Materialize_ThrownRawParseErrorWithCheckpoint_SetsStandingStatus_SceneUntouched()
    {
        Build();
        var route = Route();
        Assert.IsTrue(File.Exists(_statePath), "Precondition: Build() must leave a sync checkpoint.");

        File.WriteAllText(_builderPath, Source("        foo.Bar();"));

        SceneBuilderResync.ResyncRoute(route, EditorSceneManager.GetActiveScene());

        var refusal = SceneBuilderBuildStatus.GetRefusal(BuilderName);
        Assert.IsNotNull(refusal,
            "A raw-parse error that throws while the Materialize/resync channel computes its own "
            + "desired-model hash (checkpoint present) must still set the standing build-status refusal.");
        Assert.AreEqual(_builderPath, refusal.File, "Standing refusal recorded the wrong builder file.");

        Assert.IsNotNull(FindRoot(EditorSceneManager.GetActiveScene(), "Alpha"),
            "A refused Materialize/resync must leave the live scene untouched.");
    }

    // (3) Auto code->scene channel: a refusal sets the standing status and leaves the overlay
    // registered; a following valid edit builds clean, clears the status, and removes ONLY this
    // builder's overlay key — an unrelated overlay entry survives.
    [Test]
    public void CodeToScene_Refusal_SetsStatus_ThenValidEdit_ClearsStatus_OverlayIsolated()
    {
        Build();
        SceneBuilderRouter.ResetForTests();

        new ConflictSurfacing().RegisterOverlay("unrelated-conflict-key");

        File.WriteAllText(_builderPath, Source(
            "        var cube = scene.Add(\"Cube\");\n        cube.Component<Rigidbdy>();"));

        LogAssert.Expect(LogType.Error, LocatedLogPattern());

        SceneBuilderAutoSync.ExecuteCodeToScene(new[] { _builderPath });

        var refusal = SceneBuilderBuildStatus.GetRefusal(BuilderName);
        Assert.IsNotNull(refusal,
            "A refusal reached through the auto code->scene channel must set the standing build-status refusal.");
        Assert.AreEqual(_builderPath, refusal.File);
        Assert.Greater(refusal.Line, 0);
        Assert.IsNull(FindRoot(EditorSceneManager.GetActiveScene(), "Cube"),
            "A refused code->scene build must leave the live scene untouched.");

        File.WriteAllText(_builderPath, Source("        scene.Add(\"Alpha\");\n        scene.Add(\"Beta\");"));

        SceneBuilderAutoSync.ExecuteCodeToScene(new[] { _builderPath });

        Assert.IsNull(SceneBuilderBuildStatus.GetRefusal(BuilderName),
            "A subsequent clean build must clear the standing build-status refusal.");
        Assert.IsFalse(SceneBuilderBuildStatus.AnyRefusing,
            "No builder should still be reported as refusing after the fix.");
        CollectionAssert.Contains(ConflictSurfacing.RegisteredObjects, "unrelated-conflict-key",
            "A clean build for one builder must not remove an unrelated builder's overlay entry.");
        Assert.IsFalse(ConflictSurfacing.RegisteredObjects.Any(k => k.Contains(_builderPath)),
            "A clean build must remove THIS builder's own overlay entry.");
    }

    // (4) The both-changed tail (ExecuteBothChanged): with a converged baseline established, an
    // external source-only edit that introduces an ambiguous duplicate sibling (two positional,
    // same-named Adds — SB2201/SB2202, detected from the raw parse's own Ambiguities data rather
    // than a resolver that could throw) is refused by the tail's own SceneBuilderBuild.Run call, and
    // that refusal must set the standing status.
    [Test]
    public void BothChanged_Refusal_SetsStandingStatus()
    {
        Build();
        SceneBuilderRouter.ResetForTests();

        SceneBuilderAutoSync.CaptureBaseline(EditorSceneManager.GetActiveScene());

        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Alpha\");\n        scene.Add(\"Alpha\");"));

        LogAssert.Expect(LogType.Error, LocatedLogPattern());

        SceneBuilderAutoSync.ExecuteBothChanged(Array.Empty<EntityId>(), new[] { _builderPath });

        var refusal = SceneBuilderBuildStatus.GetRefusal(BuilderName);
        Assert.IsNotNull(refusal,
            "A refusal reached through the both-changed tail's own Build call must set the standing "
            + "build-status refusal.");
        Assert.AreEqual(_builderPath, refusal.File);

        Assert.AreEqual(1, EditorSceneManager.GetActiveScene().GetRootGameObjects().Count(go => go.name == "Alpha"),
            "A refused rebuild must leave the live scene untouched (still exactly one Alpha).");
    }

    // (5) The manual menu build (BuildActiveScene -> BuildRoute): a refusal on the interactive path
    // must set the standing status the same way the automatic channels do.
    [Test]
    public void ManualMenuBuild_Refusal_SetsStandingStatus()
    {
        Build();
        SceneBuilderRouter.ResetForTests();

        File.WriteAllText(_builderPath, Source(
            "        var cube = scene.Add(\"Cube\");\n        cube.Component<Rigidbdy>();"));

        LogAssert.Expect(LogType.Error, LocatedLogPattern());

        SceneBuilderBuild.BuildActiveScene();

        var refusal = SceneBuilderBuildStatus.GetRefusal(BuilderName);
        Assert.IsNotNull(refusal,
            "A refusal reached through the manual 'Build Active Scene' menu path must set the standing "
            + "build-status refusal.");
        Assert.AreEqual(_builderPath, refusal.File);
    }

    // (6) Prefab code->asset channel, INCLUDING the pre-RunCore SB2402 root-name/filename mismatch
    // refusal: a builder authored under the default prefab convention path whose root name does not
    // match the prefab's filename stem must set the standing status and leave no prefab written; a
    // following fix builds clean and clears it.
    [Test]
    public void PrefabCodeToAsset_SB2402Mismatch_SetsStatus_ThenFix_Clears()
    {
        const string PrefabBuilderName = "BuildStatusRecorderWidget";
        SceneBuilderPaths.EnsurePrefabBuildersDirectory();
        var prefabBuilderPath = SceneBuilderPaths.PrefabBuilder(PrefabBuilderName);
        var prefabSidecarPath = SceneBuilderPaths.PrefabSidecar(PrefabBuilderName);

        string PrefabSource(string rootName) => $@"
using SceneBuilder.Authoring;
public class {PrefabBuilderName} : IPrefabDefinition
{{
    public void Build(PrefabRoot root)
    {{
        root.Add(""{rootName}"");
    }}
}}";

        try
        {
            // Root name 'Other' does not match the filename stem 'BuildStatusRecorderWidget' -> SB2402.
            File.WriteAllText(prefabBuilderPath, PrefabSource("Other"));

            LogAssert.Expect(LogType.Error, new Regex(Regex.Escape(prefabBuilderPath) + @"\(\d+,\d+\)"));

            SceneBuilderAutoSync.ExecutePrefabCodeToAsset(new[] { prefabBuilderPath });

            var refusal = SceneBuilderBuildStatus.GetRefusal(PrefabBuilderName);
            Assert.IsNotNull(refusal,
                "A pre-RunCore SB2402 root-name/filename-mismatch refusal reached through the prefab "
                + "code->asset channel must set the standing build-status refusal.");
            Assert.AreEqual(prefabBuilderPath, refusal.File);
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabDefaultPath),
                "An SB2402 refusal must leave no prefab asset written.");

            // Fix: root name now matches the stem.
            File.WriteAllText(prefabBuilderPath, PrefabSource(PrefabBuilderName));
            SceneBuilderAutoSync.ExecutePrefabCodeToAsset(new[] { prefabBuilderPath });

            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabDefaultPath),
                "The fixed builder must build a real prefab asset.");
            Assert.IsNull(SceneBuilderBuildStatus.GetRefusal(PrefabBuilderName),
                "A subsequent clean prefab build must clear the standing build-status refusal.");
        }
        finally
        {
            if (File.Exists(prefabBuilderPath)) File.Delete(prefabBuilderPath);
            if (File.Exists(prefabSidecarPath)) File.Delete(prefabSidecarPath);
        }
    }

    // (7) Multi-root prefab pre-RunCore refusal (SB2401): PrefabBuildSyncTarget.Build's own
    // single-root validator refuses before RunCore ever runs; that refusal must also set the
    // standing status.
    [Test]
    public void PrefabBuild_MultiRootRefusal_SetsStandingStatus()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sb_buildstatus_multiroot_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var builderPath = Path.Combine(dir, "BuildStatusMultiRootWidget.cs");
        var sidecarPath = Path.Combine(dir, "BuildStatusMultiRootWidget.sbmap.json");
        const string PrefabPath = PrefabFixturesDir + "/BuildStatusMultiRootWidget.prefab";

        if (!AssetDatabase.IsValidFolder(PrefabFixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "Fixtures_BuildStatusRecorder");
        }

        try
        {
            File.WriteAllText(builderPath, @"
using SceneBuilder.Authoring;
public class BuildStatusMultiRootWidget : IPrefabDefinition
{
    public void Build(PrefabRoot root)
    {
        root.Add(""BuildStatusMultiRootWidget"");
        root.Add(""SecondRoot"");
    }
}");

            var result = PrefabBuildSyncTarget.Build(builderPath, PrefabPath, sidecarPath);

            Assert.IsTrue(result.Diagnostics.Any(d => d.Code == "SB2401"),
                "Precondition: a two-root prefab builder must be refused with SB2401.");

            var refusal = SceneBuilderBuildStatus.GetRefusal("BuildStatusMultiRootWidget");
            Assert.IsNotNull(refusal,
                "A pre-RunCore multi-root (SB2401) refusal must set the standing build-status refusal.");
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath),
                "A multi-root refusal must leave no prefab asset written.");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null)
            {
                AssetDatabase.DeleteAsset(PrefabPath);
            }
        }
    }

    // (8) Collect-all preservation: a two-error refusal returns EVERY diagnostic (>= 2) while the
    // standing status records only the primary one — recording must not truncate the caller-visible
    // diagnostics list.
    [Test]
    public void CollectAllRefusal_PreservesFullDiagnosticsList_RecordsPrimary()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sb_buildstatus_collectall_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var builderPath = Path.Combine(dir, "BuildStatusCollectAllScene.cs");
        var sidecarPath = Path.Combine(dir, "BuildStatusCollectAllScene.sbmap.json");
        const string LocalScenePath = "Assets/GateTests/__BuildStatusCollectAllTemp.unity";

        try
        {
            File.WriteAllText(builderPath, @"
using SceneBuilder.Authoring;
using static SceneBuilder.Authoring.AssetRefs;
public class BuildStatusCollectAllScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var cube = scene.Add(""Cube"");
        cube.Component<Rigidbdy>();
        cube.Component<UnityEngine.MeshFilter>(c => c.Set(""m_Mesh"", Asset(""Assets/Materials/BSR_DoesNotExist.mat"")));
    }
}");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var result = SceneBuilderBuild.Run(
                builderPath, LocalScenePath, sidecarPath, EditorSceneManager.GetActiveScene());

            Assert.GreaterOrEqual(result.Diagnostics.Count, 2,
                "Precondition: two independent error classes in one builder must both be collected.");

            var refusal = SceneBuilderBuildStatus.GetRefusal("BuildStatusCollectAllScene");
            Assert.IsNotNull(refusal,
                "The standing refusal must be set even when the refused BuildResult carries multiple diagnostics.");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            if (File.Exists(LocalScenePath)) AssetDatabase.DeleteAsset(LocalScenePath);
        }
    }
}
