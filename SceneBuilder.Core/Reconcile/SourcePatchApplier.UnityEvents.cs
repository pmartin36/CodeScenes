using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Reconcile
{
    // Resolves the 3 UnityEvent listener SourceEdit kinds (SourceEdit.cs). AppendListenerCall
    // mirrors ResolveIntroduceComponentField's closure-growth machinery (ComponentPatchApplier.cs)
    // — a listener call is another call chained onto the SAME `c => ...` closure param, structurally
    // identical to growing a `.Set(...)` closure. PatchListenerCall/RemoveListenerCall resolve their
    // OWN listener call directly by its CallSpan (never via the component's own anchor), so an edit
    // to one listener never re-renders or deletes a sibling's own statement.
    public static partial class SourcePatchApplier
    {
        // ---- AppendListenerCall ---------------------------------------------------------------

        private static void ResolveAppendListenerCall(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            AppendListenerCall edit,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var invocation = FindComponentInvocation(root, anchors, edit.Anchor);
            var arguments = invocation.ArgumentList.Arguments;

            if (arguments.Count != 0
                && (arguments.Count != 1 || arguments[0].Expression is not SimpleLambdaExpressionSyntax))
            {
                throw Fail(invocation, $"Unsupported component closure form for anchor '{edit.Anchor}'; expected a lambda like `c => ...`.");
            }

            allTargets.Add(invocation);
            appliers.Add(currentRoot =>
            {
                var current = (InvocationExpressionSyntax)currentRoot.GetCurrentNode(invocation)!;
                var currentArguments = current.ArgumentList.Arguments;

                if (currentArguments.Count == 0)
                {
                    var lambdaText = $"c => c.{edit.CallExpr}";
                    var lambdaArg = SyntaxFactory.Argument(SyntaxFactory.ParseExpression(lambdaText));
                    var newArgList = SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(lambdaArg));
                    return currentRoot.ReplaceNode(current, current.WithArgumentList(newArgList));
                }

                if (currentArguments.Count != 1 || currentArguments[0].Expression is not SimpleLambdaExpressionSyntax currentLambda)
                {
                    throw Fail(current, $"Unsupported component closure form for anchor '{edit.Anchor}'; expected a lambda like `c => ...`.");
                }

                var receiver = currentLambda.Parameter.Identifier.Text;
                var newCallText = $"{receiver}.{edit.CallExpr}";

                var newBody = currentLambda.Body is BlockSyntax block
                    ? AppendToClosureBlock(block, newCallText)
                    : ExpressionBodyToBlock((ExpressionSyntax)currentLambda.Body, newCallText);

                return currentRoot.ReplaceNode(currentLambda, currentLambda.WithBody(newBody));
            });
        }

        // ---- PatchListenerCall / RemoveListenerCall shared span resolution --------------------

        // A listener call's span starts at the `.` operator token, not the invocation's own start
        // (BuilderParser.UnityEvents.cs's `anchorStart = memberAccess.OperatorToken.SpanStart`) — no
        // syntax node begins there, so this mirrors FindAnchorInvocation's own component-anchor
        // fallback: locate the invocation whose member-access operator token starts at that
        // position.
        private static InvocationExpressionSyntax FindListenerCallInvocation(SyntaxNode root, SourceSpan callSpan)
        {
            var textSpan = TextSpan.FromBounds(callSpan.Start, callSpan.Start + callSpan.Length);
            var node = root.FindNode(textSpan, getInnermostNodeForTie: true);

            var invocation = node.FirstAncestorOrSelf<InvocationExpressionSyntax>(inv =>
                inv.Expression is MemberAccessExpressionSyntax ma && ma.OperatorToken.SpanStart == callSpan.Start);

            return invocation ?? throw Fail(root, $"Could not resolve listener call span at position {callSpan.Start}.");
        }

        // ---- PatchListenerCall ------------------------------------------------------------------

        private static void ResolvePatchListenerCall(
            CompilationUnitSyntax root,
            PatchListenerCall edit,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var invocation = FindListenerCallInvocation(root, edit.CallSpan);

            allTargets.Add(invocation);
            appliers.Add(currentRoot =>
            {
                var current = (InvocationExpressionSyntax)currentRoot.GetCurrentNode(invocation)!;
                var member = (MemberAccessExpressionSyntax)current.Expression;
                var receiverText = member.Expression.ToString();
                var replacement = SyntaxFactory.ParseExpression(receiverText + edit.NewExpr).WithTriviaFrom(current);
                return currentRoot.ReplaceNode(current, replacement);
            });
        }

        // ---- RemoveListenerCall -----------------------------------------------------------------

        // Splices the ONE listener call out — a chained sibling statement is untouched; the SOLE
        // call in a single-statement closure collapses the closure to a bare `.Component<T>()`
        // (ConfigureLambdaArgumentToRemove, reused verbatim from RemoveComponentField's own
        // sole-link collapse).
        private static void ResolveRemoveListenerCall(
            CompilationUnitSyntax root,
            RemoveListenerCall edit,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var invocation = FindListenerCallInvocation(root, edit.CallSpan);

            allTargets.Add(invocation);
            appliers.Add(currentRoot =>
            {
                var current = currentRoot.GetCurrentNode(invocation);
                if (current == null)
                {
                    // Already gone — a same-batch removal of the same call, or its enclosing
                    // component/statement was itself removed earlier in this batch.
                    return currentRoot;
                }

                if (ConfigureLambdaArgumentToRemove(current) is { } soleArgument)
                {
                    var soleArgumentList = (ArgumentListSyntax)soleArgument.Parent!;
                    var withoutSoleArgument = soleArgumentList.WithArguments(soleArgumentList.Arguments.Remove(soleArgument));
                    return currentRoot.ReplaceNode(soleArgumentList, withoutSoleArgument);
                }

                if (current.Parent is ExpressionStatementSyntax statement && statement.Parent is BlockSyntax block)
                {
                    if (block.Statements.Count == 1 && block.Statements[0] == statement
                        && block.Parent is SimpleLambdaExpressionSyntax { Parent: ArgumentSyntax lambdaArgument })
                    {
                        var lambdaArgumentList = (ArgumentListSyntax)lambdaArgument.Parent!;
                        var withoutLambdaArgument = lambdaArgumentList.WithArguments(lambdaArgumentList.Arguments.Remove(lambdaArgument));
                        return currentRoot.ReplaceNode(lambdaArgumentList, withoutLambdaArgument);
                    }

                    return currentRoot.ReplaceNode(block, block.WithStatements(block.Statements.Remove(statement)));
                }

                throw Fail(current, $"Unsupported listener closure form for field '{edit.FieldKey}'.");
            });
        }
    }
}
