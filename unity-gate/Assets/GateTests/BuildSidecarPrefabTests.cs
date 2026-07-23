using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Serialization;

// b5-t3: build-side (code->scene) sidecar write path for prefab instances. After
// SceneBuilderBuild.Run on a builder authoring scene.Instance(...), the written sidecar must carry
// Kind="PrefabInstance" per instance, a nonzero PrefabKey (TargetPrefabId/TargetObjectId) split from
// the instantiated root's GlobalObjectId, a SourcePrefabGuid == the prefab's AssetDatabase GUID, and
// an Assets[] entry with TypeHint="Prefab" for the harvested source GUID. Two same-prefab instances
// share SourcePrefabGuid with distinct TargetObjectId. A plain scene.Add sibling stays byte-stable
// (Kind="GameObject", null PrefabKey/SourcePrefabGuid) — guards the IdentityRemapper flattening
// regression this task also fixes.
//
// b6-t2 (specs/11-m10-prefab-overrides.md): the SAVE-side identity persistence for M10 root-target
// instance overrides/added-components. The fixture prefab's root also carries a BoxCollider so a
// root-target property override has something real to land on (FIXTURE NOTE, research.md b6-t2).
// Each test PRE-APPLIES the component/override directly to the live instance via PrefabUtility to
// seed real bold-override/added-component state, then a SECOND SceneBuilderBuild.Run — whose
// builder source authors the MATCHING `.AddComponent<T>()` / `.Override(e => e.Set(...))` call —
// re-reads the live instance and persists identity/pair-key/BaseValue into the sidecar. The
// authored call must match the pre-applied state: since b7-t1 wired RevertInstanceOverride for
// real, a build whose source does NOT author a matching override now correctly reverts any
// un-authored live override before the sidecar is written (code-is-truth); a backdoor-only
// mutation no longer survives a build.
public class BuildSidecarPrefabTests
{
    private const string FixturesDir = "Assets/GateTests/Fixtures_M6BuildSidecar";
    private const string PrefabPath = FixturesDir + "/M6_Enemy.prefab";
    private const string ScenePath = "Assets/GateTests/__BuildSidecarPrefabTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class BuildSidecarPrefabScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_bsp_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "BuildSidecarPrefabScene.cs");
        _sidecarPath = Path.Combine(_dir, "BuildSidecarPrefabScene.sbmap.json");

        if (!AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "Fixtures_M6BuildSidecar");
        }

        var source = new UnityEngine.GameObject("M6_Enemy_Source");
        source.AddComponent<UnityEngine.BoxCollider>();
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

    [Test]
    public void Run_SceneInstance_SidecarEntryHasPrefabInstanceKindKeyAndSourceGuid()
    {
        File.WriteAllText(_builderPath, Source($"        scene.Instance(\"{PrefabPath}\");"));

        var result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        Assert.IsEmpty(result.Diagnostics,
            "Build reported diagnostics for a valid scene.Instance(...): "
            + string.Join("; ", result.Diagnostics.Select(d => d.Message)));

        var map = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        var entry = map.Entries.SingleOrDefault(e => e.Kind == "PrefabInstance");
        Assert.IsNotNull(entry, "Sidecar has no Kind=PrefabInstance entry.\n" + File.ReadAllText(_sidecarPath));

        Assert.IsNotNull(entry.PrefabKey, "PrefabInstance entry has no PrefabKey");
        Assert.AreNotEqual(0UL, entry.PrefabKey.TargetPrefabId, "PrefabKey.TargetPrefabId is zero");
        Assert.AreNotEqual(0UL, entry.PrefabKey.TargetObjectId, "PrefabKey.TargetObjectId is zero");

        var expectedGuid = AssetDatabase.AssetPathToGUID(PrefabPath);
        Assert.AreEqual(expectedGuid, entry.SourcePrefabGuid, "SourcePrefabGuid did not match the prefab's GUID");

        var assetEntry = map.Assets.SingleOrDefault(a => a.Guid == expectedGuid);
        Assert.IsNotNull(assetEntry, "Sidecar Assets[] has no entry for the source prefab's GUID");
        Assert.AreEqual("Prefab", assetEntry.TypeHint, "Harvested prefab asset entry has the wrong TypeHint");
    }

    [Test]
    public void Run_TwoInstancesOfSamePrefab_ShareSourceGuidAndTargetObjectIdWithDistinctTargetPrefabId()
    {
        File.WriteAllText(_builderPath, Source(
            $"        scene.Instance(\"{PrefabPath}\");\n" +
            $"        scene.Instance(\"{PrefabPath}\");"));

        var result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        Assert.IsEmpty(result.Diagnostics,
            "Build reported diagnostics for two scene.Instance(...) of the same prefab: "
            + string.Join("; ", result.Diagnostics.Select(d => d.Message)));

        var map = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        var instanceEntries = map.Entries.Where(e => e.Kind == "PrefabInstance").ToList();
        Assert.AreEqual(2, instanceEntries.Count,
            "Expected exactly two PrefabInstance entries.\n" + File.ReadAllText(_sidecarPath));

        var expectedGuid = AssetDatabase.AssetPathToGUID(PrefabPath);
        Assert.IsTrue(instanceEntries.All(e => e.SourcePrefabGuid == expectedGuid),
            "Both instances must share the same SourcePrefabGuid");

        // GlobalObjectId semantics: targetObjectId is the SOURCE object's local file id, shared by
        // every instance of the same prefab; targetPrefabId is the per-instance-unique id. The
        // pair-key's distinguishing axis is TargetPrefabId, not TargetObjectId.
        Assert.AreEqual(instanceEntries[0].PrefabKey.TargetObjectId, instanceEntries[1].PrefabKey.TargetObjectId,
            "Two instances of the same prefab must share TargetObjectId (the source object's local file id)");
        Assert.AreNotEqual(instanceEntries[0].PrefabKey.TargetPrefabId, instanceEntries[1].PrefabKey.TargetPrefabId,
            "Two distinct instances must have distinct TargetPrefabId");
    }

    [Test]
    public void Run_PlainGameObjectAlongsideInstance_StaysGameObjectKindWithNullPrefabFields()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Plain\");\n" +
            $"        scene.Instance(\"{PrefabPath}\");"));

        var result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        Assert.IsEmpty(result.Diagnostics,
            "Build reported diagnostics: " + string.Join("; ", result.Diagnostics.Select(d => d.Message)));

        var map = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        var plainEntry = map.Entries.SingleOrDefault(e => e.Name == "Plain");
        Assert.IsNotNull(plainEntry, "No entry found for the plain GameObject 'Plain'");
        Assert.AreEqual("GameObject", plainEntry.Kind);
        Assert.IsNull(plainEntry.PrefabKey, "Plain GameObject entry must not carry a PrefabKey");
        Assert.IsNull(plainEntry.SourcePrefabGuid, "Plain GameObject entry must not carry a SourcePrefabGuid");
    }

    private static GameObject FindRoot(Scene scene, string name) =>
        scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);

    // b6-t2: an added root component is a genuine new scene object — it must get its own
    // Kind="Component" sidecar entry (GlobalObjectId re-read live, ParentLogicalId == the owning
    // instance) so it has stable identity across reload. Independent of b7-t1 (no executor yet): the
    // Light is added directly to the live instance (as the eventual executor will do), then a SECOND
    // Run — whose source now authors the matching .AddComponent<Light>() — is a Materialize no-op and
    // the SAVE-side re-read records the live component's identity.
    [Test]
    public void Run_InstanceWithAddedRootComponent_SidecarHasComponentEntryWithGlobalObjectId()
    {
        File.WriteAllText(_builderPath, Source($"        scene.Instance(\"{PrefabPath}\");"));
        var setupResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(setupResult.Diagnostics,
            "Setup build reported diagnostics: " + string.Join("; ", setupResult.Diagnostics.Select(d => d.Message)));

        var instanceRoot = FindRoot(EditorSceneManager.GetActiveScene(), "M6_Enemy");
        Assert.IsNotNull(instanceRoot, "Setup: instance root was not created");
        var light = instanceRoot.AddComponent<Light>();

        var instanceLogicalId = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath))
            .Entries.Single(e => e.Kind == "PrefabInstance").LogicalId;

        File.WriteAllText(_builderPath, Source($"        scene.Instance(\"{PrefabPath}\").AddComponent<Light>();"));
        var result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(result.Diagnostics,
            "Build reported diagnostics after authoring AddComponent<Light>(): "
            + string.Join("; ", result.Diagnostics.Select(d => d.Message)));

        var map = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        var componentEntry = map.Entries.SingleOrDefault(e => e.Kind == "Component" && e.ComponentType == "UnityEngine.Light");
        Assert.IsNotNull(componentEntry,
            "Sidecar has no Kind=Component entry for the added Light.\n" + File.ReadAllText(_sidecarPath));
        Assert.AreEqual(instanceLogicalId, componentEntry.ParentLogicalId,
            "Added-component entry's ParentLogicalId did not match its owning instance");
        Assert.IsFalse(string.IsNullOrEmpty(componentEntry.GlobalObjectId), "Added-component entry has no GlobalObjectId");
        Assert.AreEqual(GlobalObjectId.GetGlobalObjectIdSlow(light).ToString(), componentEntry.GlobalObjectId,
            "Added-component entry's GlobalObjectId did not match the live component");
    }

    // b6-t2/b1-t2: a root-target property override's (SubKey, ComponentType, PropertyPath) + recorded
    // BaseValue must persist onto the instance's own sidecar entry, so the target stays addressable
    // across reload and a future rebuild can stale-check against the recorded BaseValue (b3-t3).
    [Test]
    public void Run_InstanceWithRootPropertyOverride_InstanceEntryPersistsPathTargetAndBaseValue()
    {
        File.WriteAllText(_builderPath, Source($"        scene.Instance(\"{PrefabPath}\");"));
        var setupResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(setupResult.Diagnostics,
            "Setup build reported diagnostics: " + string.Join("; ", setupResult.Diagnostics.Select(d => d.Message)));

        var instanceRoot = FindRoot(EditorSceneManager.GetActiveScene(), "M6_Enemy");
        Assert.IsNotNull(instanceRoot, "Setup: instance root was not created");
        var collider = instanceRoot.GetComponent<BoxCollider>();
        Assert.IsNotNull(collider, "Setup: prefab fixture has no root BoxCollider to override");
        collider.isTrigger = true;
        PrefabUtility.RecordPrefabInstancePropertyModifications(collider);

        // The override must be authored in code (code-is-truth) or the rebuild's diff sees it as
        // un-authored live state and reverts it (b7-t1: RevertInstanceOverride is now wired for real).
        // Use the literal serialized-path Set<T>(path, value) overload (propertyPath == "m_IsTrigger")
        // rather than the typed member selector, whose transient "member:isTrigger" form does not
        // string-match the snapshot's real "m_IsTrigger" path (known b3-t2/b2-t2 gap, out of this
        // task's scope) and would churn Set+Revert every build.
        File.WriteAllText(_builderPath, Source(
            $"        scene.Instance(\"{PrefabPath}\").Override(e => e.Set<UnityEngine.BoxCollider>(\"m_IsTrigger\", true));"));
        var result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(result.Diagnostics,
            "Rebuild reported diagnostics against a live root-target override: "
            + string.Join("; ", result.Diagnostics.Select(d => d.Message)));

        var map = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        var entry = map.Entries.Single(e => e.Kind == "PrefabInstance");
        Assert.IsNotNull(entry.Overrides,
            "Instance entry has no persisted Overrides[].\n" + File.ReadAllText(_sidecarPath));
        var ov = entry.Overrides.SingleOrDefault(o => o.PropertyPath == "m_IsTrigger");
        Assert.IsNotNull(ov, "Persisted Overrides[] has no record for m_IsTrigger");
        Assert.AreEqual("UnityEngine.BoxCollider", ov.ComponentType,
            "Persisted override record ComponentType must carry the component's FullName");
        Assert.AreEqual("Property", ov.Kind, "Persisted override record Kind must be \"Property\"");
        Assert.AreEqual(entry.PrefabKey.TargetPrefabId, ov.SubKey.TargetPrefabId,
            "Persisted override record SubKey must match the instance's own root pair-key");
        Assert.AreEqual(entry.PrefabKey.TargetObjectId, ov.SubKey.TargetObjectId,
            "Persisted override record SubKey must match the instance's own root pair-key");
        Assert.AreEqual("0", ov.BaseValue,
            "Persisted override record BaseValue did not match the source prefab's current default (false -> \"0\")");
    }

    // b6-t2/b1-t2: the override target's SubKey must resolve identically across repeated builds
    // ("the same pair key" — DELIVERABLE) — it is deterministic by construction, so a second rebuild
    // must persist the identical SubKey.
    [Test]
    public void Run_SecondBuild_OverrideTargetPairKeyStable()
    {
        File.WriteAllText(_builderPath, Source($"        scene.Instance(\"{PrefabPath}\");"));
        var setupResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(setupResult.Diagnostics,
            "Setup build reported diagnostics: " + string.Join("; ", setupResult.Diagnostics.Select(d => d.Message)));

        var instanceRoot = FindRoot(EditorSceneManager.GetActiveScene(), "M6_Enemy");
        Assert.IsNotNull(instanceRoot, "Setup: instance root was not created");
        var collider = instanceRoot.GetComponent<BoxCollider>();
        collider.isTrigger = true;
        PrefabUtility.RecordPrefabInstancePropertyModifications(collider);

        // The override must be authored in code (code-is-truth) or each rebuild's diff sees it as
        // un-authored live state and reverts it (b7-t1: RevertInstanceOverride is now wired for real).
        // Use the literal serialized-path Set<T>(path, value) overload — see the sibling test above
        // for why the typed member selector form is unsafe here.
        File.WriteAllText(_builderPath, Source(
            $"        scene.Instance(\"{PrefabPath}\").Override(e => e.Set<UnityEngine.BoxCollider>(\"m_IsTrigger\", true));"));

        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        var firstOv = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath))
            .Entries.Single(e => e.Kind == "PrefabInstance")
            .Overrides?.SingleOrDefault(o => o.PropertyPath == "m_IsTrigger");
        Assert.IsNotNull(firstOv, "First rebuild did not persist the m_IsTrigger override record");

        var secondResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(secondResult.Diagnostics,
            "Second rebuild reported diagnostics: " + string.Join("; ", secondResult.Diagnostics.Select(d => d.Message)));
        var secondOv = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath))
            .Entries.Single(e => e.Kind == "PrefabInstance")
            .Overrides?.SingleOrDefault(o => o.PropertyPath == "m_IsTrigger");
        Assert.IsNotNull(secondOv, "Second rebuild did not persist the m_IsTrigger override record");

        Assert.AreEqual(firstOv.SubKey.TargetPrefabId, secondOv.SubKey.TargetPrefabId,
            "SubKey.TargetPrefabId changed across repeated builds");
        Assert.AreEqual(firstOv.SubKey.TargetObjectId, secondOv.SubKey.TargetObjectId,
            "SubKey.TargetObjectId changed across repeated builds");
    }

    // b6-t2 (iteration 3, seam fix): the persisted InstanceOverrideRecord.BaseValue must be threaded
    // back onto the DESIRED model's PropertyOverride.BaseValue by DesiredModelLoader.Load, so
    // InstanceOverrideDiff.DetectStaleOverrides (which short-circuits on a null desired BaseValue)
    // actually sees data on a real reload — not just hand-built Core POCO tests. Proves the LOAD-path
    // wiring, not just the SAVE-side persistence (already covered above).
    [Test]
    public void Run_ThenReload_DesiredOverrideBaseValueRehydrated()
    {
        File.WriteAllText(_builderPath, Source($"        scene.Instance(\"{PrefabPath}\");"));
        var setupResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(setupResult.Diagnostics,
            "Setup build reported diagnostics: " + string.Join("; ", setupResult.Diagnostics.Select(d => d.Message)));

        var instanceRoot = FindRoot(EditorSceneManager.GetActiveScene(), "M6_Enemy");
        Assert.IsNotNull(instanceRoot, "Setup: instance root was not created");
        var collider = instanceRoot.GetComponent<BoxCollider>();
        Assert.IsNotNull(collider, "Setup: prefab fixture has no root BoxCollider to override");
        collider.isTrigger = true;
        PrefabUtility.RecordPrefabInstancePropertyModifications(collider);

        var overrideSource = Source(
            $"        scene.Instance(\"{PrefabPath}\").Override(e => e.Set<UnityEngine.BoxCollider>(\"m_IsTrigger\", true));");
        File.WriteAllText(_builderPath, overrideSource);
        var result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(result.Diagnostics,
            "Rebuild reported diagnostics against a live root-target override: "
            + string.Join("; ", result.Diagnostics.Select(d => d.Message)));

        var persistedMap = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        var persistedOv = persistedMap.Entries.Single(e => e.Kind == "PrefabInstance")
            .Overrides?.SingleOrDefault(o => o.PropertyPath == "m_IsTrigger");
        Assert.IsNotNull(persistedOv, "Setup: sidecar did not persist the m_IsTrigger override record");
        Assert.IsNotNull(persistedOv.BaseValue, "Setup: sidecar did not persist a BaseValue for m_IsTrigger");

        // Re-parse the SAME authored source against the DESERIALIZED sidecar — this is the real
        // load-path seam every Build/Sync call goes through (DesiredModelLoader.Load).
        var loaded = DesiredModelLoader.Load(overrideSource, persistedMap);
        var desiredInstance = (PrefabInstanceNode)loaded.Desired.Roots.Single(r => r.Name == "M6_Enemy");
        var desiredOv = desiredInstance.Overrides.SingleOrDefault(o => o.PropertyPath == "m_IsTrigger");
        Assert.IsNotNull(desiredOv, "Desired model has no m_IsTrigger override after reload");
        Assert.IsNotNull(desiredOv.BaseValue,
            "DesiredModelLoader.Load did not rehydrate the persisted BaseValue onto the desired override");
    }
}
