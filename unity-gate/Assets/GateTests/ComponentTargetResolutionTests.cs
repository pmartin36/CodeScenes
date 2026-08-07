using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SceneBuilder.Editor;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;

// The milestone's one invariant proved against a LIVE scene, the boundary the headless suite is
// structurally blind to: a listener target resolves to a Component's OWN LogicalId, never its
// owning GameObject's, and two same-typed components on one GameObject stay distinguishable by
// their ordinal-within-type. Also proves the two collapses BuildFromIndex makes for a plain
// reference FIELD -- null on both "asset" and "dangling", and a Component normalized to its
// owner -- do NOT apply on the listener path.
public class ComponentTargetResolutionTests
{
    private const string ScenePath = "Assets/GateTests/__ComponentTargetResolutionTemp.unity";
    private const string MaterialPath = "Assets/GateTests/__ComponentTargetResolutionTemp.mat";

    [SetUp]
    public void SetUp()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    [TearDown]
    public void TearDown()
    {
        if (System.IO.File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }

        if (System.IO.File.Exists(MaterialPath))
        {
            AssetDatabase.DeleteAsset(MaterialPath);
        }
    }

    private static void SaveActiveScene() =>
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

    private static string Goid(UnityEngine.Object obj) => GlobalObjectId.GetGlobalObjectIdSlow(obj).ToString();

    [Test]
    public void Classify_TwoSameTypedComponentsAndTheirOwner_StayDistinguishableByOrdinal()
    {
        var rig = new GameObject("Rig");
        var door0 = rig.AddComponent<DoorOpener>();
        var door1 = rig.AddComponent<DoorOpener>();
        var rb = rig.AddComponent<Rigidbody>();
        SaveActiveScene();

        var map = new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry { LogicalId = "Rig", Kind = "GameObject", GlobalObjectId = Goid(rig) },
                new IdentityMapEntry { LogicalId = "Rig/DoorOpener#0", Kind = "Component", GlobalObjectId = Goid(door0) },
                new IdentityMapEntry { LogicalId = "Rig/DoorOpener#1", Kind = "Component", GlobalObjectId = Goid(door1) },
                new IdentityMapEntry { LogicalId = "Rig/Rigidbody#0", Kind = "Component", GlobalObjectId = Goid(rb) },
            },
        };
        var index = ComponentTargetIndex.ForMap(map);

        var door0Result = ComponentTargetResolution.Classify(StampedReference.Scene(Goid(door0)), index);
        var door1Result = ComponentTargetResolution.Classify(StampedReference.Scene(Goid(door1)), index);
        var rigResult = ComponentTargetResolution.Classify(StampedReference.Scene(Goid(rig)), index);

        Assert.AreEqual("Rig/DoorOpener#0", ((ListenerTarget.SceneObject)door0Result).LogicalId);
        Assert.AreEqual("Rig/DoorOpener#1", ((ListenerTarget.SceneObject)door1Result).LogicalId);
        Assert.AreEqual("Rig", ((ListenerTarget.SceneObject)rigResult).LogicalId);
    }

    [Test]
    public void ResolveListenerTarget_ComponentLogicalId_ReturnsTheExactSecondComponent()
    {
        var rig = new GameObject("Rig");
        var door0 = rig.AddComponent<DoorOpener>();
        var door1 = rig.AddComponent<DoorOpener>();
        SaveActiveScene();

        var componentsByLogicalId = new Dictionary<string, Component>
        {
            ["Rig/DoorOpener#0"] = door0,
            ["Rig/DoorOpener#1"] = door1,
        };

        var resolved = ObjectReferenceResolver.ResolveListenerTarget(
            "Rig/DoorOpener#1", new Dictionary<string, GameObject>(), componentsByLogicalId,
            new IdentityMap(), EditorSceneManager.GetActiveScene());

        Assert.AreSame(door1, resolved, "resolved to the wrong component");
        Assert.AreNotSame(door0, resolved);
        Assert.AreNotSame(rig, resolved);
    }

    [Test]
    public void ClassifyStampToValueNodeResolve_ComponentTarget_RoundTripsToTheSameComponent()
    {
        var rig = new GameObject("Rig");
        var door = rig.AddComponent<DoorOpener>();
        SaveActiveScene();

        var map = new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry { LogicalId = "Rig", Kind = "GameObject", GlobalObjectId = Goid(rig) },
                new IdentityMapEntry { LogicalId = "Rig/DoorOpener#0", Kind = "Component", GlobalObjectId = Goid(door) },
            },
        };
        var index = ComponentTargetIndex.ForMap(map);

        var stamped = ObjectReferenceResolver.StampListenerReference(door, o => Goid(o));
        var classified = ComponentTargetResolution.Classify(stamped, index);
        var node = (ValueNode.ObjectRef)ComponentTargetResolution.ToValueNode(classified);

        var resolved = ObjectReferenceResolver.ResolveListenerTarget(
            node.TargetLogicalId!, new Dictionary<string, GameObject>(), new Dictionary<string, Component>(),
            map, EditorSceneManager.GetActiveScene());

        Assert.AreSame(door, resolved);
    }

    [Test]
    public void ClassifyStampToValueNodeResolve_GameObjectTarget_RoundTripsToTheSameGameObject()
    {
        var rig = new GameObject("Rig");
        SaveActiveScene();

        var map = new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry { LogicalId = "Rig", Kind = "GameObject", GlobalObjectId = Goid(rig) },
            },
        };
        var index = ComponentTargetIndex.ForMap(map);

        var stamped = ObjectReferenceResolver.StampListenerReference(rig, o => Goid(o));
        var classified = ComponentTargetResolution.Classify(stamped, index);
        var node = (ValueNode.ObjectRef)ComponentTargetResolution.ToValueNode(classified);

        var resolved = ObjectReferenceResolver.ResolveListenerTarget(
            node.TargetLogicalId!, new Dictionary<string, GameObject>(), new Dictionary<string, Component>(),
            map, EditorSceneManager.GetActiveScene());

        Assert.AreSame(rig, resolved);
    }

    // The two cases ObjectReferenceResolver.BuildFromIndex collapses to the SAME `null` for a
    // plain reference field: a project asset and a genuinely dangling ("Missing") target. The
    // listener path must keep them distinct.
    [Test]
    public void StampThenClassify_ProjectAsset_ClassifiesAsAssetWithTheRealGuid()
    {
        AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), MaterialPath);
        var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(material, out var guid, out _);

        var index = ComponentTargetIndex.ForMap(new IdentityMap());
        var stamped = ObjectReferenceResolver.StampListenerReference(material, o => Goid(o));
        var result = ComponentTargetResolution.Classify(stamped, index);

        Assert.IsInstanceOf<ListenerTarget.Asset>(result);
        Assert.AreEqual(guid, ((ListenerTarget.Asset)result).Ref.Guid);
    }

    [Test]
    public void StampThenClassify_NullTarget_ClassifiesAsDanglingNull()
    {
        var index = ComponentTargetIndex.ForMap(new IdentityMap());

        var stamped = ObjectReferenceResolver.StampListenerReference(null, o => Goid(o));
        var result = ComponentTargetResolution.Classify(stamped, index);

        Assert.IsInstanceOf<ListenerTarget.DanglingNull>(result);
    }

    [Test]
    public void StampThenClassify_ComponentAbsentFromMap_ClassifiesUnresolvableCarryingItsGoid()
    {
        var rig = new GameObject("Rig");
        var door = rig.AddComponent<DoorOpener>();
        SaveActiveScene();

        var index = ComponentTargetIndex.ForMap(new IdentityMap());

        var stamped = ObjectReferenceResolver.StampListenerReference(door, o => Goid(o));
        var result = ComponentTargetResolution.Classify(stamped, index);

        Assert.IsInstanceOf<ListenerTarget.Unresolvable>(result);
        Assert.AreEqual(Goid(door), ((ListenerTarget.Unresolvable)result).GlobalObjectId);
    }

    // (c) regression: a plain reference FIELD assigned a Component still normalizes to the OWNER
    // GameObject's LogicalId through BuildFromIndex -- the listener path is a different function,
    // never a flag on this one.
    [Test]
    public void BuildFromIndex_ComponentTarget_StillNormalizesToTheOwningGameObject()
    {
        var jointGo = new GameObject("Joint");
        var bodyGo = new GameObject("Body");
        var joint = jointGo.AddComponent<HingeJoint>();
        var rb = bodyGo.AddComponent<Rigidbody>();
        joint.connectedBody = rb;
        SaveActiveScene();

        var goidToLogicalId = new Dictionary<string, string> { [Goid(bodyGo)] = "Body" };
        var resolver = ObjectReferenceResolver.BuildSceneRefResolver(
            new IdentityMap
            {
                Entries = new[] { new IdentityMapEntry { LogicalId = "Body", Kind = "GameObject", GlobalObjectId = Goid(bodyGo) } },
            });

        var resolved = resolver(rb);

        Assert.AreEqual("Body", resolved,
            "BuildFromIndex no longer normalizes a Component reference-field target to its owning GameObject");
    }
}
