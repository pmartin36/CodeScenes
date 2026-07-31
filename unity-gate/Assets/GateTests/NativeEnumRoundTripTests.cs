using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SceneBuilder.Editor;
using SceneBuilder.Core.Model;

// SerializedMemberMap's round-trip contract for a NATIVE enum (no backing managed FieldInfo):
// ReadComponent reports it as a typed ValueNode.Enum carrying the member NAME (never a raw int),
// and WriteField accepts a typed ValueNode.Enum by member NAME (never SerializedProperty.enumNames
// display-string matching, which is display text like "Screen Space - Camera" and can never equal
// an authored member spelling like "ScreenSpaceCamera"). Covers both an Enum-typed native property
// (Canvas.m_RenderMode) and an Integer-typed native property whose value is a [Flags] enum
// (Rigidbody.m_Constraints) — the same class, not one field.
public class NativeEnumRoundTripTests
{
    private const string ScenePath = "Assets/GateTests/__NativeEnumRoundTripTemp.unity";

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

    private static void SaveActiveScene() =>
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

    private static List<(string Message, LogType Type)> CaptureLogs(System.Action action)
    {
        var messages = new List<(string, LogType)>();
        void Handler(string condition, string stackTrace, LogType type) => messages.Add((condition, type));
        Application.logMessageReceived += Handler;
        try { action(); }
        finally { Application.logMessageReceived -= Handler; }
        return messages;
    }

    [Test]
    public void ReadComponent_CanvasWorldSpace_ReadsAsTypedEnumWithMemberName()
    {
        var canvas = new GameObject("Canvas").AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        SaveActiveScene();

        var data = SerializedFieldBridge.ReadComponent(canvas);

        Assert.IsTrue(data.Fields.TryGetValue("m_RenderMode", out var node),
            "A native enum field must still be present in the read.");
        var enumNode = node as ValueNode.Enum;
        Assert.IsNotNull(enumNode,
            $"Canvas.m_RenderMode must read as a typed ValueNode.Enum, not {node?.GetType().Name}.");
        Assert.AreEqual("UnityEngine.RenderMode", enumNode.TypeFullName);
        Assert.AreEqual(new[] { "WorldSpace" }, enumNode.Members);
        Assert.IsFalse(enumNode.IsFlags);
    }

    [Test]
    public void ReadComponent_RigidbodyConstraintsFlags_ReadsAsMultiMemberFlagsEnum()
    {
        var rb = new GameObject("Body").AddComponent<Rigidbody>();
        rb.constraints = RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezeRotationZ;
        SaveActiveScene();

        var data = SerializedFieldBridge.ReadComponent(rb);

        Assert.IsTrue(data.Fields.TryGetValue("m_Constraints", out var node),
            "Rigidbody.m_Constraints must still be present in the read.");
        var enumNode = node as ValueNode.Enum;
        Assert.IsNotNull(enumNode,
            $"m_Constraints is an Integer-typed native property whose value is a [Flags] enum; it must " +
            $"read as a typed ValueNode.Enum, not {node?.GetType().Name}.");
        Assert.AreEqual("UnityEngine.RigidbodyConstraints", enumNode.TypeFullName);
        CollectionAssert.AreEquivalent(new[] { "FreezePositionY", "FreezeRotationZ" }, enumNode.Members);
        Assert.IsTrue(enumNode.IsFlags);
    }

    [Test]
    public void WriteField_CanvasRenderMode_ByMemberName_SetsSerializedValue_NoRefusalWarning()
    {
        var canvas = new GameObject("Canvas").AddComponent<Canvas>();
        var so = new SerializedObject(canvas);
        var node = new ValueNode.Enum("UnityEngine.RenderMode", new[] { "ScreenSpaceCamera" }, IsFlags: false);

        var logs = CaptureLogs(() =>
        {
            SerializedFieldBridge.WriteField(so, "m_RenderMode", node);
            so.ApplyModifiedPropertiesWithoutUndo();
        });

        Assert.IsFalse(logs.Any(l => l.Type == LogType.Warning),
            "Writing a native enum by its authored member NAME must not refuse with a " +
            "'not found' warning (enumNames is a DISPLAY string, e.g. 'Screen Space - Camera', " +
            "never the authored spelling).\n" + string.Join("\n", logs.Select(l => l.Message)));

        var writtenValue = new SerializedObject(canvas).FindProperty("m_RenderMode").intValue;
        Assert.AreEqual((int)RenderMode.ScreenSpaceCamera, writtenValue,
            "The serialized m_RenderMode value did not land as the authored member.");
    }

    [Test]
    public void WriteField_RigidbodyConstraintsFlags_ByMemberNames_SetsCombinedValue()
    {
        var rb = new GameObject("Body").AddComponent<Rigidbody>();
        var so = new SerializedObject(rb);
        var node = new ValueNode.Enum(
            "UnityEngine.RigidbodyConstraints", new[] { "FreezePositionY", "FreezeRotationZ" }, IsFlags: true);

        SerializedFieldBridge.WriteField(so, "m_Constraints", node);
        so.ApplyModifiedPropertiesWithoutUndo();

        Assert.AreEqual(
            RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezeRotationZ,
            rb.constraints,
            "Writing a [Flags] enum by member name on an Integer-typed native property " +
            "(m_Constraints has no SerializedPropertyType.Enum) did not land the combined value.");
    }

    [Test]
    public void ReadWriteRead_CanvasRenderModeNode_IsStableAcrossTheAdapterBoundary()
    {
        var source = new GameObject("Source").AddComponent<Canvas>();
        source.renderMode = RenderMode.WorldSpace;
        SaveActiveScene();
        var originalNode = SerializedFieldBridge.ReadComponent(source).Fields["m_RenderMode"];
        Assert.IsInstanceOf<ValueNode.Enum>(originalNode,
            "PREMISE: the read must already be a typed Enum node for this round trip to prove the " +
            "read, write and model agree on one representation, not merely that a raw int is stable.");

        var target = new GameObject("Target").AddComponent<Canvas>(); // starts at a different value
        var so = new SerializedObject(target);
        SerializedFieldBridge.WriteField(so, "m_RenderMode", originalNode);
        so.ApplyModifiedPropertiesWithoutUndo();

        var roundTrippedNode = SerializedFieldBridge.ReadComponent(target).Fields["m_RenderMode"];

        Assert.AreEqual(originalNode, roundTrippedNode,
            "Reading a native enum, writing it to a different component, then reading it again must " +
            "reproduce an EQUAL node — the read, write and model must agree on one representation.");
    }
}
