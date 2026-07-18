using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;
using SceneBuilder.Core.Model;

// b5-t2: SceneSnapshotReader detects prefab instances and reads overrides opaquely
// (specs/07-m6-prefab-instances.md). Exercises the read path against a live editor scene + a real
// temp .prefab asset: the instance ROOT's SnapshotNode must carry SourcePrefabGuid/PrefabKey, treat
// the whole instance as one unit (no Components/Children enumerated), exclude modelled root
// transform/name/order mods from OpaqueOverrides (so a mere move never falsely flags an override),
// and surface a genuine non-root-transform property override as a nonempty opaque token. A plain
// (non-instance) GameObject must read all three fields null.
public class SnapshotReaderPrefabTests
{
    private const string FixturesDir = "Assets/GateTests/Fixtures_M6PrefabSnapshot";
    private const string PrefabPath = FixturesDir + "/M6_SnapshotEnemy.prefab";
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

        var source = new GameObject("M6_SnapshotEnemy_Source");
        source.AddComponent<BoxCollider>();
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
    public void InstanceRoot_PositionedButNoOverride_OpaqueOverridesNull()
    {
        var root = Instantiate("Enemy1");
        root.transform.localPosition = new Vector3(1f, 2f, 3f);
        PrefabUtility.RecordPrefabInstancePropertyModifications(root.transform);

        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.SaveScene(scene, ScenePath);

        var node = ReadRoot(scene, "Enemy1");

        Assert.IsNull(node.OpaqueOverrides,
            "A modelled root-transform override falsely leaked into OpaqueOverrides");
        Assert.AreEqual(new Vec3(1f, 2f, 3f), node.Transform.Position,
            "Reader did not reflect the moved root transform");
    }

    [Test]
    public void InstanceRoot_WithGenuinePropertyOverride_OpaqueOverridesNonEmpty()
    {
        var root = Instantiate("Enemy1");
        var collider = root.GetComponent<BoxCollider>();
        collider.center = new Vector3(9f, 9f, 9f);
        PrefabUtility.RecordPrefabInstancePropertyModifications(collider);

        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.SaveScene(scene, ScenePath);

        var node = ReadRoot(scene, "Enemy1");

        Assert.IsNotNull(node.OpaqueOverrides, "A genuine non-transform property override was not captured");
        Assert.IsNotEmpty(node.OpaqueOverrides!.RawToken, "OpaqueOverrides token was empty");
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
    }
}
