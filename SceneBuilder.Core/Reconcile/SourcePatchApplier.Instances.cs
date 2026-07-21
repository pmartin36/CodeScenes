using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SceneBuilder.Core.Reconcile
{
    // m10-b4-t1: instance-root override-authoring resolvers (AppendInstanceOverride /
    // AppendInstanceAddComponent / AppendInstanceRemoveComponent / DropInstanceCall). Third
    // partial-class file so the existing private helpers on SourcePatchApplier
    // (FindAnchorInvocation, GetChainExpression, RemoveTrailingInvocation, Fail) are reused
    // directly — mirrors ComponentPatchApplier.cs's comment on why this is a separate file.
    public static partial class SourcePatchApplier
    {
        // ---- AppendInstanceOverride / AppendInstanceAddComponent / AppendInstanceRemoveComponent --

        private static void ResolveAppendInstanceOverride(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            AppendInstanceOverride edit,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var callText = RenderInstanceOverrideCall(edit.Sets);
            AppendChainedCall(root, anchors, edit.Anchor, callText, allTargets, appliers);
        }

        private static void ResolveAppendInstanceAddComponent(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            AppendInstanceAddComponent edit,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var callText = $"AddComponent<{edit.TypeFullName}>{RenderComponentClosureArgs(edit.Fields, edit.FieldExpressions)}";
            AppendChainedCall(root, anchors, edit.Anchor, callText, allTargets, appliers);
        }

        private static void ResolveAppendInstanceRemoveComponent(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            AppendInstanceRemoveComponent edit,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var callText = $"RemoveComponent<{edit.TypeFullName}>()";
            AppendChainedCall(root, anchors, edit.Anchor, callText, allTargets, appliers);
        }

        /// <summary>
        /// Splices a new `.<paramref name="callText"/>` onto the anchor's existing chain — the same
        /// template as ResolveIntroduceFlagCall (SourcePatchApplier.cs), generalized to accept any
        /// call text (generics/lambdas) since these calls aren't the fixed single-argument shape a
        /// flag call is. Trailing trivia is preserved exactly as IntroduceFlagCall/RemoveFlagCall do.
        /// </summary>
        private static void AppendChainedCall(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            string anchor,
            string callText,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var invocation = FindAnchorInvocation(root, anchors, anchor);
            var statement = invocation.FirstAncestorOrSelf<StatementSyntax>()
                ?? throw Fail(invocation, $"Anchor '{anchor}' is not inside a statement.");

            var chainExpr = GetChainExpression(statement);

            allTargets.Add(chainExpr);
            appliers.Add(currentRoot =>
            {
                var current = currentRoot.GetCurrentNode(chainExpr)!;
                var newExprText = current.WithoutTrailingTrivia().ToFullString() + "." + callText;
                var newExpr = SyntaxFactory.ParseExpression(newExprText).WithTrailingTrivia(current.GetTrailingTrivia());
                return currentRoot.ReplaceNode(current, newExpr);
            });
        }

        // Inverse of BuilderParser.Instance.cs's ApplyOverride/ParseOverrideSet: multiple Sets fold
        // into ONE `.Override(e => e.Set(a).Set(b))` fluent chain.
        private static string RenderInstanceOverrideCall(IReadOnlyList<OverrideSetSpec> sets)
        {
            var body = "e." + string.Join(".", sets.Select(RenderOverrideSetCall));
            return $"Override(e => {body})";
        }

        private static string RenderOverrideSetCall(OverrideSetSpec set)
        {
            var valueText = set.ValueExpression ?? SourceExpr.ValueNodeLiteral(set.Value);

            if (set.PropertyPath.StartsWith("member:", StringComparison.Ordinal))
            {
                var name = set.PropertyPath.Substring("member:".Length);
                return $"Set(({set.TypeFullName} x) => x.{name}, {valueText})";
            }

            return $"Set<{set.TypeFullName}>({SourceExpr.StringLiteral(set.PropertyPath)}, {valueText})";
        }

        // ---- DropInstanceCall -----------------------------------------------------------------

        private static void ResolveDropInstanceCall(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            DropInstanceCall edit,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var invocation = FindAnchorInvocation(root, anchors, edit.Anchor);
            var statement = invocation.FirstAncestorOrSelf<StatementSyntax>()
                ?? throw Fail(invocation, $"Anchor '{edit.Anchor}' is not inside a statement.");

            var target = FindInstanceCall(statement, edit);

            allTargets.Add(target);
            appliers.Add(currentRoot =>
            {
                var current = (InvocationExpressionSyntax)currentRoot.GetCurrentNode(target)!;
                return RemoveTrailingInvocation(currentRoot, current);
            });
        }

        private static string InstanceCallMethodName(InstanceCallKind kind) => kind switch
        {
            InstanceCallKind.Override => "Override",
            InstanceCallKind.AddComponent => "AddComponent",
            InstanceCallKind.RemoveComponent => "RemoveComponent",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown InstanceCallKind."),
        };

        private static InvocationExpressionSyntax FindInstanceCall(StatementSyntax statement, DropInstanceCall edit)
        {
            var methodName = InstanceCallMethodName(edit.Kind);

            var candidates = statement.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(inv => inv.Expression is MemberAccessExpressionSyntax member && member.Name.Identifier.Text == methodName);

            foreach (var candidate in candidates)
            {
                var matches = edit.Kind == InstanceCallKind.Override
                    ? OverrideCallMatches(candidate, edit.PropertyPath)
                    : GenericTypeArgMatches(candidate, edit.TypeFullName);

                if (matches)
                {
                    return candidate;
                }
            }

            throw Fail(statement, $"No matching .{methodName}(...) call found for anchor '{edit.Anchor}'.");
        }

        private static bool OverrideCallMatches(InvocationExpressionSyntax invocation, string? propertyPath)
        {
            if (propertyPath == null)
            {
                return true;
            }

            if (invocation.ArgumentList.Arguments.Count != 1 ||
                invocation.ArgumentList.Arguments[0].Expression is not SimpleLambdaExpressionSyntax lambda)
            {
                return false;
            }

            return lambda.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .Any(inv => inv.Expression is MemberAccessExpressionSyntax member
                    && member.Name.Identifier.Text == "Set"
                    && OverrideSetTargetsPath(inv, propertyPath));
        }

        // Mirrors BuilderParser.Instance.cs's ParseOverrideSet key-form parsing (selector vs
        // string-path), just to answer "does THIS .Set(...) target this path", not to lower a value.
        private static bool OverrideSetTargetsPath(InvocationExpressionSyntax setInvocation, string propertyPath)
        {
            var args = setInvocation.ArgumentList.Arguments;
            if (args.Count != 2)
            {
                return false;
            }

            var keyExpr = args[0].Expression;

            if (keyExpr is ParenthesizedLambdaExpressionSyntax { Body: MemberAccessExpressionSyntax memberAccess })
            {
                return "member:" + memberAccess.Name.Identifier.Text == propertyPath;
            }

            if (keyExpr is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                return literal.Token.ValueText == propertyPath;
            }

            return false;
        }

        private static bool GenericTypeArgMatches(InvocationExpressionSyntax invocation, string? typeFullName)
        {
            if (typeFullName == null)
            {
                return true;
            }

            if (invocation.Expression is not MemberAccessExpressionSyntax member ||
                member.Name is not GenericNameSyntax generic ||
                generic.TypeArgumentList.Arguments.Count != 1)
            {
                return false;
            }

            return generic.TypeArgumentList.Arguments[0].ToString().Trim() == typeFullName;
        }
    }
}
