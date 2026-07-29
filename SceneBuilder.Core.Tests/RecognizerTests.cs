using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SceneBuilder.Grammar;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Tests #1-#4 for b1-t1: FlatShapeRecognizer.Analyze is the single source of truth for
    // flat-builder-shape recognition, mirroring BuilderParser's body-walk. See
    // .agent_handoffs/codescenes-analyzers/b1-t1/research.md for the throw-site mapping.
    public class RecognizerTests
    {
        private static (BlockSyntax Body, string SceneParamName) ParseBuildBody(string source)
        {
            var tree = CSharpSyntaxTree.ParseText(source);
            var root = tree.GetRoot();
            var buildMethod = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.Text == "Build");
            var paramName = buildMethod.ParameterList.Parameters[0].Identifier.Text;
            return (buildMethod.Body!, paramName);
        }

        [Fact]
        public void Recognizer_ValidFlatBuilder_ReturnsNoViolations()
        {
            const string source = @"
public class ValidScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""Root"").Tag(""Player"").Layer(8);
        root.Component<Rigidbody>(c => c.Set(""mass"", 1f));
    }
}
";
            var (body, sceneParamName) = ParseBuildBody(source);

            var violations = FlatShapeRecognizer.Analyze(body, sceneParamName);

            Assert.Empty(violations);
        }

        [Fact]
        public void Recognizer_LoopInBody_ReturnsLocatedViolation()
        {
            const string source = @"
public class LoopScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        for (int i = 0; i < 3; i++)
        {
            scene.Add(""Child"");
        }
    }
}
";
            var (body, sceneParamName) = ParseBuildBody(source);
            var forStatement = body.Statements.Single();
            var expectedStart = forStatement.SpanStart;

            var violations = FlatShapeRecognizer.Analyze(body, sceneParamName);

            var violation = Assert.Single(violations);
            Assert.Equal("SB1001", violation.Code);
            Assert.Equal(expectedStart, violation.Span.Start);
        }

        [Fact]
        public void Recognizer_UnknownBuilderCall_ReturnsViolation()
        {
            const string source = @"
public class UnknownCallScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""A"").Wiggle();
    }
}
";
            var (body, sceneParamName) = ParseBuildBody(source);

            var violations = FlatShapeRecognizer.Analyze(body, sceneParamName);

            var violation = Assert.Single(violations);
            Assert.Equal("SB1002", violation.Code);
        }

        [Fact]
        public void Recognizer_NonSetInComponentClosure_ReturnsViolation()
        {
            const string source = @"
public class NonSetClosureScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""Root"");
        root.Component<Rigidbody>(c => c.Nope());
    }
}
";
            var (body, sceneParamName) = ParseBuildBody(source);

            var violations = FlatShapeRecognizer.Analyze(body, sceneParamName);

            var violation = Assert.Single(violations);
            Assert.Equal("SB1003", violation.Code);
        }

        // ---- RectTransform (b1-t2): recognizer-only accept/reject cases --------------------------

        [Fact]
        public void Recognizer_RectTransformAllFiveNamedArgs_ReturnsNoViolations()
        {
            const string source = @"
public class RectTransformAllArgsScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Panel"").RectTransform(anchoredPos: (-10, -10), sizeDelta: (200, 120), anchorMin: (1, 1), anchorMax: (1, 1), pivot: (1, 1));
    }
}
";
            var (body, sceneParamName) = ParseBuildBody(source);

            var violations = FlatShapeRecognizer.Analyze(body, sceneParamName);

            Assert.Empty(violations);
        }

        [Fact]
        public void Recognizer_BareRectTransform_ReturnsNoViolations()
        {
            const string source = @"
public class RectTransformBareScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Panel"").RectTransform();
    }
}
";
            var (body, sceneParamName) = ParseBuildBody(source);

            var violations = FlatShapeRecognizer.Analyze(body, sceneParamName);

            Assert.Empty(violations);
        }

        [Fact]
        public void Recognizer_RectTransformUnnamedArg_ReturnsSingleLocatedViolation()
        {
            const string source = @"
public class RectTransformUnnamedScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Panel"").RectTransform((1,2));
    }
}
";
            var (body, sceneParamName) = ParseBuildBody(source);
            var argument = body.DescendantNodes().OfType<ArgumentSyntax>()
                .Single(a => a.Expression is TupleExpressionSyntax);
            var expectedStart = argument.SpanStart;

            var violations = FlatShapeRecognizer.Analyze(body, sceneParamName);

            var violation = Assert.Single(violations);
            Assert.Equal("SB1001", violation.Code);
            Assert.Equal(expectedStart, violation.Span.Start);
        }

        [Fact]
        public void Recognizer_RectTransformUnknownKeyword_ReturnsSingleLocatedViolation()
        {
            const string source = @"
public class RectTransformUnknownArgScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Panel"").RectTransform(bogus: (1,2));
    }
}
";
            var (body, sceneParamName) = ParseBuildBody(source);

            var violations = FlatShapeRecognizer.Analyze(body, sceneParamName);

            var violation = Assert.Single(violations);
            Assert.Equal("SB1001", violation.Code);
        }

        [Fact]
        public void Recognizer_RectTransformWrongTupleArity_ReturnsSingleLocatedViolation()
        {
            const string source = @"
public class RectTransformWrongArityScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Panel"").RectTransform(pivot: (1,2,3));
    }
}
";
            var (body, sceneParamName) = ParseBuildBody(source);
            var tuple = body.DescendantNodes().OfType<TupleExpressionSyntax>().Single();
            var expectedStart = tuple.SpanStart;

            var violations = FlatShapeRecognizer.Analyze(body, sceneParamName);

            var violation = Assert.Single(violations);
            Assert.Equal("SB1001", violation.Code);
            Assert.Equal(expectedStart, violation.Span.Start);
        }

        [Fact]
        public void Recognizer_RectTransformNonLiteralElement_ReturnsTwoViolations()
        {
            const string source = @"
public class RectTransformNonLiteralScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Panel"").RectTransform(pivot: (a, b));
    }
}
";
            var (body, sceneParamName) = ParseBuildBody(source);

            var violations = FlatShapeRecognizer.Analyze(body, sceneParamName);

            Assert.Equal(2, violations.Count);
            Assert.All(violations, v => Assert.Equal("SB1001", v.Code));
        }

        [Fact]
        public void Recognizer_NeverThrows_EvenOnUnrecognizedShapes()
        {
            const string source = @"
public class MixedScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        if (true) { scene.Add(""A""); }
        scene.Add(""B"").Wiggle();
    }
}
";
            var (body, sceneParamName) = ParseBuildBody(source);

            var exception = Record.Exception(() => FlatShapeRecognizer.Analyze(body, sceneParamName));

            Assert.Null(exception);
        }
    }
}
