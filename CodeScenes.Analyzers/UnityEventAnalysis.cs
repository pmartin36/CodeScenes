using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CodeScenes.Analyzers
{
    /// <summary>
    /// Registers BuilderAnalyzer's SB120x UnityEvent-signature scaffold. Recognizes the intended
    /// M8 typed method-lambda wiring shape `&lt;expr&gt;.OnClick(&lt;target&gt;, x => x.Method(...))` and,
    /// using ONLY the C# semantic model, reports SB1201 (arity mismatch) / SB1202 (non-public
    /// target). Fires ONLY when the wiring call itself binds to a real method symbol, so it stays
    /// provably inert against today's (M8-less) sources — the guard is semantic, not name-based.
    /// </summary>
    internal static class UnityEventAnalysis
    {
        public static void AnalyzeCompilationUnit(SyntaxNodeAnalysisContext ctx)
        {
            var root = (CompilationUnitSyntax)ctx.Node;

            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                {
                    continue;
                }

                var eventName = memberAccess.Name.Identifier.ValueText;
                if (eventName != "OnClick" && eventName != "OnEvent")
                {
                    continue;
                }

                // "OnEvent"'s expected arity cannot be derived from pure syntax/semantics in v0 —
                // under-flag rather than guess (precision over recall).
                if (eventName != "OnClick")
                {
                    continue;
                }

                if (!TryFindMethodLambdaArg(invocation, out var lambdaBodyInvocation, out var referencedNameNode))
                {
                    continue;
                }

                // GUARD (inert-today): only fires when the wiring call itself binds to a real
                // method symbol. If nothing in scope resolves, the M8 surface is absent and there
                // is nothing to check.
                if (ctx.SemanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol)
                {
                    continue;
                }

                if (ctx.SemanticModel.GetSymbolInfo(lambdaBodyInvocation).Symbol is not IMethodSymbol referenced)
                {
                    continue;
                }

                // The expected arity is the COUNT of arguments supplied inside the wired method's
                // own lambda call, not a fixed constant: a persistent call may carry at most ONE
                // static argument (spec 09), and Unity binds by exact signature, so a call
                // supplying 0 or 1 argument is legal only when it matches the referenced method's
                // own declared parameter count.
                var supplied = lambdaBodyInvocation.ArgumentList.Arguments.Count;
                var mismatch = referenced.Parameters.Length != supplied || supplied > 1;

                if (mismatch)
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.SB1201,
                        referencedNameNode.GetLocation(),
                        referenced.Name,
                        eventName,
                        ExpectedShape(supplied)));
                }
                else if (referenced.DeclaredAccessibility != Accessibility.Public)
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.SB1202,
                        referencedNameNode.GetLocation()));
                }
            }
        }

        // The MessageFormat's `{2}` — the shape the referenced method's signature was expected to
        // match, phrased in terms of what was actually supplied at the call site.
        private static string ExpectedShape(int supplied) => supplied switch
        {
            0 => "()",
            1 => "(one static argument)",
            _ => "() or one static argument",
        };

        /// <summary>
        /// Finds an argument shaped `x => x.Method(...)` — a simple/parenthesized lambda whose
        /// body is an invocation on the lambda's own parameter. Returns the invocation and the
        /// referenced method's simple-name node (used as the diagnostic's report location).
        /// </summary>
        private static bool TryFindMethodLambdaArg(
            InvocationExpressionSyntax invocation,
            out InvocationExpressionSyntax lambdaBodyInvocation,
            out SimpleNameSyntax referencedNameNode)
        {
            foreach (var arg in invocation.ArgumentList.Arguments)
            {
                string? parameterName = arg.Expression switch
                {
                    SimpleLambdaExpressionSyntax simple => simple.Parameter.Identifier.ValueText,
                    ParenthesizedLambdaExpressionSyntax parenthesized when parenthesized.ParameterList.Parameters.Count == 1 =>
                        parenthesized.ParameterList.Parameters[0].Identifier.ValueText,
                    _ => null,
                };

                if (parameterName is null)
                {
                    continue;
                }

                var body = arg.Expression switch
                {
                    SimpleLambdaExpressionSyntax simple => simple.Body,
                    ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.Body,
                    _ => null,
                };

                if (body is not InvocationExpressionSyntax bodyInvocation ||
                    bodyInvocation.Expression is not MemberAccessExpressionSyntax bodyMemberAccess ||
                    bodyMemberAccess.Expression is not IdentifierNameSyntax receiver ||
                    receiver.Identifier.ValueText != parameterName)
                {
                    continue;
                }

                lambdaBodyInvocation = bodyInvocation;
                referencedNameNode = bodyMemberAccess.Name;
                return true;
            }

            lambdaBodyInvocation = null!;
            referencedNameNode = null!;
            return false;
        }
    }
}
