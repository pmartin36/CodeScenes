using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SceneBuilder.Editor;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;

// b5-t2: AssetReferenceResolver.ReadObjectReference's scene-object read path (specs/06-m5-cross-object
// -references.md), the exact M4->M5 change point: a live scene GameObject/Component reference used to
// read back Unsupported("ObjectReference"); it must now reverse-map through an injected
// ObjectReferenceResolver.BuildSceneRefResolver(IdentityMap) delegate to ValueNode.ObjectRef. Routed
// here by the validator: this task is BEHAVIORAL:yes with TEST_RECOMMENDATION write and had zero
// captured evidence at its own gate (full coverage was deferred to b6-t1, which had not run). Full
// bidirectional round-trip coverage remains b6-t1's job; this is the focused adapter-read unit,
// mirroring PlanExecutorObjectRefTests' role for the write side.
//
// GlobalObjectId.GetGlobalObjectIdSlow degenerates to an identical id for every object in an UNSAVED
// scene (research.md note) — the scene here is saved under Assets before any read, mirroring
// RoundTripAssetRefTests/AutoIdentityTests.
public class AssetReferenceResolverObjectRefReadTests
{
    private const string ScenePath = "Assets/GateTests/__ObjectRefReadTemp.unity";

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
    }

    private static void SaveActiveScene()
    {
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);
    }

    [Test]
    public void ReadObjectReference_SceneGameObjectTarget_ResolvesToMappedLogicalId()
    {
        var openerGo = new GameObject("Opener");
        var doorGo = new GameObject("Door");
        var doorOpener = openerGo.AddComponent<DoorOpener>();
        doorOpener.target = doorGo;
        SaveActiveScene();

        var map = new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry
                {
                    LogicalId = "Opener", Kind = "GameObject",
                    GlobalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(openerGo).ToString(),
                },
                new IdentityMapEntry
                {
                    LogicalId = "Door", Kind = "GameObject",
                    GlobalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(doorGo).ToString(),
                },
            },
        };
        var resolver = ObjectReferenceResolver.BuildSceneRefResolver(map);

        var prop = new SerializedObject(doorOpener).FindProperty("target");
        Assert.IsNotNull(prop, "DoorOpener.target property not found");

        var node = AssetReferenceResolver.ReadObjectReference(prop, resolver);

        Assert.IsInstanceOf<ValueNode.ObjectRef>(node,
            "A live scene GameObject reference did not read back as ObjectRef (got " + node.GetType().Name + ")");
        Assert.AreEqual("Door", ((ValueNode.ObjectRef)node).TargetLogicalId,
            "ObjectRef did not resolve to the mapped target's LogicalId");
    }

    [Test]
    public void ReadObjectReference_ComponentTarget_ResolvesToOwningGameObjectLogicalId()
    {
        var jointGo = new GameObject("Joint");
        var bodyGo = new GameObject("Body");
        var joint = jointGo.AddComponent<HingeJoint>();
        var rb = bodyGo.AddComponent<Rigidbody>();
        joint.connectedBody = rb;
        SaveActiveScene();

        // Only the GameObject entry is mapped (Components carry no IdentityMap entry of their own) —
        // BuildSceneRefResolver's dictionary is built from Kind=="GameObject" entries only.
        var map = new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry
                {
                    LogicalId = "Body", Kind = "GameObject",
                    GlobalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(bodyGo).ToString(),
                },
            },
        };
        var resolver = ObjectReferenceResolver.BuildSceneRefResolver(map);

        var prop = new SerializedObject(joint).FindProperty("m_ConnectedBody");
        Assert.IsNotNull(prop, "HingeJoint.m_ConnectedBody property not found");

        var node = AssetReferenceResolver.ReadObjectReference(prop, resolver);

        Assert.IsInstanceOf<ValueNode.ObjectRef>(node,
            "A live scene Component reference did not read back as ObjectRef (got " + node.GetType().Name + ")");
        Assert.AreEqual("Body", ((ValueNode.ObjectRef)node).TargetLogicalId,
            "Component target did not normalize to its OWNING GameObject's LogicalId — resolved to the " +
            "component's own identity instead");
    }

    [Test]
    public void ReadObjectReference_UnmappedTarget_ResolvesToRawGlobalObjectId()
    {
        var openerGo = new GameObject("Opener");
        var doorGo = new GameObject("Door");
        var doorOpener = openerGo.AddComponent<DoorOpener>();
        doorOpener.target = doorGo;
        SaveActiveScene();

        // Door has NO IdentityMap entry (newly created, not yet mapped) — the resolver must carry the
        // target's raw GlobalObjectId, never null/Unsupported, so a later Sync converges it as PENDING
        // rather than silently dropping the reference.
        var map = new IdentityMap();
        var resolver = ObjectReferenceResolver.BuildSceneRefResolver(map);

        var prop = new SerializedObject(doorOpener).FindProperty("target");
        Assert.IsNotNull(prop, "DoorOpener.target property not found");

        var node = AssetReferenceResolver.ReadObjectReference(prop, resolver);

        Assert.IsInstanceOf<ValueNode.ObjectRef>(node,
            "An unmapped scene GameObject reference did not read back as ObjectRef (got " + node.GetType().Name + ")");
        var expectedRawGoid = GlobalObjectId.GetGlobalObjectIdSlow(doorGo).ToString();
        Assert.AreEqual(expectedRawGoid, ((ValueNode.ObjectRef)node).TargetLogicalId,
            "An unmapped target must carry its raw GlobalObjectId, not null or a stale id");
    }

    [Test]
    public void ReadObjectReference_NullSceneTypedField_ReadsAsObjectRefNull()
    {
        var openerGo = new GameObject("Opener");
        var doorOpener = openerGo.AddComponent<DoorOpener>();
        doorOpener.target = null;
        SaveActiveScene();

        var resolver = ObjectReferenceResolver.BuildSceneRefResolver(new IdentityMap());
        var prop = new SerializedObject(doorOpener).FindProperty("target");
        Assert.IsNotNull(prop, "DoorOpener.target property not found");

        var node = AssetReferenceResolver.ReadObjectReference(prop, resolver);

        Assert.IsInstanceOf<ValueNode.ObjectRef>(node,
            "A null GameObject-typed field did not read back as ObjectRef (got " + node.GetType().Name + ")");
        Assert.IsNull(((ValueNode.ObjectRef)node).TargetLogicalId,
            "A cleared (null) scene-typed field must read as the None form ObjectRef(null)");
    }

    [Test]
    public void ReadObjectReference_NullAssetTypedField_StaysAssetRefNull()
    {
        var go = new GameObject("Surface");
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = null;
        SaveActiveScene();

        var resolver = ObjectReferenceResolver.BuildSceneRefResolver(new IdentityMap());
        var prop = new SerializedObject(mr).FindProperty("m_Materials.Array.data[0]");
        if (prop == null)
        {
            // A default renderer may have zero material slots — force a null single-slot array.
            mr.sharedMaterials = new Material[] { null };
            prop = new SerializedObject(mr).FindProperty("m_Materials.Array.data[0]");
        }

        Assert.IsNotNull(prop, "MeshRenderer.m_Materials[0] property not found");

        var node = AssetReferenceResolver.ReadObjectReference(prop, resolver);

        Assert.IsInstanceOf<ValueNode.AssetRef>(node,
            "A null asset-typed field must NOT be reclassified as ObjectRef by the presence of a scene " +
            "resolver (got " + node.GetType().Name + ")");
        Assert.IsNull(((ValueNode.AssetRef)node).Ref, "A cleared asset field must read as AssetRef(null)");
    }

    [Test]
    public void ReadObjectReference_NoResolverSupplied_SceneRefStaysUnsupported()
    {
        var openerGo = new GameObject("Opener");
        var doorGo = new GameObject("Door");
        var doorOpener = openerGo.AddComponent<DoorOpener>();
        doorOpener.target = doorGo;
        SaveActiveScene();

        var prop = new SerializedObject(doorOpener).FindProperty("target");
        Assert.IsNotNull(prop, "DoorOpener.target property not found");

        // No resolver (the build read path, M4-preserved): a populated scene ref must stay Unsupported.
        var node = AssetReferenceResolver.ReadObjectReference(prop);

        Assert.IsInstanceOf<ValueNode.Unsupported>(node,
            "With no resolver supplied, a scene-object reference must stay Unsupported (build path, " +
            "M4-preserved) — got " + node.GetType().Name);
    }
}
