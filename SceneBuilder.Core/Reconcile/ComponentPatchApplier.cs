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

                // The natural (emission-order) position is right after the owner's own statement or
                // the previous same-batch sibling appended onto it (sameBatchAnchorAnnotation, as
                // before). A field on this component can ALSO name a same-batch handle declared
                // LATER than that natural position (a forward reference) — the declare-before-use
                // floor (StatementPlacement's MinIndexAfterNamedDeclarations) is layered on top as a
                // LOWER BOUND, not a replacement, so an ordinary multi-component append (no forward
                // reference) keeps its natural chained order untouched. A blind switch to
                // PlaceNewStatement/PeerKind.Component here would DISCARD that natural order instead
                // of bounding it — SpatialComponentSource's dedicated `.FitSize(...)`/`.AlignTo(...)`
                // calls are not "Component" peers to PlacementIndex's peer scan, so two same-batch
                // spatial appends onto one owner would collapse to the same insertion point and lose
                // their canonical FitSize-before-AlignTo order.
                var (buildMethod, _) = FindBuildMethod(root);
                var buildBody = buildMethod.Body!;
                allTargets.Add(buildBody);

                appliers.Add(currentRoot =>
                {
                    var ownerNode = (StatementSyntax)currentRoot.GetAnnotatedNodes(sameBatchAnchorAnnotation).Single();
                    var block = (BlockSyntax)currentRoot.GetCurrentNode(buildBody)!;
                    var statements = block.Statements;
                    var ownerIndex = statements.IndexOf(ownerNode);
                    var naturalIndex = ownerIndex >= 0 ? ownerIndex + 1 : statements.Count;
                    var target = Math.Max(naturalIndex, MinIndexAfterNamedDeclarations(statements, newStmt));
                    return currentRoot.ReplaceNode(block, block.WithStatements(statements.Insert(target, newStmt)));
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
            // FitSize/AlignTo always render as their dedicated fluent call — never the
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
            List<Func<SyntaxNode, SyntaxNode>> appliers,
            // spec 54: positive-proof whitelist. A component member-set whose typed selector names a
            // member NOT proven safe is downgraded to string-key form in the SAME edit that patches
            // the value — a typed selector over such a member never compiles (CS0122/CS1061).
            SafeMemberIndex? safeMembers = null)
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

            // The enclosing `.Set(...)` call, if the patched value sits inside one. When its first
            // argument is a typed member selector (`sel => sel.M`) naming an inaccessible (T, M), the
            // downgrade rewrites arg0 to the string literal `"M"` alongside the value in ONE
            // ReplaceNode over the invocation — two separate ReplaceNodes touching the same call would
            // leave the second's tracked node stale.
            var setInvocation = target.FirstAncestorOrSelf<InvocationExpressionSyntax>(inv =>
                inv.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Set" });

            if (setInvocation != null
                && setInvocation.ArgumentList.Arguments.Count > 0
                && setInvocation.ArgumentList.Arguments[0].Expression is SimpleLambdaExpressionSyntax
                    { Body: MemberAccessExpressionSyntax selectorMember }
                && safeMembers != null
                && ComponentTargetResolution.TryParseLogicalId(edit.Anchor, out _, out var typeFullName, out _)
                && !safeMembers.IsSafe(typeFullName, selectorMember.Name.Identifier.Text))
            {
                var memberName = selectorMember.Name.Identifier.Text;

                allTargets.Add(setInvocation);
                appliers.Add(currentRoot =>
                {
                    var current = (InvocationExpressionSyntax)currentRoot.GetCurrentNode(setInvocation)!;
                    var newArgList = SyntaxFactory.ParseArgumentList(
                        $"({SourceExpr.StringLiteral(memberName)}, {edit.NewExpr})");
                    return currentRoot.ReplaceNode(current, current.WithArgumentList(newArgList));
                });
                return;
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

        // ---- DowngradeComponentSelector (spec 54 diff-independent self-heal) -----------------

        // Locates the `.Set(sel => sel.MemberName, value)` call inside the component's own
        // invocation (resolved by the component anchor, not a value span — the field's VALUE has
        // not changed, so no ValueSpan/PatchComponentField reaches this edit) and rewrites ONLY
        // the selector argument to its string-key form. Reuses the identical arg0-only rewrite
        // shape ResolvePatchComponentField's downgrade arm uses, but never touches arg1 — the
        // heal must not itself reformat a value that never moved.
        private static void ResolveDowngradeComponentSelector(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            DowngradeComponentSelector edit,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var componentInvocation = FindComponentInvocation(root, anchors, edit.Anchor);

            var setInvocation = componentInvocation.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .FirstOrDefault(inv =>
                    inv.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Set" }
                    && inv.ArgumentList.Arguments.Count > 0
                    && inv.ArgumentList.Arguments[0].Expression is SimpleLambdaExpressionSyntax
                        { Body: MemberAccessExpressionSyntax selectorMember }
                    && selectorMember.Name.Identifier.Text == edit.MemberName)
                ?? throw Fail(componentInvocation, $"Could not resolve typed selector '{edit.MemberName}' on '{edit.Anchor}' to downgrade.");

            allTargets.Add(setInvocation);
            appliers.Add(currentRoot =>
            {
                var current = (InvocationExpressionSyntax)currentRoot.GetCurrentNode(setInvocation)!;
                var arg0 = current.ArgumentList.Arguments[0];
                var newArg0 = SyntaxFactory.Argument(SyntaxFactory.ParseExpression(SourceExpr.StringLiteral(edit.MemberName)))
                    .WithTriviaFrom(arg0);
                var newArgList = current.ArgumentList.WithArguments(current.ArgumentList.Arguments.Replace(arg0, newArg0));
                return currentRoot.ReplaceNode(current.ArgumentList, newArgList);
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

            // A dedicated `.FitSize(...)/.AlignTo(...)` call has ALL-named-argument shape
            // (SpatialComponentSource.RenderArguments), never the generic `c => ...` closure the
            // fallback below expects — introducing a previously-absent field (e.g. toggling a
            // AlignTo axis from unset->AbutMax) must append a new named argument in that SAME
            // "key: value" style, not throw as an unsupported closure form.
            if (IsSpatialComponentAnchor(edit.Anchor))
            {
                allTargets.Add(invocation);
                appliers.Add(currentRoot =>
                {
                    var current = (InvocationExpressionSyntax)currentRoot.GetCurrentNode(invocation)!;
                    var existingArgsText = string.Join(", ", current.ArgumentList.Arguments.Select(a => a.ToString()));

                    // An AlignTo axis introduce carries its WHOLE folded argument
                    // (`z: AxisAlign.AbutMax.Offset(0.75f)`) pre-rendered in NewExpr — the mode +
                    // offset pair renders as one keyword, which RenderKeyValue (single field) cannot
                    // fold. Append it verbatim; every other spatial field keeps the per-field render.
                    var newArgText = edit.NewExpr != null
                        && SpatialComponents.TryAlignAxisFromModeField(edit.FieldKey, out _)
                            ? edit.NewExpr
                            : SpatialComponentSource.RenderKeyValue(edit.FieldKey, edit.Value, valueExpr);
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

            // Companion applier: the fold above just grew the closure to NAME edit.NewExpr's handle
            // (e.g. `door`) — if that handle's own declaration sits at/after this component statement,
            // the fold just wrote a forward reference (CS0841). Repair declare-before-use by hoisting
            // the handle's declaration above the component through StatementPlacement's ceiling
            // (symmetric to the floor every OTHER placement already inherits), never by moving the
            // component itself. Looped because ONE component can fold several forward-referencing
            // fields in a batch (spec:389 case 7) and each hoist can shift the component's own index.
            appliers.Add(currentRoot =>
            {
                var current = (InvocationExpressionSyntax?)currentRoot.GetCurrentNode(invocation);
                if (current == null)
                {
                    return currentRoot;
                }

                while (true)
                {
                    var stmt = current!.FirstAncestorOrSelf<StatementSyntax>();
                    if (stmt?.Parent is not BlockSyntax block)
                    {
                        break;
                    }

                    var ceiling = block.Statements.IndexOf(stmt);
                    var hoisted = block.Statements;
                    foreach (var handle in NamedHandles(stmt))
                    {
                        hoisted = HoistDeclarationBeforeCeiling(hoisted, handle, ceiling);
                    }

                    if (hoisted == block.Statements)
                    {
                        break;
                    }

                    currentRoot = currentRoot.ReplaceNode(block, block.WithStatements(hoisted));
                    current = (InvocationExpressionSyntax?)currentRoot.GetCurrentNode(invocation);
                    if (current == null)
                    {
                        break;
                    }
                }

                return currentRoot;
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
