using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using SceneBuilder.Core.Model;

// Pins SceneBuilder.Core.Model.UnityEventProjection's mode/callstate numbers against the real
// shipped UnityEngine.Events enums, and pins the one serialized SerializedProperty path both
// adapter directions read/write persistent calls through. A wrong constant, an enum arity the
// engine changed, or a moved serialized path fails HERE with a message naming the pair, instead of
// silently shipping a listener that fires at the wrong time or never gets written at all.
public class UnityEventEnumPinTests
{
    // ---- Mode numbers ------------------------------------------------------------------------

    [Test]
    public void ModeNumber_Dynamic_MatchesEventDefined()
    {
        Assert.AreEqual((int)PersistentListenerMode.EventDefined, UnityEventProjection.ModeNumber(ListenerArgMode.Dynamic),
            "ListenerArgMode.Dynamic must map to PersistentListenerMode.EventDefined's numeric value");
    }

    [Test]
    public void ModeNumber_Void_MatchesVoid()
    {
        Assert.AreEqual((int)PersistentListenerMode.Void, UnityEventProjection.ModeNumber(ListenerArgMode.Void),
            "ListenerArgMode.Void must map to PersistentListenerMode.Void's numeric value");
    }

    [Test]
    public void ModeNumber_Object_MatchesObject()
    {
        Assert.AreEqual((int)PersistentListenerMode.Object, UnityEventProjection.ModeNumber(ListenerArgMode.Object),
            "ListenerArgMode.Object must map to PersistentListenerMode.Object's numeric value");
    }

    [Test]
    public void ModeNumber_Int_MatchesInt()
    {
        Assert.AreEqual((int)PersistentListenerMode.Int, UnityEventProjection.ModeNumber(ListenerArgMode.Int),
            "ListenerArgMode.Int must map to PersistentListenerMode.Int's numeric value");
    }

    [Test]
    public void ModeNumber_Float_MatchesFloat()
    {
        Assert.AreEqual((int)PersistentListenerMode.Float, UnityEventProjection.ModeNumber(ListenerArgMode.Float),
            "ListenerArgMode.Float must map to PersistentListenerMode.Float's numeric value");
    }

    [Test]
    public void ModeNumber_String_MatchesString()
    {
        Assert.AreEqual((int)PersistentListenerMode.String, UnityEventProjection.ModeNumber(ListenerArgMode.String),
            "ListenerArgMode.String must map to PersistentListenerMode.String's numeric value");
    }

    [Test]
    public void ModeNumber_Bool_MatchesBool()
    {
        Assert.AreEqual((int)PersistentListenerMode.Bool, UnityEventProjection.ModeNumber(ListenerArgMode.Bool),
            "ListenerArgMode.Bool must map to PersistentListenerMode.Bool's numeric value");
    }

    // ---- Call state numbers -- the pair the spec transposes ----------------------------------

    [Test]
    public void CallStateNumber_Off_MatchesOff()
    {
        Assert.AreEqual((int)UnityEventCallState.Off, UnityEventProjection.CallStateNumber(ListenerCallState.Off),
            "ListenerCallState.Off must map to UnityEventCallState.Off's numeric value");
    }

    [Test]
    public void CallStateNumber_EditorAndRuntime_MatchesEditorAndRuntime()
    {
        Assert.AreEqual((int)UnityEventCallState.EditorAndRuntime, UnityEventProjection.CallStateNumber(ListenerCallState.EditorAndRuntime),
            "ListenerCallState.EditorAndRuntime must map to UnityEventCallState.EditorAndRuntime's numeric value");
    }

    [Test]
    public void CallStateNumber_RuntimeOnly_MatchesRuntimeOnly()
    {
        // This is the assertion that fails if Core's table is "fixed" to match the spec's
        // (transposed) numbers instead of the shipped engine.
        Assert.AreEqual((int)UnityEventCallState.RuntimeOnly, UnityEventProjection.CallStateNumber(ListenerCallState.RuntimeOnly),
            "ListenerCallState.RuntimeOnly must map to UnityEventCallState.RuntimeOnly's numeric value");
    }

    // ---- Arity: a Unity upgrade adding a member must fail here, not become a silent Unsupported ----

    [Test]
    public void PersistentListenerMode_HasExactlySevenMembers()
    {
        Assert.AreEqual(7, Enum.GetValues(typeof(PersistentListenerMode)).Length,
            "PersistentListenerMode's member count changed; UnityEventProjection's table must be extended to match before this pin is updated");
    }

    [Test]
    public void UnityEventCallState_HasExactlyThreeMembers()
    {
        Assert.AreEqual(3, Enum.GetValues(typeof(UnityEventCallState)).Length,
            "UnityEventCallState's member count changed; UnityEventProjection's table must be extended to match before this pin is updated");
    }

    // ---- Serialized path: what both adapter directions read/write persistent calls through ----

    [Test]
    public void Button_OnClick_HasAPersistentCallsArrayAtTheExpectedSerializedPath()
    {
        var go = new GameObject("PathPinButton");
        try
        {
            var button = go.AddComponent<Button>();
            var so = new UnityEditor.SerializedObject(button);

            var calls = so.FindProperty("m_OnClick.m_PersistentCalls.m_Calls");

            Assert.IsNotNull(calls, "Button.OnClick has no m_OnClick.m_PersistentCalls.m_Calls SerializedProperty");
            Assert.IsTrue(calls.isArray, "m_OnClick.m_PersistentCalls.m_Calls is not an array-typed SerializedProperty");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }
}
