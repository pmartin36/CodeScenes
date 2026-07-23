using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Serialization;

// m-nested-props b7-t1 (specs/24-nested-prefab-overrides.md): build-side (code->scene) sidecar
// persistence for NESTED instance overrides and added/removed children. Extends
// BuildSidecarPrefabTests' idiom (two Runs: a plain setup build, then a rebuild whose source
// authors the nested op) onto the shared PrefabFacadeFixture (Tank -> LeftTurret/Barrel(Light),
// LeftTurret/Antenna(MeshRenderer)). The keystone assertion is the root-vs-nested SubKey
// discrimination: a nested override's persisted SubKey must be the NESTED sub-object's own live
// pair-key, never the instance root's PrefabKey (SceneBuilderBuild.cs:261-267).
public class NestedSidecarTests
{
    private const string Dir = "Assets/GateTests/Fixtures_NestedSidecar";
    private const string ScenePath = "Assets/GateTests/__NestedSidecarTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;
    private PrefabFacadeFixture.Handles _handles;

    private string _manifestPath;
    private bool _manifestExistedBefore;
    private byte[] _manifestBytesBefore;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class NestedSidecarScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_ns_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "NestedSidecarScene.cs");
        _sidecarPath = Path.Combine(_dir, "NestedSidecarScene.sbmap.json");

        _manifestPath = SceneBuilderPaths.FacadeManifestPath;
        _manifestExistedBefore = File.Exists(_manifestPath);
        _manifestBytesBefore = _manifestExistedBefore ? File.ReadAllBytes(_manifestPath) : null;

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        _handles = PrefabFacadeFixture.Create(Dir);
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

        PrefabFacadeFixture.Delete(Dir);

        if (_manifestExistedBefore)
        {
            File.WriteAllBytes(_manifestPath, _manifestBytesBefore);
        }
        else if (File.Exists(_manifestPath))
        {
            File.Delete(_manifestPath);
        }
    }

    private static GameObject FindRoot(Scene scene, string name) =>
        scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);

    // Keystone: a NESTED property override (below-root, addressed via `.On(...)`) must persist with
    // the nested sub-object's OWN live pair-key, never the instance's root PrefabKey — the bug the
    // unconditional `SubKey = instanceRead.Key` in the pre-b7-t1 code would produce.
    [Test]
    public void Nested_PropertyOverride_PersistsNestedSubKey_NotRootKey()
    {
        PrefabFacadeManifestGenerator.Regenerate();

        File.WriteAllText(_builderPath, Source($"        scene.Instance(\"{_handles.TankPath}\");"));
        var setupResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(setupResult.Diagnostics,
            "Setup build reported diagnostics: " + string.Join("; ", setupResult.Diagnostics.Select(d => d.Message)));

        var tank = FindRoot(EditorSceneManager.GetActiveScene(), "Tank");
        Assert.IsNotNull(tank, "Setup: Tank instance was not created");
        var barrel = tank.transform.Find("LeftTurret/Barrel").gameObject;
        var barrelKey = PrefabInstanceProbe.SubKeyOf(barrel);

        // The `.On(...)` scoped selector only resolves against the FacadeCatalog when the instance
        // is authored via the typed `Instance(Prefabs.X)` form — the plain string-path `Instance(path)`
        // form never sets NodeBuilder.SourcePrefabGuid at parse time (BuilderParser.Instance.cs:71-78).
        // `Run` never compiles the builder source (only Sync does, via BuilderCompileCheck), so a
        // syntactic `Prefabs.Tank` reference needs no real generated façade type to parse successfully.
        File.WriteAllText(_builderPath, Source(
            "        scene.Instance(Prefabs.Tank)\n" +
            "            .On(\"LeftTurret/Barrel\", b => b.Override(e => e.Set<UnityEngine.Light>(\"m_Intensity\", 4f)));"));
        var result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(result.Diagnostics,
            "Rebuild authoring a nested .On override reported diagnostics: "
            + string.Join("; ", result.Diagnostics.Select(d => d.Message)));

        var map = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        var entry = map.Entries.Single(e => e.Kind == "PrefabInstance");
        Assert.IsNotNull(entry.Overrides,
            "Instance entry has no persisted Overrides[] after a nested property override.\n" + File.ReadAllText(_sidecarPath));

        var ov = entry.Overrides.SingleOrDefault(o => o.PropertyPath == "m_Intensity" && o.Kind == "Property");
        Assert.IsNotNull(ov, "Persisted Overrides[] has no Kind=Property record for the nested m_Intensity override");
        Assert.AreEqual("UnityEngine.Light", ov.ComponentType,
            "Nested override record ComponentType did not match the nested Light component");
        Assert.AreEqual(barrelKey.TargetPrefabId, ov.SubKey.TargetPrefabId,
            "Nested override record SubKey did not match the live Barrel sub-object's own pair-key");
        Assert.AreEqual(barrelKey.TargetObjectId, ov.SubKey.TargetObjectId,
            "Nested override record SubKey did not match the live Barrel sub-object's own pair-key");
        // NOTE: TargetPrefabId alone is NOT the discriminating field — it keys off the OUTERMOST
        // prefab-instance root and is constant across every descendant (see
        // NestedDepthIdentityTests.DepthTwo_TwoNestedBarrels_BareLivePairKeysCollide_FallbackIsRequired).
        // Only TargetObjectId (and thus the whole key) varies per sub-object.
        Assert.AreNotEqual(entry.PrefabKey.TargetObjectId, ov.SubKey.TargetObjectId,
            "Nested override record SubKey's TargetObjectId must NOT equal the instance's own root PrefabKey's TargetObjectId");
        Assert.AreNotEqual(entry.PrefabKey, ov.SubKey,
            "Nested override record SubKey must NOT equal the instance's own root PrefabKey");
    }

    // Create-with-payload: an added child (below-root, via `.AddChild(...)`, no facade required)
    // must get its OWN spliced Kind=GameObject/Component Entries[] (stable GlobalObjectId across
    // reload) AND a Kind=AddedGameObject override record keyed by its own live pair-key.
    [Test]
    public void AddedChild_GetsOwnGlobalObjectIdEntry_AndAddedGameObjectRecord()
    {
        File.WriteAllText(_builderPath, Source($"        scene.Instance(\"{_handles.TankPath}\");"));
        var setupResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(setupResult.Diagnostics,
            "Setup build reported diagnostics: " + string.Join("; ", setupResult.Diagnostics.Select(d => d.Message)));

        File.WriteAllText(_builderPath, Source(
            $"        scene.Instance(\"{_handles.TankPath}\")\n" +
            "            .AddChild(\"LeftTurret\", \"MuzzleFlash\", n => n.Component<UnityEngine.Light>());"));
        var result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(result.Diagnostics,
            "Rebuild authoring AddChild reported diagnostics: " + string.Join("; ", result.Diagnostics.Select(d => d.Message)));

        var tank = FindRoot(EditorSceneManager.GetActiveScene(), "Tank");
        Assert.IsNotNull(tank, "Setup: Tank instance was not created");
        var muzzleFlash = tank.transform.Find("LeftTurret/MuzzleFlash");
        Assert.IsNotNull(muzzleFlash, "Setup: AddChild did not create the live MuzzleFlash child");
        var light = muzzleFlash.GetComponent<Light>();
        Assert.IsNotNull(light, "Setup: MuzzleFlash has no Light component");

        var map = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        var entry = map.Entries.Single(e => e.Kind == "PrefabInstance");

        var childKey = PrefabInstanceProbe.SubKeyOf(muzzleFlash.gameObject);
        var addedRecord = entry.Overrides?.SingleOrDefault(o => o.Kind == "AddedGameObject");
        Assert.IsNotNull(addedRecord,
            "Instance entry has no Kind=AddedGameObject override record.\n" + File.ReadAllText(_sidecarPath));
        Assert.AreEqual(childKey.TargetPrefabId, addedRecord.SubKey.TargetPrefabId,
            "AddedGameObject record SubKey did not match the live child's own pair-key");
        Assert.AreEqual(childKey.TargetObjectId, addedRecord.SubKey.TargetObjectId,
            "AddedGameObject record SubKey did not match the live child's own pair-key");

        var goEntry = map.Entries.SingleOrDefault(e => e.Kind == "GameObject" && e.Name == "MuzzleFlash");
        Assert.IsNotNull(goEntry,
            "Sidecar has no spliced Kind=GameObject Entries[] entry for the added child.\n" + File.ReadAllText(_sidecarPath));
        Assert.AreEqual(GlobalObjectId.GetGlobalObjectIdSlow(muzzleFlash.gameObject).ToString(), goEntry.GlobalObjectId,
            "Spliced added-child GameObject entry's GlobalObjectId did not match the live child");
        Assert.AreEqual(entry.LogicalId, goEntry.ParentLogicalId,
            "Spliced added-child GameObject entry's ParentLogicalId did not match the owning instance");

        var componentEntry = map.Entries.SingleOrDefault(
            e => e.Kind == "Component" && e.ParentLogicalId == goEntry.LogicalId && e.ComponentType == "UnityEngine.Light");
        Assert.IsNotNull(componentEntry,
            "Sidecar has no spliced Kind=Component entry for the added child's Light.\n" + File.ReadAllText(_sidecarPath));
        Assert.AreEqual(GlobalObjectId.GetGlobalObjectIdSlow(light).ToString(), componentEntry.GlobalObjectId,
            "Spliced added-child Component entry's GlobalObjectId did not match the live Light");
    }

    // Kind coverage: a removed SOURCE child (below-root, via `.RemoveChild(...)`) has no live object
    // and no Entries[] splice, but must still persist a Kind=RemovedGameObject override record.
    [Test]
    public void RemovedChild_SourceAntenna_RecordsRemovedGameObjectKind()
    {
        File.WriteAllText(_builderPath, Source($"        scene.Instance(\"{_handles.TankPath}\");"));
        var setupResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(setupResult.Diagnostics,
            "Setup build reported diagnostics: " + string.Join("; ", setupResult.Diagnostics.Select(d => d.Message)));

        File.WriteAllText(_builderPath, Source(
            $"        scene.Instance(\"{_handles.TankPath}\")\n" +
            "            .RemoveChild(\"LeftTurret/Antenna\");"));
        var result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(result.Diagnostics,
            "Rebuild authoring RemoveChild reported diagnostics: " + string.Join("; ", result.Diagnostics.Select(d => d.Message)));

        var tank = FindRoot(EditorSceneManager.GetActiveScene(), "Tank");
        Assert.IsNotNull(tank, "Setup: Tank instance was not created");
        Assert.IsNull(tank.transform.Find("LeftTurret/Antenna"), "Setup: RemoveChild did not remove the live Antenna");

        var map = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        var entry = map.Entries.Single(e => e.Kind == "PrefabInstance");
        var removedRecord = entry.Overrides?.SingleOrDefault(o => o.Kind == "RemovedGameObject");
        Assert.IsNotNull(removedRecord,
            "Instance entry has no Kind=RemovedGameObject override record.\n" + File.ReadAllText(_sidecarPath));
    }
}
