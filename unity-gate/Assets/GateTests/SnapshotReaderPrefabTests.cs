using System;
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

// b5-t2 (specs/07-m6-prefab-instances.md) + b6-t1 (specs/11-m10-prefab-overrides.md): SceneSnapshotReader
// detects prefab instances and reads their overrides. The instance ROOT's SnapshotNode must carry
// SourcePrefabGuid/PrefabKey, treat the whole instance as one unit (no Components/Children enumerated),
// exclude modelled root transform/name/order mods (so a mere move never falsely flags an override), and —
// under M10 — a ROOT-target property/added/removed override reads into the STRUCTURED
// Overrides/AddedComponents/RemovedComponents collections (never OpaqueOverrides), while a NESTED-target
// override (below the root) stays in OpaqueOverrides (M6, deferred). A plain (non-instance) GameObject
// reads all fields null/empty.
public class SnapshotReaderPrefabTests
{
    private const string FixturesDir = "Assets/GateTests/Fixtures_M6PrefabSnapshot";
    private const string PrefabPath = FixturesDir + "/M6_SnapshotEnemy.prefab";
    private const string BlueMatPath = FixturesDir + "/M10_Blue.mat";
    private const string ScenePath = "Assets/GateTests/__SnapshotReaderPrefabTemp.unity";

    private string _guid;

    [SetUp]
    public void SetUp()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        if (!AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "Fixtures_M6PrefabSnapshot");
        }

        AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), BlueMatPath);

        // Extended for M10 (b6-t1): the shared source prefab gains a DoorOpener (scene-object-typed
        // field, ObjectRef override case), a MeshRenderer (asset-typed field, AssetRef override
        // case), and a nested child with its own BoxCollider (NESTED-target opaque case). Instance
        // roots never enumerate Components/Children regardless of fixture shape (see
        // InstanceRoot_ReadsSourceGuid_PrefabKeyNonzero_ComponentsAndChildrenEmpty below), so this
        // is additive and does not disturb the pre-existing M6 assertions.
        var source = new GameObject("M6_SnapshotEnemy_Source");
        source.AddComponent<BoxCollider>();
        source.AddComponent<DoorOpener>();
        source.AddComponent<MeshRenderer>();
        var child = new GameObject("Child");
        child.transform.SetParent(source.transform);
        child.AddComponent<BoxCollider>();
        PrefabUtility.SaveAsPrefabAsset(source, PrefabPath);
        UnityEngine.Object.DestroyImmediate(source);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        _guid = AssetDatabase.AssetPathToGUID(PrefabPath);
    }

    [TearDown]
    public void TearDown()
    {
        AssetDatabase.DeleteAsset(PrefabPath);
        if (AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.DeleteAsset(FixturesDir);
        }

        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }
    }

    private GameObject Instantiate(string name)
    {
        var root = (GameObject)PrefabUtility.InstantiatePrefab(
            AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath));
        root.name = name;
        return root;
    }

    private static SnapshotNode ReadRoot(Scene scene, string name) =>
        SceneSnapshotReader.Read(scene).Roots.First(r => r.Name == name);

    private static SnapshotNode ReadRoot(Scene scene, string name, Func<UnityEngine.Object, string?> resolveSceneRef) =>
        SceneSnapshotReader.Read(scene, resolveSceneRef).Roots.First(r => r.Name == name);

    [Test]
    public void InstanceRoot_ReadsSourceGuid_PrefabKeyNonzero_ComponentsAndChildrenEmpty()
    {
        var root = Instantiate("Enemy1");
        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.SaveScene(scene, ScenePath);

        var node = ReadRoot(scene, "Enemy1");

        Assert.AreEqual(_guid, node.SourcePrefabGuid,
            "Instance root did not read the source prefab's GUID");
        Assert.IsNotNull(node.PrefabKey, "Instance root PrefabKey was not populated");
        Assert.AreNotEqual(0UL, node.PrefabKey!.TargetPrefabId, "PrefabKey.TargetPrefabId was zero");
        Assert.IsEmpty(node.Components,
            "Instance root's internal Components were enumerated — the whole instance must read as one unit");
        Assert.IsEmpty(node.Children,
            "Instance root's internal hierarchy was enumerated — reader must not descend into instances");
    }

    [Test]
    public void InstanceRoot_PositionedButNoOverride_AllOverrideCollectionsEmpty()
    {
        var root = Instantiate("Enemy1");
        root.transform.localPosition = new Vector3(1f, 2f, 3f);
        PrefabUtility.RecordPrefabInstancePropertyModifications(root.transform);

        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.SaveScene(scene, ScenePath);

        var node = ReadRoot(scene, "Enemy1");

        Assert.IsNull(node.OpaqueOverrides,
            "A modelled root-transform override falsely leaked into OpaqueOverrides");
        Assert.IsEmpty(node.Overrides,
            "A mere root move falsely leaked into structured Overrides[]");
        Assert.IsEmpty(node.AddedComponents,
            "A mere root move falsely leaked into AddedComponents[]");
        Assert.IsEmpty(node.RemovedComponents,
            "A mere root move falsely leaked into RemovedComponents[]");
        Assert.AreEqual(new Vec3(1f, 2f, 3f), node.Transform.Position,
            "Reader did not reflect the moved root transform");
    }

    // M10 (b6-t1): under the old (M6) contract a root-target property override landed in
    // OpaqueOverrides. Under M10 it is MODELLED — it must land in structured Overrides[] instead,
    // with OpaqueOverrides null. This replaces the pre-M10
    // InstanceRoot_WithGenuinePropertyOverride_OpaqueOverridesNonEmpty test (STALE_TESTS, b6-t1).
    [Test]
    public void InstanceRoot_WithRootComponentPropertyOverride_StructuredOverrideNotOpaque()
    {
        var root = Instantiate("Enemy1");
        var collider = root.GetComponent<BoxCollider>();
        collider.center = new Vector3(9f, 9f, 9f);
        PrefabUtility.RecordPrefabInstancePropertyModifications(collider);

        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.SaveScene(scene, ScenePath);

        var node = ReadRoot(scene, "Enemy1");

        Assert.IsNull(node.OpaqueOverrides,
            "A ROOT-component property override must not land in OpaqueOverrides under M10");
        Assert.AreEqual(3, node.Overrides.Length,
            "A Vector3 override (BoxCollider.center) must lower to THREE PropertyOverride entries " +
            "(.x/.y/.z), never one Vec3-valued entry (banner #2)");
        foreach (var ov in node.Overrides)
        {
            Assert.AreEqual("type:UnityEngine.BoxCollider", ov.Target.PrefabId,
                "Root-target override PrefabId must carry the type sigil, not a GUID:fileID pair");
            Assert.AreEqual(0, ov.Target.ObjectId, "Root-target override ObjectId must be 0 (v0 sigil convention)");
            StringAssert.StartsWith("m_Center", ov.PropertyPath,
                "PropertyPath must be the raw serialized path");
            Assert.IsInstanceOf<ValueNode.Primitive>(ov.Value, "Override Value must be a String primitive");
            Assert.IsNotNull(ov.BaseValue,
                "BaseValue must be populated with the source prefab's CURRENT default for stale detection");
            Assert.IsInstanceOf<ValueNode.Primitive>(ov.BaseValue, "BaseValue must be a String primitive");
        }
    }

    [Test]
    public void InstanceRoot_WithAddedRootComponent_AddedComponentsHasEntry()
    {
        var root = Instantiate("Enemy1");
        root.AddComponent<Light>();

        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.SaveScene(scene, ScenePath);

        var node = ReadRoot(scene, "Enemy1");

        Assert.AreEqual(1, node.AddedComponents.Length, "Added Light did not read into AddedComponents[]");
        var added = node.AddedComponents[0];
        Assert.AreEqual(new OverrideTarget(), added.Target,
            "AddedComponent.Target must be empty (type carried in Component.Type, not Target)");
        Assert.AreEqual("UnityEngine.Light", added.Component.Type.FullName,
            "AddedComponent.Component.Type.FullName did not match the added component's type");
        Assert.IsNull(node.OpaqueOverrides, "An added root component must not leak into OpaqueOverrides");
    }

    [Test]
    public void InstanceRoot_WithRemovedRootComponent_RemovedComponentsHasEntry()
    {
        var root = Instantiate("Enemy1");
        UnityEngine.Object.DestroyImmediate(root.GetComponent<BoxCollider>());

        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.SaveScene(scene, ScenePath);

        var node = ReadRoot(scene, "Enemy1");

        Assert.AreEqual(1, node.RemovedComponents.Length,
            "Removed root BoxCollider did not read into RemovedComponents[]");
        Assert.AreEqual("type:UnityEngine.BoxCollider", node.RemovedComponents[0].PrefabId);
        Assert.AreEqual(0, node.RemovedComponents[0].ObjectId);
        Assert.IsNull(node.OpaqueOverrides, "A removed root component must not leak into OpaqueOverrides");
    }

    [Test]
    public void InstanceRoot_WithAssetObjectReferenceOverride_OverrideReferenceIsAssetRef()
    {
        var root = Instantiate("Enemy1");
        var mr = root.GetComponent<MeshRenderer>();
        mr.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(BlueMatPath);
        PrefabUtility.RecordPrefabInstancePropertyModifications(mr);

        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.SaveScene(scene, ScenePath);

        var node = ReadRoot(scene, "Enemy1");

        var ov = node.Overrides.FirstOrDefault(o => o.Target.PrefabId == "type:UnityEngine.MeshRenderer");
        Assert.IsNotNull(ov, "MeshRenderer material override did not read into structured Overrides[]");
        Assert.IsInstanceOf<ValueNode.AssetRef>(ov!.ObjectReference,
            "A material asset override must lower ObjectReference to AssetRef (got " +
            (ov.ObjectReference?.GetType().Name ?? "null") + ")");
        var assetRef = ((ValueNode.AssetRef)ov.ObjectReference!).Ref;
        Assert.IsNotNull(assetRef, "AssetRef.Ref must not be null for a populated material override");
        Assert.AreEqual(AssetDatabase.AssetPathToGUID(BlueMatPath), assetRef!.Guid,
            "AssetRef did not resolve to the overriding material's GUID");
    }

    [Test]
    public void InstanceRoot_WithSceneObjectReferenceOverride_OverrideReferenceIsObjectRef()
    {
        var root = Instantiate("Enemy1");
        var sceneTarget = new GameObject("SceneTarget");
        var doorOpener = root.GetComponent<DoorOpener>();
        doorOpener.target = sceneTarget;
        PrefabUtility.RecordPrefabInstancePropertyModifications(doorOpener);

        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.SaveScene(scene, ScenePath);

        var map = new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry
                {
                    LogicalId = "SceneTarget", Kind = "GameObject",
                    GlobalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(sceneTarget).ToString(),
                },
            },
        };
        var resolveSceneRef = ObjectReferenceResolver.BuildSceneRefResolver(map);

        var node = ReadRoot(scene, "Enemy1", resolveSceneRef);

        var ov = node.Overrides.FirstOrDefault(o => o.Target.PrefabId == "type:DoorOpener");
        Assert.IsNotNull(ov, "DoorOpener.target override did not read into structured Overrides[]");
        Assert.AreEqual("target", ov!.PropertyPath);
        Assert.IsInstanceOf<ValueNode.ObjectRef>(ov.ObjectReference,
            "An in-scene target override must lower ObjectReference to ObjectRef (got " +
            (ov.ObjectReference?.GetType().Name ?? "null") + ")");
        Assert.AreEqual("SceneTarget", ((ValueNode.ObjectRef)ov.ObjectReference!).TargetLogicalId,
            "ObjectRef did not resolve to the mapped target's LogicalId");
    }

    [Test]
    public void InstanceRoot_WithNestedTargetOverride_StaysOpaque_StructuredCollectionsEmpty()
    {
        var root = Instantiate("Enemy1");
        var childCollider = root.transform.Find("Child").GetComponent<BoxCollider>();
        childCollider.center = new Vector3(5f, 5f, 5f);
        PrefabUtility.RecordPrefabInstancePropertyModifications(childCollider);

        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.SaveScene(scene, ScenePath);

        var node = ReadRoot(scene, "Enemy1");

        Assert.IsNotNull(node.OpaqueOverrides, "A NESTED-target override must stay in OpaqueOverrides (M10 v0 defers below-root targets)");
        Assert.IsNotEmpty(node.OpaqueOverrides!.RawToken, "OpaqueOverrides token was empty");
        Assert.IsEmpty(node.Overrides, "A NESTED-target override must not leak into structured Overrides[]");
        Assert.IsEmpty(node.AddedComponents, "A NESTED-target override must not leak into AddedComponents[]");
        Assert.IsEmpty(node.RemovedComponents, "A NESTED-target override must not leak into RemovedComponents[]");
    }

    [Test]
    public void PlainGameObject_AllThreePrefabFieldsNull()
    {
        new GameObject("PlainObject");
        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.SaveScene(scene, ScenePath);

        var node = ReadRoot(scene, "PlainObject");

        Assert.IsNull(node.SourcePrefabGuid, "Plain GameObject read a non-null SourcePrefabGuid");
        Assert.IsNull(node.PrefabKey, "Plain GameObject read a non-null PrefabKey");
        Assert.IsNull(node.OpaqueOverrides, "Plain GameObject read a non-null OpaqueOverrides");
        Assert.IsEmpty(node.Overrides, "Plain GameObject read non-empty Overrides[]");
        Assert.IsEmpty(node.AddedComponents, "Plain GameObject read non-empty AddedComponents[]");
        Assert.IsEmpty(node.RemovedComponents, "Plain GameObject read non-empty RemovedComponents[]");
    }
}
