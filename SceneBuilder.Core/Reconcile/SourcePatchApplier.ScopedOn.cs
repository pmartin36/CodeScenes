using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SceneBuilder.Core.Facades;

namespace SceneBuilder.Core.Reconcile
{
    // m-nested-props b4-t1: scoped `.On(sel => ..., x => x.<ops>)` closure-authoring resolvers
    // (AppendScopedOn / DropScopedOnCall). Fourth partial-class file — mirrors
    // SourcePatchApplier.Instances.cs's rationale for a dedicated file: this is cohesive,
    // closure-descent Roslyn logic on top of the shared FindAnchorInvocation/RemoveTrailingInvocation/
    // Fail helpers, distinct from the root chained-call machinery it composes with.
    //
    // Inverse of BuilderParser.Facade.cs's ApplyScopedOn/ParseScopedClosure: an `.On` invocation has
    // arg0 = the selector (typed lambda `sel => sel.A.B`, or string `"A/B"`) and arg1 = an
    // expression-bodied closure `x => x.op1.op2…`.
    public static partial class SourcePatchApplier
    {
        // ---- AppendScopedOn ---------------------------------------------------------------------

        /// <summary>
        /// Folds every <see cref="AppendScopedOn"/> edit in this batch by <c>(Anchor,
        /// SelectorMatchKey)</c> — same-sub-object edits land in ONE `.On(...)` block, never two
        /// (spec #15). Per group: <see cref="FindExistingOn"/> on the anchor's statement — a HIT
        /// registers an append-into-closure applier targeting the existing `.On`'s closure body; a
        /// MISS contributes a full `On(SelectorExpr, x => x.op1.op2…)` call-text into
        /// <paramref name="callTextsByAnchor"/> so the shared per-anchor <c>AppendChainedCalls</c>
        /// fold (ResolveInstanceChainedCallAppends) emits it alongside any root-level chain appends
        /// on the same anchor in this batch — both splice onto the SAME statement chain expression,
        /// and only ONE applier may `ReplaceNode` it (see ResolveInstanceChainedCallAppends's doc
        /// comment on the chain-collision hazard).
        /// </summary>
        internal static void ResolveScopedOnAppends(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            SourcePatch patch,
            Dictionary<string, List<string>> callTextsByAnchor,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers,
            HashSet<SourceEdit> consumed)
        {
            var groups = new Dictionary<(string Anchor, string SelectorMatchKey), List<AppendScopedOn>>();

            foreach (var edit in patch.Edits.OfType<AppendScopedOn>())
            {
                var key = (edit.Anchor, edit.SelectorMatchKey);
                if (!groups.TryGetValue(key, out var group))
                {
                    group = new List<AppendScopedOn>();
                    groups[key] = group;
                }

                group.Add(edit);
                consumed.Add(edit);
            }

            foreach (var ((anchor, matchKey), group) in groups)
            {
                var invocation = FindAnchorInvocation(root, anchors, anchor);
                var statement = invocation.FirstAncestorOrSelf<StatementSyntax>()
                    ?? throw Fail(invocation, $"Anchor '{anchor}' is not inside a statement.");

                var opTexts = group.SelectMany(edit => edit.Ops).Select(RenderScopedOpCall).ToList();
                var existingOn = FindExistingOn(statement, matchKey);

                if (existingOn != null)
                {
                    var lambda = (SimpleLambdaExpressionSyntax)existingOn.ArgumentList.Arguments[1].Expression;
                    var body = lambda.Body as ExpressionSyntax
                        ?? throw Fail(lambda, $"Block-bodied .On(...) closure for anchor '{anchor}' is not supported.");

                    // spec 54: a same-batch DropScopedOnCall targeting THIS (anchor, matchKey) is the
                    // in-place-value-change / self-heal shape (drop the stale op, re-append the live
                    // one) — resolving it independently (the default suffix-append path below) would
                    // have this applier AND ResolveDropScopedOnCall's independent applier both target
                    // nodes inside the SAME closure; the drop's "sole op" check runs against the
                    // ORIGINAL closure and, applied after this append extends it, deletes the whole
                    // `.On(...)` call — new op included. Fold them into ONE ReplaceNode instead: keep
                    // every surviving op's own text, drop the matched op(s), append the new one(s),
                    // and consume the matched drops so the main dispatch loop never re-targets them.
                    var matchingDrops = patch.Edits.OfType<DropScopedOnCall>()
                        .Where(d => d.Anchor == anchor && d.SelectorMatchKey == matchKey)
                        .ToList();

                    if (matchingDrops.Count > 0)
                    {
                        var survivingTexts = SurvivingOpTexts(body, matchingDrops);
                        var paramName = lambda.Parameter.Identifier.Text;
                        var newBodyText = paramName + "." + string.Join(".", survivingTexts.Concat(opTexts));

                        allTargets.Add(body);
                        appliers.Add(currentRoot =>
                        {
                            var current = currentRoot.GetCurrentNode(body)!;
                            var newBody = SyntaxFactory.ParseExpression(newBodyText).WithTrailingTrivia(current.GetTrailingTrivia());
                            return currentRoot.ReplaceNode(current, newBody);
                        });

                        foreach (var drop in matchingDrops)
                        {
                            consumed.Add(drop);
                        }

                        continue;
                    }

                    allTargets.Add(body);
                    appliers.Add(currentRoot =>
                    {
                        var current = currentRoot.GetCurrentNode(body)!;
                        var newBodyText = current.WithoutTrailingTrivia().ToFullString()
                            + string.Concat(opTexts.Select(text => "." + text));
                        var newBody = SyntaxFactory.ParseExpression(newBodyText).WithTrailingTrivia(current.GetTrailingTrivia());
                        return currentRoot.ReplaceNode(current, newBody);
                    });
                }
                else
                {
                    var selectorExpr = group.Select(edit => edit.SelectorExpr).FirstOrDefault(expr => !string.IsNullOrEmpty(expr))
                        ?? throw Fail(statement, $"No existing .On(...) found for anchor '{anchor}' selector key '{matchKey}', and no SelectorExpr to create one.");

                    var callText = $"On({selectorExpr}, x => x.{string.Join(".", opTexts)})";

                    if (!callTextsByAnchor.TryGetValue(anchor, out var list))
                    {
                        list = new List<string>();
                        callTextsByAnchor[anchor] = list;
                    }

                    list.Add(callText);
                }
            }
        }

        // Renders a ScopedOp identically to its M10 root twin (RenderInstanceOverrideCall /
        // AddComponent<T>{closure} / RemoveComponent<T>()) — same call-text, only nested inside a
        // `.On` closure instead of directly on the instance chain.
        private static string RenderScopedOpCall(ScopedOp op) => op switch
        {
            ScopedOverrideOp scopedOverride => RenderInstanceOverrideCall(scopedOverride.Sets),
            ScopedAddComponentOp scopedAddComponent =>
                $"AddComponent<{scopedAddComponent.TypeFullName}>{RenderComponentClosureArgs(scopedAddComponent.Fields, scopedAddComponent.FieldExpressions)}",
            ScopedRemoveComponentOp scopedRemoveComponent => $"RemoveComponent<{scopedRemoveComponent.TypeFullName}>()",
            _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unknown ScopedOp."),
        };

        // ---- DropScopedOnCall --------------------------------------------------------------------

        private static void ResolveDropScopedOnCall(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            DropScopedOnCall edit,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var invocation = FindAnchorInvocation(root, anchors, edit.Anchor);
            var statement = invocation.FirstAncestorOrSelf<StatementSyntax>()
                ?? throw Fail(invocation, $"Anchor '{edit.Anchor}' is not inside a statement.");

            var onInvocation = FindExistingOn(statement, edit.SelectorMatchKey)
                ?? throw Fail(statement, $"No matching .On(...) call found for anchor '{edit.Anchor}' selector key '{edit.SelectorMatchKey}'.");

            var lambda = (SimpleLambdaExpressionSyntax)onInvocation.ArgumentList.Arguments[1].Expression;
            var closureBody = lambda.Body as ExpressionSyntax
                ?? throw Fail(lambda, $"Block-bodied .On(...) closure for anchor '{edit.Anchor}' is not supported.");
            var methodName = InstanceCallMethodName(edit.Kind);

            InvocationExpressionSyntax? target = null;
            foreach (var candidate in closureBody.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                         .Where(inv => inv.Expression is MemberAccessExpressionSyntax member && member.Name.Identifier.Text == methodName))
            {
                var matches = edit.Kind == InstanceCallKind.Override
                    ? OverrideCallMatches(candidate, edit.PropertyPath)
                    : GenericTypeArgMatches(candidate, edit.TypeFullName);

                if (matches)
                {
                    target = candidate;
                    break;
                }
            }

            if (target == null)
            {
                throw Fail(statement, $"No matching .{methodName}(...) call found inside .On(...) for anchor '{edit.Anchor}'.");
            }

            if (CountChainedScopedOps(closureBody) <= 1)
            {
                // Sole op — drop the WHOLE .On(...) call, not just the op inside it.
                allTargets.Add(onInvocation);
                appliers.Add(currentRoot =>
                {
                    var current = (InvocationExpressionSyntax)currentRoot.GetCurrentNode(onInvocation)!;
                    return RemoveTrailingInvocation(currentRoot, current);
                });
            }
            else
            {
                allTargets.Add(target);
                appliers.Add(currentRoot =>
                {
                    var current = (InvocationExpressionSyntax)currentRoot.GetCurrentNode(target)!;
                    return RemoveTrailingInvocation(currentRoot, current);
                });
            }
        }

        // Counts the chained ops on a `.On` closure body (`x.op1.op2…`), walking the chain from the
        // outermost invocation down to the lambda parameter identifier.
        private static int CountChainedScopedOps(ExpressionSyntax closureBody)
        {
            var count = 0;
            var current = closureBody;
            while (current is InvocationExpressionSyntax invocation && invocation.Expression is MemberAccessExpressionSyntax member)
            {
                count++;
                current = member.Expression;
            }

            return count;
        }

        // spec 54: the closure-body twin of ResolveDropScopedOnCall's op-matching walk, used when a
        // same-batch AppendScopedOn folds one or more DropScopedOnCall edits into its own
        // ReplaceNode (see ResolveScopedOnAppends). Walks the chain outermost-to-innermost (same
        // direction as CountChainedScopedOps), matches each op against the drop list using the
        // IDENTICAL predicate ResolveDropScopedOnCall uses (method name + OverrideCallMatches/
        // GenericTypeArgMatches) so the two can never disagree on which op a drop targets, then
        // reverses back to the source's original left-to-right order. A drop matches at most one op
        // (first hit wins, then removed from the candidate list) so two drops can never both consume
        // the same surviving op.
        private static List<string> SurvivingOpTexts(ExpressionSyntax closureBody, List<DropScopedOnCall> drops)
        {
            var chain = new List<(MemberAccessExpressionSyntax Member, InvocationExpressionSyntax Invocation)>();
            var current = closureBody;
            while (current is InvocationExpressionSyntax invocation && invocation.Expression is MemberAccessExpressionSyntax member)
            {
                chain.Add((member, invocation));
                current = member.Expression;
            }

            chain.Reverse();

            var remainingDrops = new List<DropScopedOnCall>(drops);
            var survivors = new List<string>();
            foreach (var (member, invocation) in chain)
            {
                var dropIndex = remainingDrops.FindIndex(d =>
                    member.Name.Identifier.Text == InstanceCallMethodName(d.Kind)
                    && (d.Kind == InstanceCallKind.Override
                        ? OverrideCallMatches(invocation, d.PropertyPath)
                        : GenericTypeArgMatches(invocation, d.TypeFullName)));

                if (dropIndex >= 0)
                {
                    remainingDrops.RemoveAt(dropIndex);
                    continue;
                }

                survivors.Add(member.Name.ToFullString() + invocation.ArgumentList.ToFullString());
            }

            return survivors;
        }

        // ---- Shared: find-or-match an `.On` invocation by resolved-child selector key -----------

        // Scans the statement for `.On(...)` invocations (2 args) whose selector matches matchKey
        // per SelectorKeyMatches. Matches the resolved durable child, NOT literal selector text or
        // `.On` call/span order.
        private static InvocationExpressionSyntax? FindExistingOn(StatementSyntax statement, string matchKey)
        {
            return statement.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .FirstOrDefault(inv => inv.Expression is MemberAccessExpressionSyntax member
                    && member.Name.Identifier.Text == "On"
                    && inv.ArgumentList.Arguments.Count == 2
                    && SelectorKeyMatches(inv.ArgumentList.Arguments[0].Expression, matchKey));
        }

        // matchKey is always RealName-joined (b4-t2 sets it from the FacadeCatalog-resolved
        // ChildPath — see ReconcilerInstances.Nested.cs). A `.On` selector's two source forms carry
        // the child's name in two different alphabets, so each compares against matchKey
        // differently:
        //   - typed lambda (`sel => sel.Chassis.FrontWheel`): its identifiers ARE PropertyName
        //     segments (sanitized C# identifiers), never RealName. Sanitize each matchKey segment
        //     with the EXACT SAME allocator input rule the catalog builder used
        //     (FacadeCatalogBuilder.SanitizeIdentifier — reused, not reimplemented) and compare
        //     segment-by-segment. (Does not re-run the catalog's dedup-suffix allocation, which
        //     needs sibling context this catalog-free applier does not have; ordinal identifier
        //     sanitization alone is what diverges for the common space/punctuation case and is
        //     what this fix targets.)
        //   - string literal (`.On("Chassis/Front Wheel", ...)`): already RealName-joined verbatim
        //     — matches matchKey byte-for-byte, unchanged from before this fix.
        private static bool SelectorKeyMatches(ExpressionSyntax arg0, string matchKey)
        {
            if (arg0 is SimpleLambdaExpressionSyntax { Body: ExpressionSyntax body })
            {
                var segments = new List<string>();
                var current = body;
                while (current is MemberAccessExpressionSyntax memberAccess)
                {
                    segments.Insert(0, memberAccess.Name.Identifier.Text);
                    current = memberAccess.Expression;
                }

                var sanitizedMatchKeySegments = matchKey.Split('/').Select(FacadeCatalogBuilder.SanitizeIdentifier);
                return segments.SequenceEqual(sanitizedMatchKeySegments, StringComparer.Ordinal);
            }

            if (arg0 is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                return literal.Token.ValueText == matchKey;
            }

            throw Fail(arg0, "Unsupported .On(...) selector form; expected a typed member-chain lambda or a string literal.");
        }
    }
}
