using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using SceneBuilder.Editor;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Serialization;
using SceneBuilder.Core.Validation;

// b6-t3 (specs/25-typed-prefab-facades.md): the milestone's dual rename-safety guarantee at the
// LIVE Unity boundary, against the canonical Tank/Turret fixture (b6-t1). A genuine rename of a
// nested-prefab child inside Tank.prefab (LeftTurret -> Cannon, durable LocalId unchanged) fires
// the real AssetPostprocessor -> regen -> FacadeReferencePatcher chain (b5-t2/b5-t4) that rewrites
// an on-disk builder in place. Per research.md's verdict, `BuilderCompileCheck` cannot distinguish
// a valid post-rename builder from a stale one in-editor (the façade types are emitted into NO
// loaded editor assembly — the analyzer never loads in-editor), so this suite asserts the dual
// safety at the RESOLUTION boundary instead: a clean `SceneBuilderBuild.Run` with zero diagnostics
// + a preserved GlobalObjectId for the patched builder, and a located SB2203
// (`UnknownFacadeReference`) refusal for a builder hand-edited back to the stale accessor. The
// literal compile-error safety net stays HEADLESS (`FacadeGeneratorTests.StaleChain_...`, b3-t3).
// Also covers Behavior 10: a façade-registered Tank instance, reconciled scene->code by
// `SceneBuilderSync.Run`, emits the TYPED `.Instance(Prefabs.Tank)` form (b5-t5's live wiring),
// never the raw string path.
public class PrefabFacadeRenameTests
{
    private const string Dir = "Assets/GateTests/Fixtures_PrefabFacadeRename";
    private const string ScenePath = "Assets/GateTests/__PrefabFacadeRenameTemp.unity";

    private string _manifestPath;
    private bool _manifestExistedBefore;
    private byte[] _manifestBytesBefore;

    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class PrefabFacadeRenameGateScene : ISceneDefinition
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
        _manifestPath = SceneBuilderPaths.FacadeManifestPath;
        _manifestExistedBefore = File.Exists(_manifestPath);
        _manifestBytesBefore = _manifestExistedBefore ? File.ReadAllBytes(_manifestPath) : null;

        var buildersDir = SceneBuilderPaths.EnsureBuildersDirectory();
        var name = "PrefabFacadeRenameGate_" + Guid.NewGuid().ToString("N");
        _builderPath = Path.Combine(buildersDir, name + ".cs");
        _sidecarPath = Path.Combine(buildersDir, name + ".sbmap.json");

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_builderPath))
        {
            File.Delete(_builderPath);
        }
        if (File.Exists(_sidecarPath))
        {
            File.Delete(_sidecarPath);
        }

        PrefabFacadeFixture.Delete(Dir);

        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }

        if (_manifestExistedBefore)
        {
            File.WriteAllBytes(_manifestPath, _manifestBytesBefore);
        }
        else if (File.Exists(_manifestPath))
        {
            File.Delete(_manifestPath);
        }
    }

    // Renames the "LeftTurret" child of the fixture's Tank prefab to "Cannon" (LocalId unchanged)
    // via a real LoadPrefabContents/SaveAsPrefabAsset/ImportAsset cycle, firing the live
    // AssetPostprocessor -> regen -> PatchBuildersForRename chain.
    private static void RenameLeftTurretToCannon(PrefabFacadeFixture.Handles handles)
    {
        var contents = PrefabUtility.LoadPrefabContents(handles.TankPath);
        var leftTurret = contents.transform.Find("LeftTurret");
        Assert.IsNotNull(leftTurret, "Setup: fixture Tank prefab has no 'LeftTurret' child.");
        leftTurret.gameObject.name = "Cannon";
        PrefabUtility.SaveAsPrefabAsset(contents, handles.TankPath);
        PrefabUtility.UnloadPrefabContents(contents);

        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(handles.TankPath, ImportAssetOptions.ForceUpdate);
    }

    [Test]
    public void Rename_TurretChild_ReferencePatchRewritesLiveBuilder_LeavesUnchangedChainsByteIdentical()
    {
        var handles = PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();

        var original = Source(
            "        scene.Instance(Prefabs.Tank).On(t => t.LeftTurret.Barrel, b => { });\n" +
            "        scene.Instance(Prefabs.Tank).On(t => t.RightTurret.Barrel, b => { });");
        File.WriteAllText(_builderPath, original);

        RenameLeftTurretToCannon(handles);

        var patched = File.ReadAllText(_builderPath);
        var expected = original.Replace("t.LeftTurret.Barrel", "t.Cannon.Barrel");

        Assert.AreEqual(expected, patched,
            "The rename patch must rewrite ONLY the renamed segment, leaving every other byte (including the RightTurret chain) unchanged.\n" + patched);
        StringAssert.DoesNotContain("t.LeftTurret", patched, "Stale 't.LeftTurret' accessor survived the rename patch.\n" + patched);
    }

    [Test]
    public void Rename_ThenRebuildPatchedTypedBuilder_InstanceResolves_GlobalObjectIdPreserved()
    {
        var handles = PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();

        File.WriteAllText(_builderPath, Source(
            "        scene.Instance(Prefabs.Tank).On(t => t.LeftTurret.Barrel, b => { });"));

        var scene = EditorSceneManager.GetActiveScene();
        var buildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(buildResult.Diagnostics,
            "Setup build reported diagnostics: " + string.Join("; ", buildResult.Diagnostics.Select(d => d.Message)));

        var root = FindRoot(EditorSceneManager.GetActiveScene(), "Tank");
        Assert.IsNotNull(root, "Setup: Tank instance root was not created by SceneBuilderBuild.Run");
        var goidBefore = GlobalObjectId.GetGlobalObjectIdSlow(root).ToString();

        RenameLeftTurretToCannon(handles);

        var patched = File.ReadAllText(_builderPath);
        StringAssert.Contains("t.Cannon.Barrel", patched, "Builder was not patched to the renamed accessor.\n" + patched);

        var rebuildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(rebuildResult.Diagnostics,
            "Rebuild against the patched typed builder reported diagnostics: "
            + string.Join("; ", rebuildResult.Diagnostics.Select(d => d.Message)));

        var rootAfter = FindRoot(EditorSceneManager.GetActiveScene(), "Tank");
        Assert.IsNotNull(rootAfter, "Tank instance root disappeared across the rebuild");
        Assert.AreEqual(goidBefore, GlobalObjectId.GetGlobalObjectIdSlow(rootAfter).ToString(),
            "GlobalObjectId changed across the rebuild — the rename must reconcile in place, not re-instantiate");
    }

    [Test]
    public void StaleAccessorAfterRename_FailsBuildResolution_WithLocatedSB2203()
    {
        var handles = PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();

        RenameLeftTurretToCannon(handles);

        // Hand-edit the builder back to the now-stale accessor, WITHOUT going through the patcher.
        File.WriteAllText(_builderPath, Source(
            "        scene.Instance(Prefabs.Tank).On(t => t.LeftTurret.Barrel, b => { });"));

        var scene = EditorSceneManager.GetActiveScene();
        var result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        Assert.IsNotEmpty(result.Diagnostics, "A stale post-rename accessor must refuse the build, not silently succeed.");
        var diagnostic = result.Diagnostics.FirstOrDefault(d => d.Code == DiagnosticCodes.UnknownFacadeReference);
        Assert.IsNotNull(diagnostic,
            "Expected a " + DiagnosticCodes.UnknownFacadeReference + " (UnknownFacadeReference) diagnostic.\n"
            + string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        Assert.Greater(diagnostic.Line, 0, "Diagnostic must be located (Line > 0).");

        var root = FindRoot(EditorSceneManager.GetActiveScene(), "Tank");
        Assert.IsNull(root, "Scene must be untouched on a refused build.");
    }

    [Test]
    public void SceneToCodeSync_FacadeRegisteredTankInstance_EmitsTypedInstancePrefabsForm()
    {
        var handles = PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();

        File.WriteAllText(_builderPath, Source("        // no statements yet"));

        // Reconciler.HandleInstanceNode resolves a brand-new scene instance's `.Instance(...)`
        // argument from identityMap.Assets (SourcePrefabGuid -> LastKnownPath) — populating that
        // cache BEFORE reconcile runs is the adapter's own responsibility (comment at
        // ReconcilerInstances.cs), normally inherited from an earlier Build/Sync that already
        // touched this prefab. Seed it directly here so the test isolates the ONE thing under
        // test (typed vs. string emission), not that unrelated cache-population precondition.
        var seededMap = new IdentityMap
        {
            Assets = new[] { new AssetEntry { Guid = handles.TankGuid, LastKnownPath = handles.TankPath, TypeHint = "Prefab" } },
        };
        File.WriteAllText(_sidecarPath, IdentityMapJson.Serialize(seededMap));

        var tankAsset = AssetDatabase.LoadAssetAtPath<GameObject>(handles.TankPath);
        PrefabUtility.InstantiatePrefab(tankAsset, EditorSceneManager.GetActiveScene());

        // Not EmittedCodeCompiles.SyncAndAssertCompiles: per research.md, `Prefabs` is emitted into
        // NO loaded editor assembly (the façade source generator only runs INTO the real project's
        // csproj compilation, never into BuilderCompileCheck's AppDomain-only check), so an in-editor
        // compile check cannot resolve it. Assert the RESOLUTION-side contract only. Sync's own
        // internal BuilderCompileCheck (run against the SAME AppDomain-only check) therefore logs an
        // expected CS0103-on-'Prefabs' Error for this write — bracket it exactly like
        // PrefabFacadeManifestTests.DeleteTurretPrefab_... does for its own expected editor-log noise.
        var prevIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        SceneBuilderSync.SyncResult syncResult;
        try
        {
            syncResult = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        }
        finally
        {
            LogAssert.ignoreFailingMessages = prevIgnore;
        }
        Assert.IsTrue(syncResult.Changed, "Sync reported no change despite a new, unmapped facade-registered prefab instance");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains(".Instance(Prefabs.Tank)", rewritten,
            "Scene->code sync of a facade-registered Tank instance did not emit the typed Instance(Prefabs.Tank) form.\n" + rewritten);
        StringAssert.DoesNotContain($".Instance(\"{handles.TankPath}\")", rewritten,
            "Scene->code sync emitted the raw string path instead of the typed facade form.\n" + rewritten);
    }
}
