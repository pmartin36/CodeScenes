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

// m-nested-props b6-t2 (specs/24-nested-prefab-overrides.md checklist #5/#6): routes the four
// child-GameObject Plan ops (AddInstanceChild/RemoveInstanceChild/RevertAddedChild/
// RevertRemovedChild) through PlanExecutor -> InstanceOverrideExecutor IN PLACE against a live
// prefab instance, mirroring NestedOverrideWriteTests' InstantiateAndMapTank idiom and the
// shared PrefabFacadeFixture (Tank -> LeftTurret/Barrel(Light), LeftTurret/Antenna(MeshRenderer)).
public class NestedChildGameObjectTests
{
    private const string Dir = "Assets/GateTests/Fixtures_NestedChildGameObject";
    private const string ScenePath = "Assets/GateTests/__NestedChildGameObjectTemp.unity";

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

    private static OverrideTarget LeftTurretTarget => new()
    {
        ChildPath = "LeftTurret",
    };

    private static OverrideTarget AntennaTarget => new()
    {
        ChildPath = "LeftTurret/Antenna",
    };

    // Mirrors NestedOverrideWriteTests.InstantiateAndMapTank verbatim (per-file idiom, matching
    // PlanExecutorInstanceOverrideTests/NestedOverrideWriteTests convention).
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

    private static GameObjectNode MuzzleFlashNode => new()
    {
        LogicalId = "Tank/MuzzleFlash",
        Name = "MuzzleFlash",
        Components = new[]
        {
            new ComponentData
            {
                LogicalId = "Tank/MuzzleFlash/UnityEngine.Light#0",
                Type = new TypeRef("UnityEngine.Light"),
                Fields = new FieldMap(new[]
                {
                    new System.Collections.Generic.KeyValuePair<string, ValueNode>(
                        "m_Intensity", ValueNode.Primitive.Float(7f)),
                }),
            },
        },
    };

    [Test]
    public void AddChild_UnderNestedTurret_CreatesLiveChild_RecordsAddedGameObject_NoReinstantiate()
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
                new AddInstanceChild { LogicalId = "Tank", Target = LeftTurretTarget, Node = MuzzleFlashNode },
            },
        };

        PlanExecutor.Execute(plan, map, scene);

        var muzzleFlash = leftTurret.transform.Find("MuzzleFlash");
        Assert.IsNotNull(muzzleFlash,
            "AddInstanceChild did not create a live \"MuzzleFlash\" child under the resolved LeftTurret sub-object");

        var light = muzzleFlash!.GetComponent<Light>();
        Assert.IsNotNull(light, "AddInstanceChild did not apply the child's authored Light component");
        Assert.AreEqual(7f, light.intensity, 0.0001f, "AddInstanceChild did not apply the child's authored field");

        var added = PrefabUtility.GetAddedGameObjects(tank) ?? new System.Collections.Generic.List<UnityEditor.SceneManagement.AddedGameObject>();
        Assert.IsTrue(added.Any(a => a.instanceGameObject == muzzleFlash.gameObject),
            "AddInstanceChild was not recorded as an AddedGameObjects override on the Tank instance");

        Assert.IsTrue(PrefabUtility.IsAddedGameObjectOverride(muzzleFlash.gameObject),
            "AddInstanceChild did not register as an in-place AddedGameObjects override (re-instantiate check)");
        Assert.AreEqual(goidTankBefore, GlobalObjectId.GetGlobalObjectIdSlow(tank).ToString(),
            "Tank GlobalObjectId changed — AddInstanceChild must never re-instantiate the outer instance");
        Assert.AreEqual(goidTurretBefore, GlobalObjectId.GetGlobalObjectIdSlow(leftTurret).ToString(),
            "LeftTurret GlobalObjectId changed — AddInstanceChild must never re-instantiate the nested instance");
        Assert.AreEqual(goidBarrelBefore, GlobalObjectId.GetGlobalObjectIdSlow(barrel).ToString(),
            "Barrel GlobalObjectId changed — AddInstanceChild must never disturb an unrelated sub-object");
    }

    private static GameObjectNode RectTransformHudNode => new()
    {
        LogicalId = "Tank/Hud",
        Name = "Hud",
        Transform = new TransformData
        {
            Kind = RectTransformFields.Kind,
            AnchoredPosition = new Vec2(12f, -8f),
            SizeDelta = new Vec2(200f, 80f),
            AnchorMin = new Vec2(0f, 1f),
            AnchorMax = new Vec2(0f, 1f),
            Pivot = new Vec2(0f, 1f),
        },
    };

    // m-ui-recttransform b3-t1 (iteration 2, research.md): a UI child added inside a prefab
    // instance must actually BECOME a RectTransform live, not a plain Transform — today
    // InstanceOverrideExecutor.Children.cs's BuildAddedChild always does `new GameObject(name)`
    // and writes only localPosition/rotation/scale, so an authored `.RectTransform(...)` payload
    // is silently ignored on Build.
    [Test]
    public void AddChild_UiNode_CreatesRectTransformChild_AppliesAuthoredLayout()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var tank = InstantiateAndMapTank(scene, out var map);
        var leftTurret = tank.transform.Find("LeftTurret").gameObject;
        var goidTankBefore = GlobalObjectId.GetGlobalObjectIdSlow(tank).ToString();
        var goidTurretBefore = GlobalObjectId.GetGlobalObjectIdSlow(leftTurret).ToString();

        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new AddInstanceChild { LogicalId = "Tank", Target = LeftTurretTarget, Node = RectTransformHudNode },
            },
        };

        PlanExecutor.Execute(plan, map, scene);

        var hud = leftTurret.transform.Find("Hud");
        Assert.IsNotNull(hud, "AddInstanceChild did not create a live \"Hud\" child under LeftTurret");
        Assert.IsInstanceOf<RectTransform>(hud!,
            "A child whose authored Node.Transform.Kind==\"RectTransform\" must be created AS a RectTransform, not promoted-never plain Transform");

        var rt = (RectTransform)hud;
        Assert.AreEqual(new Vector2(12f, -8f), rt.anchoredPosition);
        Assert.AreEqual(new Vector2(200f, 80f), rt.sizeDelta);
        Assert.AreEqual(new Vector2(0f, 1f), rt.anchorMin);
        Assert.AreEqual(new Vector2(0f, 1f), rt.anchorMax);
        Assert.AreEqual(new Vector2(0f, 1f), rt.pivot);

        Assert.IsTrue(PrefabUtility.IsAddedGameObjectOverride(hud.gameObject),
            "AddInstanceChild did not register the RectTransform child as an in-place AddedGameObjects override");
        Assert.AreEqual(goidTankBefore, GlobalObjectId.GetGlobalObjectIdSlow(tank).ToString(),
            "Tank GlobalObjectId changed — AddInstanceChild must never re-instantiate the outer instance");
        Assert.AreEqual(goidTurretBefore, GlobalObjectId.GetGlobalObjectIdSlow(leftTurret).ToString(),
            "LeftTurret GlobalObjectId changed — AddInstanceChild must never re-instantiate the nested instance");
    }

    [Test]
    public void RemoveChild_SourceAntenna_RecordsRemovedGameObject_NoReinstantiate()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var tank = InstantiateAndMapTank(scene, out var map);
        var leftTurret = tank.transform.Find("LeftTurret").gameObject;
        Assert.IsNotNull(leftTurret.transform.Find("Antenna"), "Setup: LeftTurret did not carry an Antenna child");
        var goidTurretBefore = GlobalObjectId.GetGlobalObjectIdSlow(leftTurret).ToString();

        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new RemoveInstanceChild { LogicalId = "Tank", Target = AntennaTarget },
            },
        };

        PlanExecutor.Execute(plan, map, scene);

        Assert.IsNull(leftTurret.transform.Find("Antenna"),
            "RemoveInstanceChild did not remove the live Antenna child");

        var removed = PrefabUtility.GetRemovedGameObjects(tank) ?? new System.Collections.Generic.List<UnityEditor.SceneManagement.RemovedGameObject>();
        Assert.IsTrue(removed.Count > 0,
            "RemoveInstanceChild was not recorded as a RemovedGameObjects override on the Tank instance");

        Assert.AreEqual(goidTurretBefore, GlobalObjectId.GetGlobalObjectIdSlow(leftTurret).ToString(),
            "LeftTurret GlobalObjectId changed — RemoveInstanceChild must never re-instantiate the sub-object");
    }

    [Test]
    public void AddChild_ThenRevertAddedChild_RemovesIt()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var tank = InstantiateAndMapTank(scene, out var map);
        var leftTurret = tank.transform.Find("LeftTurret").gameObject;

        var addPlan = new Plan
        {
            Ops = new PlanOp[]
            {
                new AddInstanceChild { LogicalId = "Tank", Target = LeftTurretTarget, Node = MuzzleFlashNode },
            },
        };
        PlanExecutor.Execute(addPlan, map, scene);
        Assert.IsNotNull(leftTurret.transform.Find("MuzzleFlash"), "Setup: AddInstanceChild did not create MuzzleFlash");

        var revertPlan = new Plan
        {
            Ops = new PlanOp[]
            {
                new RevertAddedChild { LogicalId = "Tank", Target = LeftTurretTarget, ChildLogicalId = "Tank/MuzzleFlash" },
            },
        };

        PlanExecutor.Execute(revertPlan, map, scene);

        Assert.IsNull(leftTurret.transform.Find("MuzzleFlash"),
            "RevertAddedChild did not remove the previously-added live MuzzleFlash child");

        var added = PrefabUtility.GetAddedGameObjects(tank) ?? new System.Collections.Generic.List<UnityEditor.SceneManagement.AddedGameObject>();
        Assert.IsFalse(added.Any(a => a.instanceGameObject != null && a.instanceGameObject.name == "MuzzleFlash"),
            "RevertAddedChild did not clear the AddedGameObjects override for MuzzleFlash");
    }
}
