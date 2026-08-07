using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Reconcile
{
    // AppendComponentStatement resolution, PatchComponentField / IntroduceComponentField
    // resolution. Second partial-class file so the existing private helpers on
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
            List<Func<SyntaxNode, SyntaxNode>> appliers,
            // Threaded down to the RenderComponentClosureArgs fallback — see SourcePatchApplier
            // .Apply's doc comment on assetCatalog.
            AssetCatalog? assetCatalog = null)
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
                var newStmt = ParseComponentStatement(edit, receiver, indent, assetCatalog)
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

            var componentStmt = ParseComponentStatement(edit, existingReceiver, IndentOf(ownerStatement), assetCatalog);

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

        private static StatementSyntax ParseComponentStatement(
            AppendComponentStatement edit, string receiver, string indent, AssetCatalog? assetCatalog = null)
        {
            var text = BuildComponentStatementText(edit, receiver, assetCatalog);

            return SyntaxFactory.ParseStatement(text)
                .WithLeadingTrivia(SyntaxFactory.Whitespace(indent))
                .WithTrailingTrivia(SyntaxFactory.EndOfLine("\n"));
        }

        private static string BuildComponentStatementText(
            AppendComponentStatement edit, string receiver, AssetCatalog? assetCatalog = null)
        {
            // FitSize/SurfaceSnap always render as their dedicated fluent call — never the
            // generic .Component<T> form, which would fail to stamp TransformData.DrivenChannels
            // on re-parse and defeat driven-suppression.
            if (SpatialComponentSource.IsSpatial(edit.TypeFullName))
            {
                return SpatialComponentSource.RenderStatement(receiver, edit.TypeFullName, edit.Fields, edit.FieldExpressions);
            }

            var call = $"{receiver}.Component<{edit.TypeFullName}>";
            return $"{call}{RenderComponentClosureArgs(edit.Fields, edit.FieldExpressions, assetCatalog)};";
        }

        // Extracted from BuildComponentStatementText so AppendInstanceAddComponent
        // (SourcePatchApplier.Instances.cs) renders its `.AddComponent<T>(...)` closure
        // byte-identically to `.Component<T>(...)`'s — one renderer, two call sites.
        // Returns the PARENTHESIZED argument list: `()`, `(c => c.Set("k", v))`, or
        // `(c => { c.Set(...); ... })`.
        private static string RenderComponentClosureArgs(
            FieldMap fields, IReadOnlyDictionary<string, string>? fieldExpressions, AssetCatalog? assetCatalog = null)
        {
            if (fields.Count == 0)
            {
                return "()";
            }

            // FieldExpressions carries a pre-rendered override for a field SourceExpr
            // cannot format context-free (an ObjectRef handle argument) — consulted first, with
            // ValueNodeLiteral as the unchanged fallback for every other field.
            // The fallback must ALSO carry the catalog — a field never pre-rendered into
            // FieldExpressions (e.g. a List<AssetRef> like `m_Materials`, whose per-element catalog
            // lookup only ValueNodeLiteral's own recursive List arm performs) still needs it to
            // resolve to the typed `Assets.<...>` chain instead of the `Asset("path")` fallback.
            string Render(string key, ValueNode value) =>
                fieldExpressions != null && fieldExpressions.TryGetValue(key, out var expr)
                    ? expr
                    : SourceExpr.ValueNodeLiteral(value, assetCatalog);

            if (fields.Count == 1)
            {
                var (key, value) = fields[0];
                return $"(c => c.Set({SourceExpr.StringLiteral(key)}, {Render(key, value)}))";
            }

            var sets = string.Join(" ", fields.Select(kv =>
                $"c.Set({SourceExpr.StringLiteral(kv.Key)}, {Render(kv.Key, kv.Value)});"));
            return $"(c => {{ {sets} }})";
        }

        // ---- PatchComponentField -----------------------------------------------------------

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

        // ---- IntroduceComponentField ---------------------------------------------------------

        private static void ResolveIntroduceComponentField(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            IntroduceComponentField edit,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers,
            // Threaded down to the ValueNodeLiteral fallback below — see SourcePatchApplier
            // .Apply's doc comment on assetCatalog.
            AssetCatalog? assetCatalog = null)
        {
            var invocation = FindComponentInvocation(root, anchors, edit.Anchor);
            var arguments = invocation.ArgumentList.Arguments;

            // Pre-rendered ObjectRef override (mirrors AppendComponentStatement.FieldExpressions'
            // pattern) — SourceExpr.ValueNodeLiteral has no ObjectRef arm and stays pure/context-free,
            // so anything context-dependent or side-effecting is pre-rendered at EMIT time.
            // The fallback must ALSO carry the catalog — see RenderComponentClosureArgs's
            // identical comment.
            var valueExpr = edit.NewExpr ?? SourceExpr.ValueNodeLiteral(edit.Value, assetCatalog);

            // A dedicated `.FitSize(...)/.SurfaceSnap(...)` call has ALL-named-argument shape
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

            if (arguments.Count != 0
                && (arguments.Count != 1 || arguments[0].Expression is not SimpleLambdaExpressionSyntax))
            {
                throw Fail(invocation, $"Unsupported component closure form for anchor '{edit.Anchor}'; expected a lambda like `c => ...`.");
            }

            // EVERY IntroduceComponentField on one component rewrites the SAME `.Component<T>(...)`
            // invocation, and a batch routinely carries several of them — the user sets two fields on
            // one component in a single editor action. So the invocation is the only node worth
            // tracking, and the closure's shape MUST be read from the CURRENT root inside the applier:
            // by the time the second edit runs, an empty argument list has grown a lambda and an
            // expression-bodied lambda has become a block. Branching on the ORIGINAL tree's shape
            // instead made the second edit either dereference a lambda the first edit had replaced
            // (NullReferenceException, which wedged every subsequent sync) or overwrite the first
            // edit's whole argument list (the zero-arg form's silent dropped edit).
            allTargets.Add(invocation);
            appliers.Add(currentRoot =>
            {
                var current = (InvocationExpressionSyntax)currentRoot.GetCurrentNode(invocation)!;
                var currentArguments = current.ArgumentList.Arguments;

                if (currentArguments.Count == 0)
                {
                    var lambdaText = $"c => {BuildSetCallText("c", edit.FieldKey, valueExpr)}";
                    var lambdaArg = SyntaxFactory.Argument(SyntaxFactory.ParseExpression(lambdaText));
                    var newArgList = SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(lambdaArg));
                    return currentRoot.ReplaceNode(current, current.WithArgumentList(newArgList));
                }

                if (currentArguments.Count != 1 || currentArguments[0].Expression is not SimpleLambdaExpressionSyntax currentLambda)
                {
                    throw Fail(current, $"Unsupported component closure form for anchor '{edit.Anchor}'; expected a lambda like `c => ...`.");
                }

                var receiver = currentLambda.Parameter.Identifier.Text;
                var newSetText = BuildSetCallText(receiver, edit.FieldKey, valueExpr);

                var newBody = currentLambda.Body is BlockSyntax block
                    ? AppendToClosureBlock(block, newSetText)
                    : ExpressionBodyToBlock((ExpressionSyntax)currentLambda.Body, newSetText);

                return currentRoot.ReplaceNode(currentLambda, currentLambda.WithBody(newBody));
            });
        }

        // Appends one `c.Set(...)` statement to an existing closure block, matching the block's own
        // layout: a one-line `{ ...; }` closure stays on one line, a multi-line block keeps its
        // per-statement indentation.
        private static BlockSyntax AppendToClosureBlock(BlockSyntax block, string newSetText)
        {
            var statement = SyntaxFactory.ParseStatement($"{newSetText};");

            if (block.ToFullString().Contains('\n'))
            {
                var indent = block.Statements.Count > 0 ? IndentOf(block.Statements[0]) : IndentOf(block) + "    ";
                return block.AddStatements(statement
                    .WithLeadingTrivia(SyntaxFactory.Whitespace(indent))
                    .WithTrailingTrivia(SyntaxFactory.EndOfLine("\n")));
            }

            return block.AddStatements(statement.WithTrailingTrivia(SyntaxFactory.Space));
        }

        // Rewrites `c => c.Set(a)` into `c => { c.Set(a); c.Set(b); }` REUSING the authored body's
        // syntax node rather than re-parsing its text, so a PatchComponentField tracking a value node
        // inside that body in the same batch still resolves after this rewrite.
        private static BlockSyntax ExpressionBodyToBlock(ExpressionSyntax body, string newSetText)
        {
            var existing = SyntaxFactory.ExpressionStatement(body.WithoutTrivia())
                .WithTrailingTrivia(SyntaxFactory.Space);
            var added = SyntaxFactory.ParseStatement($"{newSetText};")
                .WithTrailingTrivia(SyntaxFactory.Space);

            return SyntaxFactory.Block(existing, added)
                .WithOpenBraceToken(SyntaxFactory.Token(SyntaxKind.OpenBraceToken)
                    .WithTrailingTrivia(SyntaxFactory.Space));
        }

        private static string BuildSetCallText(string receiver, string fieldKey, string valueExpr)
        {
            return $"{receiver}.Set({SourceExpr.StringLiteral(fieldKey)}, {valueExpr})";
        }

        // ---- RemoveComponentField --------------------------------------------------------------

        // Resolves the `.Set(...)` invocation from ValueSpan against the ORIGINAL tree; the removal
        // SHAPE (delete the sole argument vs. delete one statement from a block) is decided INSIDE
        // the applier, from the CURRENT root, never here — a same-batch IntroduceComponentField on
        // the SAME component can rewrite an expression-bodied closure into a block before this
        // applier runs. Reuses SourcePatchApplier.ConfigureLambdaArgumentToRemove
        // verbatim; do not write a second sole-link detector.
        private static void ResolveRemoveComponentField(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            RemoveComponentField edit,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var textSpan = TextSpan.FromBounds(edit.ValueSpan.Start, edit.ValueSpan.Start + edit.ValueSpan.Length);
            var target = root.FindNode(textSpan, getInnermostNodeForTie: true);

            var setInvocation = target.FirstAncestorOrSelf<InvocationExpressionSyntax>(inv =>
                    inv.Expression is MemberAccessExpressionSyntax ma && ma.Name.Identifier.Text == "Set")
                ?? throw Fail(root, $"Could not resolve the .Set(...) call for field '{edit.FieldKey}' on '{edit.Anchor}'.");

            allTargets.Add(setInvocation);
            appliers.Add(currentRoot =>
            {
                var current = currentRoot.GetCurrentNode(setInvocation);
                if (current == null)
                {
                    // Already gone — e.g. a same-batch removal on the SAME field, or the enclosing
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

                throw Fail(current, $"Unsupported component closure form for anchor '{edit.Anchor}'.");
            });
        }

        // A component LogicalId is always synthesized "{ownerLogicalId}/{TypeFullName}#{ordinal}"
        // (BuilderParser.AssignComponentLogicalIds) — the one place an anchor string reliably encodes
        // its component's type without threading TypeFullName onto IntroduceComponentField itself.
        private static bool IsSpatialComponentAnchor(string anchor) =>
            ComponentTargetResolution.TryParseLogicalId(anchor, out _, out var typeFullName, out _)
            && SpatialComponentSource.IsSpatial(typeFullName);

        // ---- Component-aware anchor resolution -------------------------------------------------

        // Folds the component-dot fallback into the shared FindAnchorInvocation
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
