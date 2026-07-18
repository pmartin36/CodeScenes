using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SceneBuilder.Editor;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Plan;

// b5-t1: PlanExecutor InstantiatePrefab execution (specs/07-m6-prefab-instances.md). Exercises the
// adapter write path against a live editor scene + a real temp .prefab asset: a NEW instance must be
// created via PrefabUtility.InstantiatePrefab (Connected status) at the op's transform/parent; a MOVE
// of an already-mapped instance (Kind="PrefabInstance" IdentityMap entry) must reconcile the EXISTING
// live root in place (never destroy+recreate) so GlobalObjectId stays stable; a GUID that resolves to
// no asset must throw a located error naming the GUID/LogicalId, never silently drop the op.
public class PlanExecutorInstantiatePrefabTests
{
    private const string FixturesDir = "Assets/GateTests/Fixtures_M6Prefab";
    private const string PrefabPath = FixturesDir + "/M6_Enemy.prefab";
    private const string ScenePath = "Assets/GateTests/__PlanExecutorInstantiatePrefabTemp.unity";

    private string _guid;

    [SetUp]
    public void SetUp()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        if (!AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "Fixtures_M6Prefab");
        }

        var source = new GameObject("M6_Enemy_Source");
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

    [Test]
    public void InstantiatePrefab_NewInstance_CreatesConnectedPrefabAtTransform()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new InstantiatePrefab { LogicalId = "Enemy1", Guid = _guid, ParentLogicalId = null, SiblingIndex = 0 },
                new SetField
                {
                    LogicalId = "Enemy1",
                    Path = "m_LocalPosition",
                    Value = new ValueNode.Vec3(new Vec3(1f, 2f, 3f)),
                },
            },
        };

        var result = PlanExecutor.Execute(plan, new IdentityMap(), scene);

        Assert.IsTrue(result.GameObjectsByLogicalId.TryGetValue("Enemy1", out var root),
            "InstantiatePrefab did not register the instance root under its LogicalId");
        Assert.AreEqual(PrefabInstanceStatus.Connected, PrefabUtility.GetPrefabInstanceStatus(root),
            "Instantiated object is not a connected prefab instance");
        Assert.AreEqual(new Vector3(1f, 2f, 3f), root.transform.localPosition,
            "Root transform SetField did not apply to the instantiated prefab root");
    }

    [Test]
    public void InstantiatePrefab_Move_ReparentsExistingInstance_NoRecreate_IdentityStable()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var createPlan = new Plan
        {
            Ops = new PlanOp[]
            {
                new InstantiatePrefab { LogicalId = "Enemy1", Guid = _guid, ParentLogicalId = null, SiblingIndex = 0 },
            },
        };

        var createResult = PlanExecutor.Execute(createPlan, new IdentityMap(), scene);
        Assert.IsTrue(createResult.GameObjectsByLogicalId.TryGetValue("Enemy1", out var root),
            "Setup: InstantiatePrefab did not create the instance");

        // A saved scene is required: an unsaved scene degenerates every GlobalObjectId to "Null".
        EditorSceneManager.SaveScene(scene, ScenePath);

        var goidBefore = GlobalObjectId.GetGlobalObjectIdSlow(root).ToString();
        var instanceIdBefore = root.GetEntityId();

        var map = new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry { LogicalId = "Enemy1", GlobalObjectId = goidBefore, Kind = "PrefabInstance" },
            },
        };

        var movePlan = new Plan
        {
            Ops = new PlanOp[]
            {
                new CreateObject { LogicalId = "NewParent", Name = "NewParent" },
                new SetParent { LogicalId = "Enemy1", ParentLogicalId = "NewParent" },
            },
        };

        var moveResult = PlanExecutor.Execute(movePlan, map, scene);

        Assert.IsTrue(moveResult.GameObjectsByLogicalId.TryGetValue("Enemy1", out var movedRoot),
            "Move did not resolve the mapped PrefabInstance-kind entry to the live instance root");
        Assert.AreEqual(instanceIdBefore, movedRoot.GetEntityId(),
            "Move destroyed and recreated the instance instead of reparenting it in place");
        Assert.AreEqual("NewParent", movedRoot.transform.parent != null ? movedRoot.transform.parent.name : null,
            "Instance was not reparented under NewParent");
        Assert.AreEqual(goidBefore, GlobalObjectId.GetGlobalObjectIdSlow(movedRoot).ToString(),
            "GlobalObjectId changed across the move — identity was not preserved");
        Assert.AreEqual(PrefabInstanceStatus.Connected, PrefabUtility.GetPrefabInstanceStatus(movedRoot),
            "Instance lost its Connected prefab status across the move");
    }

    [Test]
    public void InstantiatePrefab_MissingGuid_ThrowsLocatedError()
    {
        var scene = EditorSceneManager.GetActiveScene();
        const string missingGuid = "deadbeefdeadbeefdeadbeefdeadbeef";
        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new InstantiatePrefab { LogicalId = "Ghost", Guid = missingGuid, ParentLogicalId = null, SiblingIndex = 0 },
            },
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => PlanExecutor.Execute(plan, new IdentityMap(), scene));
        StringAssert.Contains(missingGuid, ex.Message, "Error does not name the missing GUID");
        StringAssert.Contains("Ghost", ex.Message, "Error does not name the LogicalId");
    }
}
