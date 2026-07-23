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
using SceneBuilder.Core.Plan;

// m-nested-props b6-t1 (specs/24-nested-prefab-overrides.md): the write-side generalization of
// InstanceOverrideExecutor from "component on the instance root" to "component on a resolved
// below-root sub-object" (ChildPath-walk resolution, SubKey default pre-bootstrap). Uses the shared
// PrefabFacadeFixture Tank -> LeftTurret/Barrel(Light) / LeftTurret/Antenna(MeshRenderer), mirroring
// PlanExecutorInstanceOverrideTests' InstantiateAndMap idiom (InstantiatePrefab op + hand-built
// IdentityMap) and NestedOverrideReadTests' PairKey helper.
public class NestedOverrideWriteTests
{
    private const string Dir = "Assets/GateTests/Fixtures_NestedOverrideWrite";
    private const string ScenePath = "Assets/GateTests/__NestedOverrideWriteTemp.unity";

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

    private static OverrideTarget BarrelLightTarget => new()
    {
        ComponentType = "UnityEngine.Light", ChildPath = "LeftTurret/Barrel",
    };

    private static OverrideTarget AntennaMeshRendererTarget => new()
    {
        ComponentType = "UnityEngine.MeshRenderer", ChildPath = "LeftTurret/Antenna",
    };

    // Mirrors PlanExecutorInstanceOverrideTests.InstantiateAndMap: instantiates the fixture Tank via
    // its own PlanExecutor.Execute call (empty IdentityMap), saves the scene (GlobalObjectId requires
    // a saved scene), then returns the live Tank root plus a Kind="PrefabInstance" IdentityMap
    // pre-resolving "Tank" for a SECOND Execute call against the already-existing instance.
    private GameObject InstantiateAndMapTank(Scene scene, out IdentityMap map)
    {
        var createPlan = new Plan
        {
            Ops = new PlanOp[]
            {
                new InstantiatePrefab { LogicalId = "Tank", Guid = _handles.TankGuid, ParentLogicalId = null, SiblingIndex = 0 },
            },
        };
        var createResult = PlanExecutor.Execute(createPlan, new IdentityMap(), scene);
        var root = createResult.GameObjectsByLogicalId["Tank"];

        EditorSceneManager.SaveScene(scene, ScenePath);

        var goid = GlobalObjectId.GetGlobalObjectIdSlow(root).ToString();
        map = new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry { LogicalId = "Tank", GlobalObjectId = goid, Kind = "PrefabInstance" },
            },
        };
        return root;
    }

    [Test]
    public void NestedBarrelLightIntensity_Write_ShowsBoldOverride_PreservesGlobalObjectIdsAtDepth()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var tank = InstantiateAndMapTank(scene, out var map);
        var leftTurret = tank.transform.Find("LeftTurret").gameObject;
        var barrel = tank.transform.Find("LeftTurret/Barrel").gameObject;
        var goidTankBefore = GlobalObjectId.GetGlobalObjectIdSlow(tank).ToString();
        var goidTurretBefore = GlobalObjectId.GetGlobalObjectIdSlow(leftTurret).ToString();
        var goidBarrelBefore = GlobalObjectId.GetGlobalObjectIdSlow(barrel).ToString();

        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new SetInstanceOverride
                {
                    LogicalId = "Tank", Target = BarrelLightTarget,
                    PropertyPath = "m_Intensity", Value = ValueNode.Primitive.Float(4f),
                },
            },
        };

        PlanExecutor.Execute(plan, map, scene);

        var barrelLight = barrel.GetComponent<Light>();
        Assert.AreEqual(4f, barrelLight.intensity, 0.0001f,
            "SetInstanceOverride with a nested Target.ChildPath did not write the live Barrel Light.intensity");

        var mods = PrefabUtility.GetPropertyModifications(tank) ?? Array.Empty<PropertyModification>();
        Assert.IsTrue(mods.Any(m => m.propertyPath == "m_Intensity" && m.value == "4"),
            "Nested SetInstanceOverride did not produce a bold m_Intensity property modification on the instance");

        Assert.AreEqual(tank, PrefabUtility.GetOutermostPrefabInstanceRoot(barrel),
            "Nested SetInstanceOverride re-instantiated instead of writing in place");
        Assert.AreEqual(goidTankBefore, GlobalObjectId.GetGlobalObjectIdSlow(tank).ToString(),
            "Tank GlobalObjectId changed — nested write must never re-instantiate the outer instance");
        Assert.AreEqual(goidTurretBefore, GlobalObjectId.GetGlobalObjectIdSlow(leftTurret).ToString(),
            "LeftTurret GlobalObjectId changed — nested write must never re-instantiate the nested instance");
        Assert.AreEqual(goidBarrelBefore, GlobalObjectId.GetGlobalObjectIdSlow(barrel).ToString(),
            "Barrel GlobalObjectId changed — nested write must never re-instantiate the sub-object");
    }

    [Test]
    public void NestedOverride_Revert_ClearsOverride_InPlace_NoStaleMod()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var tank = InstantiateAndMapTank(scene, out var map);
        var barrel = tank.transform.Find("LeftTurret/Barrel").gameObject;
        var barrelLight = barrel.GetComponent<Light>();
        var defaultIntensity = barrelLight.intensity;
        barrelLight.intensity = 5f;
        PrefabUtility.RecordPrefabInstancePropertyModifications(barrelLight);
        Assert.IsTrue((PrefabUtility.GetPropertyModifications(tank) ?? Array.Empty<PropertyModification>())
            .Any(m => m.propertyPath == "m_Intensity"),
            "Setup: manual nested intensity override was not recorded");
        var goidBarrelBefore = GlobalObjectId.GetGlobalObjectIdSlow(barrel).ToString();

        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new RevertInstanceOverride { LogicalId = "Tank", Target = BarrelLightTarget, PropertyPath = "m_Intensity" },
            },
        };

        PlanExecutor.Execute(plan, map, scene);

        Assert.AreEqual(defaultIntensity, barrelLight.intensity, 0.0001f,
            "RevertInstanceOverride on a nested Target did not restore the Barrel Light.intensity default");
        var mods = PrefabUtility.GetPropertyModifications(tank) ?? Array.Empty<PropertyModification>();
        Assert.IsFalse(mods.Any(m => m.propertyPath == "m_Intensity"),
            "RevertInstanceOverride on a nested Target did not clear the bold m_Intensity override (stale mod remains)");
        Assert.AreEqual(goidBarrelBefore, GlobalObjectId.GetGlobalObjectIdSlow(barrel).ToString(),
            "Barrel GlobalObjectId changed — nested revert must never re-instantiate the sub-object");
    }

    [Test]
    public void NestedAddComponent_OnBarrel_RecordsAddedComponentOnSubObject()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var tank = InstantiateAndMapTank(scene, out var map);
        var barrel = tank.transform.Find("LeftTurret/Barrel").gameObject;
        var goidBarrelBefore = GlobalObjectId.GetGlobalObjectIdSlow(barrel).ToString();

        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new AddInstanceComponent
                {
                    LogicalId = "Tank", Target = new OverrideTarget { ChildPath = "LeftTurret/Barrel" },
                    Component = new ComponentData
                    {
                        LogicalId = "Tank/UnityEngine.AudioSource#0", Type = new TypeRef("UnityEngine.AudioSource"), Fields = FieldMap.Empty,
                    },
                },
            },
        };

        PlanExecutor.Execute(plan, map, scene);

        var audioSource = barrel.GetComponent<AudioSource>();
        Assert.IsNotNull(audioSource,
            "AddInstanceComponent with a nested Target did not add the component to the live Barrel sub-object");

        var added = PrefabUtility.GetAddedComponents(tank) ?? new System.Collections.Generic.List<UnityEditor.SceneManagement.AddedComponent>();
        Assert.IsTrue(added.Any(a => a.instanceComponent is AudioSource && a.instanceComponent.transform == barrel.transform),
            "Nested AddInstanceComponent was not recorded as an added component owned by the Barrel sub-object");
        Assert.AreEqual(goidBarrelBefore, GlobalObjectId.GetGlobalObjectIdSlow(barrel).ToString(),
            "Barrel GlobalObjectId changed — nested add-component must never re-instantiate the sub-object");
    }

    [Test]
    public void NestedRemoveComponent_OnAntenna_RemovesIt()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var tank = InstantiateAndMapTank(scene, out var map);
        var antenna = tank.transform.Find("LeftTurret/Antenna").gameObject;
        Assert.IsNotNull(antenna.GetComponent<MeshRenderer>(), "Setup: Antenna did not carry a MeshRenderer");
        var goidAntennaBefore = GlobalObjectId.GetGlobalObjectIdSlow(antenna).ToString();

        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new RemoveInstanceComponent { LogicalId = "Tank", Target = AntennaMeshRendererTarget },
            },
        };

        PlanExecutor.Execute(plan, map, scene);

        Assert.IsNull(antenna.GetComponent<MeshRenderer>(),
            "RemoveInstanceComponent with a nested Target did not remove the live Antenna MeshRenderer");
        Assert.AreEqual(goidAntennaBefore, GlobalObjectId.GetGlobalObjectIdSlow(antenna).ToString(),
            "Antenna GlobalObjectId changed — nested remove-component must never re-instantiate the sub-object");
    }
}
