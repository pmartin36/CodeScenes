using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CodeScenes.Analyzers
{
    /// <summary>
    /// Registers BuilderAnalyzer's SB120x UnityEvent-signature scaffold. Recognizes the M8 typed
    /// method-lambda wiring shape `&lt;expr&gt;.OnClick(&lt;target&gt;, x => x.Method(...))` /
    /// `.OnEvent(&lt;target&gt;, x => x.Method(...))` (static) and the dynamic
    /// `.OnEvent(x => x.evt, target, h => h.Method, dynamic: true)` form, and, using ONLY the C#
    /// semantic model, reports SB1201 (arity/generic-arg mismatch) / SB1202 (non-public target).
    /// Fires ONLY when the wiring call itself binds to a real method symbol, so it stays provably
    /// inert against sources with no M8 surface in scope — the guard is semantic, not name-based.
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

                // GUARD (provably inert on non-M8 sources): only fires when the wiring call itself binds — either
                // cleanly, or (the dynamic form's mismatch case) as an unresolved overload whose
                // candidate set still names a real method. A dynamic method-group that mismatches
                // the event's generic args fails C# overload resolution for the call as a whole
                // (the method group can't convert to the expected `Action<T>`), so `.Symbol` alone
                // would miss exactly the shape SB1201 exists to flag; a genuinely unbound source
                // (no M8 surface in scope) resolves neither a symbol nor a candidate. Shared by the
                // static and dynamic paths below.
                var invocationSymbolInfo = ctx.SemanticModel.GetSymbolInfo(invocation);
                if (invocationSymbolInfo.Symbol is not IMethodSymbol &&
                    !invocationSymbolInfo.CandidateSymbols.Any(candidate => candidate is IMethodSymbol))
                {
                    continue;
                }

                if (TryFindMethodLambdaArg(invocation, out var lambdaBodyInvocation, out var referencedNameNode))
                {
                    if (ctx.SemanticModel.GetSymbolInfo(lambdaBodyInvocation).Symbol is not IMethodSymbol referenced)
                    {
                        continue;
                    }

                    // The expected arity is the COUNT of arguments supplied inside the wired
                    // method's own lambda call, not a fixed constant: a persistent call may carry
                    // at most ONE static argument (spec 09), and Unity binds by exact signature,
                    // so a call supplying 0 or 1 argument is legal only when it matches the
                    // referenced method's own declared parameter count.
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

                    continue;
                }

                // Dynamic `.OnEvent(selector, target, methodGroup, dynamic: true)` — the method
                // is referenced as a GROUP (not invoked), so TryFindMethodLambdaArg never matches
                // it. The expected shape is derived from the selected event field's own
                // `UnityEvent<T0..Tn>` generic argument list rather than a supplied-args count.
                if (eventName != "OnEvent")
                {
                    continue;
                }

                if (!TryFindDynamicListener(invocation, out var methodGroupNameNode, out var methodGroupBody, out var selectorBody))
                {
                    continue;
                }

                if (!TryReadUnityEventArgTypes(ctx.SemanticModel, selectorBody, out var argTypes))
                {
                    continue;
                }

                var candidates = ctx.SemanticModel.GetMemberGroup(methodGroupBody).OfType<IMethodSymbol>().ToImmutableArray();
                if (candidates.IsEmpty && ctx.SemanticModel.GetSymbolInfo(methodGroupBody).Symbol is IMethodSymbol single)
                {
                    candidates = ImmutableArray.Create(single);
                }

                if (candidates.IsEmpty)
                {
                    continue;
                }

                if (!candidates.Any(candidate => ParamsMatch(candidate, argTypes)))
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.SB1201,
                        methodGroupNameNode.GetLocation(),
                        methodGroupNameNode.Identifier.ValueText,
                        eventName,
                        DynamicShape(argTypes)));
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

        /// <summary>
        /// Finds the dynamic `.OnEvent(selector, target, methodGroup, dynamic: true)` shape:
        /// exactly 3 positional arguments (a named `dynamic:` bool is excluded), the third a
        /// method-group reference `h => h.Member` and the first a field/property selector
        /// `x => x.member`, both off the lambda's own parameter.
        /// </summary>
        private static bool TryFindDynamicListener(
            InvocationExpressionSyntax invocation,
            out SimpleNameSyntax methodGroupNameNode,
            out MemberAccessExpressionSyntax methodGroupBody,
            out MemberAccessExpressionSyntax selectorBody)
        {
            methodGroupNameNode = null!;
            methodGroupBody = null!;
            selectorBody = null!;

            var positional = invocation.ArgumentList.Arguments
                .Where(a => a.NameColon == null)
                .ToList();

            if (positional.Count != 3)
            {
                return false;
            }

            if (!TryGetMemberAccessLambdaBody(positional[2].Expression, out var methodGroup) ||
                !TryGetMemberAccessLambdaBody(positional[0].Expression, out var selector))
            {
                return false;
            }

            methodGroupBody = methodGroup;
            methodGroupNameNode = methodGroup.Name;
            selectorBody = selector;
            return true;
        }

        // Extracts the member-access body of a 1-param lambda `p => p.Member` — a method-group
        // or field/property reference, NOT an invocation — verifying the access is off the
        // lambda's own parameter.
        private static bool TryGetMemberAccessLambdaBody(
            ExpressionSyntax expression, out MemberAccessExpressionSyntax memberAccessBody)
        {
            memberAccessBody = null!;

            string? parameterName = expression switch
            {
                SimpleLambdaExpressionSyntax simple => simple.Parameter.Identifier.ValueText,
                ParenthesizedLambdaExpressionSyntax parenthesized when parenthesized.ParameterList.Parameters.Count == 1 =>
                    parenthesized.ParameterList.Parameters[0].Identifier.ValueText,
                _ => null,
            };

            if (parameterName is null)
            {
                return false;
            }

            var body = expression switch
            {
                SimpleLambdaExpressionSyntax simple => simple.Body,
                ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.Body,
                _ => null,
            };

            if (body is not MemberAccessExpressionSyntax bodyMemberAccess ||
                bodyMemberAccess.Expression is not IdentifierNameSyntax receiver ||
                receiver.Identifier.ValueText != parameterName)
            {
                return false;
            }

            memberAccessBody = bodyMemberAccess;
            return true;
        }

        /// <summary>
        /// Resolves the selector's field/property type and walks its self+base-type chain for an
        /// `UnityEngine.Events.UnityEvent&lt;T0..Tn&gt;` (namespace-strict; real event fields are
        /// SUBCLASSES, e.g. `Slider.SliderEvent : UnityEvent&lt;float&gt;`). Returns false — under-flag
        /// — for an unresolved selector or a non-generic `UnityEvent` base.
        /// </summary>
        private static bool TryReadUnityEventArgTypes(
            SemanticModel semanticModel,
            MemberAccessExpressionSyntax selectorBody,
            out ImmutableArray<ITypeSymbol> argTypes)
        {
            argTypes = ImmutableArray<ITypeSymbol>.Empty;

            var selectedType = semanticModel.GetSymbolInfo(selectorBody).Symbol switch
            {
                IFieldSymbol field => field.Type,
                IPropertySymbol property => property.Type,
                _ => null,
            };

            for (var type = selectedType as INamedTypeSymbol; type is not null; type = type.BaseType)
            {
                if (type.Name == "UnityEvent" &&
                    type.ContainingNamespace?.ToDisplayString() == "UnityEngine.Events" &&
                    type.TypeArguments.Length >= 1)
                {
                    argTypes = type.TypeArguments;
                    return true;
                }
            }

            return false;
        }

        // A candidate method-group overload matches when its declared parameter list is exactly
        // the event's own `UnityEvent<T0..Tn>` generic argument list, in order.
        private static bool ParamsMatch(IMethodSymbol candidate, ImmutableArray<ITypeSymbol> argTypes)
        {
            if (candidate.Parameters.Length != argTypes.Length)
            {
                return false;
            }

            for (var i = 0; i < argTypes.Length; i++)
            {
                if (!SymbolEqualityComparer.Default.Equals(candidate.Parameters[i].Type, argTypes[i]))
                {
                    return false;
                }
            }

            return true;
        }

        // The MessageFormat's `{2}` for the dynamic form — the event's generic argument list,
        // e.g. "(float)" or "(int, float)".
        private static string DynamicShape(ImmutableArray<ITypeSymbol> argTypes) =>
            "(" + string.Join(", ", argTypes.Select(t => t.ToDisplayString())) + ")";
    }
}
