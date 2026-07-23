using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using SceneBuilder.Editor;

// b6-t6 (specs/25-typed-prefab-facades.md): REQUIRED open-risk gate. The canonical Tank fixture
// (b6-t1) nests Turret TWICE (LeftTurret/RightTurret) at depth>=2. Core's identity model assumes a
// bare (GUID,LocalId) pair is unique — but the two nested-instance sub-objects share an IDENTICAL
// inner (Turret-scope) LocalId, so uniqueness depends entirely on the correspondence-chain
// disambiguation (PrefabHierarchyReader.ResolveNestedPrefabGuid / GetCorrespondingObjectFromSourceAtPath)
// giving each nested-instance ROOT a distinct durable LocalId. This test proves that disambiguation
// holds; it must FAIL if the reader ever collapses the two nested-instance roots to one identity.
public class PrefabFacadeNestedDepthUniquenessTests
{
    private const string Dir = "Assets/GateTests/Fixtures_PrefabFacadeNestedDepth";

    [TearDown]
    public void TearDown()
    {
        PrefabFacadeFixture.Delete(Dir);
    }

    [Test]
    public void DepthTwo_TwoNestedTurretRoots_HaveDistinctDurableLocalIds()
    {
        var handles = PrefabFacadeFixture.Create(Dir);

        var tank = PrefabHierarchyReader.Read(handles.TankPath);

        var left = tank.Root.Children.FirstOrDefault(c => c.RealName == "LeftTurret");
        var right = tank.Root.Children.FirstOrDefault(c => c.RealName == "RightTurret");
        Assert.IsNotNull(left, "Tank read has no 'LeftTurret' child node");
        Assert.IsNotNull(right, "Tank read has no 'RightTurret' child node");

        Assert.AreEqual(handles.TurretGuid, left.NestedPrefabGuid, "LeftTurret node is not detected as a Turret nested-prefab instance");
        Assert.AreEqual(handles.TurretGuid, right.NestedPrefabGuid, "RightTurret node is not detected as a Turret nested-prefab instance");
        Assert.AreEqual(0, left.Children.Count, "LeftTurret's nested subtree must be composed by reference (empty Children), not inlined");
        Assert.AreEqual(0, right.Children.Count, "RightTurret's nested subtree must be composed by reference (empty Children), not inlined");

        Assert.AreNotEqual(0, left.LocalId, "LeftTurret has no durable LocalId");
        Assert.AreNotEqual(0, right.LocalId, "RightTurret has no durable LocalId");
        Assert.AreNotEqual(left.LocalId, right.LocalId,
            "The two nested Turret instance roots collapsed to the SAME durable LocalId — depth>=2 identity is not disambiguated");
    }

    [Test]
    public void DepthTwo_BarrelSubObjects_ResolveToDistinctPairs_ViaCorrespondenceChain()
    {
        var handles = PrefabFacadeFixture.Create(Dir);

        var tank = AssetDatabase.LoadAssetAtPath<GameObject>(handles.TankPath);
        Assert.IsNotNull(tank, "Tank prefab asset did not load from " + handles.TankPath);

        long ResolveRootLocalId(string side)
        {
            var rootGo = tank.transform.Find(side).gameObject;
            Assert.IsTrue(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(rootGo, out _, out var localId),
                $"Could not resolve durable id for {side}");
            return localId;
        }

        long ResolveInnerBarrelLocalId(string side)
        {
            var barrelGo = tank.transform.Find(side + "/Barrel").gameObject;
            var innerBarrel = PrefabUtility.GetCorrespondingObjectFromSourceAtPath(barrelGo, handles.TurretPath);
            Assert.IsNotNull(innerBarrel, $"{side}/Barrel did not resolve via the correspondence chain to the Turret-scope Barrel");
            Assert.IsTrue(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(innerBarrel, out _, out var localId),
                $"Could not resolve inner durable id for {side}/Barrel");
            return localId;
        }

        var leftRootLocalId = ResolveRootLocalId("LeftTurret");
        var rightRootLocalId = ResolveRootLocalId("RightTurret");
        var leftInnerBarrelLocalId = ResolveInnerBarrelLocalId("LeftTurret");
        var rightInnerBarrelLocalId = ResolveInnerBarrelLocalId("RightTurret");

        // The bare inner (Turret-scope) LocalId collides across instances -- this is the open risk.
        Assert.AreEqual(leftInnerBarrelLocalId, rightInnerBarrelLocalId,
            "Expected the inner Turret-scope Barrel LocalId to be identical across both instances (the collision this gate guards against misuse of)");

        // The nested-root LocalId disambiguates the two instances.
        Assert.AreNotEqual(leftRootLocalId, rightRootLocalId,
            "The two nested Turret instance roots collapsed to the same durable LocalId");

        // Composed (rootLocalId, innerBarrelLocalId) pairs are therefore distinct across instances.
        Assert.AreNotEqual(
            (leftRootLocalId, leftInnerBarrelLocalId),
            (rightRootLocalId, rightInnerBarrelLocalId),
            "Composed (rootLocalId, innerBarrelLocalId) pairs must be distinct across the two nested Barrel sub-objects");
    }
}
