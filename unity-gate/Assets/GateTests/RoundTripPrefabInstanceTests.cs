using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;
using SceneBuilder.Core.Serialization;
using SceneBuilder.Core.Validation;

// b6-t1: M6 bidirectional round-trip EditMode gate (specs/07-m6-prefab-instances.md §176-191) — the
// milestone's end-to-end coverage of code<->scene sync for a real `.prefab` fixture, mirroring
// RoundTripStructuralTests / BuildSidecarPrefabTests / SnapshotReaderPrefabTests exactly (temp
// builder + sidecar in a system temp dir, a temp `.prefab` fixture under Assets/GateTests/, a saved
// temp scene). Five tests, one per checklist step: (1) one instance connects + stamps the sidecar,
// (2) two same-prefab instances share SourcePrefabGuid + TargetObjectId with distinct TargetPrefabId,
// (3) an in-scene move+reparent updates the source without recreating the instance, (4) a live
// property override survives a rebuild and surfaces the SB2301 "not modelled" flag, (5) deleting the
// instance removes its statement.
public class RoundTripPrefabInstanceTests
{
    private const string FixturesDir = "Assets/GateTests/Fixtures_M6RoundTrip";
    private const string PrefabPath = FixturesDir + "/M6_RoundTripEnemy.prefab";
    private const string InstanceName = "M6_RoundTripEnemy"; // scene.Instance(path)'s name derives from the path stem
    private const string ScenePath = "Assets/GateTests/__RoundTripPrefabInstanceTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class RoundTripPrefabInstanceScene : ISceneDefinition
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
        _dir = Path.Combine(Path.GetTempPath(), "sb_rtpi_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "RoundTripPrefabInstanceScene.cs");
        _sidecarPath = Path.Combine(_dir, "RoundTripPrefabInstanceScene.sbmap.json");

        if (!AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "Fixtures_M6RoundTrip");
        }

        var source = new GameObject("M6_RoundTripEnemy_Source");
        source.AddComponent<BoxCollider>();
        PrefabUtility.SaveAsPrefabAsset(source, PrefabPath);
        UnityEngine.Object.DestroyImmediate(source);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
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

        AssetDatabase.DeleteAsset(PrefabPath);
        if (AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.DeleteAsset(FixturesDir);
        }
    }

    // 1. One scene.Instance(...) connects at its authored transform; the sidecar stamps
    //    Kind=PrefabInstance with a non-empty GlobalObjectId, the source prefab's GUID, a nonzero
    //    PrefabKey, and an Assets[] entry for the harvested prefab.
    [Test]
    public void CodeToScene_OneInstance_ConnectedAtTransform_SidecarStamped()
    {
        File.WriteAllText(_builderPath, Source(
            $"        scene.Instance(\"{PrefabPath}\").Transform(pos: (3f, 0f, 5f));"));

        var scene = EditorSceneManager.GetActiveScene();
        var result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        Assert.IsEmpty(result.Diagnostics,
            "Build reported refusal diagnostics: " + string.Join("; ", result.Diagnostics.Select(d => d.Message)));

        var root = FindRoot(EditorSceneManager.GetActiveScene(), InstanceName);
        Assert.IsNotNull(root, "Instance root was not created by SceneBuilderBuild.Run");
        Assert.AreEqual(PrefabInstanceStatus.Connected, PrefabUtility.GetPrefabInstanceStatus(root),
            "Instantiated object is not a connected prefab instance");
        Assert.AreEqual(new Vector3(3f, 0f, 5f), root.transform.localPosition,
            "Authored .Transform(pos:...) did not apply to the instance root");

        var map = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        var entry = map.Entries.SingleOrDefault(e => e.Kind == "PrefabInstance");
        Assert.IsNotNull(entry, "Sidecar has no Kind=PrefabInstance entry.\n" + File.ReadAllText(_sidecarPath));
        Assert.IsFalse(string.IsNullOrEmpty(entry.GlobalObjectId), "Sidecar entry has no GlobalObjectId");

        var expectedGuid = AssetDatabase.AssetPathToGUID(PrefabPath);
        Assert.AreEqual(expectedGuid, entry.SourcePrefabGuid, "SourcePrefabGuid did not match the prefab's GUID");
        Assert.IsNotNull(entry.PrefabKey, "PrefabInstance entry has no PrefabKey");
        Assert.AreNotEqual(0UL, entry.PrefabKey.TargetPrefabId, "PrefabKey.TargetPrefabId is zero");

        var assetEntry = map.Assets.SingleOrDefault(a => a.Guid == expectedGuid);
        Assert.IsNotNull(assetEntry, "Sidecar Assets[] has no entry for the source prefab's GUID");
        Assert.AreEqual("Prefab", assetEntry.TypeHint, "Harvested prefab asset entry has the wrong TypeHint");
    }

    // 2. Two instances of the same prefab -> two PrefabInstance entries sharing SourcePrefabGuid and
    //    PrefabKey.TargetObjectId (the source object's local file id), with distinct TargetPrefabId
    //    (the per-instance-unique axis) — GlobalObjectId semantics, per b5-t3.
    [Test]
    public void CodeToScene_TwoSamePrefab_TwoEntries_SharedGuidAndTargetObjectId_DistinctTargetPrefabId()
    {
        File.WriteAllText(_builderPath, Source(
            $"        scene.Instance(\"{PrefabPath}\");\n" +
            $"        scene.Instance(\"{PrefabPath}\");"));

        var scene = EditorSceneManager.GetActiveScene();
        var result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        Assert.IsEmpty(result.Diagnostics,
            "Build reported diagnostics for two scene.Instance(...) of the same prefab: "
            + string.Join("; ", result.Diagnostics.Select(d => d.Message)));

        var map = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        var entries = map.Entries.Where(e => e.Kind == "PrefabInstance").ToList();
        Assert.AreEqual(2, entries.Count, "Expected exactly two PrefabInstance entries.\n" + File.ReadAllText(_sidecarPath));

        var expectedGuid = AssetDatabase.AssetPathToGUID(PrefabPath);
        Assert.IsTrue(entries.All(e => e.SourcePrefabGuid == expectedGuid),
            "Both instances must share the same SourcePrefabGuid");
        Assert.AreEqual(entries[0].PrefabKey.TargetObjectId, entries[1].PrefabKey.TargetObjectId,
            "Two instances of the same prefab must share TargetObjectId (the source object's local file id)");
        Assert.AreNotEqual(entries[0].PrefabKey.TargetPrefabId, entries[1].PrefabKey.TargetPrefabId,
            "Two distinct instances must have distinct TargetPrefabId");
    }

    // 3. Move+reparent (scene->code): moving the live instance under a handled parent and changing its
    //    position rewrites the source to a reparented `.Instance(...).Transform(pos:...)` — the
    //    instance itself is NOT destroyed+recreated (GetEntityId/GlobalObjectId stay stable across the
    //    sync AND a follow-up rebuild).
    [Test]
    public void SceneToCode_MoveReparent_UpdatesSource_InstanceNotRecreated()
    {
        File.WriteAllText(_builderPath, Source(
            "        var pack = scene.Add(\"Pickups\");\n" +
            $"        scene.Instance(\"{PrefabPath}\");"));

        var scene = EditorSceneManager.GetActiveScene();
        var buildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(buildResult.Diagnostics,
            "Setup build reported diagnostics: " + string.Join("; ", buildResult.Diagnostics.Select(d => d.Message)));

        var live = EditorSceneManager.GetActiveScene();
        var pack = FindRoot(live, "Pickups");
        var root = FindRoot(live, InstanceName);
        Assert.IsNotNull(pack, "Setup: Pickups parent was not created");
        Assert.IsNotNull(root, "Setup: instance root was not created");

        var goidBefore = GlobalObjectId.GetGlobalObjectIdSlow(root).ToString();
        var entityIdBefore = root.GetEntityId();

        root.transform.SetParent(pack.transform);
        root.transform.localPosition = new Vector3(2f, 4f, 6f);

        var syncResult = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(syncResult.Changed, "Sync reported no change despite a move+reparent");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains($"pack.Instance(\"{PrefabPath}\")", rewritten,
            "Builder source did not reparent the instance under Pickups's handle.\n" + rewritten);
        StringAssert.Contains(".Transform(pos:", rewritten,
            "Builder source did not pick up the moved position.\n" + rewritten);

        Assert.AreEqual(entityIdBefore, root.GetEntityId(),
            "Sync destroyed and recreated the instance instead of reparenting it in place");
        Assert.AreEqual(goidBefore, GlobalObjectId.GetGlobalObjectIdSlow(root).ToString(),
            "GlobalObjectId changed across sync — identity was not preserved");

        var rebuildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(rebuildResult.Diagnostics,
            "Rebuild after reparent reported diagnostics: " + string.Join("; ", rebuildResult.Diagnostics.Select(d => d.Message)));
        Assert.AreEqual(goidBefore, GlobalObjectId.GetGlobalObjectIdSlow(root).ToString(),
            "A follow-up rebuild changed GlobalObjectId — identity not stable across rebuild");
    }

    // 4. A genuine live property override (non-root-transform) survives a rebuild — never reverted —
    //    and the rebuild surfaces the SB2301 "overrides preserved, not modelled (M10)" info flag on
    //    BuildResult.Flags. The builder source gains no override authoring (out of scope until M10).
    [Test]
    public void Override_PreservedAndFlagged_RebuildDoesNotRevert()
    {
        File.WriteAllText(_builderPath, Source($"        scene.Instance(\"{PrefabPath}\");"));

        var scene = EditorSceneManager.GetActiveScene();
        var buildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(buildResult.Diagnostics,
            "Setup build reported diagnostics: " + string.Join("; ", buildResult.Diagnostics.Select(d => d.Message)));

        var root = FindRoot(EditorSceneManager.GetActiveScene(), InstanceName);
        Assert.IsNotNull(root, "Setup: instance root was not created");
        var collider = root.GetComponent<BoxCollider>();
        Assert.IsNotNull(collider, "Setup: prefab fixture has no BoxCollider to override");

        var overriddenCenter = new Vector3(9f, 9f, 9f);
        collider.center = overriddenCenter;
        PrefabUtility.RecordPrefabInstancePropertyModifications(collider);
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

        var rebuildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        Assert.IsTrue(rebuildResult.Flags.Any(d => d.Code == DiagnosticCodes.PrefabOverridesNotModelled),
            "Rebuild against a live property override did not surface SB2301 on BuildResult.Flags");

        var liveRoot = FindRoot(EditorSceneManager.GetActiveScene(), InstanceName);
        Assert.IsNotNull(liveRoot, "Instance root disappeared across rebuild");
        Assert.AreEqual(overriddenCenter, liveRoot.GetComponent<BoxCollider>().center,
            "Rebuild reverted the live property override");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.DoesNotContain("BoxCollider", rewritten,
            "Builder source gained override authoring — out of scope until M10.\n" + rewritten);
    }

    // 5. Deleting the live instance (scene->code) removes its .Instance(...) statement.
    [Test]
    public void SceneToCode_DeletedInstance_RemovesStatement()
    {
        File.WriteAllText(_builderPath, Source($"        scene.Instance(\"{PrefabPath}\");"));

        var scene = EditorSceneManager.GetActiveScene();
        var buildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(buildResult.Diagnostics,
            "Setup build reported diagnostics: " + string.Join("; ", buildResult.Diagnostics.Select(d => d.Message)));

        var root = FindRoot(EditorSceneManager.GetActiveScene(), InstanceName);
        Assert.IsNotNull(root, "Setup: instance root was not created");
        UnityEngine.Object.DestroyImmediate(root);

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite a destroyed prefab instance");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.DoesNotContain($".Instance(\"{PrefabPath}\")", rewritten,
            "Builder source still references the deleted instance.\n" + rewritten);
    }
}
