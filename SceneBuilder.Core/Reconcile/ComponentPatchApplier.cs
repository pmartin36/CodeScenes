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

                var sameBatchAnchorAnnotation = lastSiblingByParent.TryGetValue(edit.Anchor, out var siblingAnnotation)
                    ? siblingAnnotation
                    : appendAnnotations[edit.Anchor];

                var ownAnnotation = new SyntaxAnnotation();
                lastSiblingByParent[edit.Anchor] = ownAnnotation;

                var indent = BodyIndent(root);
                var newStmt = ParseComponentStatement(edit, receiver, indent)
                    .WithAdditionalAnnotations(ownAnnotation);

                appliers.Add(currentRoot =>
                {
                    var ownerNode = currentRoot.GetAnnotatedNodes(sameBatchAnchorAnnotation).Single();
                    return currentRoot.InsertNodesAfter(ownerNode, new[] { newStmt });
                });
                return;
            }

            var invocation = FindAnchorInvocation(root, anchors, edit.Anchor);
            var ownerStatement = invocation.FirstAncestorOrSelf<StatementSyntax>()
                ?? throw Fail(invocation, $"Anchor '{edit.Anchor}' is not inside a statement.");

            // The owner's receiver: an authored `var`, or the handle the handle-introduction pre-pass
            // has already queued a rewrite for (so `ownerStatement` is still an ExpressionStatement
            // HERE, in the original tree, but will be a declaration by the time this applier runs).
            var existingReceiver = edit.OwnerHandle
                ?? (ownerStatement is LocalDeclarationStatementSyntax ownerLocal && ownerLocal.Declaration.Variables.Count == 1
                    ? ownerLocal.Declaration.Variables[0].Identifier.Text
                    : throw Fail(ownerStatement, $"Anchor '{edit.Anchor}' has no handle variable; component attach is not expressible."));

            var componentStmt = ParseComponentStatement(edit, existingReceiver, IndentOf(ownerStatement));

            // A component list is ORDERED, so this goes through the same placement path as every other
            // append (see StatementPlacement.cs). Inserting it "right after the owner" instead put a
            // new component ahead of the ones already attached, and the next sync silently re-Reordered
            // it — the component-list instance of BUG B.
            var ownerBlock = EnclosingBlock(ownerStatement)
                ?? throw Fail(ownerStatement, $"Anchor '{edit.Anchor}' statement is not inside a block.");

            allTargets.Add(ownerBlock);
            appliers.Add(currentRoot => PlaceNewStatement(
                currentRoot,
                ownerBlock,
                componentStmt,
                existingReceiver,
                PeerKind.Component,
                edit.NewSiblingIndex));
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
            // b4-t1: FitSize/SurfaceSnap always render as their dedicated fluent call — never the
            // generic .Component<T> form, which would fail to stamp TransformData.DrivenChannels
            // on re-parse and defeat b3 driven-suppression.
            if (SpatialComponentSource.IsSpatial(edit.TypeFullName))
            {
                return SpatialComponentSource.RenderStatement(receiver, edit.TypeFullName, edit.Fields, edit.FieldExpressions);
            }

            var call = $"{receiver}.Component<{edit.TypeFullName}>";

            if (edit.Fields.Count == 0)
            {
                return $"{call}();";
            }

            // b4-t3: FieldExpressions carries a pre-rendered override for a field SourceExpr
            // cannot format context-free (an ObjectRef handle argument) — consulted first, with
            // ValueNodeLiteral as the unchanged fallback for every other field.
            string Render(string key, ValueNode value) =>
                edit.FieldExpressions != null && edit.FieldExpressions.TryGetValue(key, out var expr)
                    ? expr
                    : SourceExpr.ValueNodeLiteral(value);

            if (edit.Fields.Count == 1)
            {
                var (key, value) = edit.Fields[0];
                return $"{call}(c => c.Set({SourceExpr.StringLiteral(key)}, {Render(key, value)}));";
            }

            var sets = string.Join(" ", edit.Fields.Select(kv =>
                $"c.Set({SourceExpr.StringLiteral(kv.Key)}, {Render(kv.Key, kv.Value)});"));
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

            // Two span shapes: a value-only span (`2.3457f`) replaces just the expression; a
            // whole-argument span (`down: true`, used when a spatial enum-axis flip rewrites the
            // KEYWORD as well as the value — NewExpr is then `up: true`) replaces the whole argument.
            var isArgument = target is ArgumentSyntax && target.Span == textSpan;
            if (!isArgument && (target is not ExpressionSyntax || target.Span != textSpan))
            {
                throw Fail(root, $"Could not resolve value span for component field patch on '{edit.Anchor}'.");
            }

            allTargets.Add(target);
            appliers.Add(currentRoot =>
            {
                var current = currentRoot.GetCurrentNode(target)!;
                SyntaxNode replacement = isArgument
                    ? SyntaxFactory.ParseArgumentList("(" + edit.NewExpr + ")").Arguments[0].WithTriviaFrom(current)
                    : SyntaxFactory.ParseExpression(edit.NewExpr).WithTriviaFrom(current);
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

            // b4-t3: pre-rendered ObjectRef override (mirrors AppendComponentStatement.FieldExpressions'
            // pattern) — SourceExpr.ValueNodeLiteral has no ObjectRef arm and stays pure/context-free,
            // so anything context-dependent or side-effecting is pre-rendered at EMIT time.
            var valueExpr = edit.NewExpr ?? SourceExpr.ValueNodeLiteral(edit.Value);

            // b7-t1 fix: a dedicated `.FitSize(...)/.SurfaceSnap(...)` call has ALL-named-argument shape
            // (SpatialComponentSource.RenderArguments), never the generic `c => ...` closure the
            // fallback below expects — introducing a previously-absent field (e.g. toggling a
            // SurfaceSnap flag from unset->true) must append a new named argument in that SAME
            // "key: value" style, not throw as an unsupported closure form.
            if (IsSpatialComponentAnchor(edit.Anchor))
            {
                allTargets.Add(invocation);
                appliers.Add(currentRoot =>
                {
                    var current = (InvocationExpressionSyntax)currentRoot.GetCurrentNode(invocation)!;
                    var existingArgsText = string.Join(", ", current.ArgumentList.Arguments.Select(a => a.ToString()));
                    var newArgText = SpatialComponentSource.RenderKeyValue(edit.FieldKey, edit.Value, valueExpr);
                    var combined = existingArgsText.Length > 0 ? $"{existingArgsText}, {newArgText}" : newArgText;
                    var newArgList = SyntaxFactory.ParseArgumentList($"({combined})");
                    return currentRoot.ReplaceNode(current, current.WithArgumentList(newArgList));
                });
                return;
            }

            if (arguments.Count == 0)
            {
                allTargets.Add(invocation);
                appliers.Add(currentRoot =>
                {
                    var current = (InvocationExpressionSyntax)currentRoot.GetCurrentNode(invocation)!;
                    var lambdaText = $"c => {BuildSetCallText("c", edit.FieldKey, valueExpr)}";
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
                    var newStatement = SyntaxFactory.ParseStatement($"{BuildSetCallText(receiver, edit.FieldKey, valueExpr)};")
                        .WithLeadingTrivia(SyntaxFactory.Whitespace(indent))
                        .WithTrailingTrivia(SyntaxFactory.EndOfLine("\n"));
                    var newBlock = block.AddStatements(newStatement);
                    return currentRoot.ReplaceNode(currentLambda, currentLambda.WithBody(newBlock));
                }

                var exprBody = (ExpressionSyntax)currentLambda.Body;
                var originalText = exprBody.WithoutTrivia().ToFullString();
                var newSetText = BuildSetCallText(receiver, edit.FieldKey, valueExpr);
                var blockText = $"{{ {originalText}; {newSetText}; }}";
                var newLambda = (SimpleLambdaExpressionSyntax)SyntaxFactory.ParseExpression($"{receiver} => {blockText}")
                    .WithTriviaFrom(currentLambda);

                return currentRoot.ReplaceNode(currentLambda, newLambda);
            });
        }

        private static string BuildSetCallText(string receiver, string fieldKey, string valueExpr)
        {
            return $"{receiver}.Set({SourceExpr.StringLiteral(fieldKey)}, {valueExpr})";
        }

        // A component LogicalId is always synthesized "{ownerLogicalId}/{TypeFullName}#{ordinal}"
        // (BuilderParser.AssignComponentLogicalIds) — the one place an anchor string reliably encodes
        // its component's type without threading TypeFullName onto IntroduceComponentField itself.
        private static bool IsSpatialComponentAnchor(string anchor)
        {
            var hashIndex = anchor.LastIndexOf('#');
            if (hashIndex <= 0)
            {
                return false;
            }

            var slashIndex = anchor.LastIndexOf('/', hashIndex - 1);
            if (slashIndex < 0)
            {
                return false;
            }

            var typeFullName = anchor.Substring(slashIndex + 1, hashIndex - slashIndex - 1);
            return SpatialComponentSource.IsSpatial(typeFullName);
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
