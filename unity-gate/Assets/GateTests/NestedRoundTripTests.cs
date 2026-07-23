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

// m-nested-props b7-t2 (specs/24-nested-prefab-overrides.md): the product-level round-trip for a
// nested override FIRST captured by a scene->code Sync (the "first-author bootstrap" — spec
// language), against the canonical Tank/Turret fixture (b6-t1). Proves NestedOverrideBootstrap's key
// resolution (research.md): without it, a REBUILD or a Sync compares the parsed desired target
// (SubKey always (0,0) — source never carries a GUID/fileID) against the live snapshot's REAL
// SubKey, a mismatch that manifests as a spurious [SetInstanceOverride(0,0), RevertInstanceOverride
// (realKey)] pair on rebuild (ERASES the override) or a spurious drop+re-append on sync.
public class NestedRoundTripTests
{
    private const string Dir = "Assets/GateTests/Fixtures_NestedRoundTrip";
    private const string ScenePath = "Assets/GateTests/__NestedRoundTripTemp.unity";

    private string _manifestPath;
    private bool _manifestExistedBefore;
    private byte[] _manifestBytesBefore;

    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class NestedRoundTripGateScene : ISceneDefinition
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
        var name = "NestedRoundTripGate_" + Guid.NewGuid().ToString("N");
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
    // AssetPostprocessor -> regen -> PatchBuildersForRename chain (mirrors PrefabFacadeRenameTests).
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

    private static SceneBuilderSync.SyncResult SyncIgnoringFacadeCompileNoise(string builderPath, string sidecarPath, Scene scene)
    {
        var prevIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        try
        {
            return SceneBuilderSync.Run(builderPath, sidecarPath, scene);
        }
        finally
        {
            LogAssert.ignoreFailingMessages = prevIgnore;
        }
    }

    // Builds a bare `.Instance(Prefabs.Tank)`, live-authors a nested Barrel.Light.intensity override
    // in Unity (the real user action — right-click is out of scope headlessly, this is its data
    // effect: RecordPrefabInstancePropertyModifications), then Syncs so the FIRST-author bootstrap
    // captures the typed `.On(sel => sel.LeftTurret.Barrel, ...)` into source. Returns the live Barrel.
    private Transform SetUpTankWithSyncedNestedOverride(PrefabFacadeFixture.Handles handles)
    {
        File.WriteAllText(_builderPath, Source("        scene.Instance(Prefabs.Tank);"));

        var scene = EditorSceneManager.GetActiveScene();
        var buildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(buildResult.Diagnostics,
            "Setup build reported diagnostics: " + string.Join("; ", buildResult.Diagnostics.Select(d => d.Message)));

        var root = FindRoot(EditorSceneManager.GetActiveScene(), "Tank");
        Assert.IsNotNull(root, "Setup: Tank instance root was not created");
        var barrel = root.transform.Find("LeftTurret/Barrel");
        Assert.IsNotNull(barrel, "Setup: LeftTurret/Barrel not found");
        var light = barrel.GetComponent<Light>();
        light.intensity = 4f;
        PrefabUtility.RecordPrefabInstancePropertyModifications(light);
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

        var syncResult = SyncIgnoringFacadeCompileNoise(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(syncResult.Changed, "Setup: Sync did not capture the live nested override into source");

        var synced = File.ReadAllText(_builderPath);
        StringAssert.Contains("sel => sel.LeftTurret.Barrel", synced,
            "Setup: Sync did not emit the TYPED nested selector.\n" + synced);

        return FindRoot(EditorSceneManager.GetActiveScene(), "Tank").transform.Find("LeftTurret/Barrel");
    }

    [Test]
    public void Rebuild_NestedOverride_NotErased_Converges()
    {
        var handles = PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();

        var barrel = SetUpTankWithSyncedNestedOverride(handles);
        var root = barrel.transform.root.gameObject;
        Assert.AreEqual(4f, barrel.GetComponent<Light>().intensity, 0.0001f,
            "Setup: live override was not 4f before rebuild");
        var goidBefore = GlobalObjectId.GetGlobalObjectIdSlow(root).ToString();

        var rebuildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(rebuildResult.Diagnostics,
            "Rebuild reported diagnostics: " + string.Join("; ", rebuildResult.Diagnostics.Select(d => d.Message)));

        var rootAfter = FindRoot(EditorSceneManager.GetActiveScene(), "Tank");
        Assert.IsNotNull(rootAfter, "Tank instance root disappeared on rebuild");
        var barrelAfter = rootAfter.transform.Find("LeftTurret/Barrel");
        Assert.IsNotNull(barrelAfter, "LeftTurret/Barrel disappeared on rebuild");
        Assert.AreEqual(4f, barrelAfter.GetComponent<Light>().intensity, 0.0001f,
            "REBUILD erased the nested override — the desired target's SubKey must be stamped from the "
            + "live sub-object BEFORE materialize, or materialize sees [Set(0,0), Revert(realKey)] and "
            + "removes the override it just re-applied.");
        Assert.AreEqual(goidBefore, GlobalObjectId.GetGlobalObjectIdSlow(rootAfter).ToString(),
            "Rebuild must not re-instantiate the Tank instance");
    }

    // NOTE: checklist #8 (right-click Revert on a nested override -> Sync drops the op from `.On`
    // and drops the now-empty `.On`) is NOT covered here — verified empirically to already PASS
    // against current (pre-b7-t2) code: when the live override is fully reverted, the snapshot has
    // NO override at that childPath, so the (0,0)-keyed model entry is dropped with nothing
    // mismatched to spuriously re-append. It only regresses in the "override still present, target
    // mismatched" case, which the tests above (rebuild / rename) already cover. Not re-asserted here
    // per the RED-only rule (a currently-passing assertion isn't a valid RED test).

    [Test]
    public void Rename_Turret_KeepsOverride_SubKeyStable()
    {
        var handles = PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();

        var barrelBefore = SetUpTankWithSyncedNestedOverride(handles);
        Assert.AreEqual(4f, barrelBefore.GetComponent<Light>().intensity, 0.0001f,
            "Setup: live override was not 4f before rename");
        var goidBarrelBefore = GlobalObjectId.GetGlobalObjectIdSlow(barrelBefore.gameObject).ToString();

        RenameLeftTurretToCannon(handles);

        var patched = File.ReadAllText(_builderPath);
        StringAssert.Contains("sel.Cannon.Barrel", patched, "Rename did not auto-rewrite the accessor.\n" + patched);

        var rebuildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(rebuildResult.Diagnostics,
            "Rebuild after rename reported diagnostics: " + string.Join("; ", rebuildResult.Diagnostics.Select(d => d.Message)));

        var rootAfter = FindRoot(EditorSceneManager.GetActiveScene(), "Tank");
        Assert.IsNotNull(rootAfter, "Tank instance root disappeared after rename+rebuild");
        var barrelAfter = rootAfter.transform.Find("Cannon/Barrel");
        Assert.IsNotNull(barrelAfter, "Cannon/Barrel not found after rename+rebuild");
        Assert.AreEqual(4f, barrelAfter.GetComponent<Light>().intensity, 0.0001f,
            "Rename must not drop the nested override — SubKey is stable across a name-only rename, "
            + "only ChildPath changes");
        Assert.AreEqual(goidBarrelBefore, GlobalObjectId.GetGlobalObjectIdSlow(barrelAfter.gameObject).ToString(),
            "Barrel GlobalObjectId changed across rename+rebuild — must not re-instantiate");
    }
}
