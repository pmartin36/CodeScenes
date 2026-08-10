using System.Linq;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // The listener-wiring authoring surface beyond the void `.OnClick(target, x => x.Method())`
    // form: a static argument selected by the wired method's own arity, a `callState:` named
    // argument, the `dynamic:` method-group form, the `.OnEvent(...)` generic event-field selector,
    // several listeners on one event in authored order, an asset target, and a GameObject target
    // (as opposed to a component target).
    public class UnityEventAuthoringFormsTests
    {
        private static UnityEventListener SingleListener(SceneModel model, string rootName, string fieldKey)
        {
            var root = model.Roots.Single(r => r.Name == rootName);
            var component = root.Components.Single();
            var listeners = (ValueNode.UnityEventListeners)component.Fields[fieldKey];
            return Assert.Single(listeners.Listeners);
        }

        [Fact]
        public void Parse_OnClick_ArgumentPresenceSelectsVoidOrIntMode_AgainstAnOverloadedTarget()
        {
            const string source = @"
public class RepeaterScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var r = scene.Add(""Repeater"").Component<Repeater>(_ => { }).Ref<Repeater>();
        scene.Add(""VoidButton"").Component<UnityEngine.UI.Button>(b => b.OnClick(r, x => x.Apply()));
        scene.Add(""IntButton"").Component<UnityEngine.UI.Button>(b => b.OnClick(r, x => x.Apply(2)));
    }
}
";
            var result = BuilderParser.Parse(source);

            var voidListener = SingleListener(result.Model, "VoidButton", "m_OnClick");
            Assert.Equal("Apply", voidListener.MethodName);
            Assert.Equal(ListenerArgMode.Void, voidListener.ArgMode);
            Assert.Null(voidListener.ArgValue);

            var intListener = SingleListener(result.Model, "IntButton", "m_OnClick");
            Assert.Equal("Apply", intListener.MethodName);
            Assert.Equal(ListenerArgMode.Int, intListener.ArgMode);
            Assert.Equal(ValueNode.Primitive.Int(2), intListener.ArgValue);
        }

        [Fact]
        public void Parse_OnClick_CallStateNamedArgument_LowersByMemberNameRegardlessOfQualifier()
        {
            const string source = @"
public class CallStateScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var opener = scene.Add(""Door"").Component<DoorOpener>(_ => { }).Ref<DoorOpener>();
        scene.Add(""A"").Component<UnityEngine.UI.Button>(b => b.OnClick(opener, o => o.Open(), callState: UnityEngine.Events.UnityEventCallState.EditorAndRuntime));
        scene.Add(""B"").Component<UnityEngine.UI.Button>(b => b.OnClick(opener, o => o.Open(), callState: UnityEventCallState.Off));
    }
}
";
            var result = BuilderParser.Parse(source);

            var listenerA = SingleListener(result.Model, "A", "m_OnClick");
            Assert.Equal(ListenerCallState.EditorAndRuntime, listenerA.CallState);

            var listenerB = SingleListener(result.Model, "B", "m_OnClick");
            Assert.Equal(ListenerCallState.Off, listenerB.CallState);
        }

        [Fact]
        public void Parse_OnEvent_DynamicForm_CarriesTheMethodGroupWithNoStaticArgument()
        {
            const string source = @"
public class DynamicScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var hud = scene.Add(""Hud"").Component<Hud>(_ => { }).Ref<Hud>();
        scene.Add(""Btn"").Component<UnityEngine.UI.Button>(b => b.OnEvent(x => x.onLevelChanged, hud, h => h.SetValue, dynamic: true));
    }
}
";
            var result = BuilderParser.Parse(source);

            var listener = SingleListener(result.Model, "Btn", "member:onLevelChanged");
            Assert.Equal("SetValue", listener.MethodName);
            Assert.Equal(ListenerArgMode.Dynamic, listener.ArgMode);
            Assert.Null(listener.ArgValue);
        }

        [Fact]
        public void Parse_OnEvent_VoidForm_ComposesTheSameMemberFieldKeyAsSet()
        {
            const string source = @"
public class OnEventVoidScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var hud = scene.Add(""Hud"").Component<Hud>(_ => { }).Ref<Hud>();
        scene.Add(""Btn"").Component<UnityEngine.UI.Button>(b => b.OnEvent(x => x.onPinged, hud, h => h.Refresh()));
    }
}
";
            var result = BuilderParser.Parse(source);

            var button = result.Model.Roots.Single(r => r.Name == "Btn");
            var component = button.Components.Single();
            Assert.True(component.Fields.ContainsKey("member:onPinged"));

            var listener = SingleListener(result.Model, "Btn", "member:onPinged");
            Assert.Equal("Refresh", listener.MethodName);
            Assert.Equal(ListenerArgMode.Void, listener.ArgMode);
            Assert.Equal(ListenerCallState.RuntimeOnly, listener.CallState);
        }

        [Fact]
        public void Parse_TwoOnClickStatementsInOneClosure_PreserveAuthoredOrderUnderOneFieldKey()
        {
            const string source = @"
public class OrderScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var first = scene.Add(""A"").Component<Opener>(_ => { }).Ref<Opener>();
        var second = scene.Add(""B"").Component<Opener>(_ => { }).Ref<Opener>();
        scene.Add(""Btn"").Component<UnityEngine.UI.Button>(b =>
        {
            b.OnClick(first, x => x.First());
            b.OnClick(second, x => x.Second());
        });
    }
}
";
            var result = BuilderParser.Parse(source);

            var button = result.Model.Roots.Single(r => r.Name == "Btn");
            var listeners = (ValueNode.UnityEventListeners)button.Components.Single().Fields["m_OnClick"];

            Assert.Equal(2, listeners.Listeners.Count);
            Assert.Equal("First", listeners.Listeners[0].MethodName);
            Assert.Equal("Second", listeners.Listeners[1].MethodName);
        }

        [Fact]
        public void Parse_OnClickAssetTarget_LowersToAssetRef_NotObjectRefOrUnsupported()
        {
            var catalog = new AssetCatalog
            {
                Entries = new[]
                {
                    new AssetCatalogEntry
                    {
                        Group = "Prefabs",
                        FolderSegments = new[] { "Speakers" },
                        MemberName = "Speaker",
                        Guid = "dddddddddddddddddddddddddddddddd",
                        Path = "Assets/Prefabs/Speakers/Speaker.prefab",
                    },
                },
            };

            const string source = @"
public class AssetTargetScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Btn"").Component<UnityEngine.UI.Button>(b => b.OnClick<AudioSrc>(Assets.Prefabs.Speakers.Speaker, a => a.Play()));
    }
}
";
            var result = BuilderParser.Parse(source, null, null, catalog);

            var listener = SingleListener(result.Model, "Btn", "m_OnClick");
            var assetTarget = Assert.IsType<ValueNode.AssetRef>(listener.Target);
            Assert.NotNull(assetTarget.Ref);
            Assert.Equal("dddddddddddddddddddddddddddddddd", assetTarget.Ref!.Guid);
        }

        [Fact]
        public void Parse_OnClickGameObjectTarget_LowersToTheNodesOwnLogicalId_NeverAComponentId()
        {
            const string source = @"
public class GameObjectTargetScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var door = scene.Add(""Door"");
        scene.Add(""Btn"").Component<UnityEngine.UI.Button>(b => b.OnClick(door, g => g.SetActive(false)));
    }
}
";
            var result = BuilderParser.Parse(source);

            var door = result.Model.Roots.Single(r => r.Name == "Door");
            var listener = SingleListener(result.Model, "Btn", "m_OnClick");
            var target = Assert.IsType<ValueNode.ObjectRef>(listener.Target);
            Assert.Equal(door.LogicalId, target.TargetLogicalId);
            Assert.DoesNotContain("#", target.TargetLogicalId);

            Assert.Equal(ListenerArgMode.Bool, listener.ArgMode);
            Assert.Equal(ValueNode.Primitive.Bool(false), listener.ArgValue);
        }

        [Fact]
        public void Parse_OnClickObjectModeArgument_TypedPassThroughUnwrapsToTheSceneReference()
        {
            const string source = @"
public class ObjectArgScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var door = scene.Add(""Door"");
        var marquee = scene.Add(""Marquee"").Component<Marquee>(_ => { }).Ref<Marquee>();
        scene.Add(""Btn"").Component<UnityEngine.UI.Button>(b => b.OnClick(marquee, m => m.SetSubject(door.As<UnityEngine.GameObject>())));
    }
}
";
            var result = BuilderParser.Parse(source);

            var door = result.Model.Roots.Single(r => r.Name == "Door");
            var listener = SingleListener(result.Model, "Btn", "m_OnClick");
            Assert.Equal(ListenerArgMode.Object, listener.ArgMode);
            var argTarget = Assert.IsType<ValueNode.ObjectRef>(listener.ArgValue);
            Assert.Equal(door.LogicalId, argTarget.TargetLogicalId);
        }
    }
}
