using System.Linq;
using CodeScenes.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace SceneBuilder.Analyzers.Tests
{
    // SB1203 fires when two position-driver calls (AlignTo/Between) on one node claim the same
    // axis in the same frame, and stays silent when their claims are provably orthogonal.
    public class SpatialAuthorityAnalysisTests
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

        private static SimpleNameSyntax MethodName(SyntaxTree tree, string methodName, int occurrence = 0) =>
            tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(i => i.Expression is MemberAccessExpressionSyntax m && m.Name.Identifier.Text == methodName)
                .Select(i => ((MemberAccessExpressionSyntax)i.Expression).Name)
                .ElementAt(occurrence);

        [Fact]
        public async System.Threading.Tasks.Task Analyzer_AlignToDown_AndBetweenAxisY_SameNode_FiresSB1203()
        {
            const string source = @"
public class SameAxisScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""Root"");
        root.AlignTo(floor, y: AxisAlign.AbutMax).Between(from: a, to: b, fraction: 0.5f, axis: Between.Axis.Y);
    }
}
";
            var tree = CSharpSyntaxTree.ParseText(source);
            var expected = ExpectedAt(DiagnosticDescriptors.SB1203, MethodName(tree, "Between"));

            await AnalyzerVerifier.VerifyAsync(source, expected);
        }

        [Fact]
        public async System.Threading.Tasks.Task Analyzer_AlignToDown_AndBetweenAxisX_SameNode_NoFire()
        {
            const string source = @"
public class DifferentAxisScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""Root"");
        root.AlignTo(floor, y: AxisAlign.AbutMax).Between(from: a, to: b, fraction: 0.5f, axis: Between.Axis.X);
    }
}
";
            await AnalyzerVerifier.VerifyAsync(source);
        }

        [Fact]
        public async System.Threading.Tasks.Task Analyzer_TwoBetweens_DifferentOrientationRefs_FiresSB1203()
        {
            const string source = @"
public class DifferentFramesScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""Root"");
        root.Between(from: a1, to: b1, fraction: 0.5f, axis: Between.Axis.X, alongOrientationOf: rootA)
            .Between(from: a2, to: b2, fraction: 0.5f, axis: Between.Axis.X, alongOrientationOf: rootB);
    }
}
";
            var tree = CSharpSyntaxTree.ParseText(source);
            // The fluent chain's outer invocation is the textually-later `.Between(...)` call and is
            // visited before the inner one under DescendantNodes()'s pre-order walk, so occurrence 0
            // is the later call SB1203 reports at.
            var expected = ExpectedAt(DiagnosticDescriptors.SB1203, MethodName(tree, "Between", occurrence: 0));

            await AnalyzerVerifier.VerifyAsync(source, expected);
        }

        [Fact]
        public async System.Threading.Tasks.Task Analyzer_CrossStatement_SameHandle_AlignToThenBetweenSameAxis_FiresSB1203()
        {
            const string source = @"
public class CrossStatementScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var r = scene.Add(""r"");
        r.AlignTo(floor, y: AxisAlign.AbutMax);
        r.Between(from: a, to: b, fraction: 0.5f, axis: Between.Axis.Y);
    }
}
";
            var tree = CSharpSyntaxTree.ParseText(source);
            var expected = ExpectedAt(DiagnosticDescriptors.SB1203, MethodName(tree, "Between"));

            await AnalyzerVerifier.VerifyAsync(source, expected);
        }

        [Fact]
        public async System.Threading.Tasks.Task Analyzer_SingleDriver_NoFire()
        {
            const string source = @"
public class SingleDriverScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""Root"");
        root.Between(from: a, to: b, fraction: 0.5f, axis: Between.Axis.Y);
    }
}
";
            await AnalyzerVerifier.VerifyAsync(source);
        }
    }
}
