using System;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SceneBuilder.Editor;
using SceneBuilder.Core.Model;

// The adapter READ path for a [SerializeReference] polymorphic field (specs/10-m9-serializereference.md):
// a live managed-reference instance must read back as ValueNode.ManagedReference under
// ComponentData.Fields[fieldPath], with the concrete type's bare FullName (no assembly hint, so an
// idempotent re-Materialize never spuriously diffs against the assembly-less authored TypeRef) and its
// own serialized fields nested underneath. A null reference reads as ManagedReference with a null
// ConcreteType, distinct from an instance with empty fields. Two fields (or a self-referencing cycle)
// sharing the same managed-reference instance read as ValueNode.Unsupported on every sharing field,
// never forked into duplicate ManagedReference trees.
public class ManagedReferenceReadTests
{
    [SetUp]
    public void SetUp()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    private static AiBrain BuildBrain()
    {
        var go = new GameObject("Brain");
        return go.AddComponent<AiBrain>();
    }

    [Test]
    public void ReadComponent_NonNullConcreteInstance_ReadsAsManagedReferenceWithBareTypeNameAndFields()
    {
        var brain = BuildBrain();
        var so = new SerializedObject(brain);
        so.FindProperty("strategy").managedReferenceValue = new Aggressive { range = 5f };
        so.ApplyModifiedPropertiesWithoutUndo();

        var data = SerializedFieldBridge.ReadComponent(brain, resolveSceneRef: null);

        Assert.IsTrue(data.Fields.TryGetValue("strategy", out var node),
            "A non-null [SerializeReference] field must survive CollectFields, not be dropped as Unsupported.");
        Assert.IsInstanceOf<ValueNode.ManagedReference>(node,
            $"strategy must read as ValueNode.ManagedReference; got {node?.GetType().Name}");
        var managedRef = (ValueNode.ManagedReference)node;
        Assert.AreEqual("Aggressive", managedRef.ConcreteType?.FullName);
        Assert.IsNull(managedRef.ConcreteType?.AssemblyHint,
            "The read TypeRef must carry no assembly hint, matching the authored (assembly-less) form.");
        Assert.IsTrue(managedRef.Fields.TryGetValue("range", out var rangeNode));
        Assert.AreEqual(ValueNode.Primitive.Float(5f), rangeNode);
    }

    [Test]
    public void ReadComponent_NullReference_ReadsAsManagedReferenceWithNullConcreteTypeAndEmptyFields()
    {
        var brain = BuildBrain();
        // strategy is already null by default -- no assignment needed.

        var data = SerializedFieldBridge.ReadComponent(brain, resolveSceneRef: null);

        Assert.IsTrue(data.Fields.TryGetValue("strategy", out var node),
            "A null [SerializeReference] field must still read as a field, distinct from being dropped.");
        Assert.IsInstanceOf<ValueNode.ManagedReference>(node);
        var managedRef = (ValueNode.ManagedReference)node;
        Assert.IsNull(managedRef.ConcreteType, "A null reference must read with a null ConcreteType.");
        Assert.AreEqual(0, managedRef.Fields.Count, "A null reference carries no fields.");
    }

    [Test]
    public void ReadComponent_NestedManagedRef_ReadsPrimaryAndFallbackAsManagedReferenceRecursively()
    {
        var brain = BuildBrain();
        var so = new SerializedObject(brain);
        so.FindProperty("strategy").managedReferenceValue = new Composite
        {
            primary = new Aggressive { range = 5f },
            fallback = new Flee { speed = 3f },
        };
        so.ApplyModifiedPropertiesWithoutUndo();

        var data = SerializedFieldBridge.ReadComponent(brain, resolveSceneRef: null);

        Assert.IsTrue(data.Fields.TryGetValue("strategy", out var node));
        var composite = (ValueNode.ManagedReference)node;
        Assert.AreEqual("Composite", composite.ConcreteType?.FullName);
        Assert.IsTrue(composite.Fields.TryGetValue("primary", out var primaryNode));
        Assert.IsInstanceOf<ValueNode.ManagedReference>(primaryNode,
            "A nested managed-ref field must itself read as ValueNode.ManagedReference, recursing at depth.");
        Assert.AreEqual("Aggressive", ((ValueNode.ManagedReference)primaryNode).ConcreteType?.FullName);
        Assert.IsTrue(composite.Fields.TryGetValue("fallback", out var fallbackNode));
        Assert.AreEqual("Flee", ((ValueNode.ManagedReference)fallbackNode).ConcreteType?.FullName);
    }

    [Test]
    public void ReadComponent_ManagedRefList_ReadsEachElementAsManagedReferenceElementForElement()
    {
        var brain = BuildBrain();
        var so = new SerializedObject(brain);
        var listProp = so.FindProperty("strategies");
        listProp.arraySize = 2;
        listProp.GetArrayElementAtIndex(0).managedReferenceValue = new Aggressive { range = 5f };
        listProp.GetArrayElementAtIndex(1).managedReferenceValue = new Flee { speed = 3f };
        so.ApplyModifiedPropertiesWithoutUndo();

        var data = SerializedFieldBridge.ReadComponent(brain, resolveSceneRef: null);

        Assert.IsTrue(data.Fields.TryGetValue("strategies", out var node));
        Assert.IsInstanceOf<ValueNode.List>(node);
        var items = ((ValueNode.List)node).Items;
        Assert.AreEqual(2, items.Count);
        Assert.IsInstanceOf<ValueNode.ManagedReference>(items[0]);
        Assert.AreEqual("Aggressive", ((ValueNode.ManagedReference)items[0]).ConcreteType?.FullName);
        Assert.IsInstanceOf<ValueNode.ManagedReference>(items[1]);
        Assert.AreEqual("Flee", ((ValueNode.ManagedReference)items[1]).ConcreteType?.FullName);
    }

    [Test]
    public void ReadComponent_SameInstanceOnTwoFields_ReadsAsUnsupportedSharedMarkerOnBothFieldsNoFork()
    {
        var brain = BuildBrain();
        var so = new SerializedObject(brain);
        var shared = new Aggressive { range = 5f };
        so.FindProperty("strategy").managedReferenceValue = shared;
        var listProp = so.FindProperty("strategies");
        listProp.arraySize = 1;
        listProp.GetArrayElementAtIndex(0).managedReferenceValue = shared;
        so.ApplyModifiedPropertiesWithoutUndo();

        var data = SerializedFieldBridge.ReadComponent(brain, resolveSceneRef: null);

        Assert.IsTrue(data.Fields.TryGetValue("strategy", out var strategyNode));
        Assert.IsInstanceOf<ValueNode.Unsupported>(strategyNode,
            "A managed-ref instance shared by two fields must read as Unsupported, never a forked ManagedReference tree.");
        Assert.IsTrue(
            ((ValueNode.Unsupported)strategyNode).RawToken.StartsWith(ManagedReferenceReader.SharedMarker, StringComparison.Ordinal),
            "The shared field's Unsupported token must carry the reader's shared-reference marker prefix.");

        Assert.IsTrue(data.Fields.TryGetValue("strategies", out var strategiesNode));
        var listItem = ((ValueNode.List)strategiesNode).Items[0];
        Assert.IsInstanceOf<ValueNode.Unsupported>(listItem);
        Assert.IsTrue(
            ((ValueNode.Unsupported)listItem).RawToken.StartsWith(ManagedReferenceReader.SharedMarker, StringComparison.Ordinal));
    }

    [Test]
    public void ReadComponent_SelfReferencingCycle_ReadsBackEdgeAsUnsupportedSharedMarkerAndTerminates()
    {
        var brain = BuildBrain();
        var so = new SerializedObject(brain);
        var composite = new Composite();
        composite.primary = composite; // back-edge: primary points at its own instance
        so.FindProperty("strategy").managedReferenceValue = composite;
        so.ApplyModifiedPropertiesWithoutUndo();

        var data = SerializedFieldBridge.ReadComponent(brain, resolveSceneRef: null);

        Assert.IsTrue(data.Fields.TryGetValue("strategy", out var node));
        Assert.IsInstanceOf<ValueNode.ManagedReference>(node,
            "The outer (non-repeated) managed-ref instance must still read as ManagedReference.");
        var outer = (ValueNode.ManagedReference)node;
        Assert.IsTrue(outer.Fields.TryGetValue("primary", out var primaryNode));
        Assert.IsInstanceOf<ValueNode.Unsupported>(primaryNode,
            "The back-edge of a self-referencing cycle must read as Unsupported, not recurse forever.");
        Assert.IsTrue(
            ((ValueNode.Unsupported)primaryNode).RawToken.StartsWith(ManagedReferenceReader.SharedMarker, StringComparison.Ordinal));
    }
}
