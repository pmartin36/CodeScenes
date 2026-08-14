using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Reconcile
{
    // m10-b4-t1: instance-root override-authoring resolvers (AppendInstanceOverride /
    // AppendInstanceAddComponent / AppendInstanceRemoveComponent / DropInstanceCall). Third
    // partial-class file so the existing private helpers on SourcePatchApplier
    // (FindAnchorInvocation, AnchorChainRoot, RemoveTrailingInvocation, Fail) are reused
    // directly — mirrors ComponentPatchApplier.cs's comment on why this is a separate file.
    public static partial class SourcePatchApplier
    {
        // ---- AppendInstanceOverride / AppendInstanceAddComponent / AppendInstanceRemoveComponent --

        /// <summary>
        /// Pre-folds every AppendInstanceOverride / AppendInstanceAddComponent /
        /// AppendInstanceRemoveComponent edit in this batch into ONE combined chained-call append
        /// per anchor, mirroring ResolveTransformIntroductions (SourcePatchApplier.cs): a single
        /// instance can carry an override AND an added component AND a removed component in one
        /// Reconcile pass, and each targets the SAME anchor's chain expression. Resolving/applying
        /// them as separate AppendChainedCall calls would have each applier's `ReplaceNode` retarget
        /// the chain to a fresh, untracked node, orphaning the tracked node the next applier looks
        /// up via `GetCurrentNode` (which then returns null). Folding into one applier — like
        /// ResolveIntroduceTransformCall folds pos/rot/scale into one `.Transform(...)` — makes the
        /// whole anchor's set of appends land via a single ReplaceNode. Returns the edits it
        /// consumed so the main loop skips them.
        /// </summary>
        private static HashSet<SourceEdit> ResolveInstanceChainedCallAppends(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            SourcePatch patch,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var consumed = new HashSet<SourceEdit>();
            var callTextsByAnchor = new Dictionary<string, List<string>>();
            // b3-t1: drops sharing an anchor with a chained-call append fold into that anchor's
            // single ReplaceNode too — see the class doc comment. Collected here (NOT marked
            // consumed yet) so a drop whose anchor never gains an append call-text falls through to
            // the existing independent ResolveDropInstanceCall path unchanged.
            var dropsByAnchor = new Dictionary<string, List<DropInstanceCall>>();

            void Collect(string anchor, string callText, SourceEdit edit)
            {
                if (!callTextsByAnchor.TryGetValue(anchor, out var list))
                {
                    list = new List<string>();
                    callTextsByAnchor[anchor] = list;
                }

                list.Add(callText);
                consumed.Add(edit);
            }

            foreach (var edit in patch.Edits)
            {
                switch (edit)
                {
                    case AppendInstanceOverride appendOverride:
                        Collect(appendOverride.Anchor, RenderInstanceOverrideCall(appendOverride.Sets), appendOverride);
                        break;
                    case AppendInstanceAddComponent appendAddComponent:
                        Collect(
                            appendAddComponent.Anchor,
                            $"AddComponent<{appendAddComponent.TypeFullName}>{RenderComponentClosureArgs(appendAddComponent.Fields, appendAddComponent.FieldExpressions)}",
                            appendAddComponent);
                        break;
                    case AppendInstanceRemoveComponent appendRemoveComponent:
                        Collect(appendRemoveComponent.Anchor, $"RemoveComponent<{appendRemoveComponent.TypeFullName}>()", appendRemoveComponent);
                        break;
                    case AppendInstanceAddChild appendAddChild:
                        Collect(appendAddChild.Anchor, RenderAddChildCall(appendAddChild), appendAddChild);
                        break;
                    case AppendInstanceRemoveChild appendRemoveChild:
                        Collect(appendRemoveChild.Anchor, $"RemoveChild({RemoveChildArg(appendRemoveChild)})", appendRemoveChild);
                        break;
                    case DropInstanceCall dropInstanceCall:
                        if (!dropsByAnchor.TryGetValue(dropInstanceCall.Anchor, out var drops))
                        {
                            drops = new List<DropInstanceCall>();
                            dropsByAnchor[dropInstanceCall.Anchor] = drops;
                        }

                        drops.Add(dropInstanceCall);
                        break;
                }
            }

            // m-nested-props b4-t1: AppendScopedOn shares this SAME callTextsByAnchor map — a
            // find-or-create MISS contributes its `On(...)` call-text here so it composes with root
            // chain appends on the same anchor via ONE AppendChainedCalls/ReplaceNode below (a HIT
            // is resolved entirely inside ResolveScopedOnAppends, targeting the existing `.On`'s
            // closure body instead).
            ResolveScopedOnAppends(root, anchors, patch, callTextsByAnchor, allTargets, appliers, consumed);

            foreach (var (anchor, callTexts) in callTextsByAnchor)
            {
                dropsByAnchor.TryGetValue(anchor, out var anchorDrops);

                // A virgin variant root has no existing `.Override`/`.AddComponent`/
                // `.RemoveComponent` call to chain onto — its anchor stays the SourceSpan default
                // (BuilderParser.Instance.cs's ProcessVariantRootChain only sets it once a root-level
                // verb statement is actually parsed). Every other anchor (a matched instance's own
                // `Instance(...)` call, or a variant root that already carries an authored verb) is a
                // real invocation, so it keeps chaining onto it exactly as before.
                if (anchors.TryGetValue(anchor, out var span) && span == default)
                {
                    AppendRootVerbStatement(root, anchor, callTexts, allTargets, appliers);
                }
                else
                {
                    AppendChainedCalls(root, anchors, anchor, callTexts, anchorDrops, allTargets, appliers);
                }

                if (anchorDrops != null)
                {
                    foreach (var drop in anchorDrops)
                    {
                        consumed.Add(drop);
                    }
                }
            }

            return consumed;
        }

        /// <summary>
        /// Introduces every queued call as ONE brand-new top-level statement on the Build method's
        /// scene/root param — the anchor-less counterpart to <see cref="AppendChainedCalls"/>, for a
        /// receiver with no existing invocation to splice onto (a variant root authoring its first-ever
        /// override/add/remove-component call). Reuses the general statement-placement path
        /// (StatementPlacement.cs) that every appended statement goes through: with no `Add`/`Instance`
        /// peers under this receiver and no local declaration to seat after, it lands at the end of the
        /// Build method's body.
        /// </summary>
        private static void AppendRootVerbStatement(
            CompilationUnitSyntax root,
            string anchor,
            IReadOnlyList<string> callTexts,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var (buildMethod, sceneParamName) = FindBuildMethod(root);
            var body = buildMethod.Body ?? throw Fail(buildMethod, $"Build method has no body to append anchor '{anchor}' into.");
            var indent = BodyIndent(root);

            var statementText = sceneParamName + string.Concat(callTexts.Select(callText => "." + callText)) + ";";
            var newStmt = SyntaxFactory.ParseStatement(statementText)
                .WithLeadingTrivia(SyntaxFactory.Whitespace(indent))
                .WithTrailingTrivia(SyntaxFactory.EndOfLine("\n"));

            allTargets.Add(body);
            appliers.Add(currentRoot => PlaceNewStatement(currentRoot, body, newStmt, sceneParamName, PeerKind.Child, 0));
        }

        /// <summary>
        /// Splices one or more new `.<c>callText</c>` calls onto the anchor's existing chain, ALL via
        /// a single ReplaceNode — the same template as ResolveIntroduceFlagCall
        /// (SourcePatchApplier.cs), generalized to accept any call text (generics/lambdas) since
        /// these calls aren't the fixed single-argument shape a flag call is. Trailing trivia is
        /// preserved exactly as IntroduceFlagCall/RemoveFlagCall do.
        /// </summary>
        private static void AppendChainedCalls(
            CompilationUnitSyntax root,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            string anchor,
            IReadOnlyList<string> callTexts,
            IReadOnlyList<DropInstanceCall>? drops,
            List<SyntaxNode> allTargets,
            List<Func<SyntaxNode, SyntaxNode>> appliers)
        {
            var invocation = FindAnchorInvocation(root, anchors, anchor);
            var chainExpr = AnchorChainRoot(invocation);

            allTargets.Add(chainExpr);
            appliers.Add(currentRoot =>
            {
                var current = currentRoot.GetCurrentNode(chainExpr)!;
                var originalTrailing = current.GetTrailingTrivia();

                // b3-t1: splice out any same-anchor drop(s) from a DETACHED copy of the chain
                // BEFORE appending the new calls, so the drop target is located inside `working`
                // rather than via a separately-tracked node the ReplaceNode below would orphan.
                var working = (ExpressionSyntax)current;
                if (drops != null)
                {
                    foreach (var drop in drops)
                    {
                        var dropCall = FindInstanceCall(working, drop);
                        working = (ExpressionSyntax)RemoveTrailingInvocation(working, dropCall);
                    }
                }

                var newExprText = working.WithoutTrailingTrivia().ToFullString()
                    + string.Concat(callTexts.Select(callText => "." + callText));
                var newExpr = SyntaxFactory.ParseExpression(newExprText).WithTrailingTrivia(originalTrailing);
                return currentRoot.ReplaceNode(current, newExpr);
            });
        }

        // m-nested-props b4-t3 (spec #14): renders `.AddChild(parent, name)` or, when the added
        // child's Node carries >=1 representable (non-Transform) component, the payload-carrying
        // `.AddChild(parent, name, cfg => cfg.Component<T>(...))` form so the component converges
        // in the SAME Reconcile pass instead of being silently dropped. Round-trips through
        // BuilderParser.Instance.cs's ApplyAddChild -> ProcessClosure (3rd-arg NodeHandle grammar).
        private static string RenderAddChildCall(AppendInstanceAddChild appendAddChild)
        {
            var call = $"AddChild({AddChildParentArg(appendAddChild)}, {SourceExpr.StringLiteral(appendAddChild.Name)}";
            var closure = RenderAddChildClosure(appendAddChild);
            return closure is null ? call + ")" : $"{call}, {closure})";
        }

        // Renders the removed-child / parent argument as the typed façade selector (`sel => sel.A.B`)
        // the reconciler resolved, falling back to the string path form for edits produced without a
        // catalog (older edits carry an empty SelectorExpr) — matching `.On`'s emit (specs/27).
        private static string RemoveChildArg(AppendInstanceRemoveChild edit) =>
            string.IsNullOrEmpty(edit.SelectorExpr) ? SourceExpr.StringLiteral(edit.ChildPath) : edit.SelectorExpr;

        private static string AddChildParentArg(AppendInstanceAddChild edit) =>
            string.IsNullOrEmpty(edit.ParentSelectorExpr) ? SourceExpr.StringLiteral(edit.ParentPath) : edit.ParentSelectorExpr;

        // Reuses ComponentPatchApplier's RenderComponentClosureArgs (same partial class) for each
        // component's field-set — one renderer, no reinvented field-value formatting. Null when
        // there is neither a rect payload nor a representable component, so the caller keeps the
        // exact 2-arg AddChild form (PrefabInstanceReconcileTests.cs:712 regression guard).
        // m-ui-recttransform b3-t1 (iteration 2): the rect call (when present) renders FIRST,
        // sharing the same call-list/closure-form machinery a component payload already uses —
        // §13 one-pass convergence for rect + component on the SAME added child.
        private static string? RenderAddChildClosure(AppendInstanceAddChild edit)
        {
            var calls = new List<string>();

            if (edit.RectTransform is { } rect)
            {
                calls.Add("cfg." + RenderRectTransformCall(rect));
            }

            var components = ComponentReconciler.ExcludeTransform(edit.Node.Components);
            calls.AddRange(components.Select(c => $"cfg.Component<{c.Type.FullName}>{RenderComponentClosureArgs(c.Fields, edit.FieldExpressions)}"));

            if (calls.Count == 0)
            {
                return null;
            }

            return calls.Count == 1
                ? $"cfg => {calls[0]}"
                : $"cfg => {{ {string.Join(" ", calls.Select(c => c + ";"))} }}";
        }

        // Inverse of BuilderParser.Instance.cs's ApplyOverride/ParseOverrideSet: multiple Sets fold
        // into ONE `.Override(e => e.Set(a).Set(b))` fluent chain.
        private static string RenderInstanceOverrideCall(IReadOnlyList<OverrideSetSpec> sets)
        {
            var body = "e." + string.Join(".", sets.Select(RenderOverrideSetCall));
            return $"Override(e => {body})";
        }

        // Key spelling follows the path's form: a "member:<name>" key renders the typed selector
        // `(T x) => x.<name>`, any other key renders the string form `Set<T>("path", ...)`. Every
        // spec built by ReconcilerInstances.BuildOverrideSetSpec carries a SNAPSHOT override's path,
        // which is Unity's serialized propertyPath, so the sigil form here serves patches built
        // directly against this API. The sigil does reach this file from a drop edit, where
        // OverrideSetTargetsPath matches it as a call-match key.
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
            InstanceCallKind.AddChild => "AddChild",
            InstanceCallKind.RemoveChild => "RemoveChild",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown InstanceCallKind."),
        };

        // b3-t1: `scope` widened from StatementSyntax to SyntaxNode — when this is called from
        // AppendChainedCalls's drop splice, the anchorless single-call case has the `.Override(...)`
        // (or other instance call) AS the chain expression itself, not merely a descendant of a
        // statement, so DescendantNodesAndSelf (not DescendantNodes) is required to find it.
        private static InvocationExpressionSyntax FindInstanceCall(SyntaxNode scope, DropInstanceCall edit)
        {
            var methodName = InstanceCallMethodName(edit.Kind);

            var candidates = scope.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .Where(inv => inv.Expression is MemberAccessExpressionSyntax member && member.Name.Identifier.Text == methodName);

            foreach (var candidate in candidates)
            {
                var matches = edit.Kind switch
                {
                    InstanceCallKind.Override => OverrideCallMatches(candidate, edit.PropertyPath),
                    // .AddChild(parentPath, "<name>") — match on the 2nd (name) argument.
                    InstanceCallKind.AddChild => StringLiteralArgMatches(candidate, 1, edit.PropertyPath),
                    // .RemoveChild(<child>) — the arg is a typed selector or a string; match either
                    // form against the RealName-joined childPath via SelectorKeyMatches (specs/27).
                    InstanceCallKind.RemoveChild => RemoveChildArgMatches(candidate, edit.PropertyPath),
                    _ => GenericTypeArgMatches(candidate, edit.TypeFullName),
                };

                if (matches)
                {
                    return candidate;
                }
            }

            throw Fail(scope, $"No matching .{methodName}(...) call found for anchor '{edit.Anchor}'.");
        }

        // AddChild/RemoveChild drop matcher: mirrors GenericTypeArgMatches's "null == match any"
        // convention. edit.PropertyPath carries the identifying string arg (name for AddChild,
        // childPath for RemoveChild) per DropInstanceCall's doc comment.
        private static bool StringLiteralArgMatches(InvocationExpressionSyntax invocation, int argIndex, string? value)
        {
            if (value == null)
            {
                return true;
            }

            if (invocation.ArgumentList.Arguments.Count <= argIndex)
            {
                return false;
            }

            return invocation.ArgumentList.Arguments[argIndex].Expression is LiteralExpressionSyntax literal
                && literal.IsKind(SyntaxKind.StringLiteralExpression)
                && literal.Token.ValueText == value;
        }

        // .RemoveChild drop matcher: the sole arg is a typed façade selector (`t => t.A.B`) or a
        // string path. Reuses SelectorKeyMatches (SourcePatchApplier.ScopedOn.cs) — the same
        // sanitized-typed / verbatim-string comparison `.On` uses — so a typed-authored RemoveChild
        // reverts correctly (specs/27). Null == match any, mirroring GenericTypeArgMatches.
        private static bool RemoveChildArgMatches(InvocationExpressionSyntax invocation, string? value)
        {
            if (value == null)
            {
                return true;
            }

            return invocation.ArgumentList.Arguments.Count >= 1
                && SelectorKeyMatches(invocation.ArgumentList.Arguments[0].Expression, value);
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
        //
        // b2-t1: a drop edit's PropertyPath is now the NORMALIZED serialized path (AuthoredPathResolver,
        // b1-t2) — e.g. "m_Intensity" — while the source's typed `.Set((Light l) => l.intensity, v)`
        // form still carries the raw member name. Match BOTH the transient "member:<name>" literal form
        // (AppendInstanceOverride's own render convention + the existing SourcePatchInstanceTests, which
        // stay valid) AND the normalized form, via NormalizedMemberPathMatches.
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
                var member = memberAccess.Name.Identifier.Text;
                return "member:" + member == propertyPath || NormalizedMemberPathMatches(member, propertyPath);
            }

            if (keyExpr is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                return literal.Token.ValueText == propertyPath;
            }

            return false;
        }

        // Mirrors AuthoredPathResolver.ResolvePath's (com.codescenes/Editor) member -> serialized-path
        // convention WITHOUT a live SerializedObject probe — Core has no Unity dependency, so this
        // replays the same naming rule instead of re-resolving it: a user-serialized field's path is
        // the bare member name unchanged; a built-in Unity field is "m_" + Capitalize(member). Either
        // candidate matching the edit's (already-normalized) PropertyPath is a match.
        private static bool NormalizedMemberPathMatches(string member, string propertyPath)
        {
            if (member == propertyPath)
            {
                return true;
            }

            if (member.Length == 0)
            {
                return false;
            }

            var mangled = "m_" + char.ToUpperInvariant(member[0]) + member.Substring(1);
            return mangled == propertyPath;
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
