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
    // Applies a SourcePatch's SourceEdits to builder .cs source via Roslyn syntax-node
    // replacement, preserving all unrelated trivia (comments, blank lines, formatting).
    public static partial class SourcePatchApplier
    {
        private static readonly string[] TransformPositionalArgs = { "pos", "rot", "scale" };

        public static string Apply(
            string source,
            SourcePatch patch,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            // b7-t2: catalogued AssetRef fields that were NOT pre-rendered into FieldExpressions at
            // emit time (e.g. a List<AssetRef> field, whose element-wise catalog lookup only
            // SourceExpr.ValueNodeLiteral's recursive List arm performs) must still resolve to their
            // typed `Assets.<...>` chain at APPLY time — the fallback render below was previously
            // calling ValueNodeLiteral with no catalog at all, silently downgrading every such field
            // to the `Asset("path")` string form. Optional/trailing so every pre-existing call site
            // stays green unchanged.
            AssetCatalog? assetCatalog = null,
            // spec 54: positive-proof whitelist. Threaded to BOTH emit sites (the component `.Set`
            // selector-downgrade arm AND the prefab-instance override render) -- the two share no
            // caller, so each must receive it independently. Optional/trailing, default null, so
            // every pre-existing call site stays green unchanged.
            SafeMemberIndex? safeMembers = null)
        {
            var tree = CSharpSyntaxTree.ParseText(source);
            var root = (CompilationUnitSyntax)tree.GetRoot();

            // Resolve every edit's target node(s) against the ORIGINAL (unmutated) tree first,
            // then compose all edits via TrackNodes so earlier edits don't invalidate later targets.
            var allTargets = new List<SyntaxNode>();
            var appliers = new List<Func<SyntaxNode, SyntaxNode>>();

            // One annotation per AppendStatement in this batch, keyed by NewLogicalId, so a
            // same-batch child can locate its parent's freshly-inserted statement even though the
            // Applier otherwise resolves every edit against the original unmutated tree.
            var appendAnnotations = new Dictionary<string, SyntaxAnnotation>();
            foreach (var appendEdit in patch.Edits.OfType<AppendStatement>())
            {
                appendAnnotations[appendEdit.NewLogicalId] = new SyntaxAnnotation();
            }

            // Tracks the most-recently-resolved same-batch sibling under each fresh parent, so
            // multiple children appended under one new-subtree parent preserve emission order.
            var lastSiblingByParent = new Dictionary<string, SyntaxAnnotation>();

            // A pos/rot/scale PatchArgument needs a `.Transform(...)` call to patch. When the
            // authored statement has NONE — the everyday case: the user drags an object authored as
            // a plain `scene.Add("X")` — the call must be INTRODUCED, exactly as an absent flag call
            // is (IntroduceFlagCall). Resolved HERE, once, before the main loop, because all of one
            // anchor's transform args must become a SINGLE `.Transform(pos: ..., rot: ...)` call; a
            // per-edit introduction would chain three separate `.Transform(...)` calls for a
            // pos+rot+scale batch, each clobbering the last.
            var introducedTransformEdits = ResolveTransformIntroductions(root, anchors, patch, allTargets, appliers);

            // Handle introductions are resolved HERE, before any edit that PLACES a statement, for
            // two reasons that make a per-resolver introduction structurally unsafe — see
            // ResolveHandleIntroductions.
            ResolveHandleIntroductions(root, anchors, patch, allTargets, appliers);

            // AppendInstanceOverride/AppendInstanceAddComponent/AppendInstanceRemoveComponent all
            // splice onto the SAME anchor's chain expression; folded here — once per anchor — for
            // the same reason transform args are folded above. See
            // SourcePatchApplier.Instances.cs's ResolveInstanceChainedCallAppends.
            var consumedChainedCallEdits = ResolveInstanceChainedCallAppends(root, anchors, patch, allTargets, appliers, safeMembers);

            foreach (var edit in patch.Edits)
            {
                switch (edit)
                {
                    case PatchArgument patchArgument:
                        // Already folded into an introduced `.Transform(...)` call above.
                        if (introducedTransformEdits.Contains(patchArgument))
                        {
                            break;
                        }

                        ResolvePatchArgument(root, anchors, patchArgument, allTargets, appliers);
                        break;
                    case MoveStatement moveStatement:
                        ResolveMoveStatement(root, anchors, moveStatement, allTargets, appliers);
                        break;
                    case ReorderStatement reorderStatement:
                        ResolveReorderStatement(root, anchors, reorderStatement, allTargets, appliers);
                        break;
                    case RemoveStatement removeStatement:
                        ResolveRemoveStatement(root, anchors, removeStatement, allTargets, appliers);
                        break;
                    case AppendStatement appendStatement:
                        ResolveAppendStatement(root, anchors, appendStatement, appendAnnotations, lastSiblingByParent, allTargets, appliers);
                        break;
                    case AppendComponentStatement appendComponentStatement:
                        ResolveAppendComponentStatement(root, anchors, appendComponentStatement, appendAnnotations, lastSiblingByParent, allTargets, appliers, assetCatalog);
                        break;
                    case PatchFlagArgument patchFlagArgument:
                        ResolvePatchFlagArgument(root, anchors, patchFlagArgument, allTargets, appliers);
                        break;
                    case IntroduceFlagCall introduceFlagCall:
                        ResolveIntroduceFlagCall(root, anchors, introduceFlagCall, allTargets, appliers);
                        break;
                    case RemoveFlagCall removeFlagCall:
                        ResolveRemoveFlagCall(root, anchors, removeFlagCall, allTargets, appliers);
                        break;
                    case IntroduceHandle:
                        // Fully resolved by the ResolveHandleIntroductions pre-pass above; nothing
                        // left to do in the main dispatch loop.
                        break;
                    case PatchComponentField patchComponentField:
                        ResolvePatchComponentField(root, anchors, patchComponentField, allTargets, appliers, safeMembers);
                        break;
                    case DowngradeComponentSelector downgradeComponentSelector:
                        ResolveDowngradeComponentSelector(root, anchors, downgradeComponentSelector, allTargets, appliers);
                        break;
                    case IntroduceComponentField introduceComponentField:
                        ResolveIntroduceComponentField(root, anchors, introduceComponentField, allTargets, appliers, assetCatalog);
                        break;
                    case RemoveComponentField removeComponentField:
                        ResolveRemoveComponentField(root, anchors, removeComponentField, allTargets, appliers);
                        break;
                    case AppendListenerCall appendListenerCall:
                        ResolveAppendListenerCall(root, anchors, appendListenerCall, allTargets, appliers);
                        break;
                    case PatchListenerCall patchListenerCall:
                        ResolvePatchListenerCall(root, patchListenerCall, allTargets, appliers);
                        break;
                    case RemoveListenerCall removeListenerCall:
                        ResolveRemoveListenerCall(root, removeListenerCall, allTargets, appliers);
                        break;
                    case AppendInstanceOverride or AppendInstanceAddComponent or AppendInstanceRemoveComponent
                        or AppendInstanceAddChild or AppendInstanceRemoveChild or AppendScopedOn:
                        // Already folded into a combined chained-call append (or a scoped-On
                        // append-into-existing-closure applier) above.
                        if (!consumedChainedCallEdits.Contains(edit))
                        {
                            throw Fail(root, $"Unresolved instance chained-call edit '{edit.GetType().Name}'.");
                        }

                        break;
                    case DropInstanceCall dropInstanceCall:
                        // b3-t1: a drop sharing an anchor with a chained-call append was already
                        // folded into that anchor's single ReplaceNode by
                        // ResolveInstanceChainedCallAppends above; resolving it again here would
                        // independently track its target node, which the fold's re-parse orphans.
                        if (!consumedChainedCallEdits.Contains(edit))
                        {
                            ResolveDropInstanceCall(root, anchors, dropInstanceCall, allTargets, appliers);
                        }

                        break;
                    case DropScopedOnCall dropScopedOnCall:
                        // spec 54: a drop sharing an (anchor, matchKey) with a same-batch
                        // AppendScopedOn was already folded into that closure's single ReplaceNode
                        // by ResolveScopedOnAppends above — see its doc comment on the chain-collision
                        // hazard (mirrors DropInstanceCall's fold, just above).
                        if (!consumedChainedCallEdits.Contains(edit))
                        {
                            ResolveDropScopedOnCall(root, anchors, dropScopedOnCall, allTargets, appliers);
                        }

                        break;
                    default:
                        throw Fail(root, $"Unsupported SourceEdit kind '{edit.GetType().Name}'.");
                }
            }

            SyntaxNode currentRoot = root.TrackNodes(allTargets.Distinct());

            foreach (var apply in appliers)
            {
                currentRoot = apply(currentRoot);
            }

            // Self-consistency pass over the FINAL root, so every edit kind inherits it by default:
            // the patched file must not merely parse, it must COMPILE — it lives in Assets/ and Unity
            // builds it. Any edit that emitted a short `Asset(...)` call needs its using directive.
            if (currentRoot is CompilationUnitSyntax patchedUnit)
            {
                currentRoot = EnsureAssetRefsUsing(patchedUnit);
            }

            // Declare-before-use, over the FINAL root rather than per-edit: a batch of several
            // ReorderStatement edits (one per node whose sibling index moved) settles the block's
            // Child/Component peer order correctly, but a statement that named a moved handle and
            // was not itself part of the batch can still end up above that handle's new declaration
            // — the per-edit hoist in PlaceExistingStatement only repairs the handle(s) ITS OWN
            // moved group declares. This pass catches whatever is left, whichever edit combination
            // produced it.
            if (currentRoot is CompilationUnitSyntax preRepairUnit)
            {
                currentRoot = EnsureDeclareBeforeUse(preRepairUnit);
            }

            return currentRoot.ToFullString();
        }

        // ---- PatchArgument ------------------------------------------------------------------

        // ---- IntroduceTransformCall -------------------------------------------------------------

        /// <summary>
        /// Finds every transform <see cref="PatchArgument"/> whose target statement carries NO
        /// <c>.Transform(...)</c> call and introduces one per anchor, folding all of that anchor's
        /// pos/rot/scale args into a single call. Returns the edits it consumed so the main loop
        /// skips them.
        /// </summary>
        /// <remarks>
        /// Absent a Transform call the applier used to THROW ("No .Transform(...) call found"),
        /// which made the most ordinary edit in the product — nudging an object that was authored
        /// without an explicit transform — a hard sync failure. Introducing the call is the same
        /// treatment an absent flag call already gets.
        /// </remarks>
        private static HashSet<PatchArgument> ResolveTransformIntroductions(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            SourcePatch patch,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var consumed = new HashSet<PatchArgument>();
            var groups = new List<(string Anchor, ArgumentCall Call, List<PatchArgument> Args)>();

            foreach (var edit in patch.Edits.OfType<PatchArgument>())
            {
                // `name` patches the Add("...") argument, not a chained call. Anything CallFor
                // doesn't recognize is left to ResolvePatchArgument so it reports the precise failure.
                var call = CallFor(edit.ArgName);
                if (call == null)
                {
                    continue;
                }

                var anchorInvocation = FindAnchorInvocation(root, anchors, edit.Anchor);
                var chainRoot = AnchorChainRoot(anchorInvocation);
                if (FindChainCall(chainRoot, call.MethodName) != null)
                {
                    continue;
                }

                var existing = groups.FirstOrDefault(g => g.Anchor == edit.Anchor && g.Call == call);
                if (existing.Args == null)
                {
                    existing = (edit.Anchor, call, new List<PatchArgument>());
                    groups.Add(existing);
                }

                existing.Args.Add(edit);
                consumed.Add(edit);
            }

            // One anchor may need BOTH calls introduced in the same sync (e.g. a promoted node with
            // no `.Transform(...)` either) — folded into a SINGLE applier per anchor, chaining
            // Transform first then RectTransform, because two independent appliers replacing the
            // same tracked chain expression would lose tracking and NRE on GetCurrentNode(...).
            foreach (var anchorGroup in groups.GroupBy(g => g.Anchor))
            {
                var ordered = anchorGroup
                    .OrderBy(g => g.Call == TransformCall ? 0 : 1)
                    .Select(g => (g.Call, g.Args))
                    .ToList();
                ResolveIntroduceCalls(root, anchors, anchorGroup.Key, ordered, allTargets, appliers);
            }

            return consumed;
        }

        private static void ResolveIntroduceCalls(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            string anchor,
            List<(ArgumentCall Call, List<PatchArgument> Args)> calls,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var invocation = FindAnchorInvocation(root, anchors, anchor);
            var chainExpr = AnchorChainRoot(invocation);

            allTargets.Add(chainExpr);
            appliers.Add(currentRoot =>
            {
                var current = currentRoot.GetCurrentNode(chainExpr)!;
                var trailingTrivia = current.GetTrailingTrivia();
                ExpressionSyntax chain = current.WithoutTrailingTrivia();

                foreach (var (call, args) in calls)
                {
                    // Canonical argument order with named arguments, so the emission is
                    // indistinguishable from a hand-authored call — including when only later args
                    // are present, where positional syntax would be wrong.
                    var ordered = call.PositionalArgs
                        .Select(name => args.FirstOrDefault(a => a.ArgName == name))
                        .Where(a => a != null)
                        .ToList();

                    // NormalizeWhitespace so the introduced call's `name: value, name: value` spacing
                    // matches every other named-argument emitter in this file (RenderRectTransformCall,
                    // StatementText, InsertTransformArgument) — a byte-identical fixed point requires an
                    // introduced call to render exactly like a fresh-authored one.
                    var argList = SyntaxFactory.ArgumentList(
                        SyntaxFactory.SeparatedList(ordered.Select(a =>
                            SyntaxFactory.Argument(SyntaxFactory.ParseExpression(a!.NewExpr))
                                .WithNameColon(SyntaxFactory.NameColon(a!.ArgName)))))
                        .NormalizeWhitespace();

                    chain = SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            chain,
                            SyntaxFactory.IdentifierName(call.MethodName)),
                        argList);
                }

                return currentRoot.ReplaceNode(current, chain.WithTrailingTrivia(trailingTrivia));
            });
        }

        private static void ResolvePatchArgument(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            PatchArgument edit,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var invocation = FindAnchorInvocation(root, anchors, edit.Anchor);

            if (edit.ArgName == "name")
            {
                var argExpr = invocation.ArgumentList.Arguments[0].Expression;
                allTargets.Add(argExpr);
                appliers.Add(currentRoot =>
                {
                    var current = currentRoot.GetCurrentNode(argExpr)!;
                    var replacement = SyntaxFactory.ParseExpression(edit.NewExpr).WithTriviaFrom(current);
                    return currentRoot.ReplaceNode(current, replacement);
                });
                return;
            }

            var chainRoot = AnchorChainRoot(invocation);

            // Fall back to TransformCall so an unrecognized ArgName's failure text is unchanged
            // (CallFor only returns null for "name", already handled above).
            var call = CallFor(edit.ArgName) ?? TransformCall;
            var callInvocation = FindChainCall(chainRoot, call.MethodName);
            if (callInvocation == null)
            {
                throw Fail(chainRoot, $"No .{call.MethodName}(...) call found for anchor '{edit.Anchor}'.");
            }

            var (existingArg, _) = FindTransformArgument(callInvocation.ArgumentList, edit.ArgName, call.PositionalArgs);
            if (existingArg != null)
            {
                var oldExpr = existingArg.Expression;
                allTargets.Add(oldExpr);
                appliers.Add(currentRoot =>
                {
                    var current = currentRoot.GetCurrentNode(oldExpr)!;
                    var replacement = SyntaxFactory.ParseExpression(edit.NewExpr).WithTriviaFrom(current);
                    return currentRoot.ReplaceNode(current, replacement);
                });
            }
            else
            {
                var argList = callInvocation.ArgumentList;
                allTargets.Add(argList);
                appliers.Add(currentRoot =>
                {
                    var current = currentRoot.GetCurrentNode(argList)!;
                    var replacement = InsertTransformArgument(current, edit.ArgName, edit.NewExpr, call.PositionalArgs);
                    return currentRoot.ReplaceNode(current, replacement);
                });
            }
        }

        private static (ArgumentSyntax? Argument, int Index) FindTransformArgument(
            ArgumentListSyntax argList, string argName, string[] positionalArgs)
        {
            for (var i = 0; i < argList.Arguments.Count; i++)
            {
                var arg = argList.Arguments[i];
                var name = arg.NameColon != null
                    ? arg.NameColon.Name.Identifier.Text
                    : (i < positionalArgs.Length ? positionalArgs[i] : null);

                if (name == argName)
                {
                    return (arg, i);
                }
            }

            return (null, -1);
        }

        private static ArgumentListSyntax InsertTransformArgument(
            ArgumentListSyntax argList, string argName, string newExpr, string[] positionalArgs)
        {
            var canonicalIndex = Array.IndexOf(positionalArgs, argName);
            var newArgument = SyntaxFactory.Argument(
                SyntaxFactory.NameColon(argName),
                default,
                SyntaxFactory.ParseExpression(newExpr));

            var arguments = argList.Arguments;
            var insertAt = arguments.Count;
            for (var i = 0; i < arguments.Count; i++)
            {
                var existingName = arguments[i].NameColon != null
                    ? arguments[i].NameColon!.Name.Identifier.Text
                    : (i < positionalArgs.Length ? positionalArgs[i] : null);
                var existingCanonical = existingName != null ? Array.IndexOf(positionalArgs, existingName) : int.MaxValue;

                if (existingCanonical > canonicalIndex)
                {
                    insertAt = i;
                    break;
                }
            }

            var newArguments = arguments.Insert(insertAt, newArgument);
            return argList.WithArguments(newArguments);
        }

        // ---- Handle introduction ----------------------------------------------------------------

        /// <summary>
        /// Rewrites every handle-less statement that ANY edit in this batch needs to NAME into a
        /// `var &lt;handle&gt; = ...;` declaration — once per anchor, before any placement runs.
        /// </summary>
        /// <remarks>
        /// Hoisted out of the individual resolvers because both properties it provides are invariants
        /// no call site can be trusted to remember:
        /// <list type="bullet">
        /// <item>ONCE — a reparent onto Delta, a child appended under Delta and a component attached
        /// to Delta can all land in ONE sync, and each needs Delta to have a handle. Introduced
        /// per-resolver, the second rewrite finds a declaration already there and hard-fails.</item>
        /// <item>FIRST — placement floors a statement at its receiver's declaration. If that
        /// declaration is introduced by a LATER applier, the floor sees no declaration, reads 0, and
        /// happily seats the statement above the `var` that is about to appear: CS0841.</item>
        /// </list>
        /// The Reconciler independently guarantees one NAME per parent (Reconciler.ResolveOwnerHandle);
        /// this pass guarantees one REWRITE per parent. Conflicting names are a Reconciler bug and are
        /// reported as such rather than silently picking one.
        /// </remarks>
        private static void ResolveHandleIntroductions(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            SourcePatch patch,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var requested = new List<(string Anchor, string Handle)>();
            var requestedHandleByAnchor = new Dictionary<string, string>(StringComparer.Ordinal);

            void Request(string? anchor, string? handle, bool introduce)
            {
                if (!introduce || anchor == null || handle == null)
                {
                    return;
                }

                if (requestedHandleByAnchor.TryGetValue(anchor, out var existing))
                {
                    if (existing != handle)
                    {
                        throw Fail(root, $"Conflicting handle introductions for anchor '{anchor}': '{existing}' and '{handle}'.");
                    }

                    return;
                }

                requestedHandleByAnchor[anchor] = handle;
                requested.Add((anchor, handle));
            }

            foreach (var edit in patch.Edits)
            {
                switch (edit)
                {
                    case AppendStatement append:
                        Request(append.ParentAnchor, append.ParentHandle, append.IntroduceParentHandle);
                        break;
                    case MoveStatement move:
                        Request(move.NewParentAnchor, move.NewParentHandle, move.IntroduceNewParentHandle);
                        break;
                    case AppendComponentStatement component:
                        Request(component.Anchor, component.OwnerHandle, component.IntroduceOwnerHandle);
                        break;
                    case IntroduceHandle introduceHandle:
                        Request(introduceHandle.Anchor, introduceHandle.Handle, true);
                        break;
                }
            }

            foreach (var (anchor, handle) in requested)
            {
                // A same-batch parent has no anchor in the ORIGINAL source: it is declared by its own
                // AppendStatement, so there is nothing here to rewrite.
                if (!anchors.ContainsKey(anchor))
                {
                    continue;
                }

                var invocation = FindAnchorInvocation(root, anchors, anchor);
                var statement = invocation.FirstAncestorOrSelf<StatementSyntax>()
                    ?? throw Fail(invocation, $"Anchor '{anchor}' is not inside a statement.");

                if (statement is not ExpressionStatementSyntax)
                {
                    throw Fail(statement, $"Anchor '{anchor}' already declares a handle; cannot introduce '{handle}' again.");
                }

                allTargets.Add(statement);
                appliers.Add(currentRoot =>
                {
                    // Built HERE, from the node as it exists in the tracked tree — not at resolve time.
                    // A declaration built from the pre-tracking node would splice an un-annotated copy of
                    // the expression back in, silently detaching every other edit that targets something
                    // inside this statement.
                    var current = (ExpressionStatementSyntax)currentRoot.GetCurrentNode(statement)!;
                    return currentRoot.ReplaceNode(current, BuildHandleDeclaration(current, handle));
                });
            }
        }

        // ---- MoveStatement --------------------------------------------------------------------

        private static void ResolveMoveStatement(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            MoveStatement edit,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var invocation = FindAnchorInvocation(root, anchors, edit.Anchor);
            var movedStatement = invocation.FirstAncestorOrSelf<StatementSyntax>()
                ?? throw Fail(invocation, $"Anchor '{edit.Anchor}' is not inside a statement.");

            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess
                || memberAccess.Expression is not IdentifierNameSyntax receiverIdentifier)
            {
                throw Fail(invocation, $"Cannot rewrite receiver for anchor '{edit.Anchor}'.");
            }

            var (buildMethod, sceneParamName) = FindBuildMethod(root);
            string newHandleName;
            BlockSyntax targetBlock;

            if (edit.NewParentAnchor != null)
            {
                var parentInvocation = FindAnchorInvocation(root, anchors, edit.NewParentAnchor);
                var parentStatement = parentInvocation.FirstAncestorOrSelf<StatementSyntax>()
                    ?? throw Fail(parentInvocation, $"Anchor '{edit.NewParentAnchor}' is not inside a statement.");

                // The new parent's handle: an authored `var`, or one the Reconciler asked to be
                // introduced for exactly this purpose (the handle-introduction pre-pass has already
                // queued the rewrite). A handle-less parent is no longer a dead end.
                newHandleName = edit.NewParentHandle
                    ?? (parentStatement is LocalDeclarationStatementSyntax parentLocal
                        && parentLocal.Declaration.Variables.Count == 1
                            ? parentLocal.Declaration.Variables[0].Identifier.Text
                            : throw Fail(parentStatement, $"New parent anchor '{edit.NewParentAnchor}' has no handle variable; reparent is not expressible."));

                targetBlock = EnclosingBlock(parentStatement)
                    ?? throw Fail(parentStatement, $"Anchor '{edit.NewParentAnchor}' statement is not inside a block.");
            }
            else
            {
                newHandleName = sceneParamName;
                targetBlock = buildMethod.Body!;
            }

            allTargets.Add(movedStatement);
            allTargets.Add(receiverIdentifier);
            allTargets.Add(targetBlock);

            appliers.Add(currentRoot =>
            {
                var currentReceiver = currentRoot.GetCurrentNode(receiverIdentifier)!;
                var newReceiver = SyntaxFactory.IdentifierName(newHandleName).WithTriviaFrom(currentReceiver);
                currentRoot = currentRoot.ReplaceNode(currentReceiver, newReceiver);

                return PlaceExistingStatement(
                    currentRoot,
                    targetBlock,
                    movedStatement,
                    newHandleName,
                    PeerKind.Child,
                    edit.NewSiblingIndex);
            });
        }

        // ---- ReorderStatement -----------------------------------------------------------------

        private static void ResolveReorderStatement(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            ReorderStatement edit,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var invocation = FindAnchorInvocation(root, anchors, edit.Anchor);

            // b3-t5: a component anchored on a call CHAINED inside somebody else's
            // statement has no representable absolute position of its own — moving the ENCLOSING
            // statement would silently reorder whichever real scene-graph sibling happens to sit
            // next to it. No-op rather than throw: a PatchException here would
            // abort the whole patch over one unrepresentable cosmetic reorder, dropping the user's
            // real edits along with it. ComponentReconciler.ReconcileComponents' REORDER-pass gate
            // keeps this unreached whenever ParseResult.ChainedComponents
            // is threaded; this is the structural backstop for a caller that does not thread it.
            if (IsChainedNonStatementCall(invocation))
            {
                return;
            }

            var statement = invocation.FirstAncestorOrSelf<StatementSyntax>()
                ?? throw Fail(invocation, $"Anchor '{edit.Anchor}' is not inside a statement.");

            if (statement.Parent is not BlockSyntax block)
            {
                throw Fail(statement, $"Anchor '{edit.Anchor}' statement is not inside a block.");
            }

            allTargets.Add(block);
            allTargets.Add(statement);

            appliers.Add(currentRoot =>
            {
                // b3-t5: the statement-removal applier can land EARLIER in this same batch
                // (a parent GameObject removed alongside a closure-authored child that resolves to
                // the SAME statement, Reconciler.DetectRemovals cascade) — by the time this
                // applier runs, the tracked statement may already be gone. No-op rather than throw,
                // for the same reason the chained arm below already null-guards.
                var currentStatement = currentRoot.GetCurrentNode(statement);
                if (currentStatement == null)
                {
                    return currentRoot;
                }

                // The receiver is read at APPLY time: a MoveStatement earlier in this same batch may
                // already have re-pointed this statement at a different parent, and the reorder must
                // seat it among THAT parent's children.
                var receiver = RootReceiverName(currentStatement);
                if (receiver == null)
                {
                    return currentRoot;
                }

                return PlaceExistingStatement(
                    currentRoot,
                    block,
                    statement,
                    receiver,
                    PeerKindOf(currentStatement),
                    edit.NewSiblingIndex);
            });
        }

        // ---- RemoveStatement --------------------------------------------------------------------

        private static void ResolveRemoveStatement(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            RemoveStatement edit,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var invocation = FindAnchorInvocation(root, anchors, edit.Anchor);

            // b3-t5: a chained call's anchor is NOT its own statement — deleting the
            // enclosing statement would remove the node/handle it creates along with every OTHER
            // chained call on it, and can leave source that no longer compiles.
            // Splice ONLY this call out of its chain, via the same shape ResolveRemoveFlagCall /
            // IdCollisionHealer already use for a dead `.Id(...)`/flag call — EXCEPT the one shape
            // where splicing itself would corrupt the source (see ConfigureLambdaArgumentToRemove's
            // doc in SourcePatchApplier.AnchorChain.cs), which removes the whole configure-lambda
            // argument instead.
            if (IsChainedNonStatementCall(invocation))
            {
                if (ConfigureLambdaArgumentToRemove(invocation) is { } lambdaArgument)
                {
                    allTargets.Add(lambdaArgument);
                    appliers.Add(currentRoot =>
                    {
                        var current = currentRoot.GetCurrentNode(lambdaArgument);
                        if (current == null)
                        {
                            return currentRoot;
                        }

                        var argumentList = (ArgumentListSyntax)current.Parent!;
                        var newArgumentList = argumentList.WithArguments(argumentList.Arguments.Remove(current));
                        return currentRoot.ReplaceNode(argumentList, newArgumentList);
                    });
                    return;
                }

                allTargets.Add(invocation);
                appliers.Add(currentRoot =>
                {
                    // The owner's own RemoveStatement can land EARLIER in this same batch
                    // (Reconciler.DetectRemovals cascade emits the owner first, its chained
                    // components second) — by the time this applier runs, the enclosing statement
                    // (and this invocation with it) may already be gone.
                    var current = currentRoot.GetCurrentNode(invocation);
                    return current == null ? currentRoot : RemoveTrailingInvocation(currentRoot, current);
                });
                return;
            }

            var statement = invocation.FirstAncestorOrSelf<StatementSyntax>()
                ?? throw Fail(invocation, $"Anchor '{edit.Anchor}' is not inside a statement.");

            allTargets.Add(statement);
            appliers.Add(currentRoot =>
            {
                // b3-t5: the SAME batch can carry a RemoveStatement for a closure-authored
                // parent AND one for each of its Kind=="Component"/"GameObject" dependents that
                // resolve to this SAME statement (Reconciler.DetectRemovals cascade) — by the
                // time a later applier in the batch runs, this statement may already be gone.
                var current = currentRoot.GetCurrentNode(statement);
                return current == null ? currentRoot : currentRoot.RemoveNode(current, SyntaxRemoveOptions.KeepNoTrivia)!;
            });
        }

        // ---- AppendStatement ------------------------------------------------------------------

        private static void ResolveAppendStatement(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            AppendStatement edit,
            Dictionary<string, SyntaxAnnotation> appendAnnotations,
            Dictionary<string, SyntaxAnnotation> lastSiblingByParent,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var ownAnnotation = appendAnnotations[edit.NewLogicalId];

            if (edit.ParentAnchor == null)
            {
                var (buildMethod, sceneParamName) = FindBuildMethod(root);
                var body = buildMethod.Body!;
                var indent = BodyIndent(root);
                var newStmt = ParseAppendStatement(edit, sceneParamName, indent)
                    .WithAdditionalAnnotations(ownAnnotation);

                allTargets.Add(body);
                appliers.Add(currentRoot => PlaceNewStatement(
                    currentRoot,
                    body,
                    newStmt,
                    sceneParamName,
                    PeerKind.Child,
                    edit.NewSiblingIndex));
            }
            else if (appendAnnotations.ContainsKey(edit.ParentAnchor))
            {
                // Parent is appended in THIS SAME batch (e.g. a b2-t3 new-subtree: parent+child
                // both AppendStatements), so it has no anchor in the ORIGINAL source yet. Relay
                // placement via the parent's (or previous same-batch sibling's) annotation instead
                // of FindAnchorInvocation.
                var receiver = edit.ParentHandle
                    ?? throw Fail(root, $"AppendStatement '{edit.NewLogicalId}' targets same-batch parent '{edit.ParentAnchor}' but has no ParentHandle.");

                var anchorAnnotation = lastSiblingByParent.TryGetValue(edit.ParentAnchor, out var siblingAnnotation)
                    ? siblingAnnotation
                    : appendAnnotations[edit.ParentAnchor];
                lastSiblingByParent[edit.ParentAnchor] = ownAnnotation;

                var indent = BodyIndent(root);
                var newStmt = ParseAppendStatement(edit, receiver, indent)
                    .WithAdditionalAnnotations(ownAnnotation);

                appliers.Add(currentRoot =>
                {
                    var parentNode = currentRoot.GetAnnotatedNodes(anchorAnnotation).Single();
                    return currentRoot.InsertNodesAfter(parentNode, new[] { newStmt });
                });
            }
            else
            {
                var parentInvocation = FindAnchorInvocation(root, anchors, edit.ParentAnchor);
                var parentStatement = parentInvocation.FirstAncestorOrSelf<StatementSyntax>()
                    ?? throw Fail(parentInvocation, $"Anchor '{edit.ParentAnchor}' is not inside a statement.");

                // A handle-less parent is rewritten into a declaration by the handle-introduction
                // pre-pass, which has already queued its applier — so by the time this one runs, the
                // receiver is in scope and placement's floor can see its declaration.
                var receiver = edit.ParentHandle
                    ?? throw Fail(parentStatement, $"Anchor '{edit.ParentAnchor}' has no parent handle to append under.");

                var newStmt = ParseAppendStatement(edit, receiver, IndentOf(parentStatement))
                    .WithAdditionalAnnotations(ownAnnotation);

                var parentBlock = EnclosingBlock(parentStatement)
                    ?? throw Fail(parentStatement, $"Anchor '{edit.ParentAnchor}' statement is not inside a block.");

                allTargets.Add(parentBlock);
                appliers.Add(currentRoot => PlaceNewStatement(
                    currentRoot,
                    parentBlock,
                    newStmt,
                    receiver,
                    PeerKind.Child,
                    edit.NewSiblingIndex));
            }
        }

        // ---- PatchFlagArgument ------------------------------------------------------------------

        private static void ResolvePatchFlagArgument(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            PatchFlagArgument edit,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var invocation = FindAnchorInvocation(root, anchors, edit.Anchor);
            var chainRoot = AnchorChainRoot(invocation);

            var flagName = FlagName(edit.Flag);
            var flagInvocation = FindChainCall(chainRoot, flagName)
                ?? throw Fail(chainRoot, $"No .{flagName}(...) call found for anchor '{edit.Anchor}'.");

            if (flagInvocation.ArgumentList.Arguments.Count < 1)
            {
                throw Fail(flagInvocation, $".{flagName}(...) call for anchor '{edit.Anchor}' has no argument to patch.");
            }

            var argExpr = flagInvocation.ArgumentList.Arguments[0].Expression;
            allTargets.Add(argExpr);
            appliers.Add(currentRoot =>
            {
                var current = currentRoot.GetCurrentNode(argExpr)!;
                var replacement = SyntaxFactory.ParseExpression(edit.NewExpr).WithTriviaFrom(current);
                return currentRoot.ReplaceNode(current, replacement);
            });
        }

        // ---- IntroduceFlagCall ------------------------------------------------------------------

        private static void ResolveIntroduceFlagCall(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            IntroduceFlagCall edit,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var invocation = FindAnchorInvocation(root, anchors, edit.Anchor);
            var chainExpr = AnchorChainRoot(invocation);
            var flagName = FlagName(edit.Flag);

            allTargets.Add(chainExpr);
            appliers.Add(currentRoot =>
            {
                var current = currentRoot.GetCurrentNode(chainExpr)!;

                var argList = edit.ArgExpr == null
                    ? SyntaxFactory.ArgumentList()
                    : SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(SyntaxFactory.ParseExpression(edit.ArgExpr))));

                var newCall = SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            current.WithoutTrailingTrivia(),
                            SyntaxFactory.IdentifierName(flagName)),
                        argList)
                    .WithTrailingTrivia(current.GetTrailingTrivia());

                return currentRoot.ReplaceNode(current, newCall);
            });
        }

        // ---- RemoveFlagCall ---------------------------------------------------------------------

        private static void ResolveRemoveFlagCall(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            RemoveFlagCall edit,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var invocation = FindAnchorInvocation(root, anchors, edit.Anchor);
            var chainRoot = AnchorChainRoot(invocation);

            var flagName = FlagName(edit.Flag);
            var flagInvocation = FindChainCall(chainRoot, flagName)
                ?? throw Fail(chainRoot, $"No .{flagName}(...) call found for anchor '{edit.Anchor}'.");

            allTargets.Add(flagInvocation);
            appliers.Add(currentRoot =>
            {
                var current = currentRoot.GetCurrentNode(flagInvocation)!;
                return RemoveTrailingInvocation(currentRoot, current);
            });
        }

        /// <summary>
        /// Drops a trailing `.Name(args)` invocation from a chain, keeping the receiver expression
        /// and the call's trailing trivia. Shared by ResolveRemoveFlagCall and IdCollisionHealer
        /// (splicing out a dead `.Id(...)` call) — the chained-call-removal shape is identical.
        /// </summary>
        internal static SyntaxNode RemoveTrailingInvocation(SyntaxNode root, InvocationExpressionSyntax call)
        {
            var member = (MemberAccessExpressionSyntax)call.Expression;
            var replacement = member.Expression.WithTrailingTrivia(call.GetTrailingTrivia());
            return root.ReplaceNode(call, replacement);
        }

        // ---- Flag helpers -----------------------------------------------------------------------

        private static string FlagName(FlagKind flag) => flag switch
        {
            FlagKind.Tag => "Tag",
            FlagKind.Layer => "Layer",
            FlagKind.Active => "Active",
            FlagKind.Static => "Static",
            _ => throw new ArgumentOutOfRangeException(nameof(flag), flag, "Unknown FlagKind."),
        };

        // ---- Anchor resolution ------------------------------------------------------------------

        private static InvocationExpressionSyntax FindAnchorInvocation(
            SyntaxNode root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            string anchorId)
        {
            if (!anchors.TryGetValue(anchorId, out var span))
            {
                throw Fail(root, $"No anchor found for logical id '{anchorId}'.");
            }

            var textSpan = TextSpan.FromBounds(span.Start, span.Start + span.Length);
            var node = root.FindNode(textSpan, getInnermostNodeForTie: true);

            // Gate 1: GameObject anchors, whose span starts at the invocation's own start
            // (e.g. `scene.Add(...)`). Gate 2 (tried only when gate 1 misses): component
            // anchors, whose span starts mid-statement at the `.Component` member-access dot
            // (BuilderParser.cs — anchorStart = memberAccess.OperatorToken.SpanStart), so no
            // invocation begins at span.Start; match on the operator token instead.
            var invocation =
                node.FirstAncestorOrSelf<InvocationExpressionSyntax>(inv => inv.Span.Start == span.Start)
                ?? node.FirstAncestorOrSelf<InvocationExpressionSyntax>(inv =>
                    inv.Expression is MemberAccessExpressionSyntax ma && ma.OperatorToken.SpanStart == span.Start);

            if (invocation == null)
            {
                throw Fail(root, $"Could not locate anchor node for logical id '{anchorId}'.");
            }

            return invocation;
        }

        private static (MethodDeclarationSyntax Method, string SceneParamName) FindBuildMethod(SyntaxNode root)
        {
            var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .First(m => m.Identifier.Text == "Build");
            var paramName = method.ParameterList.Parameters[0].Identifier.Text;
            return (method, paramName);
        }

        // ---- Fail-loud helper -------------------------------------------------------------------

        private static PatchException Fail(SyntaxNode node, string message)
        {
            var position = node.GetLocation().GetLineSpan().StartLinePosition;
            var line = position.Line + 1;
            var column = position.Character + 1;
            return new PatchException($"{message} at line {line}, column {column}.", line, column);
        }
    }
}
