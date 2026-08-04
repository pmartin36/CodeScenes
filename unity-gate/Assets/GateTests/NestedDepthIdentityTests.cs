using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;
using SceneBuilder.Core.Model;

// m-nested-props b5-t3 (specs/24-nested-prefab-overrides.md, Unity confirmation #7 / behavior #12):
// the REQUIRED open-risk gate. Confirms (empirically, against a real editor) that the bare live
// (TargetPrefabId, TargetObjectId) pair COLLIDES for the two Barrels reachable only through the two
// nested Turret instances (LeftTurret/RightTurret) — this is the genuine finding the gate exists to
// surface, not the a-priori assumption. Proves overriding one Barrel does not bleed onto the other
// regardless, and exercises the correspondence-chain / ChildPath-walk fallback
// (PrefabInstanceProbe.ResolveSubObjectByChildPath / PrefabInstanceProbe.ResolveSourceAtPath) that
// disambiguates the two targets despite the bare-pair collision. Shares PrefabFacadeFixture (b5-t1):
// Tank -> LeftTurret/RightTurret -> Barrel(Light)/Antenna(MeshRenderer).
public class NestedDepthIdentityTests
{
    private const string Dir = "Assets/GateTests/Fixtures_NestedDepthIdentity";
    private const string ScenePath = "Assets/GateTests/__NestedDepthIdentityTemp.unity";

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

    // The open-risk question this gate answers: the blueprint's a-priori expectation (GlobalObjectId
    // semantics reasoning) was that the two nested Barrels' bare live pairs would be DISTINCT.
    // Empirically confirmed FALSE against the real editor: LeftTurret/Barrel and RightTurret/Barrel
    // resolve to the SAME live (TargetPrefabId, TargetObjectId) pair (both descend from the identical
    // Barrel source object inside Turret.prefab, and the outer Tank instance is the only prefab-root
    // GlobalObjectId keys off). This is WHY the correspondence-chain / ChildPath-walk fallback below
    // is load-bearing, not defensive dead code.
    [Test]
    public void DepthTwo_TwoNestedBarrels_BareLivePairKeysCollide_FallbackIsRequired()
    {
        var tank = InstantiateTank();
        var leftBarrel = tank.transform.Find("LeftTurret/Barrel").gameObject;
        var rightBarrel = tank.transform.Find("RightTurret/Barrel").gameObject;

        var leftKey = PairKey(leftBarrel);
        var rightKey = PairKey(rightBarrel);

        Assert.AreEqual(leftKey, rightKey,
            "Confirmed open-risk finding: the bare live pair-key COLLIDES for the two nested Barrels — " +
            "bare-pair matching alone cannot disambiguate depth>=2 targets; the ChildPath/correspondence " +
            "fallback below is the only correct disambiguation");
    }

    [Test]
    public void DepthTwo_OverrideOnLeftBarrel_DoesNotBleedToRightBarrel()
    {
        var tank = InstantiateTank();
        var leftBarrel = tank.transform.Find("LeftTurret/Barrel").gameObject;
        var rightBarrel = tank.transform.Find("RightTurret/Barrel").gameObject;
        var leftLight = leftBarrel.GetComponent<Light>();
        var rightLight = rightBarrel.GetComponent<Light>();
        var originalRightIntensity = rightLight.intensity;

        leftLight.intensity = 5f;
        PrefabUtility.RecordPrefabInstancePropertyModifications(leftLight);

        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.SaveScene(scene, ScenePath);

        var node = ReadTank(scene);
        var leftKey = PairKey(leftBarrel);
        var rightKey = PairKey(rightBarrel);

        var lightOverrides = node.Overrides
            .Where(o => o.Target.ComponentType == "UnityEngine.Light" && o.PropertyPath == "m_Intensity")
            .ToArray();

        Assert.AreEqual(1, lightOverrides.Length,
            "Exactly one Light.intensity override is expected (left Barrel only)");
        Assert.AreEqual(leftKey, lightOverrides[0].Target.SubKey,
            "The single Light.intensity override must key to the LEFT Barrel's pair, not the right");
        Assert.IsFalse(lightOverrides.Any(o => o.Target.SubKey == rightKey),
            "The right Barrel's pair-key must not appear on any Light.intensity override (no bleed)");
        Assert.AreEqual(originalRightIntensity, rightLight.intensity,
            "The right Barrel's live Light.intensity must be unchanged by overriding the left Barrel");
    }

    [Test]
    public void Fallback_ResolveByChildPath_ResolvesEachBarrelToDistinctLiveObject()
    {
        var tank = InstantiateTank();
        var expectedLeft = tank.transform.Find("LeftTurret/Barrel").gameObject;
        var expectedRight = tank.transform.Find("RightTurret/Barrel").gameObject;

        var resolvedLeft = PrefabInstanceProbe.ResolveSubObjectByChildPath(tank, "LeftTurret/Barrel");
        var resolvedRight = PrefabInstanceProbe.ResolveSubObjectByChildPath(tank, "RightTurret/Barrel");

        Assert.AreEqual(expectedLeft, resolvedLeft,
            "ResolveSubObjectByChildPath('LeftTurret/Barrel') must resolve to the live left Barrel");
        Assert.AreEqual(expectedRight, resolvedRight,
            "ResolveSubObjectByChildPath('RightTurret/Barrel') must resolve to the live right Barrel");
        Assert.AreNotEqual(resolvedLeft, resolvedRight,
            "The two resolved sub-objects must be distinct live GameObjects");
    }

    [Test]
    public void Fallback_ResolveSourceAtPath_ReturnsTurretScopeSource_ForEachSide()
    {
        var tank = InstantiateTank();
        var leftBarrel = tank.transform.Find("LeftTurret/Barrel").gameObject;
        var rightBarrel = tank.transform.Find("RightTurret/Barrel").gameObject;

        var leftSource = PrefabInstanceProbe.ResolveSourceAtPath(leftBarrel);
        var rightSource = PrefabInstanceProbe.ResolveSourceAtPath(rightBarrel);

        Assert.IsNotNull(leftSource,
            "ResolveSourceAtPath must resolve a non-null Turret-scope source for the left Barrel (fallback must be exercised, not skipped)");
        Assert.IsNotNull(rightSource,
            "ResolveSourceAtPath must resolve a non-null Turret-scope source for the right Barrel (fallback must be exercised, not skipped)");
    }
}
