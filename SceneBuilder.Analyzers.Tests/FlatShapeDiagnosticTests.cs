using System.Linq;
using CodeScenes.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace SceneBuilder.Analyzers.Tests
{
    // Tests #6-#8 (+ a no-Build-method guard) for b2-t2: BuilderAnalyzer must translate every
    // FlatShapeRecognizer.ShapeViolation over a file's Build body into exactly one diagnostic with
    // matching (Id, Severity, Span) — ZERO grammar logic in the analyzer itself. See
    // .agent_handoffs/codescenes-analyzers/b2-t2/research.md.
    public class FlatShapeDiagnosticTests
    {
        // Expected spans are computed by parsing the SAME source text the analyzer test runs
        // against and reading the target node's line/col — mirrors SceneBuilder.Core.Tests'
        // RecognizerTests.cs posture, so this pins behavior, not hand-counted source offsets.
        private static DiagnosticResult ExpectedAt(DiagnosticDescriptor descriptor, SyntaxNode node)
        {
            var lineSpan = node.GetLocation().GetLineSpan();
            return AnalyzerVerifier.Diagnostic(descriptor).WithSpan(
                lineSpan.StartLinePosition.Line + 1,
                lineSpan.StartLinePosition.Character + 1,
                lineSpan.EndLinePosition.Line + 1,
                lineSpan.EndLinePosition.Character + 1);
        }

        // Stale-test update (b2-t3, research.md CRITICAL SEAM): the original source used
        // `.Tag("Player")`/`.Layer(8)`/`.Set("mass", 1f)` — all-literal forms that, once b2-t3
        // wires SB11xx nudges, would trip SB1104/SB1105/SB1106 and break "valid builder = zero
        // diagnostics". Swapped for the typed `.Set(x => x.mass, 1f)` selector form (the ONLY
        // typed escape from a nudge for these calls that the current flat-shape recognizer also
        // accepts as valid shape — Tag/Layer have no typed-argument form the recognizer accepts
        // today, so they are dropped from this sample rather than swapped to a typed-catalog call
        // that would itself trip SB1001).
        [Fact]
        public async System.Threading.Tasks.Task Analyzer_ValidBuilder_ZeroDiagnostics()
        {
            const string source = @"
public class ValidScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""Root"");
        root.Component<Rigidbody>(c => c.Set(x => x.mass, 1f));
    }
}
";
            await AnalyzerVerifier.VerifyAsync(source);
        }

        [Fact]
        public async System.Threading.Tasks.Task Analyzer_Loop_YieldsSB1001Error()
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
            var tree = CSharpSyntaxTree.ParseText(source);
            var forStatement = tree.GetRoot().DescendantNodes().OfType<ForStatementSyntax>().Single();
            var expected = ExpectedAt(DiagnosticDescriptors.SB1001, forStatement);

            await AnalyzerVerifier.VerifyAsync(source, expected);
        }

        [Fact]
        public async System.Threading.Tasks.Task Analyzer_UnknownCall_YieldsSB1002()
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
            var tree = CSharpSyntaxTree.ParseText(source);
            var wiggleArgs = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Single(i => i.Expression is MemberAccessExpressionSyntax m && m.Name.Identifier.Text == "Wiggle")
                .ArgumentList;
            var expected = ExpectedAt(DiagnosticDescriptors.SB1002, wiggleArgs);

            await AnalyzerVerifier.VerifyAsync(source, expected);
        }

        [Fact]
        public async System.Threading.Tasks.Task Analyzer_NonSetClosure_YieldsSB1003()
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
            var tree = CSharpSyntaxTree.ParseText(source);
            var nopeCall = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Single(i => i.Expression is MemberAccessExpressionSyntax m && m.Name.Identifier.Text == "Nope");
            var expected = ExpectedAt(DiagnosticDescriptors.SB1003, nopeCall);

            await AnalyzerVerifier.VerifyAsync(source, expected);
        }

        [Fact]
        public async System.Threading.Tasks.Task Analyzer_NoBuildMethod_ZeroDiagnostics()
        {
            const string source = @"
public class NotASceneDefinition
{
    public void SomeMethod() { }
}
";
            await AnalyzerVerifier.VerifyAsync(source);
        }
    }
}
