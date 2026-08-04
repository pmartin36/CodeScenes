using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;
using SceneBuilder.Core.Model;

// m-nested-props b5-t2 (specs/24-nested-prefab-overrides.md): undoes the M6/M10 below-root
// opaque-collapse. A PropertyModification/added-GameObject/removed-GameObject whose target is
// BELOW the instance root now resolves (via the live source->live descendant map) to a real
// SubKey + ChildPath and lowers to a structured PropertyOverride/AddedGameObject/RemovedGameObject
// entry instead of OpaqueOverrides. The instance still reads as ONE SnapshotNode (no sub-objects
// emitted as regular Components/Children). Uses the shared PrefabFacadeFixture Tank (b5-t1):
// Tank -> LeftTurret/RightTurret (nested Turret instances) -> Barrel(Light)/Antenna(MeshRenderer).
public class NestedOverrideReadTests
{
    private const string Dir = "Assets/GateTests/Fixtures_NestedOverrideRead";
    private const string ScenePath = "Assets/GateTests/__NestedOverrideReadTemp.unity";

    private PrefabFacadeFixture.Handles _handles;

    [SetUp]
    public void SetUp()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        _handles = PrefabFacadeFixture.Create(Dir);
    }

    [TearDown]
    public void TearDown()
    {
        PrefabFacadeFixture.Delete(Dir);
        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }
    }

    private GameObject InstantiateTank()
    {
        var root = (GameObject)PrefabUtility.InstantiatePrefab(
            AssetDatabase.LoadAssetAtPath<GameObject>(_handles.TankPath));
        root.name = "Tank";
        return root;
    }

    private static SnapshotNode ReadTank(Scene scene) =>
        SceneSnapshotReader.Read(scene, resolveSceneRef: null).Roots.First(r => r.Name == "Tank");

    private static PrefabInstanceKey PairKey(GameObject go)
    {
        var goid = GlobalObjectId.GetGlobalObjectIdSlow(go);
        return new PrefabInstanceKey { TargetPrefabId = goid.targetPrefabId, TargetObjectId = goid.targetObjectId };
    }

    [Test]
    public void NestedBarrelLightIntensity_LowersToStructuredOverride_WithLivePairKey_NotOpaque()
    {
        var tank = InstantiateTank();
        var barrel = tank.transform.Find("LeftTurret/Barrel").gameObject;
        var light = barrel.GetComponent<Light>();
        light.intensity = 5f;
        PrefabUtility.RecordPrefabInstancePropertyModifications(light);

        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.SaveScene(scene, ScenePath);

        var node = ReadTank(scene);

        Assert.IsNull(node.OpaqueOverrides,
            "A below-root Light.intensity override must no longer stay in OpaqueOverrides (b5-t2 undoes the opaque-collapse)");

        var expectedKey = PairKey(barrel);
        var ov = node.Overrides.FirstOrDefault(o =>
            o.Target.ComponentType == "UnityEngine.Light" && o.PropertyPath == "m_Intensity");
        Assert.IsNotNull(ov, "Barrel Light.intensity override did not lower into structured Overrides[]");
        Assert.AreEqual(expectedKey, ov!.Target.SubKey,
            "Nested override's Target.SubKey did not resolve to the live Barrel's own pair-key");
        Assert.AreEqual("LeftTurret/Barrel", ov.Target.ChildPath,
            "Nested override's Target.ChildPath did not name the live path to the Barrel");
    }

    [Test]
    public void AddedChildUnderSubObject_ReadsIntoAddedGameObjects_ParentSubKeyIsParentPair()
    {
        var tank = InstantiateTank();
        var barrel = tank.transform.Find("LeftTurret/Barrel").gameObject;
        var newChild = new GameObject("NewLight");
        newChild.transform.SetParent(barrel.transform);

        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.SaveScene(scene, ScenePath);

        var node = ReadTank(scene);

        var added = node.AddedGameObjects.FirstOrDefault(a => a.Node.Name == "NewLight");
        Assert.IsNotNull(added, "A child dragged under a sub-object did not read into AddedGameObjects[]");
        Assert.AreEqual(PairKey(barrel), added!.Parent.SubKey,
            "AddedGameObject.Parent.SubKey did not resolve to the live sub-object parent's pair-key");
    }

    [Test]
    public void RemovedSourceChild_ReadsIntoRemovedGameObjects_ChildPathNamesIt()
    {
        var tank = InstantiateTank();
        var antenna = tank.transform.Find("LeftTurret/Antenna").gameObject;
        Object.DestroyImmediate(antenna);

        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.SaveScene(scene, ScenePath);

        var node = ReadTank(scene);

        Assert.IsTrue(node.RemovedGameObjects.Any(r => r.ChildPath == "LeftTurret/Antenna"),
            "Removing a source child did not read into RemovedGameObjects[] with the expected ChildPath");
    }

    [Test]
    public void SubObjectNameMod_IsFiltered_NoOverride()
    {
        var tank = InstantiateTank();
        var antenna = tank.transform.Find("LeftTurret/Antenna").gameObject;
        antenna.name = "AntennaRenamed";
        PrefabUtility.RecordPrefabInstancePropertyModifications(antenna);

        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.SaveScene(scene, ScenePath);

        var node = ReadTank(scene);

        Assert.IsEmpty(node.Overrides,
            "A sub-object m_Name bookkeeping mod must be filtered, never lowered to a structured PropertyOverride");
        Assert.IsNull(node.OpaqueOverrides,
            "A sub-object m_Name bookkeeping mod must be filtered, never leak into OpaqueOverrides either");
    }
}
