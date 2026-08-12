using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using SceneBuilder.Editor;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Plan;
using SceneBuilder.Core.Reconcile;

// The adapter READ path for a UnityEvent field (specs/09-m8-unityevents.md): a live persistent-call
// list must read back as ValueNode.UnityEventListeners on ComponentData.Fields[eventPath], with the
// component-target (not owning-GameObject) LogicalId, order preserved. Each fixture is wired through
// the shipped SetUnityEvent WRITE path (PlanExecutor), then read back with
// SceneSnapshotReader.Read/ObjectReferenceResolver, mirroring UnityEventWriteTests's hand-built-plan
// pattern for the opposite direction.
public class UnityEventReadTests
{
    private const string AssetDir = "Assets/UnityEventReadTestsTmp";
    private const string MaterialPath = AssetDir + "/ReadTestMaterial.mat";
    private const string ScenePath = AssetDir + "/__UnityEventReadTestsTemp.unity";

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

    // GlobalObjectId.GetGlobalObjectIdSlow returns the same placeholder id for every object in an
    // unsaved scene, so every listener target would collapse onto one identity-map entry; every
    // fixture must persist the scene before taking a GlobalObjectId, matching
    // ComponentTargetResolutionTests / SnapshotReaderPrefabTests.
    private static void SaveActiveScene()
    {
        if (!AssetDatabase.IsValidFolder(AssetDir))
        {
            AssetDatabase.CreateFolder("Assets", "UnityEventReadTestsTmp");
        }

        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);
    }

    // Builds a Button + a target component wired with `listeners`, plus the read-side IdentityMap
    // (the button component and every named target, keyed the same as the write plan's LogicalIds)
    // needed to resolve listener references on read. A target with a null typeName is created as a
    // plain GameObject (no component) -- used for an object-mode argument pointing at a scene
    // GameObject rather than a component. Every referenced object is created in THIS SAME plan
    // execution, so an object-mode argument targeting it resolves through the write's own
    // just-created map rather than needing to pre-exist.
    private static IdentityMap WireButton(
        Scene scene, UnityEventListener[] listeners, params (string logicalId, string parentName, string typeName)[] targets)
    {
        var ops = new System.Collections.Generic.List<PlanOp>
        {
            new CreateObject { LogicalId = "ButtonHost", Name = "ButtonHost" },
            new AddComponent { LogicalId = "ButtonHost/Button#0", Type = new TypeRef("UnityEngine.UI.Button") },
        };
        foreach (var (logicalId, parentName, typeName) in targets)
        {
            ops.Add(new CreateObject { LogicalId = parentName, Name = parentName });
            if (typeName != null)
            {
                ops.Add(new AddComponent { LogicalId = logicalId, Type = new TypeRef(typeName) });
            }
        }

        ops.Add(new SetUnityEvent
        {
            LogicalId = "ButtonHost/Button#0",
            Path = "m_OnClick",
            Listeners = new ValueNode.UnityEventListeners(listeners),
        });

        PlanExecutor.Execute(new Plan { Ops = ops.ToArray() }, new IdentityMap(), scene);
        SaveActiveScene();

        var entries = new System.Collections.Generic.List<IdentityMapEntry>
        {
            ComponentEntry("ButtonHost/Button#0", Find("ButtonHost").GetComponent<Button>(), "ButtonHost"),
        };
        foreach (var (logicalId, parentName, typeName) in targets)
        {
            if (typeName != null)
            {
                var component = (Component)Find(parentName).GetComponent(typeName);
                entries.Add(ComponentEntry(logicalId, component, parentName));
            }
            else
            {
                var go = Find(parentName);
                entries.Add(new IdentityMapEntry
                {
                    LogicalId = logicalId,
                    GlobalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(go).ToString(),
                    Kind = "GameObject",
                });
            }
        }

        return new IdentityMap { Entries = entries.ToArray() };
    }

    private static IdentityMapEntry ComponentEntry(string logicalId, Component component, string parentLogicalId) =>
        new()
        {
            LogicalId = logicalId,
            GlobalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(component).ToString(),
            Kind = "Component",
            ComponentType = component.GetType().FullName,
            ParentLogicalId = parentLogicalId,
        };

    private static ValueNode ReadOnClickField(Scene scene, IdentityMap map)
    {
        var snapshot = SceneSnapshotReader.Read(
            scene, ObjectReferenceResolver.BuildSceneRefResolver(map), ObjectReferenceResolver.BuildListenerResolver(map));
        var buttonNode = snapshot.Roots.First(r => r.Name == "ButtonHost");
        var buttonData = buttonNode.Components.First(c => c.Type.FullName == "UnityEngine.UI.Button");
        return buttonData.Fields["m_OnClick"];
    }

    private static UnityEventListener OneListener(Scene scene, IdentityMap map)
    {
        var field = ReadOnClickField(scene, map);
        Assert.IsInstanceOf<ValueNode.UnityEventListeners>(field,
            $"m_OnClick must read as ValueNode.UnityEventListeners; got {field.GetType().Name}");
        var listeners = (ValueNode.UnityEventListeners)field;
        Assert.AreEqual(1, listeners.Listeners.Count, "Expected exactly one persistent call.");
        return listeners.Listeners[0];
    }

    [Test]
    public void ReadOnClick_VoidListener_ReadsTargetMethodModeAndDefaultCallState()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var map = WireButton(
            scene,
            new[] { new UnityEventListener(new ValueNode.ObjectRef("Opener/DoorOpener#0"), "Open", ListenerArgMode.Void) },
            ("Opener/DoorOpener#0", "Opener", "DoorOpener"));

        var listener = OneListener(scene, map);

        Assert.AreEqual(new ValueNode.ObjectRef("Opener/DoorOpener#0"), listener.Target,
            "Target must resolve to the DoorOpener component's own LogicalId.");
        Assert.AreEqual("Open", listener.MethodName);
        Assert.AreEqual(ListenerArgMode.Void, listener.ArgMode);
        Assert.AreEqual(ListenerCallState.RuntimeOnly, listener.CallState, "Default CallState must read as RuntimeOnly.");
    }

    [Test]
    public void ReadOnClick_IntListener_ReadsIntArgument()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var map = WireButton(
            scene,
            new[]
            {
                new UnityEventListener(
                    new ValueNode.ObjectRef("LampHost/Lamp#0"), "SetLevel", ListenerArgMode.Int, ValueNode.Primitive.Int(7)),
            },
            ("LampHost/Lamp#0", "LampHost", "Lamp"));

        var listener = OneListener(scene, map);

        Assert.AreEqual(ListenerArgMode.Int, listener.ArgMode);
        Assert.AreEqual(ValueNode.Primitive.Int(7), listener.ArgValue);
    }

    [Test]
    public void ReadOnClick_FloatListener_ReadsFloatArgument()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var map = WireButton(
            scene,
            new[]
            {
                new UnityEventListener(
                    new ValueNode.ObjectRef("MixerHost/Mixer#0"), "SetVol", ListenerArgMode.Float, ValueNode.Primitive.Float(0.5f)),
            },
            ("MixerHost/Mixer#0", "MixerHost", "Mixer"));

        var listener = OneListener(scene, map);

        Assert.AreEqual(ListenerArgMode.Float, listener.ArgMode);
        Assert.AreEqual(ValueNode.Primitive.Float(0.5f), listener.ArgValue);
    }

    [Test]
    public void ReadOnClick_StringListener_ReadsStringArgument()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var map = WireButton(
            scene,
            new[]
            {
                new UnityEventListener(
                    new ValueNode.ObjectRef("MarqueeHost/Marquee#0"), "SetText", ListenerArgMode.String, ValueNode.Primitive.String("hi")),
            },
            ("MarqueeHost/Marquee#0", "MarqueeHost", "Marquee"));

        var listener = OneListener(scene, map);

        Assert.AreEqual(ListenerArgMode.String, listener.ArgMode);
        Assert.AreEqual(ValueNode.Primitive.String("hi"), listener.ArgValue);
    }

    [Test]
    public void ReadOnClick_BoolListener_ReadsBoolArgument()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var map = WireButton(
            scene,
            new[]
            {
                new UnityEventListener(
                    new ValueNode.ObjectRef("MarqueeHost/Marquee#0"), "SetVisible", ListenerArgMode.Bool, ValueNode.Primitive.Bool(true)),
            },
            ("MarqueeHost/Marquee#0", "MarqueeHost", "Marquee"));

        var listener = OneListener(scene, map);

        Assert.AreEqual(ListenerArgMode.Bool, listener.ArgMode);
        Assert.AreEqual(ValueNode.Primitive.Bool(true), listener.ArgValue);
    }

    [Test]
    public void ReadOnClick_ObjectListenerWithSceneTargetArg_ReadsResolvedSceneObjectArgument()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var map = WireButton(
            scene,
            new[]
            {
                new UnityEventListener(
                    new ValueNode.ObjectRef("MarqueeHost/Marquee#0"), "SetSubject", ListenerArgMode.Object,
                    new ValueNode.ObjectRef("Subject")),
            },
            ("MarqueeHost/Marquee#0", "MarqueeHost", "Marquee"),
            ("Subject", "Subject", null));

        var listener = OneListener(scene, map);

        Assert.AreEqual(ListenerArgMode.Object, listener.ArgMode);
        Assert.AreEqual(new ValueNode.ObjectRef("Subject"), listener.ArgValue);
    }

    [Test]
    public void ReadOnClick_ObjectListenerWithAssetTargetArg_ReadsResolvedAssetArgument()
    {
        if (!AssetDatabase.IsValidFolder(AssetDir))
        {
            AssetDatabase.CreateFolder("Assets", "UnityEventReadTestsTmp");
        }

        var material = new Material(Shader.Find("Standard"));
        AssetDatabase.CreateAsset(material, MaterialPath);
        Assert.IsTrue(
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(material, out var guid, out long fileId),
            "Failed to resolve the created test material's GUID/fileId");

        var scene = EditorSceneManager.GetActiveScene();
        var map = WireButton(
            scene,
            new[]
            {
                new UnityEventListener(
                    new ValueNode.ObjectRef("MarqueeHost/Marquee#0"), "SetMaterial", ListenerArgMode.Object,
                    new ValueNode.AssetRef(new AssetRef { Guid = guid, FileId = fileId })),
            },
            ("MarqueeHost/Marquee#0", "MarqueeHost", "Marquee"));

        var listener = OneListener(scene, map);

        Assert.AreEqual(ListenerArgMode.Object, listener.ArgMode);
        Assert.AreEqual(new ValueNode.AssetRef(new AssetRef { Guid = guid, FileId = fileId }), listener.ArgValue);
    }

    [Test]
    public void ReadOnClick_DynamicListener_ReadsTargetAndMethodWithNoStoredArgument()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var map = WireButton(
            scene,
            new[] { new UnityEventListener(new ValueNode.ObjectRef("HudHost/Hud#0"), "SetValue", ListenerArgMode.Dynamic) },
            ("HudHost/Hud#0", "HudHost", "Hud"));

        var listener = OneListener(scene, map);

        Assert.AreEqual(ListenerArgMode.Dynamic, listener.ArgMode);
        Assert.IsNull(listener.ArgValue, "Dynamic mode must carry no static argument.");
    }

    [Test]
    public void ReadOnClick_NonDefaultCallState_ReadsEditorAndRuntime()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var map = WireButton(
            scene,
            new[]
            {
                new UnityEventListener(
                    new ValueNode.ObjectRef("Opener/DoorOpener#0"), "Open", ListenerArgMode.Void,
                    argValue: null, callState: ListenerCallState.EditorAndRuntime),
            },
            ("Opener/DoorOpener#0", "Opener", "DoorOpener"));

        var listener = OneListener(scene, map);

        Assert.AreEqual(ListenerCallState.EditorAndRuntime, listener.CallState);
    }

    [Test]
    public void ReadOnClick_TwoListeners_PreservesAuthoredOrder()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var map = WireButton(
            scene,
            new[]
            {
                new UnityEventListener(new ValueNode.ObjectRef("Opener/DoorOpener#0"), "Open", ListenerArgMode.Void),
                new UnityEventListener(
                    new ValueNode.ObjectRef("LampHost/Lamp#0"), "SetLevel", ListenerArgMode.Int, ValueNode.Primitive.Int(7)),
            },
            ("Opener/DoorOpener#0", "Opener", "DoorOpener"),
            ("LampHost/Lamp#0", "LampHost", "Lamp"));

        var field = ReadOnClickField(scene, map);
        Assert.IsInstanceOf<ValueNode.UnityEventListeners>(field);
        var listeners = ((ValueNode.UnityEventListeners)field).Listeners;

        Assert.AreEqual(2, listeners.Count);
        Assert.AreEqual("Open", listeners[0].MethodName, "Index 0 must be the first-authored listener.");
        Assert.AreEqual("SetLevel", listeners[1].MethodName, "Index 1 must be the second-authored listener.");
    }

    // A listener target resolves to the TARGET COMPONENT's own LogicalId, never its owning
    // GameObject's -- Lamp lives on a DIFFERENT GameObject (LampHost) than the Button, so the two
    // LogicalIds are distinguishable strings.
    [Test]
    public void ReadOnClick_ComponentTarget_ResolvesToComponentLogicalIdNotOwningGameObjectLogicalId()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var map = WireButton(
            scene,
            new[]
            {
                new UnityEventListener(
                    new ValueNode.ObjectRef("LampHost/Lamp#0"), "SetLevel", ListenerArgMode.Int, ValueNode.Primitive.Int(3)),
            },
            ("LampHost/Lamp#0", "LampHost", "Lamp"));

        var lampGoid = GlobalObjectId.GetGlobalObjectIdSlow(Find("LampHost")).ToString();
        map = map with
        {
            Entries = map.Entries.Append(new IdentityMapEntry
            {
                LogicalId = "LampHost", GlobalObjectId = lampGoid, Kind = "GameObject",
            }).ToArray(),
        };

        var listener = OneListener(scene, map);

        Assert.AreEqual(new ValueNode.ObjectRef("LampHost/Lamp#0"), listener.Target,
            "Target must be the Lamp COMPONENT's own LogicalId.");
        Assert.AreNotEqual(new ValueNode.ObjectRef("LampHost"), listener.Target,
            "Target must NOT resolve to the owning GameObject's LogicalId.");
    }

    [Test]
    public void ReadOnClick_EmptyListenerList_ReadsAsEmptyUnityEventListeners()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var map = WireButton(scene, Array.Empty<UnityEventListener>());

        var field = ReadOnClickField(scene, map);

        Assert.IsInstanceOf<ValueNode.UnityEventListeners>(field,
            $"An empty m_OnClick must still read as ValueNode.UnityEventListeners([]); got {field.GetType().Name}");
        Assert.AreEqual(0, ((ValueNode.UnityEventListeners)field).Listeners.Count);
    }

    // SceneRefResolver.Generation, keyed only on the mapped-NODE projection, must also move when a
    // Component identity-map entry is added or retargeted, or ChangeScopedSnapshot keeps serving a
    // node built before the component became resolvable. Exercised directly against
    // SceneRefResolver.ForMap (no live scene needed).
    [Test]
    public void SceneRefResolver_ForMap_GenerationMovesWhenAComponentEntryIsAddedOrRetargeted()
    {
        var withoutComponent = new IdentityMap { Entries = Array.Empty<IdentityMapEntry>() };
        var withComponentA = new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry
                {
                    LogicalId = "LampHost/Lamp#0", GlobalObjectId = "goid-a", Kind = "Component",
                    ComponentType = "Lamp", ParentLogicalId = "LampHost",
                },
            },
        };
        var retargeted = new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry
                {
                    LogicalId = "LampHost/Lamp#0", GlobalObjectId = "goid-b", Kind = "Component",
                    ComponentType = "Lamp", ParentLogicalId = "LampHost",
                },
            },
        };

        var genWithout = SceneRefResolver.ForMap(withoutComponent).Generation;
        var genWithComponentA = SceneRefResolver.ForMap(withComponentA).Generation;
        var genRetargeted = SceneRefResolver.ForMap(retargeted).Generation;

        Assert.AreNotEqual(genWithout, genWithComponentA,
            "Adding a Component identity-map entry must move SceneRefResolver.Generation.");
        Assert.AreNotEqual(genWithComponentA, genRetargeted,
            "Retargeting a mapped Component's GlobalObjectId must move SceneRefResolver.Generation.");
    }

    [Test]
    public void ConflictSurfacing_Severity_UnsyncableListener_ReturnsWarning()
    {
        var conflict = Conflict.UnsyncableListener(
            "ButtonHost/Button#0", "UnityEngine.UI.Button", "m_OnClick", 0, ListenerReportReason.UnresolvedTarget);

        Assert.AreEqual("WARNING", ConflictSurfacing.Severity(conflict),
            "An UnsyncableListener report must surface at the same fail-loud severity as an unrepresentable value.");
    }
}
