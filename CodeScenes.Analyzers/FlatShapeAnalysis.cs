using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using SceneBuilder.Grammar;

namespace CodeScenes.Analyzers
{
    /// <summary>
    /// Registers BuilderAnalyzer's SB100x flat-shape check. ZERO grammar logic lives here — it
    /// only locates the Build body (via SceneBuilder.Grammar.FlatShapeRecognizer.TryFindBuildMethod),
    /// runs FlatShapeRecognizer.Analyze, and maps each ShapeViolation to a Diagnostic.
    /// </summary>
    internal static class FlatShapeAnalysis
    {
        public static void AnalyzeCompilationUnit(SyntaxNodeAnalysisContext ctx)
        {
            var root = (CompilationUnitSyntax)ctx.Node;

            if (!FlatShapeRecognizer.TryFindBuildMethod(root, out var buildBody, out var sceneParamName))
            {
                return;
            }

            var violations = FlatShapeRecognizer.Analyze(buildBody!, sceneParamName!);

            foreach (var violation in violations)
            {
                if (!DiagnosticDescriptors.ById.TryGetValue(violation.Code, out var descriptor))
                {
                    continue;
                }

                var location = Location.Create(
                    ctx.Node.SyntaxTree,
                    new TextSpan(violation.Span.Start, violation.Span.Length));

                ctx.ReportDiagnostic(Diagnostic.Create(descriptor, location, violation.Message));
            }
        }
    }
}
