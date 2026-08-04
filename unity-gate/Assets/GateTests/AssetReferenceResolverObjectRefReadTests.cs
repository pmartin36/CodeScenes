using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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
        // BuildSceneRefResolver's dictionary is built from mapped node entries (GameObject and
        // PrefabInstance).
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

        // No resolver: a populated scene ref must stay Unsupported.
        var node = AssetReferenceResolver.ReadObjectReference(prop, resolveSceneRef: null);

        Assert.IsInstanceOf<ValueNode.Unsupported>(node,
            "With no resolver supplied, a scene-object reference must stay Unsupported — got " +
            node.GetType().Name);
    }

    // A component field referencing a PREFAB-INSTANCE ROOT must reverse-map through
    // BuildSceneRefResolver to the instance's LogicalId, the same as a plain GameObject target —
    // the resolver's node index must accept both mapped node kinds.
    [Test]
    public void ReadObjectReference_PrefabInstanceRootTarget_ResolvesToItsLogicalId()
    {
        const string fixturesDir = "Assets/GateTests/Fixtures_ObjRefPrefabInstanceRead";
        const string prefabPath = fixturesDir + "/RefTarget.prefab";

        if (!AssetDatabase.IsValidFolder(fixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "Fixtures_ObjRefPrefabInstanceRead");
        }

        var prefabSource = new GameObject("RefTarget");
        PrefabUtility.SaveAsPrefabAsset(prefabSource, prefabPath);
        Object.DestroyImmediate(prefabSource);

        try
        {
            var instanceRoot = (GameObject)PrefabUtility.InstantiatePrefab(
                AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath));
            var openerGo = new GameObject("Opener");
            var doorOpener = openerGo.AddComponent<DoorOpener>();
            doorOpener.target = instanceRoot;
            SaveActiveScene();

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry
                    {
                        LogicalId = "Tank", Kind = "PrefabInstance",
                        GlobalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(instanceRoot).ToString(),
                    },
                },
            };
            var resolver = ObjectReferenceResolver.BuildSceneRefResolver(map);

            var prop = new SerializedObject(doorOpener).FindProperty("target");
            Assert.IsNotNull(prop, "DoorOpener.target property not found");

            var node = AssetReferenceResolver.ReadObjectReference(prop, resolver);

            Assert.IsInstanceOf<ValueNode.ObjectRef>(node,
                "A live prefab-instance-root reference did not read back as ObjectRef (got " + node.GetType().Name + ")");
            Assert.AreEqual("Tank", ((ValueNode.ObjectRef)node).TargetLogicalId,
                "A prefab-instance-root target must resolve to its mapped LogicalId ('Tank'), not its raw " +
                "GlobalObjectId — got '" + ((ValueNode.ObjectRef)node).TargetLogicalId + "'");
        }
        finally
        {
            AssetDatabase.DeleteAsset(prefabPath);
            if (AssetDatabase.IsValidFolder(fixturesDir))
            {
                AssetDatabase.DeleteAsset(fixturesDir);
            }
        }
    }

    // Every adapter read that can encounter an in-scene object reference must STATE its
    // scene-identity answer (a resolver, or an explicit null); it must never OMIT the answer via a
    // default parameter value or sticky mutable state (a settable property or a non-readonly field).
    // The scan set is DISCOVERED by reflecting over every type in the production assembly, not listed
    // by hand, so a type added later inherits the rule without anyone remembering to register it.
    private sealed class ResolverOmissionScan
    {
        public List<string> Parameters = new List<string>();
        public List<string> Properties = new List<string>();
        public List<string> Fields = new List<string>();
    }

    private static ResolverOmissionScan ScanForOmittableResolvers(Assembly assembly)
    {
        var resolverType = typeof(System.Func<UnityEngine.Object, string>);
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        System.Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).ToArray();
        }

        var scan = new ResolverOmissionScan();

        foreach (var type in types)
        {
            if (type.IsDefined(typeof(CompilerGeneratedAttribute), false))
            {
                continue;
            }

            foreach (var method in type.GetMethods(flags))
            {
                foreach (var parameter in method.GetParameters())
                {
                    if (parameter.ParameterType == resolverType && parameter.HasDefaultValue)
                    {
                        scan.Parameters.Add(type.Name + "." + method.Name + "(" + parameter.Name + ")");
                    }
                }
            }

            foreach (var ctor in type.GetConstructors(flags))
            {
                foreach (var parameter in ctor.GetParameters())
                {
                    if (parameter.ParameterType == resolverType && parameter.HasDefaultValue)
                    {
                        scan.Parameters.Add(type.Name + ".ctor(" + parameter.Name + ")");
                    }
                }
            }

            foreach (var property in type.GetProperties(flags))
            {
                if (property.PropertyType == resolverType && property.CanWrite)
                {
                    scan.Properties.Add(type.Name + "." + property.Name);
                }
            }

            foreach (var field in type.GetFields(flags))
            {
                if (field.FieldType == resolverType && !field.IsInitOnly)
                {
                    scan.Fields.Add(type.Name + "." + field.Name);
                }
            }
        }

        return scan;
    }

    // Deliberate offender fixture: never instantiated, referenced only by reflection, so
    // SceneRefResolverGuard_DetectsEveryOmissionShape can prove the scan actually detects each
    // omission shape rather than passing on an empty result by construction.
    private static class ResolverGuardOffenderFixture
    {
        public static System.Func<UnityEngine.Object, string> Sticky;

        public static System.Func<UnityEngine.Object, string> Settable { get; set; }

        public static void Read(SerializedProperty p,
            System.Func<UnityEngine.Object, string> resolveSceneRef = null)
        {
        }
    }

    [Test]
    public void SceneRefResolverParameters_AreRequired_NotDefaulted()
    {
        var scan = ScanForOmittableResolvers(typeof(SerializedFieldBridge).Assembly);

        Assert.IsEmpty(scan.Parameters,
            "Every scene-ref resolver parameter must be REQUIRED, not defaulted, so a caller cannot " +
            "silently omit whether an object-reference field resolves to scene identity. Offending: " +
            string.Join(", ", scan.Parameters));
        Assert.IsEmpty(scan.Properties,
            "No type may expose a settable scene-ref resolver property — sticky mutable state is the " +
            "same omission hazard as a defaulted parameter. Offending: " + string.Join(", ", scan.Properties));
        Assert.IsEmpty(scan.Fields,
            "No type may expose a non-readonly scene-ref resolver field — sticky mutable state is the " +
            "same omission hazard as a defaulted parameter. Offending: " + string.Join(", ", scan.Fields));
    }

    [Test]
    public void SceneRefResolverGuard_DetectsEveryOmissionShape()
    {
        var scan = ScanForOmittableResolvers(Assembly.GetExecutingAssembly());

        CollectionAssert.Contains(scan.Parameters,
            "ResolverGuardOffenderFixture.Read(resolveSceneRef)",
            "The scan must detect a defaulted resolver parameter. Found: " + string.Join(", ", scan.Parameters));
        CollectionAssert.Contains(scan.Properties,
            "ResolverGuardOffenderFixture.Settable",
            "The scan must detect a settable resolver property. Found: " + string.Join(", ", scan.Properties));
        CollectionAssert.Contains(scan.Fields,
            "ResolverGuardOffenderFixture.Sticky",
            "The scan must detect a non-readonly resolver field. Found: " + string.Join(", ", scan.Fields));
    }
}
