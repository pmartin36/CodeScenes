using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Reconcile
{
    // AppendComponentStatement resolution (b3-t1), PatchComponentField / IntroduceComponentField
    // resolution (b3-t2). Second partial-class file so the existing private helpers on
    // SourcePatchApplier (FindAnchorInvocation, BuildHandleDeclaration, IndentOf, BodyIndent, Fail)
    // are reused directly — no visibility changes, no duplication.
    public static partial class SourcePatchApplier
    {
        // ---- AppendComponentStatement -----------------------------------------------------------

        private static void ResolveAppendComponentStatement(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            AppendComponentStatement edit,
            Dictionary<string, SyntaxAnnotation> appendAnnotations,
            Dictionary<string, SyntaxAnnotation> lastSiblingByParent,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            if (appendAnnotations.ContainsKey(edit.Anchor))
            {
                // SAME-BATCH owner (§13): owner is an AppendStatement inserted this batch, so it has
                // no anchor in the ORIGINAL source yet. Relay placement via the owner's (or previous
                // same-batch sibling's) annotation instead of FindAnchorInvocation. Mirrors
                // ResolveAppendStatement's same-batch branch (:357-380).
                var receiver = edit.OwnerHandle
                    ?? throw Fail(root, $"AppendComponentStatement '{edit.ComponentLogicalId}' targets same-batch owner '{edit.Anchor}' but has no OwnerHandle.");

                var anchorAnnotation = lastSiblingByParent.TryGetValue(edit.Anchor, out var siblingAnnotation)
                    ? siblingAnnotation
                    : appendAnnotations[edit.Anchor];

                var ownAnnotation = new SyntaxAnnotation();
                lastSiblingByParent[edit.Anchor] = ownAnnotation;

                var indent = BodyIndent(root);
                var newStmt = ParseComponentStatement(edit, receiver, indent)
                    .WithAdditionalAnnotations(ownAnnotation);

                appliers.Add(currentRoot =>
                {
                    var ownerNode = currentRoot.GetAnnotatedNodes(anchorAnnotation).Single();
                    return currentRoot.InsertNodesAfter(ownerNode, new[] { newStmt });
                });
                return;
            }

            var invocation = FindAnchorInvocation(root, anchors, edit.Anchor);
            var ownerStatement = invocation.FirstAncestorOrSelf<StatementSyntax>()
                ?? throw Fail(invocation, $"Anchor '{edit.Anchor}' is not inside a statement.");

            if (edit.IntroduceOwnerHandle)
            {
                if (ownerStatement is not ExpressionStatementSyntax ownerExprStatement)
                {
                    throw Fail(ownerStatement, $"Anchor '{edit.Anchor}' already declares a handle; cannot introduce '{edit.OwnerHandle}' again.");
                }

                var receiver = edit.OwnerHandle
                    ?? throw Fail(ownerStatement, $"Anchor '{edit.Anchor}' has no owner handle to introduce.");

                var declaration = BuildHandleDeclaration(ownerExprStatement, receiver);
                var annotation = new SyntaxAnnotation();
                var annotatedDeclaration = declaration.WithAdditionalAnnotations(annotation);

                var firstComponentAnnotation = new SyntaxAnnotation();
                var newStmt = ParseComponentStatement(edit, receiver, IndentOf(ownerStatement))
                    .WithAdditionalAnnotations(firstComponentAnnotation);

                // Seed the same-batch dictionaries so any subsequent component for this owner
                // (IntroduceOwnerHandle=false, same OwnerHandle/Anchor) is sequenced by the
                // top-of-method same-batch branch (:29-55), mirroring §13.
                appendAnnotations[edit.Anchor] = annotation;
                lastSiblingByParent[edit.Anchor] = firstComponentAnnotation;

                allTargets.Add(ownerStatement);
                appliers.Add(currentRoot =>
                {
                    var currentOwner = currentRoot.GetCurrentNode(ownerStatement)!;
                    currentRoot = currentRoot.ReplaceNode(currentOwner, annotatedDeclaration);
                    var declaredNode = currentRoot.GetAnnotatedNodes(annotation).Single();
                    return currentRoot.InsertNodesAfter(declaredNode, new[] { newStmt });
                });
                return;
            }

            if (ownerStatement is not LocalDeclarationStatementSyntax ownerLocal || ownerLocal.Declaration.Variables.Count != 1)
            {
                throw Fail(ownerStatement, $"Anchor '{edit.Anchor}' has no handle variable; component attach is not expressible.");
            }

            var existingReceiver = ownerLocal.Declaration.Variables[0].Identifier.Text;
            var componentStmt = ParseComponentStatement(edit, existingReceiver, IndentOf(ownerStatement));

            allTargets.Add(ownerStatement);
            appliers.Add(currentRoot =>
            {
                var currentOwner = currentRoot.GetCurrentNode(ownerStatement)!;
                return currentRoot.InsertNodesAfter(currentOwner, new[] { componentStmt });
            });
        }

        private static StatementSyntax ParseComponentStatement(AppendComponentStatement edit, string receiver, string indent)
        {
            var text = BuildComponentStatementText(edit, receiver);

            return SyntaxFactory.ParseStatement(text)
                .WithLeadingTrivia(SyntaxFactory.Whitespace(indent))
                .WithTrailingTrivia(SyntaxFactory.EndOfLine("\n"));
        }

        private static string BuildComponentStatementText(AppendComponentStatement edit, string receiver)
        {
            var call = $"{receiver}.Component<{edit.TypeFullName}>";

            if (edit.Fields.Count == 0)
            {
                return $"{call}();";
            }

            if (edit.Fields.Count == 1)
            {
                var (key, value) = edit.Fields[0];
                return $"{call}(c => c.Set({SourceExpr.StringLiteral(key)}, {SourceExpr.ValueNodeLiteral(value)}));";
            }

            var sets = string.Join(" ", edit.Fields.Select(kv =>
                $"c.Set({SourceExpr.StringLiteral(kv.Key)}, {SourceExpr.ValueNodeLiteral(kv.Value)});"));
            return $"{call}(c => {{ {sets} }});";
        }

        // ---- PatchComponentField (b3-t2) ---------------------------------------------------

        private static void ResolvePatchComponentField(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            PatchComponentField edit,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var textSpan = TextSpan.FromBounds(edit.ValueSpan.Start, edit.ValueSpan.Start + edit.ValueSpan.Length);
            var target = root.FindNode(textSpan, getInnermostNodeForTie: true);

            if (target is not ExpressionSyntax || target.Span != textSpan)
            {
                throw Fail(root, $"Could not resolve value span for component field patch on '{edit.Anchor}'.");
            }

            allTargets.Add(target);
            appliers.Add(currentRoot =>
            {
                var current = currentRoot.GetCurrentNode(target)!;
                var replacement = SyntaxFactory.ParseExpression(edit.NewExpr).WithTriviaFrom(current);
                return currentRoot.ReplaceNode(current, replacement);
            });
        }

        // ---- IntroduceComponentField (b3-t2) -----------------------------------------------

        private static void ResolveIntroduceComponentField(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            IntroduceComponentField edit,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var invocation = FindComponentInvocation(root, anchors, edit.Anchor);
            var arguments = invocation.ArgumentList.Arguments;

            if (arguments.Count == 0)
            {
                allTargets.Add(invocation);
                appliers.Add(currentRoot =>
                {
                    var current = (InvocationExpressionSyntax)currentRoot.GetCurrentNode(invocation)!;
                    var lambdaText = $"c => {BuildSetCallText("c", edit.FieldKey, edit.Value)}";
                    var lambdaArg = SyntaxFactory.Argument(SyntaxFactory.ParseExpression(lambdaText));
                    var newArgList = SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(lambdaArg));
                    return currentRoot.ReplaceNode(current, current.WithArgumentList(newArgList));
                });
                return;
            }

            if (arguments.Count != 1 || arguments[0].Expression is not SimpleLambdaExpressionSyntax lambda)
            {
                throw Fail(invocation, $"Unsupported component closure form for anchor '{edit.Anchor}'; expected a lambda like `c => ...`.");
            }

            var receiver = lambda.Parameter.Identifier.Text;

            allTargets.Add(lambda);
            appliers.Add(currentRoot =>
            {
                var currentLambda = (SimpleLambdaExpressionSyntax)currentRoot.GetCurrentNode(lambda)!;

                if (currentLambda.Body is BlockSyntax block)
                {
                    var indent = block.Statements.Count > 0 ? IndentOf(block.Statements[0]) : IndentOf(block) + "    ";
                    var newStatement = SyntaxFactory.ParseStatement($"{BuildSetCallText(receiver, edit.FieldKey, edit.Value)};")
                        .WithLeadingTrivia(SyntaxFactory.Whitespace(indent))
                        .WithTrailingTrivia(SyntaxFactory.EndOfLine("\n"));
                    var newBlock = block.AddStatements(newStatement);
                    return currentRoot.ReplaceNode(currentLambda, currentLambda.WithBody(newBlock));
                }

                var exprBody = (ExpressionSyntax)currentLambda.Body;
                var originalText = exprBody.WithoutTrivia().ToFullString();
                var newSetText = BuildSetCallText(receiver, edit.FieldKey, edit.Value);
                var blockText = $"{{ {originalText}; {newSetText}; }}";
                var newLambda = (SimpleLambdaExpressionSyntax)SyntaxFactory.ParseExpression($"{receiver} => {blockText}")
                    .WithTriviaFrom(currentLambda);

                return currentRoot.ReplaceNode(currentLambda, newLambda);
            });
        }

        private static string BuildSetCallText(string receiver, string fieldKey, ValueNode value)
        {
            return $"{receiver}.Set({SourceExpr.StringLiteral(fieldKey)}, {SourceExpr.ValueNodeLiteral(value)})";
        }

        // ---- Component-aware anchor resolution (b3-t2) ---------------------------------------

        // b3-t3 folded the component-dot fallback into the shared FindAnchorInvocation
        // (SourcePatchApplier.cs), which now resolves both GameObject and component anchors.
        // Kept as a thin delegate so this file's one caller (ResolveIntroduceComponentField)
        // doesn't need to change.
        private static InvocationExpressionSyntax FindComponentInvocation(
            SyntaxNode root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            string anchorId)
        {
            return FindAnchorInvocation(root, anchors, anchorId);
        }
    }
}
