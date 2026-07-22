using System.Linq;
using CodeScenes.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace SceneBuilder.Analyzers.Tests
{
    // Tests #9-#12 for b2-t3: BuilderAnalyzer must nudge string-escape-hatch call shapes
    // (SB1101-SB1106) purely syntactically (no semantic model) — see
    // .agent_handoffs/codescenes-analyzers/b2-t3/research.md. Each case asserts exact
    // (Code, span, severity) via the same ExpectedAt helper pattern as FlatShapeDiagnosticTests.
    public class NudgeDiagnosticTests
    {
        private static DiagnosticResult ExpectedAt(DiagnosticDescriptor descriptor, SyntaxNode node)
        {
            var lineSpan = node.GetLocation().GetLineSpan();
            return AnalyzerVerifier.Diagnostic(descriptor).WithSpan(
                lineSpan.StartLinePosition.Line + 1,
                lineSpan.StartLinePosition.Character + 1,
                lineSpan.EndLinePosition.Line + 1,
                lineSpan.EndLinePosition.Character + 1);
        }

        private static LiteralExpressionSyntax StringLiteral(SyntaxTree tree, string value) =>
            tree.GetRoot().DescendantNodes().OfType<LiteralExpressionSyntax>()
                .Single(l => l.IsKind(SyntaxKind.StringLiteralExpression) && l.Token.ValueText == value);

        private static LiteralExpressionSyntax NumericLiteral(SyntaxTree tree) =>
            tree.GetRoot().DescendantNodes().OfType<LiteralExpressionSyntax>()
                .Single(l => l.IsKind(SyntaxKind.NumericLiteralExpression));

        [Fact]
        public async System.Threading.Tasks.Task Analyzer_StringSetKey_YieldsSB1101Info()
        {
            const string source = @"
public class SetKeyScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""Root"");
        root.Component<Rigidbody>(c => c.Set(""m_Mass"", 5f));
    }
}
";
            var tree = CSharpSyntaxTree.ParseText(source);
            var expected = ExpectedAt(DiagnosticDescriptors.SB1101, StringLiteral(tree, "m_Mass"));

            await AnalyzerVerifier.VerifyAsync(source, expected);
        }

        [Fact]
        public async System.Threading.Tasks.Task Analyzer_StringPrefabPath_YieldsSB1102()
        {
            const string source = @"
public class PrefabPathScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(""Assets/Prefabs/Tank.prefab"");
    }
}
";
            var tree = CSharpSyntaxTree.ParseText(source);
            var expected = ExpectedAt(DiagnosticDescriptors.SB1102, StringLiteral(tree, "Assets/Prefabs/Tank.prefab"));

            await AnalyzerVerifier.VerifyAsync(source, expected);
        }

        // `.On(...)` is NOT in the flat-shape whitelist (SceneBuilder.Grammar.FlatShapeRecognizer's
        // ApplyChainedCalls switch), so placing it on any builder chain ALSO trips SB1002 (Error) —
        // expected two-check co-occurrence, not a bug (research.md).
        [Fact]
        public async System.Threading.Tasks.Task Analyzer_StringOnPath_YieldsSB1103()
        {
            const string source = @"
public class OnPathScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""Root"");
        root.On(""Turret/Barrel"", h => { });
    }
}
";
            var tree = CSharpSyntaxTree.ParseText(source);
            var onInvocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Single(i => i.Expression is MemberAccessExpressionSyntax m && m.Name.Identifier.Text == "On");
            var expectedShapeError = ExpectedAt(DiagnosticDescriptors.SB1002, onInvocation.ArgumentList);
            var expectedNudge = ExpectedAt(DiagnosticDescriptors.SB1103, StringLiteral(tree, "Turret/Barrel"));

            await AnalyzerVerifier.VerifyAsync(source, expectedShapeError, expectedNudge);
        }

        [Fact]
        public async System.Threading.Tasks.Task Analyzer_StringTag_YieldsSB1104Warning()
        {
            const string source = @"
public class TagScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Root"").Tag(""Player"");
    }
}
";
            var tree = CSharpSyntaxTree.ParseText(source);
            var expected = ExpectedAt(DiagnosticDescriptors.SB1104, StringLiteral(tree, "Player"));

            await AnalyzerVerifier.VerifyAsync(source, expected);
        }

        [Fact]
        public async System.Threading.Tasks.Task Analyzer_StringLayer_YieldsSB1105Warning()
        {
            const string source = @"
public class LayerScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Root"").Layer(6);
    }
}
";
            var tree = CSharpSyntaxTree.ParseText(source);
            var expected = ExpectedAt(DiagnosticDescriptors.SB1105, NumericLiteral(tree));

            await AnalyzerVerifier.VerifyAsync(source, expected);
        }

        [Fact]
        public async System.Threading.Tasks.Task Analyzer_UntypedSetOverload_YieldsSB1106Info()
        {
            const string source = @"
public class UntypedSetScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""Root"");
        root.Component<Rigidbody>(c => c.Set(""mass"", 1f));
    }
}
";
            var tree = CSharpSyntaxTree.ParseText(source);
            var expected = ExpectedAt(DiagnosticDescriptors.SB1106, StringLiteral(tree, "mass"));

            await AnalyzerVerifier.VerifyAsync(source, expected);
        }
    }
}
