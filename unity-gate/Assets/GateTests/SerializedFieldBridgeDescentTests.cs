using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using GateFixtures;
using SceneBuilder.Editor;
using SceneBuilder.Core.Model;
using CoreColor = SceneBuilder.Core.Model.Color;

// SerializedFieldBridge.WriteField's List and Nested arms descend a live SerializedProperty
// recursively rather than writing a single leaf. These tests call WriteField DIRECTLY (never through
// a Plan/PlanExecutor) with a container-shaped ValueNode, so each one isolates the arm it names and
// asserts the resulting live component state after ApplyModifiedProperties.

// A struct whose only member requires a SerializedMemberMap lookup (private-backed, mapped-name
// property) so that resolving its declaring type from the WRONG node in a multi-level descent — the
// enclosing struct instead of the struct that actually owns the member — fails to find the child
// property rather than accidentally succeeding.
[System.Serializable]
public struct DescentMappedLeaf
{
    [SerializeField] private float m_Depth;
    public float depth { get => m_Depth; set => m_Depth = value; }
}

// Holds the mapped leaf under an identity-named member, so only the INNER key needs the member map
// and the outer struct itself never resolves a member spelled "depth".
[System.Serializable]
public struct DescentMappedOuter
{
    public DescentMappedLeaf leaf;
    public float y;
}

public class DescentMappedDepthBehaviour : MonoBehaviour
{
    public DescentMappedOuter Deep;
    public DescentMappedLeaf[] Leaves;
}

public class SerializedFieldBridgeDescentTests
{
    [SetUp]
    public void SetUp()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    private static FieldMap FM(params (string Key, ValueNode Value)[] pairs) =>
        new FieldMap(pairs.Select(p => new KeyValuePair<string, ValueNode>(p.Key, p.Value)));

    // ---- arm (a): ValueNode.List ------------------------------------------------------------

    [Test]
    public void WriteField_ListValue_SizesTheArrayThenWritesEachElementInOrder()
    {
        var go = new GameObject("Sample");
        var sample = go.AddComponent<GateSampleBehaviour>();
        sample.Volley = new[] { new Damage { amount = 9f, kind = 9 }, new Damage { amount = 8f, kind = 8 }, new Damage { amount = 7f, kind = 7 } };
        var so = new SerializedObject(sample);

        SerializedFieldBridge.WriteField(so, "Volley", new ValueNode.List(new ValueNode[]
        {
            new ValueNode.Nested("GateFixtures.Damage", FM(("amount", ValueNode.Primitive.Float(5f)), ("kind", ValueNode.Primitive.Int(1)))),
            new ValueNode.Nested("GateFixtures.Damage", FM(("amount", ValueNode.Primitive.Float(6f)), ("kind", ValueNode.Primitive.Int(2)))),
        }));
        so.ApplyModifiedProperties();

        Assert.AreEqual(2, sample.Volley.Length, "arraySize was not applied before the per-item writes ran");
        Assert.AreEqual(5f, sample.Volley[0].amount, "element 0's amount was not written");
        Assert.AreEqual(1, sample.Volley[0].kind, "element 0's kind was not written");
        Assert.AreEqual(6f, sample.Volley[1].amount, "element 1's amount was not written");
        Assert.AreEqual(2, sample.Volley[1].kind, "element 1's kind was not written");
    }

    [Test]
    public void WriteField_EmptyListValue_TruncatesTheLiveArrayToZero()
    {
        var go = new GameObject("Sample");
        var sample = go.AddComponent<GateSampleBehaviour>();
        sample.Volley = new[] { new Damage { amount = 9f, kind = 9 }, new Damage { amount = 8f, kind = 8 } };
        var so = new SerializedObject(sample);

        SerializedFieldBridge.WriteField(so, "Volley", new ValueNode.List(System.Array.Empty<ValueNode>()));
        so.ApplyModifiedProperties();

        Assert.AreEqual(0, sample.Volley.Length, "an empty List value did not truncate the live array");
    }

    [Test]
    public void WriteField_UnwritableLeafKindInsideAList_WarnsPerElementNamingTheElementPath()
    {
        var go = new GameObject("Path");
        var waypointPath = go.AddComponent<WaypointPath>();
        var so = new SerializedObject(waypointPath);

        LogAssert.Expect(LogType.Warning, new Regex(@"waypoints\.Array\.data\[0\]"));
        LogAssert.Expect(LogType.Warning, new Regex(@"waypoints\.Array\.data\[1\]"));
        SerializedFieldBridge.WriteField(so, "waypoints", new ValueNode.List(new ValueNode[]
        {
            new ValueNode.ObjectRef("A"),
            new ValueNode.ObjectRef("B"),
        }));
        so.ApplyModifiedProperties();

        Assert.AreEqual(2, waypointPath.waypoints.Length, "the array was not sized even though its elements could not be written");
        Assert.IsNull(waypointPath.waypoints[0], "an unwritable element must not silently write a value");
        Assert.IsNull(waypointPath.waypoints[1], "an unwritable element must not silently write a value");
    }

    // ---- arm (b): ValueNode.Nested through SerializedMemberMap ------------------------------

    [Test]
    public void WriteField_NestedValueKeyedByPublicMemberSpelling_WritesThroughTheSerializedMemberMap()
    {
        Assert.AreEqual(typeof(ColorBlock), SerializedMemberMap.ResolveManagedFieldType(typeof(Button), "m_Colors"),
            "premise: Button.m_Colors no longer resolves to ColorBlock");
        Assert.IsTrue(SerializedMemberMap.TrySerializedName(typeof(ColorBlock), "normalColor", out var serializedName),
            "premise: ColorBlock.normalColor no longer maps to a serialized name");
        Assert.AreEqual("m_NormalColor", serializedName, "premise: the public->serialized mapping for normalColor changed");

        var go = new GameObject("Btn");
        var btn = go.AddComponent<Button>();
        var so = new SerializedObject(btn);

        SerializedFieldBridge.WriteField(so, "m_Colors", new ValueNode.Nested("UnityEngine.UI.ColorBlock", FM(
            ("normalColor", new ValueNode.Color(new CoreColor(1f, 0f, 0f, 1f))),
            ("fadeDuration", ValueNode.Primitive.Float(0.25f)))));
        so.ApplyModifiedProperties();

        Assert.AreEqual(new UnityEngine.Color(1f, 0f, 0f, 1f), btn.colors.normalColor,
            "the public-spelling nested member was not written through the serialized member map");
        Assert.AreEqual(0.25f, btn.colors.fadeDuration, "the public-spelling nested member was not written through the serialized member map");
    }

    [Test]
    public void WriteField_NestedValueKeyedByRawSerializedName_StillWrites()
    {
        var go = new GameObject("Btn");
        var btn = go.AddComponent<Button>();
        var so = new SerializedObject(btn);

        SerializedFieldBridge.WriteField(so, "m_Colors", new ValueNode.Nested("UnityEngine.UI.ColorBlock", FM(
            ("m_PressedColor", new ValueNode.Color(new CoreColor(0f, 1f, 0f, 1f))))));
        so.ApplyModifiedProperties();

        Assert.AreEqual(new UnityEngine.Color(0f, 1f, 0f, 1f), btn.colors.pressedColor,
            "a nested member keyed by its raw serialized name was not written");
    }

    [Test]
    public void WriteField_NestedMemberKeyMatchingNoChildProperty_IsSkippedLeavingSiblingsWritten()
    {
        var go = new GameObject("Btn");
        var btn = go.AddComponent<Button>();
        var so = new SerializedObject(btn);

        Assert.DoesNotThrow(() => SerializedFieldBridge.WriteField(so, "m_Colors", new ValueNode.Nested("UnityEngine.UI.ColorBlock", FM(
            ("normalColor", new ValueNode.Color(new CoreColor(1f, 0f, 0f, 1f))),
            ("noSuchMember", ValueNode.Primitive.Float(1f)),
            ("fadeDuration", ValueNode.Primitive.Float(0.5f))))),
            "a nested member key with no matching child property must not throw");
        so.ApplyModifiedProperties();

        Assert.AreEqual(new UnityEngine.Color(1f, 0f, 0f, 1f), btn.colors.normalColor, "the sibling before the unmapped key was not written");
        Assert.AreEqual(0.5f, btn.colors.fadeDuration, "the sibling after the unmapped key was not written");
    }

    [Test]
    public void WriteField_NestedMemberKeyMatchingNoChildPropertyCarryingANestedValue_SkipsTheWholeSubtree()
    {
        var go = new GameObject("Btn");
        var btn = go.AddComponent<Button>();
        var so = new SerializedObject(btn);
        var pressedBefore = btn.colors.pressedColor;
        var multiplierBefore = btn.colors.colorMultiplier;

        Assert.DoesNotThrow(() => SerializedFieldBridge.WriteField(so, "m_Colors", new ValueNode.Nested("UnityEngine.UI.ColorBlock", FM(
            ("normalColor", new ValueNode.Color(new CoreColor(1f, 0f, 0f, 1f))),
            ("noSuchMember", new ValueNode.Nested("UnityEngine.UI.ColorBlock", FM(
                ("pressedColor", new ValueNode.Color(new CoreColor(0f, 1f, 0f, 1f))),
                ("colorMultiplier", ValueNode.Primitive.Float(3f))))),
            ("fadeDuration", ValueNode.Primitive.Float(0.5f))))),
            "an unmapped member key whose value is a nested value must not throw");
        so.ApplyModifiedProperties();

        Assert.AreEqual(new UnityEngine.Color(1f, 0f, 0f, 1f), btn.colors.normalColor, "the sibling before the unmapped key was not written");
        Assert.AreEqual(0.5f, btn.colors.fadeDuration, "the sibling after the unmapped key was not written");
        Assert.AreEqual(pressedBefore, btn.colors.pressedColor, "a member of the skipped subtree must not leak into the enclosing struct");
        Assert.AreEqual(multiplierBefore, btn.colors.colorMultiplier, "a member of the skipped subtree must not leak into the enclosing struct");
    }

    [Test]
    public void WriteField_NestedMemberKeyMatchingNoChildPropertyCarryingAListValue_SkipsTheWholeSubtree()
    {
        var go = new GameObject("Btn");
        var btn = go.AddComponent<Button>();
        var so = new SerializedObject(btn);
        var pressedBefore = btn.colors.pressedColor;
        var multiplierBefore = btn.colors.colorMultiplier;

        Assert.DoesNotThrow(() => SerializedFieldBridge.WriteField(so, "m_Colors", new ValueNode.Nested("UnityEngine.UI.ColorBlock", FM(
            ("normalColor", new ValueNode.Color(new CoreColor(1f, 0f, 0f, 1f))),
            ("noSuchMember", new ValueNode.List(new ValueNode[]
            {
                new ValueNode.Nested("UnityEngine.UI.ColorBlock", FM(("pressedColor", new ValueNode.Color(new CoreColor(0f, 1f, 0f, 1f))))),
                ValueNode.Primitive.Float(4f),
            })),
            ("fadeDuration", ValueNode.Primitive.Float(0.5f))))),
            "an unmapped member key whose value is a list must not throw");
        so.ApplyModifiedProperties();

        Assert.AreEqual(new UnityEngine.Color(1f, 0f, 0f, 1f), btn.colors.normalColor, "the sibling before the unmapped key was not written");
        Assert.AreEqual(0.5f, btn.colors.fadeDuration, "the sibling after the unmapped key was not written");
        Assert.AreEqual(pressedBefore, btn.colors.pressedColor, "a member of the skipped subtree must not leak into the enclosing struct");
        Assert.AreEqual(multiplierBefore, btn.colors.colorMultiplier, "a member of the skipped subtree must not leak into the enclosing struct");
    }

    [Test]
    public void WriteField_NestedInsideNested_WritesBothLevels()
    {
        var go = new GameObject("Deep");
        var deep = go.AddComponent<NestedInNestedFixtureBehaviour>();
        var so = new SerializedObject(deep);

        SerializedFieldBridge.WriteField(so, "Deep", new ValueNode.Nested("GateFixtures.DeepOuter", FM(
            ("inner", new ValueNode.Nested("GateFixtures.DeepInner", FM(
                ("a", ValueNode.Primitive.Float(2f)),
                ("b", ValueNode.Primitive.Float(3f))))),
            ("y", ValueNode.Primitive.Float(1f)))));
        so.ApplyModifiedProperties();

        Assert.AreEqual(2f, deep.Deep.inner.a, "the doubly-nested inner.a was not written");
        Assert.AreEqual(3f, deep.Deep.inner.b, "the doubly-nested inner.b was not written");
        Assert.AreEqual(1f, deep.Deep.y, "the outer sibling y was not written");
    }

    [Test]
    public void WriteField_UnsupportedLeafInsideANestedValue_LeavesThatMemberAndWritesTheRest()
    {
        var go = new GameObject("Deep");
        var deep = go.AddComponent<NestedInNestedFixtureBehaviour>();
        var so = new SerializedObject(deep);

        SerializedFieldBridge.WriteField(so, "Deep", new ValueNode.Nested("GateFixtures.DeepOuter", FM(
            ("inner", new ValueNode.Nested("GateFixtures.DeepInner", FM(
                ("a", ValueNode.Primitive.Float(2f)),
                ("b", ValueNode.Primitive.Float(3f))))),
            ("y", ValueNode.Primitive.Float(1f)))));
        so.ApplyModifiedProperties();

        SerializedFieldBridge.WriteField(so, "Deep", new ValueNode.Nested("GateFixtures.DeepOuter", FM(
            ("inner", new ValueNode.Nested("GateFixtures.DeepInner", FM(
                ("a", new ValueNode.Unsupported("X")),
                ("b", ValueNode.Primitive.Float(9f))))),
            ("y", ValueNode.Primitive.Float(4f)))));
        so.ApplyModifiedProperties();

        Assert.AreEqual(2f, deep.Deep.inner.a, "an Unsupported leaf must leave its member unchanged, not overwrite it");
        Assert.AreEqual(9f, deep.Deep.inner.b, "the sibling member next to the Unsupported leaf was not written");
        Assert.AreEqual(4f, deep.Deep.y, "the outer sibling was not written on the second pass");
    }

    // ---- mapped-name member at container depth >= 2 -----------------------------------------
    // Damage/DeepOuter/DeepInner above are all identity-named (public field name == serialized
    // name), so a wrong declaring type at the inner node still falls back to a working raw key.
    // DescentMappedLeaf.depth only maps through the SerializedMemberMap, so resolving its
    // declaring type from the wrong node in the descent produces a childProp of null instead of a
    // value silently written to the wrong place.

    [Test]
    public void WriteField_NestedMappedNameMemberAtDepthTwo_WritesThroughTheChildTypesMemberMap()
    {
        Assert.AreEqual(typeof(DescentMappedLeaf), SerializedMemberMap.ResolveManagedFieldType(typeof(DescentMappedDepthBehaviour), "Deep.leaf"),
            "premise: Deep.leaf no longer resolves to DescentMappedLeaf");
        Assert.IsTrue(SerializedMemberMap.TrySerializedName(typeof(DescentMappedLeaf), "depth", out var serializedName),
            "premise: DescentMappedLeaf.depth no longer maps to a serialized name");
        Assert.AreEqual("m_Depth", serializedName, "premise: the public->serialized mapping for depth changed");
        Assert.IsFalse(SerializedMemberMap.TrySerializedName(typeof(DescentMappedOuter), "depth", out _),
            "premise: the enclosing struct must not itself resolve the inner member's key");

        var go = new GameObject("Deep2");
        var deep = go.AddComponent<DescentMappedDepthBehaviour>();
        var so = new SerializedObject(deep);

        SerializedFieldBridge.WriteField(so, "Deep", new ValueNode.Nested("DescentMappedOuter", FM(
            ("leaf", new ValueNode.Nested("DescentMappedLeaf", FM(("depth", ValueNode.Primitive.Float(2.5f))))),
            ("y", ValueNode.Primitive.Float(1f)))));
        so.ApplyModifiedProperties();

        Assert.AreEqual(2.5f, deep.Deep.leaf.depth, "the depth-two mapped-name member was not written through its own declaring type's member map");
        Assert.AreEqual(1f, deep.Deep.y, "the depth-one sibling next to the mapped-name descent was not written");
    }

    [Test]
    public void WriteField_NestedMappedNameMemberInsideAListElement_WritesThroughTheElementTypesMemberMap()
    {
        Assert.AreEqual(typeof(DescentMappedLeaf), SerializedMemberMap.ResolveManagedFieldType(typeof(DescentMappedDepthBehaviour), "Leaves.Array.data[0]"),
            "premise: a list element path no longer resolves to DescentMappedLeaf");

        var go = new GameObject("Leaves");
        var deep = go.AddComponent<DescentMappedDepthBehaviour>();
        var so = new SerializedObject(deep);

        SerializedFieldBridge.WriteField(so, "Leaves", new ValueNode.List(new ValueNode[]
        {
            new ValueNode.Nested("DescentMappedLeaf", FM(("depth", ValueNode.Primitive.Float(7f)))),
        }));
        so.ApplyModifiedProperties();

        Assert.AreEqual(1, deep.Leaves.Length, "the list of mapped-name Nested elements was not sized");
        Assert.AreEqual(7f, deep.Leaves[0].depth, "the mapped-name member inside a list element was not written through its own declaring type's member map");
    }
}
