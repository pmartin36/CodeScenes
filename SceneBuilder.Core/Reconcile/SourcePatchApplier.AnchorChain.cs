using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SceneBuilder.Core.Reconcile
{
    // m-ui-recttransform b3-t3: every anchored chained-call edit (Transform, RectTransform,
    // Tag/Layer/Active/Static, and instance chained-call appends) must resolve against the
    // ANCHORED NODE'S OWN fluent chain, never its enclosing STATEMENT. A statement can carry more
    // than one node's calls via the `Add(string, Action<NodeHandle>)` configure-closure form
    // (NodeHandle.Add — com.codescenes/Runtime/NodeHandle.cs; parsed by BuilderParser.cs:344-359),
    // so resolution is scoped to the anchor's own chain: these two helpers are a spine-only walk
    // that never crosses a lambda-body / argument-list boundary.
    public static partial class SourcePatchApplier
    {
        // The outermost expression that is still THIS anchor's fluent chain. Ascends only through
        // `receiver.Name(...)` links whose RECEIVER is the node we came from, so it stops at a lambda
        // body / argument boundary — the point where the enclosing statement starts describing a
        // DIFFERENT node.
        //   `var p = scene.Add("P").Transform(...);`   anchor scene.Add("P")  -> the whole chain
        //   `scene.Add("P", p => p.Add("L"));`          anchor p.Add("L")      -> `p.Add("L")`
        //   `scene.Add("P", p => p.Add("L"));`          anchor scene.Add("P",…)-> the outer chain
        private static ExpressionSyntax AnchorChainRoot(InvocationExpressionSyntax anchorInvocation)
        {
            ExpressionSyntax chain = anchorInvocation;
            while (chain.Parent is MemberAccessExpressionSyntax ma
                && ma.Expression == chain
                && ma.Parent is InvocationExpressionSyntax outer)
            {
                chain = outer;
            }

            return chain;
        }

        // Spine-only lookup: walks Invocation -> MemberAccess -> receiver down the chain and never
        // enters an argument list, so a `.RectTransform(...)` inside a configure closure can never be
        // mistaken for the anchored node's own call.
        private static InvocationExpressionSyntax? FindChainCall(ExpressionSyntax chainRoot, string methodName)
        {
            var expr = chainRoot;
            while (expr is InvocationExpressionSyntax inv && inv.Expression is MemberAccessExpressionSyntax ma)
            {
                if (ma.Name.Identifier.Text == methodName)
                {
                    return inv;
                }

                expr = ma.Expression;
            }

            return null;
        }

        // m-ui-recttransform b3-t5: whether `anchorInvocation` is a call CHAINED
        // inside somebody ELSE's statement — a component/spatial call on an `Add`/`Instance` chain,
        // a multi-link chain, or an expression-bodied configure lambda — rather than its own
        // statement. A RemoveStatement/ReorderStatement anchored on a TRUE result must never resolve
        // against `FirstAncestorOrSelf<StatementSyntax>()` — that statement belongs to a DIFFERENT
        // node/handle and destroying or moving it corrupts the scene graph.
        // Purely syntactic — needs no parse-time data, so this guard cannot be bypassed by a caller
        // forgetting to thread ParseResult.ChainedComponents.
        //
        // ONE predicate over the anchor's OWN CHAIN ROOT (not over the anchor itself: the two
        // coincide only when the anchor IS the outermost link):
        //   (a) the chain root is not a statement's own expression -> the anchor lives inside
        //       somebody else's statement (a configure-lambda body / an argument) -> CHAINED;
        //   (b) another call sits BELOW the anchor on the same statement spine (its own receiver is
        //       itself an invocation) -> the statement is not the anchor's -> CHAINED;
        //   (c) bottom of a statement-level chain: a node-creating call (`Add`/`Instance`) OWNS its
        //       statement (every link above it is that node's payload); anything else owns it only
        //       when it is the chain's SOLE link.
        internal static bool IsChainedNonStatementCall(InvocationExpressionSyntax anchorInvocation)
        {
            if (anchorInvocation.Expression is not MemberAccessExpressionSyntax ma)
            {
                return false;
            }

            var chainRoot = AnchorChainRoot(anchorInvocation);
            if (!IsStatementLevelExpression(chainRoot))
            {
                return true;
            }

            if (ma.Expression is InvocationExpressionSyntax)
            {
                return true;
            }

            return !IsNodeCreatingCall(anchorInvocation) && chainRoot != anchorInvocation;
        }

        // `NodeHandle.Add(string)`/`NodeHandle.Instance(string)` (and `SceneRoot`'s mirrors) return a
        // NEW handle — the created node — so every link chained ABOVE such a call describes THAT
        // node (its payload or its descendants), never the call's own receiver. The SAME two names
        // the parser dispatches on (BuilderParser.cs) and StatementPlacement.cs spells.
        // `InstanceHandle.AddChild` is DELIBERATELY EXCLUDED: measured, the parser attributes calls
        // chained after an `AddChild` to the INSTANCE, not the child, so splicing that shape is
        // correct — do not "complete" this set with it.
        private static bool IsNodeCreatingCall(InvocationExpressionSyntax call) =>
            call.Expression is MemberAccessExpressionSyntax ma && ma.Name.Identifier.Text is "Add" or "Instance";

        // Non-null only for the shape `scene.Add("X", x => x.Component<T>()...)` where `anchor` is
        // the LAMBDA BODY'S OUTERMOST link: splicing such an anchor via RemoveTrailingInvocation
        // would leave `x => x` (the call replaced by its bare-identifier receiver) — syntactically
        // legal C#, but BuilderParser cannot re-parse a configure closure whose body is not itself a
        // call chain (ProcessBuilderChain requires calls.Count > 0). Removing the WHOLE argument
        // instead collapses the statement to the node's bare creation call, which is correct when the
        // anchor is a node-creating call (the whole subtree it created is a payload of this lambda) OR
        // it is the body's SOLE link (nothing else in the lambda to preserve). Every other case
        // (a non-node-creating call with siblings above/below it in the same lambda body) falls
        // through to the ordinary splice.
        internal static ArgumentSyntax? ConfigureLambdaArgumentToRemove(InvocationExpressionSyntax anchor)
        {
            if (anchor.Expression is not MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax receiver })
            {
                return null;
            }

            var chainRoot = AnchorChainRoot(anchor);
            if (chainRoot.Parent is not SimpleLambdaExpressionSyntax lambda || lambda.Body != chainRoot)
            {
                return null;
            }

            if (lambda.Parameter.Identifier.Text != receiver.Identifier.Text)
            {
                return null;
            }

            if (lambda.Parent is not ArgumentSyntax argument)
            {
                return null;
            }

            return IsNodeCreatingCall(anchor) || chainRoot == anchor ? argument : null;
        }

        // Mirrors BuilderParser.cs's two `ProcessStatement` arms that pass `statementLevel: true`:
        // the expression IS the whole `ExpressionStatementSyntax`, or it is a
        // `LocalDeclarationStatementSyntax`'s sole declarator's initializer value. Applied to the
        // anchor's CHAIN ROOT (see IsChainedNonStatementCall), not the anchor itself.
        private static bool IsStatementLevelExpression(ExpressionSyntax expression)
        {
            if (expression.Parent is ExpressionStatementSyntax exprStatement && exprStatement.Expression == expression)
            {
                return true;
            }

            return expression.Parent is EqualsValueClauseSyntax equalsValue
                && equalsValue.Value == expression
                && equalsValue.Parent is VariableDeclaratorSyntax;
        }
    }
}
