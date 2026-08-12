using System;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using SceneBuilder.Editor;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Plan;

// PlanExecutor SetUnityEvent execution -- the code->scene adapter write path for UnityEvent
// persistent-call listeners (specs/09-m8-unityevents.md). Drives PlanExecutor.Execute with hand-built
// SetUnityEvent ops against a live editor scene and reads the LIVE component's
// m_PersistentCalls.m_Calls array back with SerializedObject/UnityEventProjection's numbers, mirroring
// PlanExecutorObjectRefTests's hand-built-plan pattern.
public class UnityEventWriteTests
{
    private const string AssetDir = "Assets/UnityEventWriteTestsTmp";
    private const string MaterialPath = AssetDir + "/WriteTestMaterial.mat";

    [SetUp]
    public void SetUp()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    [TearDown]
    public void TearDown()
    {
        if (AssetDatabase.IsValidFolder(AssetDir))
        {
            AssetDatabase.DeleteAsset(AssetDir);
        }
    }

    private static GameObject Find(string name) => GameObject.Find(name);

    private static SerializedProperty CallsArray(Component comp, string eventPath)
    {
        var so = new SerializedObject(comp);
        var calls = so.FindProperty($"{eventPath}.m_PersistentCalls.m_Calls");
        Assert.IsNotNull(calls, $"'{eventPath}.m_PersistentCalls.m_Calls' not found on {comp.GetType().Name}");
        return calls;
    }

    private static SerializedProperty CallAt(Component comp, string eventPath, int index)
    {
        var calls = CallsArray(comp, eventPath);
        Assert.Greater(calls.arraySize, index,
            $"Persistent call array on '{eventPath}' has {calls.arraySize} entries; expected at least {index + 1}.");
        return calls.GetArrayElementAtIndex(index);
    }

    [Test]
    public void SetUnityEvent_VoidListener_WritesTargetMethodModeAndDefaultCallState()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new CreateObject { LogicalId = "ButtonHost", Name = "ButtonHost" },
                new AddComponent { LogicalId = "ButtonHost/Button#0", Type = new TypeRef("UnityEngine.UI.Button") },
                new CreateObject { LogicalId = "Opener", Name = "Opener" },
                new AddComponent { LogicalId = "Opener/DoorOpener#0", Type = new TypeRef("DoorOpener") },
                new SetUnityEvent
                {
                    LogicalId = "ButtonHost/Button#0",
                    Path = "m_OnClick",
                    Listeners = new ValueNode.UnityEventListeners(new[]
                    {
                        new UnityEventListener(new ValueNode.ObjectRef("Opener/DoorOpener#0"), "Open", ListenerArgMode.Void),
                    }),
                },
            },
        };

        PlanExecutor.Execute(plan, new IdentityMap(), scene);

        var button = Find("ButtonHost").GetComponent<Button>();
        var doorOpener = Find("Opener").GetComponent<DoorOpener>();
        var call = CallAt(button, "m_OnClick", 0);

        Assert.AreEqual(doorOpener, call.FindPropertyRelative("m_Target").objectReferenceValue,
            "m_Target must resolve to the DoorOpener COMPONENT the ComponentRef names, not its owning GameObject");
        Assert.AreEqual("Open", call.FindPropertyRelative("m_MethodName").stringValue);
        Assert.AreEqual((int)PersistentListenerMode.Void, call.FindPropertyRelative("m_Mode").intValue);
        Assert.AreEqual((int)UnityEventCallState.RuntimeOnly, call.FindPropertyRelative("m_CallState").intValue,
            "Default CallState (RuntimeOnly) was not written");
        StringAssert.Contains(nameof(DoorOpener), call.FindPropertyRelative("m_TargetAssemblyTypeName").stringValue,
            "m_TargetAssemblyTypeName must name the resolved target's own type");
    }

    [Test]
    public void SetUnityEvent_IntListener_WritesIntArgument()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new CreateObject { LogicalId = "ButtonHost", Name = "ButtonHost" },
                new AddComponent { LogicalId = "ButtonHost/Button#0", Type = new TypeRef("UnityEngine.UI.Button") },
                new CreateObject { LogicalId = "LampHost", Name = "LampHost" },
                new AddComponent { LogicalId = "LampHost/Lamp#0", Type = new TypeRef("Lamp") },
                new SetUnityEvent
                {
                    LogicalId = "ButtonHost/Button#0",
                    Path = "m_OnClick",
                    Listeners = new ValueNode.UnityEventListeners(new[]
                    {
                        new UnityEventListener(
                            new ValueNode.ObjectRef("LampHost/Lamp#0"), "SetLevel",
                            ListenerArgMode.Int, ValueNode.Primitive.Int(7)),
                    }),
                },
            },
        };

        PlanExecutor.Execute(plan, new IdentityMap(), scene);

        var button = Find("ButtonHost").GetComponent<Button>();
        var call = CallAt(button, "m_OnClick", 0);

        Assert.AreEqual((int)PersistentListenerMode.Int, call.FindPropertyRelative("m_Mode").intValue);
        Assert.AreEqual(7, call.FindPropertyRelative("m_Arguments.m_IntArgument").intValue);
    }

    [Test]
    public void SetUnityEvent_FloatListener_WritesFloatArgument()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new CreateObject { LogicalId = "ButtonHost", Name = "ButtonHost" },
                new AddComponent { LogicalId = "ButtonHost/Button#0", Type = new TypeRef("UnityEngine.UI.Button") },
                new CreateObject { LogicalId = "MixerHost", Name = "MixerHost" },
                new AddComponent { LogicalId = "MixerHost/Mixer#0", Type = new TypeRef("Mixer") },
                new SetUnityEvent
                {
                    LogicalId = "ButtonHost/Button#0",
                    Path = "m_OnClick",
                    Listeners = new ValueNode.UnityEventListeners(new[]
                    {
                        new UnityEventListener(
                            new ValueNode.ObjectRef("MixerHost/Mixer#0"), "SetVol",
                            ListenerArgMode.Float, ValueNode.Primitive.Float(0.5f)),
                    }),
                },
            },
        };

        PlanExecutor.Execute(plan, new IdentityMap(), scene);

        var button = Find("ButtonHost").GetComponent<Button>();
        var call = CallAt(button, "m_OnClick", 0);

        Assert.AreEqual((int)PersistentListenerMode.Float, call.FindPropertyRelative("m_Mode").intValue);
        Assert.AreEqual(0.5f, call.FindPropertyRelative("m_Arguments.m_FloatArgument").floatValue, 0.0001f);
    }

    [Test]
    public void SetUnityEvent_StringListener_WritesStringArgument()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new CreateObject { LogicalId = "ButtonHost", Name = "ButtonHost" },
                new AddComponent { LogicalId = "ButtonHost/Button#0", Type = new TypeRef("UnityEngine.UI.Button") },
                new CreateObject { LogicalId = "MarqueeHost", Name = "MarqueeHost" },
                new AddComponent { LogicalId = "MarqueeHost/Marquee#0", Type = new TypeRef("Marquee") },
                new SetUnityEvent
                {
                    LogicalId = "ButtonHost/Button#0",
                    Path = "m_OnClick",
                    Listeners = new ValueNode.UnityEventListeners(new[]
                    {
                        new UnityEventListener(
                            new ValueNode.ObjectRef("MarqueeHost/Marquee#0"), "SetText",
                            ListenerArgMode.String, ValueNode.Primitive.String("hi")),
                    }),
                },
            },
        };

        PlanExecutor.Execute(plan, new IdentityMap(), scene);

        var button = Find("ButtonHost").GetComponent<Button>();
        var call = CallAt(button, "m_OnClick", 0);

        Assert.AreEqual((int)PersistentListenerMode.String, call.FindPropertyRelative("m_Mode").intValue);
        Assert.AreEqual("hi", call.FindPropertyRelative("m_Arguments.m_StringArgument").stringValue);
    }

    [Test]
    public void SetUnityEvent_BoolListener_WritesBoolArgument()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new CreateObject { LogicalId = "ButtonHost", Name = "ButtonHost" },
                new AddComponent { LogicalId = "ButtonHost/Button#0", Type = new TypeRef("UnityEngine.UI.Button") },
                new CreateObject { LogicalId = "MarqueeHost", Name = "MarqueeHost" },
                new AddComponent { LogicalId = "MarqueeHost/Marquee#0", Type = new TypeRef("Marquee") },
                new SetUnityEvent
                {
                    LogicalId = "ButtonHost/Button#0",
                    Path = "m_OnClick",
                    Listeners = new ValueNode.UnityEventListeners(new[]
                    {
                        new UnityEventListener(
                            new ValueNode.ObjectRef("MarqueeHost/Marquee#0"), "SetVisible",
                            ListenerArgMode.Bool, ValueNode.Primitive.Bool(true)),
                    }),
                },
            },
        };

        PlanExecutor.Execute(plan, new IdentityMap(), scene);

        var button = Find("ButtonHost").GetComponent<Button>();
        var call = CallAt(button, "m_OnClick", 0);

        Assert.AreEqual((int)PersistentListenerMode.Bool, call.FindPropertyRelative("m_Mode").intValue);
        Assert.IsTrue(call.FindPropertyRelative("m_Arguments.m_BoolArgument").boolValue);
    }

    [Test]
    public void SetUnityEvent_ObjectListenerWithSceneTargetArg_WritesLiveObjectArgument()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new CreateObject { LogicalId = "ButtonHost", Name = "ButtonHost" },
                new AddComponent { LogicalId = "ButtonHost/Button#0", Type = new TypeRef("UnityEngine.UI.Button") },
                new CreateObject { LogicalId = "MarqueeHost", Name = "MarqueeHost" },
                new AddComponent { LogicalId = "MarqueeHost/Marquee#0", Type = new TypeRef("Marquee") },
                new CreateObject { LogicalId = "Subject", Name = "Subject" },
                new SetUnityEvent
                {
                    LogicalId = "ButtonHost/Button#0",
                    Path = "m_OnClick",
                    Listeners = new ValueNode.UnityEventListeners(new[]
                    {
                        new UnityEventListener(
                            new ValueNode.ObjectRef("MarqueeHost/Marquee#0"), "SetSubject",
                            ListenerArgMode.Object, new ValueNode.ObjectRef("Subject")),
                    }),
                },
            },
        };

        PlanExecutor.Execute(plan, new IdentityMap(), scene);

        var button = Find("ButtonHost").GetComponent<Button>();
        var subject = Find("Subject");
        var call = CallAt(button, "m_OnClick", 0);

        Assert.AreEqual((int)PersistentListenerMode.Object, call.FindPropertyRelative("m_Mode").intValue);
        Assert.AreEqual(subject, call.FindPropertyRelative("m_Arguments.m_ObjectArgument").objectReferenceValue,
            "m_Arguments.m_ObjectArgument did not resolve to the live scene GameObject");
        StringAssert.Contains(nameof(GameObject),
            call.FindPropertyRelative("m_Arguments.m_ObjectArgumentAssemblyTypeName").stringValue,
            "m_Arguments.m_ObjectArgumentAssemblyTypeName must name the resolved object argument's own type");
    }

    [Test]
    public void SetUnityEvent_ObjectListenerWithAssetTargetArg_WritesResolvedAsset()
    {
        if (!AssetDatabase.IsValidFolder(AssetDir))
        {
            AssetDatabase.CreateFolder("Assets", "UnityEventWriteTestsTmp");
        }

        var material = new Material(Shader.Find("Standard"));
        AssetDatabase.CreateAsset(material, MaterialPath);
        Assert.IsTrue(
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(material, out var guid, out long fileId),
            "Failed to resolve the created test material's GUID/fileId");

        var scene = EditorSceneManager.GetActiveScene();
        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new CreateObject { LogicalId = "ButtonHost", Name = "ButtonHost" },
                new AddComponent { LogicalId = "ButtonHost/Button#0", Type = new TypeRef("UnityEngine.UI.Button") },
                new CreateObject { LogicalId = "MarqueeHost", Name = "MarqueeHost" },
                new AddComponent { LogicalId = "MarqueeHost/Marquee#0", Type = new TypeRef("Marquee") },
                new SetUnityEvent
                {
                    LogicalId = "ButtonHost/Button#0",
                    Path = "m_OnClick",
                    Listeners = new ValueNode.UnityEventListeners(new[]
                    {
                        new UnityEventListener(
                            new ValueNode.ObjectRef("MarqueeHost/Marquee#0"), "SetMaterial",
                            ListenerArgMode.Object,
                            new ValueNode.AssetRef(new AssetRef { Guid = guid, FileId = fileId })),
                    }),
                },
            },
        };

        PlanExecutor.Execute(plan, new IdentityMap(), scene);

        var button = Find("ButtonHost").GetComponent<Button>();
        var call = CallAt(button, "m_OnClick", 0);

        Assert.AreEqual((int)PersistentListenerMode.Object, call.FindPropertyRelative("m_Mode").intValue);
        Assert.AreEqual(material, call.FindPropertyRelative("m_Arguments.m_ObjectArgument").objectReferenceValue,
            "m_Arguments.m_ObjectArgument did not resolve to the project material asset");
    }

    [Test]
    public void SetUnityEvent_DynamicListener_WritesTargetAndMethodWithNoStoredArgument()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new CreateObject { LogicalId = "ButtonHost", Name = "ButtonHost" },
                new AddComponent { LogicalId = "ButtonHost/Button#0", Type = new TypeRef("UnityEngine.UI.Button") },
                new CreateObject { LogicalId = "HudHost", Name = "HudHost" },
                new AddComponent { LogicalId = "HudHost/Hud#0", Type = new TypeRef("Hud") },
                new SetUnityEvent
                {
                    LogicalId = "ButtonHost/Button#0",
                    Path = "m_OnClick",
                    Listeners = new ValueNode.UnityEventListeners(new[]
                    {
                        new UnityEventListener(
                            new ValueNode.ObjectRef("HudHost/Hud#0"), "SetValue", ListenerArgMode.Dynamic),
                    }),
                },
            },
        };

        PlanExecutor.Execute(plan, new IdentityMap(), scene);

        var button = Find("ButtonHost").GetComponent<Button>();
        var hud = Find("HudHost").GetComponent<Hud>();
        var call = CallAt(button, "m_OnClick", 0);

        Assert.AreEqual(hud, call.FindPropertyRelative("m_Target").objectReferenceValue);
        Assert.AreEqual("SetValue", call.FindPropertyRelative("m_MethodName").stringValue);
        Assert.AreEqual((int)PersistentListenerMode.EventDefined, call.FindPropertyRelative("m_Mode").intValue);
        Assert.AreEqual(0f, call.FindPropertyRelative("m_Arguments.m_FloatArgument").floatValue,
            "Dynamic mode must leave the argument slot at its default, unpopulated by a static value");
    }

    [Test]
    public void SetUnityEvent_NonDefaultCallState_WritesEditorAndRuntime()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new CreateObject { LogicalId = "ButtonHost", Name = "ButtonHost" },
                new AddComponent { LogicalId = "ButtonHost/Button#0", Type = new TypeRef("UnityEngine.UI.Button") },
                new CreateObject { LogicalId = "Opener", Name = "Opener" },
                new AddComponent { LogicalId = "Opener/DoorOpener#0", Type = new TypeRef("DoorOpener") },
                new SetUnityEvent
                {
                    LogicalId = "ButtonHost/Button#0",
                    Path = "m_OnClick",
                    Listeners = new ValueNode.UnityEventListeners(new[]
                    {
                        new UnityEventListener(
                            new ValueNode.ObjectRef("Opener/DoorOpener#0"), "Open",
                            ListenerArgMode.Void, argValue: null, callState: ListenerCallState.EditorAndRuntime),
                    }),
                },
            },
        };

        PlanExecutor.Execute(plan, new IdentityMap(), scene);

        var button = Find("ButtonHost").GetComponent<Button>();
        var call = CallAt(button, "m_OnClick", 0);

        Assert.AreEqual((int)UnityEventCallState.EditorAndRuntime, call.FindPropertyRelative("m_CallState").intValue);
    }

    [Test]
    public void SetUnityEvent_TwoListeners_PreservesAuthoredOrder()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new CreateObject { LogicalId = "ButtonHost", Name = "ButtonHost" },
                new AddComponent { LogicalId = "ButtonHost/Button#0", Type = new TypeRef("UnityEngine.UI.Button") },
                new CreateObject { LogicalId = "Opener", Name = "Opener" },
                new AddComponent { LogicalId = "Opener/DoorOpener#0", Type = new TypeRef("DoorOpener") },
                new CreateObject { LogicalId = "LampHost", Name = "LampHost" },
                new AddComponent { LogicalId = "LampHost/Lamp#0", Type = new TypeRef("Lamp") },
                new SetUnityEvent
                {
                    LogicalId = "ButtonHost/Button#0",
                    Path = "m_OnClick",
                    Listeners = new ValueNode.UnityEventListeners(new[]
                    {
                        new UnityEventListener(new ValueNode.ObjectRef("Opener/DoorOpener#0"), "Open", ListenerArgMode.Void),
                        new UnityEventListener(
                            new ValueNode.ObjectRef("LampHost/Lamp#0"), "SetLevel",
                            ListenerArgMode.Int, ValueNode.Primitive.Int(7)),
                    }),
                },
            },
        };

        PlanExecutor.Execute(plan, new IdentityMap(), scene);

        var button = Find("ButtonHost").GetComponent<Button>();
        var calls = CallsArray(button, "m_OnClick");
        Assert.AreEqual(2, calls.arraySize);

        var first = calls.GetArrayElementAtIndex(0);
        var second = calls.GetArrayElementAtIndex(1);
        Assert.AreEqual("Open", first.FindPropertyRelative("m_MethodName").stringValue,
            "Index 0 must be the first-authored listener (Open), the array is not in authored order");
        Assert.AreEqual("SetLevel", second.FindPropertyRelative("m_MethodName").stringValue,
            "Index 1 must be the second-authored listener (SetLevel), the array is not in authored order");
    }

    [Test]
    public void SetUnityEvent_EmptyListenerList_ClearsArray()
    {
        var scene = EditorSceneManager.GetActiveScene();

        // A real Button whose m_OnClick already carries ONE persistent call BEFORE Execute, so the
        // second (empty-list) SetUnityEvent's effect is distinguishable from an already-empty array
        // that was simply never touched.
        var buttonGo = new GameObject("ButtonHost");
        var button = buttonGo.AddComponent<Button>();
        var openerGo = new GameObject("Opener");
        var doorOpener = openerGo.AddComponent<DoorOpener>();
        UnityEventTools.AddVoidPersistentListener(button.onClick, doorOpener.Open);
        Assert.AreEqual(1, CallsArray(button, "m_OnClick").arraySize, "Test setup did not seed one persistent call");

        var buttonGoid = GlobalObjectId.GetGlobalObjectIdSlow(button).ToString();
        var map = new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry
                {
                    LogicalId = "ButtonHost/Button#0", GlobalObjectId = buttonGoid, Kind = "Component",
                    ComponentType = "UnityEngine.UI.Button", ParentLogicalId = "ButtonHost",
                },
            },
        };

        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new SetUnityEvent
                {
                    LogicalId = "ButtonHost/Button#0",
                    Path = "m_OnClick",
                    Listeners = new ValueNode.UnityEventListeners(Array.Empty<UnityEventListener>()),
                },
            },
        };

        PlanExecutor.Execute(plan, map, scene);

        Assert.AreEqual(0, CallsArray(button, "m_OnClick").arraySize,
            "A SetUnityEvent carrying an empty listener list must clear the persistent-call array");
    }

    [Test]
    public void SetUnityEvent_DanglingTarget_LeavesTargetMissingWithoutThrowing()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new CreateObject { LogicalId = "ButtonHost", Name = "ButtonHost" },
                new AddComponent { LogicalId = "ButtonHost/Button#0", Type = new TypeRef("UnityEngine.UI.Button") },
                new SetUnityEvent
                {
                    LogicalId = "ButtonHost/Button#0",
                    Path = "m_OnClick",
                    Listeners = new ValueNode.UnityEventListeners(new[]
                    {
                        new UnityEventListener(target: null, "Open", ListenerArgMode.Void),
                    }),
                },
            },
        };

        Assert.DoesNotThrow(() => PlanExecutor.Execute(plan, new IdentityMap(), scene),
            "A dangling (null Target) listener must not throw while writing the persistent call");

        var button = Find("ButtonHost").GetComponent<Button>();
        var call = CallAt(button, "m_OnClick", 0);
        Assert.IsNull(call.FindPropertyRelative("m_Target").objectReferenceValue,
            "A null listener Target must leave m_Target Missing (null), not throw and not resolve to a live object");
        Assert.AreEqual("Open", call.FindPropertyRelative("m_MethodName").stringValue);
    }
}
