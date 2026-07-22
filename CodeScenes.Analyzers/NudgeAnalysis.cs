using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using SceneBuilder.Grammar;

namespace CodeScenes.Analyzers
{
    /// <summary>
    /// Registers BuilderAnalyzer's SB11xx string-escape-hatch nudges. Purely syntactic — scopes to
    /// the Build body via SceneBuilder.Grammar.FlatShapeRecognizer.TryFindBuildMethod (same discovery
    /// as FlatShapeAnalysis), then pattern-matches each invocation's method name + first-argument
    /// literal shape to an SB11xx descriptor. No semantic model, no grammar/shape logic.
    /// </summary>
    internal static class NudgeAnalysis
    {
        public static void AnalyzeCompilationUnit(SyntaxNodeAnalysisContext ctx)
        {
            var root = (CompilationUnitSyntax)ctx.Node;

            if (!FlatShapeRecognizer.TryFindBuildMethod(root, out var buildBody, out _))
            {
                return;
            }

            foreach (var invocation in buildBody!.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                {
                    continue;
                }

                var descriptor = Match(memberAccess.Name.Identifier.ValueText, invocation.ArgumentList.Arguments, out var arg0);
                if (descriptor is null || arg0 is null)
                {
                    continue;
                }

                ctx.ReportDiagnostic(Diagnostic.Create(descriptor, arg0.Expression.GetLocation()));
            }
        }

        private static DiagnosticDescriptor? Match(
            string methodName,
            SeparatedSyntaxList<ArgumentSyntax> args,
            out ArgumentSyntax? arg0)
        {
            arg0 = args.Count > 0 ? args[0] : null;

            switch (methodName)
            {
                case "Set" when args.Count >= 2 && IsStringLiteral(arg0, out var key):
                    return key!.StartsWith("m_") ? DiagnosticDescriptors.SB1101 : DiagnosticDescriptors.SB1106;

                case "Instance" when args.Count == 1 && IsStringLiteral(arg0, out _):
                    return DiagnosticDescriptors.SB1102;

                case "On" when args.Count >= 1 && IsStringLiteral(arg0, out _):
                    return DiagnosticDescriptors.SB1103;

                case "Tag" when args.Count == 1 && IsStringLiteral(arg0, out _):
                    return DiagnosticDescriptors.SB1104;

                case "Layer" when args.Count == 1 && (IsStringLiteral(arg0, out _) || IsNumericLiteral(arg0)):
                    return DiagnosticDescriptors.SB1105;

                default:
                    arg0 = null;
                    return null;
            }
        }

        private static bool IsStringLiteral(ArgumentSyntax? arg, out string? value)
        {
            value = null;
            if (arg?.Expression is not LiteralExpressionSyntax literal ||
                !literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                return false;
            }

            value = literal.Token.ValueText;
            return true;
        }

        private static bool IsNumericLiteral(ArgumentSyntax? arg) =>
            arg?.Expression is LiteralExpressionSyntax literal &&
            literal.IsKind(SyntaxKind.NumericLiteralExpression);
    }
}
