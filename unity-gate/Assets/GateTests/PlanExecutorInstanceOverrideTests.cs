using System;
using System.Collections.Generic;
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
using UnityAddedComponent = UnityEditor.SceneManagement.AddedComponent;
using UnityRemovedComponent = UnityEditor.SceneManagement.RemovedComponent;

// b7-t1: PlanExecutor instance-override execution (specs/11-m10-prefab-overrides.md) — the adapter
// write path for the six M10 instance-override Plan ops against a live prefab instance: a scalar
// SetInstanceOverride shows as a bold PropertyModification; an ObjectReference-valued
// SetInstanceOverride (AssetRef / ObjectRef) sets a real asset/scene reference; AddInstanceComponent /
// RemoveInstanceComponent are structurally auto-recorded by Unity; the three Revert ops clear an
// existing override/added/removed-component state via the matching PrefabUtility.Revert* call. Every
// op applies IN PLACE against the outermost instance root — GlobalObjectId is asserted stable, never
// re-instantiated. Mirrors PlanExecutorInstantiatePrefabTests.cs / PlanExecutorObjectRefTests.cs:
// hand-built Plan + IdentityMap against a real temp .prefab fixture, exercised via
// PlanExecutor.Execute directly (no SceneBuilderBuild round trip needed for this adapter unit).
public class PlanExecutorInstanceOverrideTests
{
    private const string FixturesDir = "Assets/GateTests/Fixtures_M10ExecOverride";
    private const string PrefabPath = FixturesDir + "/M10_ExecOverrideEnemy.prefab";
    private const string MaterialPath = FixturesDir + "/M10_ExecOverrideMaterial.asset";
    private const string ScenePath = "Assets/GateTests/__PlanExecutorInstanceOverrideTemp.unity";

    private string _prefabGuid;
    private string _materialGuid;

    [SetUp]
    public void SetUp()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        if (!AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "Fixtures_M10ExecOverride");
        }

        // Root carries a native component (BoxCollider — scalar + AssetRef override target) and a
        // managed component (DoorOpener — ObjectRef override target), per SUGGESTED_TESTS.
        var source = new GameObject("M10_ExecOverrideEnemy_Source");
        source.AddComponent<BoxCollider>();
        source.AddComponent<DoorOpener>();
        PrefabUtility.SaveAsPrefabAsset(source, PrefabPath);
        UnityEngine.Object.DestroyImmediate(source);

        AssetDatabase.CreateAsset(new PhysicsMaterial(), MaterialPath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        _prefabGuid = AssetDatabase.AssetPathToGUID(PrefabPath);
        _materialGuid = AssetDatabase.AssetPathToGUID(MaterialPath);
    }

    [TearDown]
    public void TearDown()
    {
        AssetDatabase.DeleteAsset(PrefabPath);
        AssetDatabase.DeleteAsset(MaterialPath);
        if (AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.DeleteAsset(FixturesDir);
        }

        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }
    }

    private static OverrideTarget BoxColliderTarget => new() { ComponentType = "UnityEngine.BoxCollider" };
    private static OverrideTarget LightTarget => new() { ComponentType = "UnityEngine.Light" };
    private static OverrideTarget DoorOpenerTarget => new() { ComponentType = "DoorOpener" };

    // Instantiates the fixture prefab (own Execute call, empty IdentityMap — mirrors
    // PlanExecutorInstantiatePrefabTests) and saves the scene (GlobalObjectId requires a saved
    // scene), then returns the live root plus a Kind="PrefabInstance" IdentityMap pre-resolving it
    // by LogicalId — the shape PlanExecutor needs for a SECOND Execute call against an
    // ALREADY-EXISTING instance (mirrors InstantiatePrefab_Move_ReparentsExistingInstance...).
    private GameObject InstantiateAndMap(Scene scene, string logicalId, out IdentityMap map)
    {
        var createPlan = new Plan
        {
            Ops = new PlanOp[]
            {
                new InstantiatePrefab { LogicalId = logicalId, Guid = _prefabGuid, ParentLogicalId = null, SiblingIndex = 0 },
            },
        };
        var createResult = PlanExecutor.Execute(createPlan, new IdentityMap(), scene);
        var root = createResult.GameObjectsByLogicalId[logicalId];

        EditorSceneManager.SaveScene(scene, ScenePath);

        var goid = GlobalObjectId.GetGlobalObjectIdSlow(root).ToString();
        map = new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry { LogicalId = logicalId, GlobalObjectId = goid, Kind = "PrefabInstance" },
            },
        };
        return root;
    }

    [Test]
    public void Execute_SetInstanceOverride_ScalarShowsBoldOverride_SameGlobalObjectId()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var root = InstantiateAndMap(scene, "Enemy1", out var map);
        var goidBefore = GlobalObjectId.GetGlobalObjectIdSlow(root).ToString();

        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new SetInstanceOverride
                {
                    LogicalId = "Enemy1", Target = BoxColliderTarget,
                    PropertyPath = "m_IsTrigger", Value = ValueNode.Primitive.Bool(true),
                },
            },
        };

        PlanExecutor.Execute(plan, map, scene);

        var mods = PrefabUtility.GetPropertyModifications(root) ?? Array.Empty<PropertyModification>();
        Assert.IsTrue(mods.Any(m => m.propertyPath == "m_IsTrigger" && m.value == "1"),
            "SetInstanceOverride did not produce a bold m_IsTrigger property modification");
        Assert.IsTrue(root.GetComponent<BoxCollider>().isTrigger,
            "SetInstanceOverride did not apply the value to the live instance");
        Assert.AreEqual(goidBefore, GlobalObjectId.GetGlobalObjectIdSlow(root).ToString(),
            "GlobalObjectId changed — instance was not updated in place");
    }

    [Test]
    public void Execute_SetInstanceOverride_AssetRefValue_SetsAssetReferenceOverride()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var root = InstantiateAndMap(scene, "Enemy1", out var map);
        var goidBefore = GlobalObjectId.GetGlobalObjectIdSlow(root).ToString();
        var expectedMaterial = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(MaterialPath);

        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new SetInstanceOverride
                {
                    LogicalId = "Enemy1", Target = BoxColliderTarget, PropertyPath = "m_Material",
                    ObjectReference = new ValueNode.AssetRef(new AssetRef { Guid = _materialGuid, FileId = 0 }),
                },
            },
        };

        PlanExecutor.Execute(plan, map, scene);

        Assert.AreEqual(expectedMaterial, root.GetComponent<BoxCollider>().sharedMaterial,
            "SetInstanceOverride with an AssetRef ObjectReference did not assign the real material asset");
        Assert.AreEqual(goidBefore, GlobalObjectId.GetGlobalObjectIdSlow(root).ToString(),
            "GlobalObjectId changed — instance was not updated in place");
    }

    [Test]
    public void Execute_SetInstanceOverride_ObjectRefValue_SetsSceneReferenceOverride()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var root = InstantiateAndMap(scene, "Enemy1", out var map);
        var goidBefore = GlobalObjectId.GetGlobalObjectIdSlow(root).ToString();

        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new CreateObject { LogicalId = "Door", Name = "Door" },
                new SetInstanceOverride
                {
                    LogicalId = "Enemy1", Target = DoorOpenerTarget, PropertyPath = "target",
                    ObjectReference = new ValueNode.ObjectRef("Door"),
                },
            },
        };

        PlanExecutor.Execute(plan, map, scene);

        var door = GameObject.Find("Door");
        Assert.IsNotNull(door, "Setup: Door was not created");
        Assert.AreEqual(door, root.GetComponent<DoorOpener>().target,
            "SetInstanceOverride with an ObjectRef ObjectReference did not resolve to the live scene GameObject");
        Assert.AreEqual(goidBefore, GlobalObjectId.GetGlobalObjectIdSlow(root).ToString(),
            "GlobalObjectId changed — instance was not updated in place");
    }

    [Test]
    public void Execute_AddInstanceComponent_Light_AppearsInGetAddedComponents_SameGlobalObjectId()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var root = InstantiateAndMap(scene, "Enemy1", out var map);
        var goidBefore = GlobalObjectId.GetGlobalObjectIdSlow(root).ToString();

        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new AddInstanceComponent
                {
                    LogicalId = "Enemy1", Target = LightTarget,
                    Component = new ComponentData
                    {
                        LogicalId = "Enemy1/UnityEngine.Light#0", Type = new TypeRef("UnityEngine.Light"), Fields = FieldMap.Empty,
                    },
                },
            },
        };

        PlanExecutor.Execute(plan, map, scene);

        var added = PrefabUtility.GetAddedComponents(root) ?? new List<UnityAddedComponent>();
        Assert.IsTrue(added.Any(a => a.instanceComponent is Light && a.instanceComponent.transform == root.transform),
            "AddInstanceComponent did not add a root-target Light recorded as an added component");
        Assert.AreEqual(goidBefore, GlobalObjectId.GetGlobalObjectIdSlow(root).ToString(),
            "GlobalObjectId changed — instance was not updated in place");
    }

    [Test]
    public void Execute_RemoveInstanceComponent_AppearsInGetRemovedComponents_SameGlobalObjectId()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var root = InstantiateAndMap(scene, "Enemy1", out var map);
        var goidBefore = GlobalObjectId.GetGlobalObjectIdSlow(root).ToString();

        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new RemoveInstanceComponent { LogicalId = "Enemy1", Target = BoxColliderTarget },
            },
        };

        PlanExecutor.Execute(plan, map, scene);

        Assert.IsNull(root.GetComponent<BoxCollider>(), "RemoveInstanceComponent did not remove the live BoxCollider");
        var removed = PrefabUtility.GetRemovedComponents(root) ?? new List<UnityRemovedComponent>();
        Assert.IsTrue(removed.Any(r => r.assetComponent != null && r.assetComponent.GetType() == typeof(BoxCollider)),
            "RemoveInstanceComponent did not record a BoxCollider removed-component override");
        Assert.AreEqual(goidBefore, GlobalObjectId.GetGlobalObjectIdSlow(root).ToString(),
            "GlobalObjectId changed — instance was not updated in place");
    }

    [Test]
    public void Execute_RevertInstanceOverride_ClearsBoldOverride_SameGlobalObjectId()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var root = InstantiateAndMap(scene, "Enemy1", out var map);
        var collider = root.GetComponent<BoxCollider>();
        collider.isTrigger = true;
        PrefabUtility.RecordPrefabInstancePropertyModifications(collider);
        Assert.IsTrue((PrefabUtility.GetPropertyModifications(root) ?? Array.Empty<PropertyModification>())
            .Any(m => m.propertyPath == "m_IsTrigger"),
            "Setup: manual isTrigger override was not recorded");
        var goidBefore = GlobalObjectId.GetGlobalObjectIdSlow(root).ToString();

        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new RevertInstanceOverride { LogicalId = "Enemy1", Target = BoxColliderTarget, PropertyPath = "m_IsTrigger" },
            },
        };

        PlanExecutor.Execute(plan, map, scene);

        var mods = PrefabUtility.GetPropertyModifications(root) ?? Array.Empty<PropertyModification>();
        Assert.IsFalse(mods.Any(m => m.propertyPath == "m_IsTrigger"),
            "RevertInstanceOverride did not clear the bold m_IsTrigger override");
        Assert.AreEqual(goidBefore, GlobalObjectId.GetGlobalObjectIdSlow(root).ToString(),
            "GlobalObjectId changed — instance was not updated in place");
    }

    [Test]
    public void Execute_RevertAddedComponent_RemovesAddedComponent()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var root = InstantiateAndMap(scene, "Enemy1", out var map);
        root.AddComponent<Light>();
        Assert.IsTrue((PrefabUtility.GetAddedComponents(root) ?? new List<UnityAddedComponent>())
            .Any(a => a.instanceComponent is Light),
            "Setup: manually added Light was not recorded as an added component");
        var goidBefore = GlobalObjectId.GetGlobalObjectIdSlow(root).ToString();

        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new RevertAddedComponent
                {
                    LogicalId = "Enemy1", Target = LightTarget, ComponentLogicalId = "Enemy1/UnityEngine.Light#0",
                },
            },
        };

        PlanExecutor.Execute(plan, map, scene);

        Assert.IsNull(root.GetComponent<Light>(), "RevertAddedComponent did not remove the added Light");
        Assert.AreEqual(goidBefore, GlobalObjectId.GetGlobalObjectIdSlow(root).ToString(),
            "GlobalObjectId changed — instance was not updated in place");
    }

    [Test]
    public void Execute_RevertRemovedComponent_RestoresComponent()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var root = InstantiateAndMap(scene, "Enemy1", out var map);
        UnityEngine.Object.DestroyImmediate(root.GetComponent<BoxCollider>());
        Assert.IsNull(root.GetComponent<BoxCollider>(), "Setup: BoxCollider was not removed");
        var goidBefore = GlobalObjectId.GetGlobalObjectIdSlow(root).ToString();

        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new RevertRemovedComponent { LogicalId = "Enemy1", Target = BoxColliderTarget },
            },
        };

        PlanExecutor.Execute(plan, map, scene);

        Assert.IsNotNull(root.GetComponent<BoxCollider>(), "RevertRemovedComponent did not restore the BoxCollider");
        Assert.AreEqual(goidBefore, GlobalObjectId.GetGlobalObjectIdSlow(root).ToString(),
            "GlobalObjectId changed — instance was not updated in place");
    }
}
