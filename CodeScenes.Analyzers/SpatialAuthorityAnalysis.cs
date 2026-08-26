using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using SceneBuilder.Grammar;

namespace CodeScenes.Analyzers
{
    /// <summary>
    /// Detects two position-driver calls (AlignTo/Between) on one node that claim the same axis
    /// in the same frame, and reports SB1203. Purely syntactic (no SemanticModel), mirroring
    /// NudgeAnalysis's scope-to-Build-body posture. Grammar cannot reference SceneBuilder.Core's
    /// SpatialComponents keyword tables (netstandard2.0 vs 2.1), so the keyword-&gt;axis mapping is
    /// reimplemented keyword-only below (see SpatialComponents.AlignToDrivenMask for the source of
    /// truth these keywords mirror).
    /// </summary>
    internal static class SpatialAuthorityAnalysis
    {
        private enum Axis
        {
            X,
            Y,
            Z,
        }

        private sealed class Claim
        {
            public string? Frame;
            public HashSet<Axis> Axes = new();
            public SimpleNameSyntax NameNode = null!;
            public int SortKey;
        }

        private sealed class WalkState
        {
            public readonly Dictionary<string, List<Claim>> ClaimsByNode = new();
            public int AnonCounter;
        }

        public static void AnalyzeCompilationUnit(SyntaxNodeAnalysisContext ctx)
        {
            var root = (CompilationUnitSyntax)ctx.Node;

            if (!FlatShapeRecognizer.TryFindBuildMethod(root, out var buildBody, out _))
            {
                return;
            }

            var state = new WalkState();

            foreach (var statement in buildBody!.Statements)
            {
                ProcessStatement(statement, state);
            }

            foreach (var claims in state.ClaimsByNode.Values)
            {
                claims.Sort((a, b) => a.SortKey.CompareTo(b.SortKey));

                var conflicting = FindFirstConflict(claims);
                if (conflicting != null)
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.SB1203, conflicting.NameNode.GetLocation()));
                }
            }
        }

        // ---- Statement / chain walk (mirrors FlatShapeRecognizer.ProcessStatement/ProcessBuilderChain,
        // reimplemented locally: this walk only needs node identity + position-driver calls, not the
        // full shape-violation grammar). ------------------------------------------------------------

        private static void ProcessStatement(StatementSyntax statement, WalkState state)
        {
            switch (statement)
            {
                case LocalDeclarationStatementSyntax { Declaration.Variables: { Count: 1 } variables } local
                    when variables[0].Initializer != null:
                    ProcessChain(variables[0].Initializer!.Value, variables[0].Identifier.Text, state);
                    break;

                case ExpressionStatementSyntax exprStatement:
                    ProcessChain(exprStatement.Expression, null, state);
                    break;
            }
        }

        private static void ProcessChain(ExpressionSyntax expression, string? handleName, WalkState state)
        {
            var (receiver, calls) = UnwrapChain(expression);
            if (receiver == null || calls.Count == 0)
            {
                return;
            }

            List<(string Method, ArgumentListSyntax Args, InvocationExpressionSyntax Invocation)> remaining;
            string nodeId;

            if (calls[0].Method is "Add" or "Instance")
            {
                nodeId = handleName ?? $"$anon{state.AnonCounter++}";
                remaining = calls.Skip(1).ToList();

                if (calls[0].Method == "Add" && calls[0].Args.Arguments.Count > 1)
                {
                    ProcessClosure(calls[0].Args.Arguments[1].Expression, state);
                }
            }
            else
            {
                nodeId = receiver.Identifier.Text;
                remaining = calls;
            }

            foreach (var call in remaining)
            {
                if (call.Method is not ("AlignTo" or "Between"))
                {
                    continue;
                }

                var claim = TryResolveClaim(call);
                if (claim == null)
                {
                    continue;
                }

                if (!state.ClaimsByNode.TryGetValue(nodeId, out var claims))
                {
                    claims = new List<Claim>();
                    state.ClaimsByNode[nodeId] = claims;
                }

                claims.Add(claim);
            }
        }

        private static void ProcessClosure(ExpressionSyntax closureExpression, WalkState state)
        {
            if (closureExpression is not SimpleLambdaExpressionSyntax lambda)
            {
                return;
            }

            switch (lambda.Body)
            {
                case BlockSyntax block:
                    foreach (var statement in block.Statements)
                    {
                        ProcessStatement(statement, state);
                    }
                    break;

                case ExpressionSyntax exprBody:
                    ProcessChain(exprBody, null, state);
                    break;
            }
        }

        private static (IdentifierNameSyntax? Receiver, List<(string Method, ArgumentListSyntax Args, InvocationExpressionSyntax Invocation)> Calls) UnwrapChain(
            ExpressionSyntax expression)
        {
            var calls = new List<(string Method, ArgumentListSyntax Args, InvocationExpressionSyntax Invocation)>();
            var current = expression;

            while (true)
            {
                if (current is InvocationExpressionSyntax invocation)
                {
                    if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                    {
                        calls.Add((memberAccess.Name.Identifier.Text, invocation.ArgumentList, invocation));
                        current = memberAccess.Expression;
                        continue;
                    }

                    return (null, calls);
                }

                if (current is IdentifierNameSyntax identifier)
                {
                    calls.Reverse();
                    return (identifier, calls);
                }

                return (null, calls);
            }
        }

        // ---- Claim resolution — the single owner of AlignTo keyword and Between `axis:` member
        // recognition (rg -n 'AlignTo|Between' inside this file resolves only here). ------------

        private static Claim? TryResolveClaim((string Method, ArgumentListSyntax Args, InvocationExpressionSyntax Invocation) call)
        {
            var nameNode = ((MemberAccessExpressionSyntax)call.Invocation.Expression).Name;

            if (call.Method == "AlignTo")
            {
                var axes = new HashSet<Axis>();
                string? frame = null;

                foreach (var arg in call.Args.Arguments)
                {
                    if (arg.NameColon == null)
                    {
                        continue;
                    }

                    switch (arg.NameColon.Name.Identifier.Text)
                    {
                        case "x" when IsPinnedAxisAlign(arg.Expression):
                            axes.Add(Axis.X);
                            break;
                        case "y" when IsPinnedAxisAlign(arg.Expression):
                            axes.Add(Axis.Y);
                            break;
                        case "z" when IsPinnedAxisAlign(arg.Expression):
                            axes.Add(Axis.Z);
                            break;
                        case "frame":
                            frame = arg.Expression.ToString().Trim();
                            break;
                    }
                }

                return axes.Count == 0 ? null : new Claim { Frame = frame, Axes = axes, NameNode = nameNode, SortKey = nameNode.SpanStart };
            }

            if (call.Method == "Between")
            {
                Axis? axis = null;
                string? frame = null;

                foreach (var arg in call.Args.Arguments)
                {
                    if (arg.NameColon == null)
                    {
                        continue;
                    }

                    switch (arg.NameColon.Name.Identifier.Text)
                    {
                        case "axis":
                            axis = ResolveAxisMember(arg.Expression);
                            break;
                        case "alongOrientationOf":
                            frame = arg.Expression.ToString().Trim();
                            break;
                    }
                }

                if (axis == null)
                {
                    return null;
                }

                return new Claim { Frame = frame, Axes = new HashSet<Axis> { axis.Value }, NameNode = nameNode, SortKey = nameNode.SpanStart };
            }

            return null;
        }

        // An `x:`/`y:`/`z:` argument claims its axis whenever its value is anything other than the
        // unpinned default: the parameterless `default` literal, or a member access ending in `None`
        // (`AxisAlign.None`). Anything else — a preset, a preset with `.Offset(...)`, or an
        // unrecognized expression — is conservatively treated as pinned.
        private static bool IsPinnedAxisAlign(ExpressionSyntax expression)
        {
            if (expression.IsKind(SyntaxKind.DefaultLiteralExpression) || expression.IsKind(SyntaxKind.DefaultExpression))
            {
                return false;
            }

            return expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "None" };
        }

        private static Axis? ResolveAxisMember(ExpressionSyntax expression) =>
            expression is MemberAccessExpressionSyntax memberAccess
                ? memberAccess.Name.Identifier.Text switch
                {
                    "X" => Axis.X,
                    "Y" => Axis.Y,
                    "Z" => Axis.Z,
                    _ => (Axis?)null,
                }
                : null;

        // ---- Conflict predicate ------------------------------------------------------------------

        private static Claim? FindFirstConflict(List<Claim> claims)
        {
            for (var i = 0; i < claims.Count; i++)
            {
                for (var j = i + 1; j < claims.Count; j++)
                {
                    if (Conflict(claims[i], claims[j]))
                    {
                        return claims[j];
                    }
                }
            }

            return null;
        }

        private static bool Conflict(Claim a, Claim b) => a.Frame != b.Frame || a.Axes.Overlaps(b.Axes);
    }
}
