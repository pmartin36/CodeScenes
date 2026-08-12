using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SceneBuilder.Grammar
{
    // Total mirror of BuilderParser.UnityEvents.cs's `.Ref<T>()`/`.OnClick(...)`/`.OnEvent(...)`
    // shape checks. Grammar cannot reference Core, so the split/shape helpers below are
    // re-implemented locally (see FlatShapeRecognizer.cs's file-header note) -- both must
    // reject/accept the SAME shapes, pinned by RecognizerAgreementTests.
    public static partial class FlatShapeRecognizer
    {
        // Splits a trailing `.Ref<T>(...)` off a chain, mirroring BuilderParser's split: `.Ref` not
        // last stays in the returned chain, so it falls to ApplyChainedCalls' existing `default`
        // (SB1002) unchanged -- no new rule for that shape.
        private static (List<(string Method, ArgumentListSyntax Args, InvocationExpressionSyntax Invocation)> Chain, InvocationExpressionSyntax? RefCall)
            SplitTrailingComponentRef(List<(string Method, ArgumentListSyntax Args, InvocationExpressionSyntax Invocation)> calls)
        {
            if (calls.Count == 0 || calls[calls.Count - 1].Method != "Ref")
            {
                return (calls, null);
            }

            return (calls.GetRange(0, calls.Count - 1), calls[calls.Count - 1].Invocation);
        }

        // Mirrors BuilderParser.ReadComponentRef's shape check: exactly one type argument, and at
        // most one ordinal argument, which must be numeric.
        private static void CheckComponentRefCall(InvocationExpressionSyntax refCall, RecognizerContext ctx)
        {
            if (refCall.Expression is not MemberAccessExpressionSyntax memberAccess ||
                memberAccess.Name is not GenericNameSyntax generic ||
                generic.TypeArgumentList.Arguments.Count != 1)
            {
                Report(ctx, refCall, SB1001, "Ref<T>() requires exactly one type argument");
                return;
            }

            var args = refCall.ArgumentList.Arguments;
            if (args.Count == 0)
            {
                return;
            }

            if (args.Count > 1)
            {
                Report(ctx, refCall.ArgumentList, SB1001, "Ref<T>(...) takes at most one ordinal argument");
                return;
            }

            if (!IsNumericLiteral(args[0].Expression))
            {
                Report(ctx, args[0].Expression, SB1001, "Expected a numeric literal");
            }
        }

        // Mirrors BuilderParser.ParseListenerCall's shape check: a fixed positional layout
        // (`isOnEvent` shifts it by one for the leading event-field selector), an optional
        // `callState:`/`dynamic:` named tail, a target shape (identifier or member-access chain —
        // never a literal, a listener target is always a reference), and a method lambda whose
        // body is either an invocation (0-or-1 static argument) or, under `dynamic: true`, a bare
        // method-group member access.
        private static void CheckListenerCall(InvocationExpressionSyntax invocation, RecognizerContext ctx, bool isOnEvent)
        {
            var kind = isOnEvent ? "OnEvent" : "OnClick";
            var positional = new List<ArgumentSyntax>();
            ArgumentSyntax? callStateArg = null;
            ArgumentSyntax? dynamicArg = null;

            foreach (var arg in invocation.ArgumentList.Arguments)
            {
                if (arg.NameColon == null)
                {
                    positional.Add(arg);
                    continue;
                }

                switch (arg.NameColon.Name.Identifier.Text)
                {
                    case "callState":
                        callStateArg = arg;
                        break;
                    case "dynamic":
                        dynamicArg = arg;
                        break;
                    default:
                        Report(ctx, arg, SB1003, $"Unsupported named argument '{arg.NameColon.Name.Identifier.Text}'");
                        return;
                }
            }

            if (dynamicArg != null && !isOnEvent)
            {
                Report(ctx, dynamicArg, SB1003, "OnClick(...) does not accept a 'dynamic' argument");
                return;
            }

            var expectedPositional = isOnEvent ? 3 : 2;
            if (positional.Count != expectedPositional)
            {
                Report(ctx, invocation, SB1003, $"{kind}(...) requires exactly {expectedPositional} arguments");
                return;
            }

            if (callStateArg != null)
            {
                if (callStateArg.Expression is not MemberAccessExpressionSyntax callStateAccess)
                {
                    Report(ctx, callStateArg.Expression, SB1003, "Expected a member access like `UnityEventCallState.Off`");
                    return;
                }

                var callStateName = callStateAccess.Name.Identifier.Text;
                if (callStateName != "Off" && callStateName != "RuntimeOnly" && callStateName != "EditorAndRuntime")
                {
                    Report(ctx, callStateArg.Expression, SB1003, $"Unknown callState '{callStateName}'; expected Off, RuntimeOnly, or EditorAndRuntime");
                    return;
                }
            }

            var isDynamic = false;
            if (dynamicArg != null)
            {
                if (!IsBoolLiteral(dynamicArg.Expression))
                {
                    Report(ctx, dynamicArg.Expression, SB1003, "Expected a boolean literal");
                    return;
                }

                isDynamic = ((LiteralExpressionSyntax)dynamicArg.Expression).IsKind(SyntaxKind.TrueLiteralExpression);
            }

            if (isOnEvent && !IsEventSelectorShape(positional[0].Expression))
            {
                Report(ctx, positional[0].Expression, SB1003, "Expected an event-field selector like `c => c.field`");
                return;
            }

            var targetExpr = positional[isOnEvent ? 1 : 0].Expression;
            if (!IsListenerTargetShape(targetExpr))
            {
                Report(ctx, targetExpr, SB1003, "Expected a component-ref/node/asset handle target");
                return;
            }

            var methodExpr = positional[isOnEvent ? 2 : 1].Expression;
            if (methodExpr is not SimpleLambdaExpressionSyntax lambda)
            {
                Report(ctx, methodExpr, SB1003, "Expected a lambda like `x => x.Method()`");
                return;
            }

            if (isDynamic)
            {
                if (lambda.Body is not MemberAccessExpressionSyntax methodGroupAccess ||
                    methodGroupAccess.Expression is not IdentifierNameSyntax methodGroupReceiver ||
                    methodGroupReceiver.Identifier.Text != lambda.Parameter.Identifier.Text)
                {
                    Report(ctx, lambda.Body, SB1003, "Expected a method-group reference like `x => x.Method` for a dynamic listener");
                }

                return;
            }

            if (lambda.Body is not InvocationExpressionSyntax methodInvocation ||
                methodInvocation.Expression is not MemberAccessExpressionSyntax methodAccess ||
                methodAccess.Expression is not IdentifierNameSyntax methodReceiver ||
                methodReceiver.Identifier.Text != lambda.Parameter.Identifier.Text)
            {
                Report(ctx, lambda.Body, SB1003, "Expected a call to a method on the lambda's own parameter");
                return;
            }

            var methodArgs = methodInvocation.ArgumentList.Arguments;
            if (methodArgs.Count > 1)
            {
                Report(ctx, methodInvocation.ArgumentList, SB1003, $"{kind}(...) supports a zero-argument method or one static argument");
                return;
            }

            if (methodArgs.Count == 1 && !IsStaticArgShape(methodArgs[0].Expression))
            {
                Report(ctx, methodArgs[0].Expression, SB1003, "Unsupported static argument form");
            }
        }

        // Target shape: a bare identifier (a component-ref/node-handle var) or a member-access
        // chain rooted at literal `Assets` (an Assets.<Group>...<Leaf> typed-catalog reference).
        // Never a literal — a listener target is always a reference, never an authored constant —
        // and never a member-access chain rooted at anything else (e.g. an enum member access like
        // `MyEnum.Fast`): there is no target slot for it, mirroring IsStaticArgShape's identical
        // Assets-root rule below.
        private static bool IsListenerTargetShape(ExpressionSyntax expr)
        {
            if (expr is IdentifierNameSyntax)
            {
                return true;
            }

            var root = expr;
            while (root is MemberAccessExpressionSyntax memberAccess)
            {
                root = memberAccess.Expression;
            }

            return root is IdentifierNameSyntax rootIdentifier && rootIdentifier.Identifier.Text == "Assets";
        }

        // `c => c.field` — a selector lambda whose body is a member access on the lambda's own
        // parameter, mirroring `.Set(x => x.field)`'s key-selector shape.
        private static bool IsEventSelectorShape(ExpressionSyntax expr) =>
            expr is SimpleLambdaExpressionSyntax lambda &&
            lambda.Body is MemberAccessExpressionSyntax memberAccess &&
            memberAccess.Expression is IdentifierNameSyntax receiver &&
            receiver.Identifier.Text == lambda.Parameter.Identifier.Text;

        // A static argument's shape: a literal in `BuilderParser.UnityEvents.cs`'s
        // `ClassifyStaticArg` range, a bare identifier, an `Assets.<...>` typed-catalog
        // member-access chain, or one of those wrapped in the authoring-only `.As<T>()`/`.As()`
        // typed pass-through. A non-`Assets`-rooted member-access chain (e.g. an enum member
        // access like `MyEnum.Fast`) has no `ListenerArgMode` slot in `ClassifyStaticArg` and is
        // rejected here so the recognizer/parser mirror never disagrees.
        private static bool IsStaticArgShape(ExpressionSyntax expr)
        {
            if (IsTypedRefUnwrap(expr, out var receiver))
            {
                expr = receiver;
            }

            var negative = false;
            if (expr is PrefixUnaryExpressionSyntax unary && unary.OperatorToken.IsKind(SyntaxKind.MinusToken))
            {
                negative = true;
                expr = unary.Operand;
            }

            if (expr is LiteralExpressionSyntax literal)
            {
                // A long literal that cannot narrow into ClassifyStaticArg's `checked((int)l)` cast
                // has no persistent-call slot to occupy.
                if (literal.Token.Value is long longValue)
                {
                    var signed = negative ? -longValue : longValue;
                    return signed >= int.MinValue && signed <= int.MaxValue;
                }

                return true;
            }

            if (expr is IdentifierNameSyntax)
            {
                return true;
            }

            // An `Asset("path"[, subAsset])`/`Builtin(name[, typeHint])` factory call — the
            // object-mode arg's non-catalog asset-ref form (item 4's `.As<T>()` render unwraps
            // to this receiver). Mirrors ValueNodeParser.Parse's own `Asset`/`Builtin` callee-name
            // check; arg count/shape is that parser's concern, not this recognizer's.
            if (expr is InvocationExpressionSyntax assetCall
                && assetCall.Expression is IdentifierNameSyntax assetCallee
                && (assetCallee.Identifier.Text == "Asset" || assetCallee.Identifier.Text == "Builtin"))
            {
                return true;
            }

            if (expr is not MemberAccessExpressionSyntax)
            {
                return false;
            }

            while (expr is MemberAccessExpressionSyntax memberAccess)
            {
                expr = memberAccess.Expression;
            }

            return expr is IdentifierNameSyntax root && root.Identifier.Text == "Assets";
        }

        // Mirrors BuilderParser.UnwrapTypedRef's shape: a zero-argument `.As(...)` invocation.
        private static bool IsTypedRefUnwrap(ExpressionSyntax expr, out ExpressionSyntax receiver)
        {
            if (expr is InvocationExpressionSyntax invocation &&
                invocation.ArgumentList.Arguments.Count == 0 &&
                invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Name.Identifier.Text == "As")
            {
                receiver = memberAccess.Expression;
                return true;
            }

            receiver = expr;
            return false;
        }
    }
}
