using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using SceneBuilder.Editor;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Plan;

// The adapter-write half of a scene-reference list: SetArraySize sizes the live array/List<T>
// before its per-index SetReference ops run, and every unhandled write-dispatch site (an unmatched
// ValueNode kind in SerializedFieldBridge.WriteProperty, an unmatched op in PlanExecutor.Execute's
// switch) warns instead of silently dropping the write.
public class ReferenceListWriteTests
{
    private const string ScenePath = "Assets/GateTests/__ReferenceListWriteTemp.unity";

    [SetUp]
    public void SetUp()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }
    }

    private static GameObject Find(string name) => GameObject.Find(name);

    [Test]
    public void SetArraySizeThenSetReference_SizesArrayFieldAndAssignsEachElement()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new CreateObject { LogicalId = "Path", Name = "Path" },
                new CreateObject { LogicalId = "A", Name = "A" },
                new CreateObject { LogicalId = "B", Name = "B" },
                new AddComponent { LogicalId = "Path/WaypointPath#0", Type = new TypeRef("WaypointPath") },
                new SetArraySize { LogicalId = "Path/WaypointPath#0", Path = "waypoints", Size = 2 },
                new SetReference { LogicalId = "Path/WaypointPath#0", Path = "waypoints[0]", TargetLogicalId = "A" },
                new SetReference { LogicalId = "Path/WaypointPath#0", Path = "waypoints[1]", TargetLogicalId = "B" },
            },
        };

        PlanExecutor.Execute(plan, new IdentityMap(), scene);

        var path = Find("Path");
        Assert.IsNotNull(path, "Path was not created");
        var waypointPath = path.GetComponent<WaypointPath>();
        Assert.IsNotNull(waypointPath, "WaypointPath was not added to Path");
        Assert.AreEqual(2, waypointPath.waypoints.Length, "waypoints array was not sized to the authored length");
        Assert.AreEqual(Find("A"), waypointPath.waypoints[0], "waypoints[0] did not resolve to A");
        Assert.AreEqual(Find("B"), waypointPath.waypoints[1], "waypoints[1] did not resolve to B");
    }

    [Test]
    public void SetArraySizeThenSetReference_SizesGenericListFieldAndAssignsEachElement()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new CreateObject { LogicalId = "Path", Name = "Path" },
                new CreateObject { LogicalId = "A", Name = "A" },
                new CreateObject { LogicalId = "B", Name = "B" },
                new AddComponent { LogicalId = "Path/WaypointPath#0", Type = new TypeRef("WaypointPath") },
                new SetArraySize { LogicalId = "Path/WaypointPath#0", Path = "targets", Size = 2 },
                new SetReference { LogicalId = "Path/WaypointPath#0", Path = "targets[0]", TargetLogicalId = "A" },
                new SetReference { LogicalId = "Path/WaypointPath#0", Path = "targets[1]", TargetLogicalId = "B" },
            },
        };

        PlanExecutor.Execute(plan, new IdentityMap(), scene);

        var waypointPath = Find("Path").GetComponent<WaypointPath>();
        Assert.IsNotNull(waypointPath, "WaypointPath was not added to Path");
        Assert.AreEqual(2, waypointPath.targets.Count, "targets list was not sized to the authored length");
        Assert.AreEqual(Find("A"), waypointPath.targets[0], "targets[0] did not resolve to A");
        Assert.AreEqual(Find("B"), waypointPath.targets[1], "targets[1] did not resolve to B");
    }

    [Test]
    public void SetArraySize_ShrinksAPreExistingLiveArrayLeavingNoStaleTrailingElement()
    {
        var scene = EditorSceneManager.GetActiveScene();

        var pathGo = new GameObject("Path");
        var aGo = new GameObject("A");
        var bGo = new GameObject("B");
        var cGo = new GameObject("C");
        var waypointPath = pathGo.AddComponent<WaypointPath>();
        waypointPath.waypoints = new[] { aGo, bGo, cGo };

        // Multiple root GameObjects are mapped here by GlobalObjectId; an unsaved scene collapses
        // every object's id to the same degenerate value, so the scene must be saved first for each
        // id to resolve back to its own object.
        EditorSceneManager.SaveScene(scene, ScenePath);

        var map = new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry { LogicalId = "Path", GlobalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(pathGo).ToString(), Kind = "GameObject" },
                new IdentityMapEntry { LogicalId = "A", GlobalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(aGo).ToString(), Kind = "GameObject" },
                new IdentityMapEntry { LogicalId = "B", GlobalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(bGo).ToString(), Kind = "GameObject" },
                new IdentityMapEntry
                {
                    LogicalId = "Path/WaypointPath#0", GlobalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(waypointPath).ToString(),
                    Kind = "Component", ComponentType = "WaypointPath", ParentLogicalId = "Path",
                },
            },
        };

        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new SetArraySize { LogicalId = "Path/WaypointPath#0", Path = "waypoints", Size = 2 },
                new SetReference { LogicalId = "Path/WaypointPath#0", Path = "waypoints[0]", TargetLogicalId = "A" },
                new SetReference { LogicalId = "Path/WaypointPath#0", Path = "waypoints[1]", TargetLogicalId = "B" },
            },
        };

        PlanExecutor.Execute(plan, map, scene);

        Assert.AreEqual(2, waypointPath.waypoints.Length, "shrinking SetArraySize did not truncate the live array");
        Assert.AreEqual(aGo, waypointPath.waypoints[0]);
        Assert.AreEqual(bGo, waypointPath.waypoints[1]);
    }

    [Test]
    public void SetReference_NullTargetOnOneElement_ClearsOnlyThatSlot()
    {
        var scene = EditorSceneManager.GetActiveScene();

        var pathGo = new GameObject("Path");
        var aGo = new GameObject("A");
        var bGo = new GameObject("B");
        var waypointPath = pathGo.AddComponent<WaypointPath>();
        waypointPath.waypoints = new[] { aGo, bGo };

        var map = new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry { LogicalId = "Path", GlobalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(pathGo).ToString(), Kind = "GameObject" },
                new IdentityMapEntry
                {
                    LogicalId = "Path/WaypointPath#0", GlobalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(waypointPath).ToString(),
                    Kind = "Component", ComponentType = "WaypointPath", ParentLogicalId = "Path",
                },
            },
        };

        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new SetReference { LogicalId = "Path/WaypointPath#0", Path = "waypoints[1]", TargetLogicalId = null },
            },
        };

        PlanExecutor.Execute(plan, map, scene);

        Assert.AreEqual(aGo, waypointPath.waypoints[0], "the untouched element was disturbed");
        Assert.IsNull(waypointPath.waypoints[1], "SetReference with a null target did not clear the slot");
    }

    [Test]
    public void WriteField_UnmatchedValueNodeKind_WarnsNamingKindAndPropertyWritesNothing()
    {
        var openerGo = new GameObject("Opener");
        var doorOpener = openerGo.AddComponent<DoorOpener>();

        var so = new SerializedObject(doorOpener);

        LogAssert.Expect(LogType.Warning, new Regex("ObjectRef.*target|target.*ObjectRef"));
        SerializedFieldBridge.WriteField(so, "target", new ValueNode.ObjectRef("x"));

        Assert.IsNull(doorOpener.target, "the unmatched ValueNode kind wrote a value instead of being reported and dropped");
    }

    private sealed record UnknownTestOp : PlanOp;

    [Test]
    public void Execute_UnmatchedPlanOpKind_WarnsNamingTheOpType()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new UnknownTestOp { LogicalId = "Nothing" },
            },
        };

        LogAssert.Expect(LogType.Warning, new Regex("UnknownTestOp"));
        PlanExecutor.Execute(plan, new IdentityMap(), scene);
    }
}
