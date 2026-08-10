using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Grammar;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // `.Ref<T>()` (a component-only reference, never a GameObject LogicalId) and the void
    // `.OnClick(target, x => x.Method())` listener-wiring shape. A listener target always resolves
    // to the WIRED COMPONENT's own LogicalId, never the GameObject it lives on, and a `.Ref<T>()`
    // naming a type the node carries no matching component of stays unresolved rather than
    // fabricating a LogicalId.
    public class UnityEventAuthoringParseTests
    {
        private static IReadOnlyList<ShapeViolation> Recognize(string source)
        {
            var tree = CSharpSyntaxTree.ParseText(source);
            var root = tree.GetRoot();
            var buildMethod = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.Text == "Build");
            var sceneParamName = buildMethod.ParameterList.Parameters[0].Identifier.Text;
            return FlatShapeRecognizer.Analyze(buildMethod.Body!, sceneParamName);
        }

        private const string OnClickVoidSource = @"
public class OnClickScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var opener = scene.Add(""Door"").Component<DoorOpener>(_ => { }).Ref<DoorOpener>();
        scene.Add(""QuitButton"").Component<UnityEngine.UI.Button>(b => b.OnClick(opener, o => o.Open()));
    }
}
";

        [Fact]
        public void Parse_OnClickVoidForm_TargetsWiredComponentsLogicalId_NotTheNodes()
        {
            var result = BuilderParser.Parse(OnClickVoidSource);

            var door = result.Model.Roots.Single(r => r.Name == "Door");
            Assert.Equal("Door/0", door.LogicalId);

            var button = result.Model.Roots.Single(r => r.Name == "QuitButton");
            var buttonComponent = Assert.Single(button.Components);

            var expected = new ValueNode.UnityEventListeners(new[]
            {
                new UnityEventListener(
                    new ValueNode.ObjectRef("Door/0/DoorOpener#0"),
                    "Open",
                    ListenerArgMode.Void,
                    null,
                    ListenerCallState.RuntimeOnly),
            });
            Assert.Equal(expected, buttonComponent.Fields["m_OnClick"]);
        }

        [Fact]
        public void Parse_RefWithExplicitOrdinal_SelectsMatchingComponentOfSameType()
        {
            const string source = @"
public class OrdinalScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var second = scene.Add(""Door"").Component<DoorOpener>(_ => { }).Component<DoorOpener>(_ => { }).Ref<DoorOpener>(1);
        scene.Add(""QuitButton"").Component<UnityEngine.UI.Button>(b => b.OnClick(second, o => o.Open()));
    }
}
";
            var result = BuilderParser.Parse(source);

            var button = result.Model.Roots.Single(r => r.Name == "QuitButton");
            var listeners = (ValueNode.UnityEventListeners)button.Components.Single().Fields["m_OnClick"];
            var listener = Assert.Single(listeners.Listeners);
            Assert.Equal(new ValueNode.ObjectRef("Door/0/DoorOpener#1"), listener.Target);
        }

        [Fact]
        public void Parse_RefNamingComponentTypeNodeLacks_YieldsUnsupportedTarget()
        {
            const string source = @"
public class MissingComponentScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var opener = scene.Add(""Door"").Ref<DoorOpener>();
        scene.Add(""QuitButton"").Component<UnityEngine.UI.Button>(b => b.OnClick(opener, o => o.Open()));
    }
}
";
            var result = BuilderParser.Parse(source);

            var button = result.Model.Roots.Single(r => r.Name == "QuitButton");
            var listeners = (ValueNode.UnityEventListeners)button.Components.Single().Fields["m_OnClick"];
            var listener = Assert.Single(listeners.Listeners);
            Assert.Equal(new ValueNode.Unsupported("opener"), listener.Target);
        }

        [Fact]
        public void Recognizer_AcceptsValidRefOnClickShape_ZeroViolations()
        {
            var violations = Recognize(OnClickVoidSource);

            Assert.Empty(violations);
        }
    }
}
