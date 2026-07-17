using System;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SceneBuilder.Editor;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Plan;

// b5-t1: PlanExecutor SetReference execution — the code->scene adapter write path for cross-object
// references (specs/06-m5-cross-object-references.md). Exercises PlanExecutor.Execute directly with
// hand-built ops against a live editor scene: GameObject-typed target resolution, Component-typed
// (NATIVE field, HingeJoint.connectedBody per spec confirmation #6) target resolution via
// GetComponent, null-clear, mutual A<->B two-pass ordering (no create-order dependency), and a
// located error on an unresolved target — never a silent null. Full bidirectional round-trip
// coverage (incl. scene->code) is b6-t1's mandatory Unity gate suite; this is the adapter-write unit.
public class PlanExecutorObjectRefTests
{
    [SetUp]
    public void SetUp()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    private static GameObject Find(string name) => GameObject.Find(name);

    [Test]
    public void SetReference_GameObjectTypedField_ResolvesToTargetGameObject()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new CreateObject { LogicalId = "Opener", Name = "Opener" },
                new CreateObject { LogicalId = "Door", Name = "Door" },
                new AddComponent { LogicalId = "Opener/DoorOpener#0", Type = new TypeRef("DoorOpener") },
                new SetReference { LogicalId = "Opener/DoorOpener#0", Path = "target", TargetLogicalId = "Door" },
            },
        };

        PlanExecutor.Execute(plan, new IdentityMap(), scene);

        var opener = Find("Opener");
        var door = Find("Door");
        Assert.IsNotNull(opener, "Opener was not created");
        Assert.IsNotNull(door, "Door was not created");
        var doorOpener = opener.GetComponent<DoorOpener>();
        Assert.IsNotNull(doorOpener, "DoorOpener was not added to Opener");
        Assert.AreEqual(door, doorOpener.target,
            "SetReference did not resolve the GameObject-typed target field to the live Door GameObject");
    }

    [Test]
    public void SetReference_ComponentTypedNativeField_ResolvesToComponentNotGameObject()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new CreateObject { LogicalId = "Joint", Name = "Joint" },
                new CreateObject { LogicalId = "Body", Name = "Body" },
                new AddComponent { LogicalId = "Joint/HingeJoint#0", Type = new TypeRef("UnityEngine.HingeJoint") },
                new AddComponent { LogicalId = "Body/Rigidbody#0", Type = new TypeRef("UnityEngine.Rigidbody") },
                // The authored handle names the GameObject "Body" (not its Rigidbody component) — the
                // adapter must resolve to the MATCHING COMPONENT because the native field wants one.
                new SetReference { LogicalId = "Joint/HingeJoint#0", Path = "m_ConnectedBody", TargetLogicalId = "Body" },
            },
        };

        PlanExecutor.Execute(plan, new IdentityMap(), scene);

        var jointGo = Find("Joint");
        var bodyGo = Find("Body");
        Assert.IsNotNull(jointGo, "Joint was not created");
        Assert.IsNotNull(bodyGo, "Body was not created");
        var joint = jointGo.GetComponent<HingeJoint>();
        Assert.IsNotNull(joint, "HingeJoint was not added to Joint");
        var rb = bodyGo.GetComponent<Rigidbody>();
        Assert.IsNotNull(rb, "Rigidbody was not added to Body");
        Assert.AreEqual(rb, joint.connectedBody,
            "SetReference resolved the Component-typed target to the GameObject instead of its Rigidbody component");
    }

    [Test]
    public void SetReference_NullTarget_ClearsSlot()
    {
        var scene = EditorSceneManager.GetActiveScene();

        // Real DoorOpener with a live, non-null target BEFORE Execute — SetReference(null) must clear it.
        var openerGo = new GameObject("Opener");
        var doorGo = new GameObject("Door");
        var doorOpener = openerGo.AddComponent<DoorOpener>();
        doorOpener.target = doorGo;

        var openerGoid = GlobalObjectId.GetGlobalObjectIdSlow(openerGo).ToString();
        var doorOpenerGoid = GlobalObjectId.GetGlobalObjectIdSlow(doorOpener).ToString();

        var map = new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry { LogicalId = "Opener", GlobalObjectId = openerGoid, Kind = "GameObject" },
                new IdentityMapEntry
                {
                    LogicalId = "Opener/DoorOpener#0", GlobalObjectId = doorOpenerGoid, Kind = "Component",
                    ComponentType = "DoorOpener", ParentLogicalId = "Opener",
                },
            },
        };

        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new SetReference { LogicalId = "Opener/DoorOpener#0", Path = "target", TargetLogicalId = null },
            },
        };

        PlanExecutor.Execute(plan, map, scene);

        Assert.IsNull(doorOpener.target, "SetReference with a null TargetLogicalId did not clear the field");
    }

    [Test]
    public void SetReference_MutualReferences_WireBothWithNoOrderingError()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new CreateObject { LogicalId = "A", Name = "A" },
                new CreateObject { LogicalId = "B", Name = "B" },
                new AddComponent { LogicalId = "A/DoorOpener#0", Type = new TypeRef("DoorOpener") },
                new AddComponent { LogicalId = "B/DoorOpener#0", Type = new TypeRef("DoorOpener") },
                new SetReference { LogicalId = "A/DoorOpener#0", Path = "target", TargetLogicalId = "B" },
                new SetReference { LogicalId = "B/DoorOpener#0", Path = "target", TargetLogicalId = "A" },
            },
        };

        Assert.DoesNotThrow(() => PlanExecutor.Execute(plan, new IdentityMap(), scene),
            "Mutual A<->B references produced an ordering error");

        var a = Find("A");
        var b = Find("B");
        Assert.IsNotNull(a, "A was not created");
        Assert.IsNotNull(b, "B was not created");
        Assert.AreEqual(b, a.GetComponent<DoorOpener>().target, "A's target did not resolve to B");
        Assert.AreEqual(a, b.GetComponent<DoorOpener>().target, "B's target did not resolve to A");
    }

    [Test]
    public void SetReference_UnresolvedTarget_ThrowsLocatedError()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new CreateObject { LogicalId = "Opener", Name = "Opener" },
                new AddComponent { LogicalId = "Opener/DoorOpener#0", Type = new TypeRef("DoorOpener") },
                // "Ghost" was never created and has no IdentityMap entry — must be a loud, located error.
                new SetReference { LogicalId = "Opener/DoorOpener#0", Path = "target", TargetLogicalId = "Ghost" },
            },
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => PlanExecutor.Execute(plan, new IdentityMap(), scene),
            "An unresolved SetReference target did not throw a located error");
        StringAssert.Contains("Opener", ex.Message, "Error does not name the owning object.\n" + ex.Message);
        StringAssert.Contains("target", ex.Message, "Error does not name the field.\n" + ex.Message);
    }
}
