using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

// b6-t1 (specs/25-typed-prefab-facades.md): structural smoke test for the shared canonical fixture
// (PrefabFacadeFixture) — guards the ONE fixture every other b6 EditMode test depends on against
// drift. Asserts: LeftTurret/RightTurret are genuine nested-prefab instances of Turret.prefab;
// Chassis has children in order ["Wheel","Wheel","Front Wheel"] with the two "Wheel"s distinct
// transforms; each turret's Barrel carries a Light component.
public class PrefabFacadeFixtureSmokeTests
{
    private const string Dir = "Assets/GateTests/Fixtures_PrefabFacadeSmoke";

    [TearDown]
    public void TearDown()
    {
        try
        {
            PrefabFacadeFixture.Delete(Dir);
        }
        catch (System.NotImplementedException)
        {
            // fixture teardown not yet implemented (RED phase) — nothing to clean up.
        }
    }

    [Test]
    public void Fixture_Authors_TankWithTwoNestedTurrets()
    {
        var handles = PrefabFacadeFixture.Create(Dir);

        var tank = AssetDatabase.LoadAssetAtPath<GameObject>(handles.TankPath);
        Assert.IsNotNull(tank, "Tank prefab asset did not load from " + handles.TankPath);

        var left = tank.transform.Find("LeftTurret");
        var right = tank.transform.Find("RightTurret");
        Assert.IsNotNull(left, "Tank has no 'LeftTurret' child");
        Assert.IsNotNull(right, "Tank has no 'RightTurret' child");

        var leftSource = PrefabUtility.GetCorrespondingObjectFromSource(left.gameObject);
        var rightSource = PrefabUtility.GetCorrespondingObjectFromSource(right.gameObject);
        Assert.IsNotNull(leftSource, "LeftTurret is not a nested-prefab instance (no corresponding source object)");
        Assert.IsNotNull(rightSource, "RightTurret is not a nested-prefab instance (no corresponding source object)");
        Assert.AreEqual(handles.TurretPath, AssetDatabase.GetAssetPath(leftSource),
            "LeftTurret's nested-prefab source is not Turret.prefab");
        Assert.AreEqual(handles.TurretPath, AssetDatabase.GetAssetPath(rightSource),
            "RightTurret's nested-prefab source is not Turret.prefab");
    }

    [Test]
    public void Fixture_Chassis_HasDuplicateWheelSiblings()
    {
        var handles = PrefabFacadeFixture.Create(Dir);
        var tank = AssetDatabase.LoadAssetAtPath<GameObject>(handles.TankPath);

        var chassis = tank.transform.Find("Chassis");
        Assert.IsNotNull(chassis, "Tank has no 'Chassis' child");

        var names = Enumerable.Range(0, chassis.childCount).Select(i => chassis.GetChild(i).name).ToArray();
        Assert.AreEqual(new[] { "Wheel", "Wheel", "Front Wheel" }, names,
            "Chassis children were not exactly [\"Wheel\",\"Wheel\",\"Front Wheel\"] in order");

        Assert.AreNotSame(chassis.GetChild(0), chassis.GetChild(1),
            "The two 'Wheel' siblings must be distinct transforms");
    }

    [Test]
    public void Fixture_Barrel_CarriesLight()
    {
        var handles = PrefabFacadeFixture.Create(Dir);
        var tank = AssetDatabase.LoadAssetAtPath<GameObject>(handles.TankPath);

        foreach (var turretName in new[] { "LeftTurret", "RightTurret" })
        {
            var barrel = tank.transform.Find(turretName + "/Barrel");
            Assert.IsNotNull(barrel, $"{turretName} has no 'Barrel' child");
            Assert.IsNotNull(barrel.GetComponent<Light>(), $"{turretName}/Barrel has no Light component");
        }
    }

    // m-nested-props b5-t1 (specs/24-nested-prefab-overrides.md): Antenna is the fixture's
    // removed-component / removed-child target (`.On(sel => sel.Turret.Antenna, a =>
    // a.RemoveComponent<MeshRenderer>())` / `.RemoveChild("Turret/Antenna")`), depth>=2 present on
    // both nested Turret instances.
    [Test]
    public void Fixture_Turret_HasAntennaWithMeshRenderer()
    {
        var handles = PrefabFacadeFixture.Create(Dir);
        var tank = AssetDatabase.LoadAssetAtPath<GameObject>(handles.TankPath);

        foreach (var turretName in new[] { "LeftTurret", "RightTurret" })
        {
            var antenna = tank.transform.Find(turretName + "/Antenna");
            Assert.IsNotNull(antenna, $"{turretName} has no 'Antenna' child");
            Assert.IsNotNull(antenna.GetComponent<MeshRenderer>(), $"{turretName}/Antenna has no MeshRenderer component");
        }
    }
}
