using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SceneBuilder.Grammar
{
    // Total (never-throwing) mirror of SceneBuilder.Core.Parsing.BuilderParser's body-walk shape
    // recognition. Every point BuilderParser would `throw Fail(node, msg)` becomes a located
    // ShapeViolation here instead, using the SAME node/message; the walk never stops on error —
    // it keeps walking so every reachable violation in the body is reported (encounter order).
    // See .agent_handoffs/codescenes-analyzers/b1-t1/research.md for the throw-site -> code map.
    // Grammar cannot reference Core (ns2.0 vs ns2.1), so any Core-side lookup table the parser
    // relies on (e.g. SpatialComponents' keyword tables) is reimplemented locally, keyword-only —
    // this recognizer never needs the enum/type-name side of those tables, only which argument
    // names are structurally valid.
    public static partial class FlatShapeRecognizer
    {
        private const string SB1001 = "SB1001";
        private const string SB1002 = "SB1002";
        private const string SB1003 = "SB1003";

        private static readonly string[] TransformPositionalArgs = { "pos", "rot", "scale" };

        public static IReadOnlyList<ShapeViolation> Analyze(BlockSyntax buildBody, string sceneParamName, bool isVariant = false)
        {
            var ctx = new RecognizerContext(sceneParamName, isVariant);

            foreach (var statement in buildBody.Statements)
            {
                ProcessStatement(statement, ctx);
            }

            return ctx.Violations;
        }

        // ---- Statement walk (mirrors BuilderParser.ProcessStatement) --------------------------

        private static void ProcessStatement(StatementSyntax statement, RecognizerContext ctx)
        {
            switch (statement)
            {
                case LocalDeclarationStatementSyntax local:
                    if (local.Declaration.Variables.Count != 1)
                    {
                        Report(ctx, local, SB1001, "Unsupported local declaration (expected a single builder handle)");
                        return;
                    }

                    var declarator = local.Declaration.Variables[0];
                    if (declarator.Initializer == null)
                    {
                        Report(ctx, local, SB1001, "Unsupported local declaration (expected a builder call initializer)");
                        return;
                    }

                    ProcessBuilderChain(declarator.Initializer.Value, declarator.Identifier.Text, ctx);
                    return;

                case ExpressionStatementSyntax exprStatement:
                    ProcessBuilderChain(exprStatement.Expression, null, ctx);
                    return;

                default:
                    Report(ctx, statement, SB1001, $"Unsupported interleaved control flow ({statement.Kind()})");
                    return;
            }
        }

        private static void ProcessBuilderChain(ExpressionSyntax expression, string? handleName, RecognizerContext ctx)
        {
            var (receiver, calls) = UnwrapChain(expression, ctx);
            if (receiver == null)
            {
                return;
            }

            if (calls.Count == 0)
            {
                Report(ctx, expression, SB1001, "Expected a builder call chain");
                return;
            }

            if (calls[0].Method == "Instance")
            {
                ProcessInstanceChain(receiver, calls, handleName, ctx);
                return;
            }

            if (calls[0].Method == "Add")
            {
                ProcessAddChain(receiver, calls, handleName, ctx);
                return;
            }

            // A variant's root-level `.Override`/`.AddComponent`/`.RemoveComponent`/`.On` verbs,
            // authored directly on the Build param — mirrors BuilderParser.cs's variant arm.
            if (ctx.IsVariant && receiver.Identifier.Text == ctx.SceneParamName &&
                calls[0].Method is "Override" or "AddComponent" or "RemoveComponent" or "On")
            {
                ProcessVariantRootChain(calls, ctx);
                return;
            }

            // Setter-only chain applied directly to an already-created node. Mirrors
            // BuilderParser.cs:200's exact condition (NOT IsKnownReceiver) — the scene param
            // itself is not a valid setter-only receiver, only a declared handle is.
            if (receiver.Identifier.Text == ctx.SceneParamName || !ctx.Scope.Contains(receiver.Identifier.Text))
            {
                Report(ctx, receiver, SB1001, $"Unknown receiver '{receiver.Identifier.Text}'");
                return;
            }

            var (remainingCalls, refCall) = SplitTrailingComponentRef(calls);
            ApplyChainedCalls(remainingCalls, ctx);

            if (refCall != null)
            {
                CheckComponentRefCall(refCall, ctx);
            }
            else if (handleName != null)
            {
                ctx.Scope.Add(handleName);
            }
        }

        private static void ProcessAddChain(IdentifierNameSyntax receiver, List<(string Method, ArgumentListSyntax Args, InvocationExpressionSyntax Invocation)> calls, string? handleName, RecognizerContext ctx)
        {
            if (!IsKnownReceiver(receiver, ctx))
            {
                Report(ctx, receiver, SB1001, $"Unknown receiver '{receiver.Identifier.Text}'");
                return;
            }

            var addArgs = calls[0].Args.Arguments;
            if (addArgs.Count == 0)
            {
                Report(ctx, calls[0].Args, SB1001, "Add requires a name argument");
                return;
            }

            if (!IsStringLiteral(addArgs[0].Expression))
            {
                Report(ctx, addArgs[0].Expression, SB1001, "Expected a string literal");
            }

            var (remainingCalls, refCall) = SplitTrailingComponentRef(calls.Skip(1).ToList());
            ApplyChainedCalls(remainingCalls, ctx);

            if (refCall != null)
            {
                CheckComponentRefCall(refCall, ctx);
            }
            else if (handleName != null)
            {
                ctx.Scope.Add(handleName);
            }

            if (addArgs.Count > 1)
            {
                ProcessClosure(addArgs[1].Expression, ctx);
            }
        }

        // Applies non-Add chained calls; mirrors BuilderParser.ApplyChainedCalls' dispatch switch.
        // Shared by Add-chain remainders, setter-only chains, and Instance-chain remainders
        // (Override/AddComponent/RemoveComponent are filtered out by the caller for instance
        // chains, so those names still hit `default` -> SB1002 on a node/Add chain — context-
        // sensitive per research.md).
        private static void ApplyChainedCalls(List<(string Method, ArgumentListSyntax Args, InvocationExpressionSyntax Invocation)> calls, RecognizerContext ctx)
        {
            foreach (var (method, args, invocation) in calls)
            {
                switch (method)
                {
                    case "Transform":
                        ApplyTransform(args, ctx);
                        break;
                    case "Tag":
                        CheckStringArg(args, ctx);
                        break;
                    case "Layer":
                        CheckFloatArg(args, ctx);
                        break;
                    case "Active":
                        CheckBoolArg(args, ctx);
                        break;
                    case "Static":
                        break;
                    case "Id":
                        CheckStringArg(args, ctx);
                        break;
                    case "Component":
                        ApplyComponent(args, invocation, ctx);
                        break;
                    case "FitSize":
                        ApplyFitSize(args, invocation, ctx);
                        break;
                    case "SurfaceSnap":
                        ApplySurfaceSnap(args, invocation, ctx);
                        break;
                    case "RectTransform":
                        ApplyRectTransform(args, ctx);
                        break;
                    default:
                        Report(ctx, args, SB1002, $"Unsupported builder call '.{method}(...)'");
                        break;
                }
            }
        }

        private static void ProcessClosure(ExpressionSyntax closureExpression, RecognizerContext ctx)
        {
            if (closureExpression is not SimpleLambdaExpressionSyntax lambda)
            {
                Report(ctx, closureExpression, SB1001, "Unsupported closure form; expected a lambda like `m => ...`");
                return;
            }

            var paramName = lambda.Parameter.Identifier.Text;
            var hadPrevious = ctx.Scope.Contains(paramName);
            ctx.Scope.Add(paramName);

            switch (lambda.Body)
            {
                case BlockSyntax block:
                    foreach (var statement in block.Statements)
                    {
                        ProcessStatement(statement, ctx);
                    }
                    break;

                case ExpressionSyntax exprBody:
                    ProcessBuilderChain(exprBody, null, ctx);
                    break;

                default:
                    Report(ctx, lambda.Body, SB1001, "Unsupported lambda body");
                    break;
            }

            if (!hadPrevious)
            {
                ctx.Scope.Remove(paramName);
            }
        }

        // ---- Component<T> parsing (mirrors BuilderParser's b3-t1 Component arm) ---------------

        private static void ApplyComponent(ArgumentListSyntax args, InvocationExpressionSyntax invocation, RecognizerContext ctx)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess ||
                memberAccess.Name is not GenericNameSyntax generic ||
                generic.TypeArgumentList.Arguments.Count != 1)
            {
                Report(ctx, invocation, SB1001, "Component<T>() requires exactly one type argument");
                return;
            }

            if (args.Arguments.Count > 0)
            {
                ProcessComponentClosure(args.Arguments[0].Expression, ctx);
            }
        }

        // Mirrors ProcessComponentClosure/ProcessComponentSetCall — the Component<T>() and
        // AddComponent<T>() closure sub-grammar (`c => c.Set(key,value)`, `c => c.OnClick(...)` or
        // `c => c.OnEvent(...)` calls only). Every Fail site in this sub-grammar maps to SB1003.
        private static void ProcessComponentClosure(ExpressionSyntax closureExpression, RecognizerContext ctx)
        {
            if (closureExpression is not SimpleLambdaExpressionSyntax lambda)
            {
                Report(ctx, closureExpression, SB1003, "Unsupported closure form; expected a lambda like `c => ...`");
                return;
            }

            var paramName = lambda.Parameter.Identifier.Text;

            switch (lambda.Body)
            {
                case BlockSyntax block:
                    foreach (var statement in block.Statements)
                    {
                        if (statement is not ExpressionStatementSyntax exprStatement)
                        {
                            Report(ctx, statement, SB1003, "Unsupported statement in component closure (expected .Set(...), .OnClick(...) or .OnEvent(...) calls)");
                            continue;
                        }

                        ProcessComponentSetCall(exprStatement.Expression, paramName, ctx);
                    }
                    break;

                case ExpressionSyntax exprBody:
                    ProcessComponentSetCall(exprBody, paramName, ctx);
                    break;

                default:
                    Report(ctx, lambda.Body, SB1003, "Unsupported lambda body");
                    break;
            }
        }

        private static void ProcessComponentSetCall(ExpressionSyntax expression, string paramName, RecognizerContext ctx)
        {
            if (expression is not InvocationExpressionSyntax invocation ||
                invocation.Expression is not MemberAccessExpressionSyntax memberAccess ||
                memberAccess.Expression is not IdentifierNameSyntax receiver ||
                receiver.Identifier.Text != paramName)
            {
                Report(ctx, expression, SB1003, "Expected a `.Set(...)`, `.OnClick(...)`, `.OnEvent(...)` or `.SetRef(...)` call in component closure");
                return;
            }

            switch (memberAccess.Name.Identifier.Text)
            {
                case "Set":
                    CheckSetCall(invocation, ctx);
                    break;
                case "OnClick":
                    CheckListenerCall(invocation, ctx, isOnEvent: false);
                    break;
                case "OnEvent":
                    CheckListenerCall(invocation, ctx, isOnEvent: true);
                    break;
                case "SetRef":
                    CheckSetRefCall(invocation, ctx);
                    break;
                default:
                    Report(ctx, expression, SB1003, "Expected a `.Set(...)`, `.OnClick(...)`, `.OnEvent(...)` or `.SetRef(...)` call in component closure");
                    break;
            }
        }

        private static void CheckSetCall(InvocationExpressionSyntax setInvocation, RecognizerContext ctx)
        {
            var setArgs = setInvocation.ArgumentList.Arguments;
            if (setArgs.Count != 2)
            {
                Report(ctx, setInvocation, SB1003, "Set(...) requires exactly two arguments");
                return;
            }

            var keyExpr = setArgs[0].Expression;
            if (keyExpr is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                return;
            }

            if (keyExpr is SimpleLambdaExpressionSyntax { Body: MemberAccessExpressionSyntax })
            {
                return;
            }

            Report(ctx, keyExpr, SB1003, "Unsupported Set(...) key form (expected a string literal or `r => r.member`)");
        }

        // ---- Invocation-chain unwrap (mirrors BuilderParser.UnwrapChain) ----------------------

        private static (IdentifierNameSyntax? Receiver, List<(string Method, ArgumentListSyntax Args, InvocationExpressionSyntax Invocation)> Calls) UnwrapChain(ExpressionSyntax expression, RecognizerContext ctx)
        {
            var calls = new List<(string Method, ArgumentListSyntax Args, InvocationExpressionSyntax Invocation)>();
            ExpressionSyntax current = expression;

            while (true)
            {
                if (current is InvocationExpressionSyntax invocation)
                {
                    if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                    {
                        calls.Add((memberAccess.Name.Identifier.Text, invocation.ArgumentList, invocation));
                        current = memberAccess.Expression;
                        continue;
                    }

                    Report(ctx, invocation, SB1001, "Unsupported invocation form");
                    return (null, calls);
                }

                if (current is IdentifierNameSyntax identifier)
                {
                    calls.Reverse();
                    return (identifier, calls);
                }

                Report(ctx, current, SB1001, "Unsupported receiver expression");
                return (null, calls);
            }
        }

        // ---- Transform argument structure (mirrors BuilderParser.ApplyTransform) --------------

        private static void ApplyTransform(ArgumentListSyntax args, RecognizerContext ctx)
        {
            for (var i = 0; i < args.Arguments.Count; i++)
            {
                var arg = args.Arguments[i];
                string paramName;
                if (arg.NameColon != null)
                {
                    paramName = arg.NameColon.Name.Identifier.Text;
                }
                else if (i < TransformPositionalArgs.Length)
                {
                    paramName = TransformPositionalArgs[i];
                }
                else
                {
                    Report(ctx, arg, SB1001, "Too many Transform arguments");
                    continue;
                }

                if (arg.Expression is not TupleExpressionSyntax tuple || tuple.Arguments.Count != 3)
                {
                    Report(ctx, arg.Expression, SB1001, $"Transform.{paramName} expects a 3-tuple");
                    continue;
                }

                if (!IsNumericLiteral(tuple.Arguments[0].Expression))
                {
                    Report(ctx, tuple.Arguments[0].Expression, SB1001, "Expected a numeric literal");
                }

                if (!IsNumericLiteral(tuple.Arguments[1].Expression))
                {
                    Report(ctx, tuple.Arguments[1].Expression, SB1001, "Expected a numeric literal");
                }

                if (!IsNumericLiteral(tuple.Arguments[2].Expression))
                {
                    Report(ctx, tuple.Arguments[2].Expression, SB1001, "Expected a numeric literal");
                }

                switch (paramName)
                {
                    case "pos":
                    case "rot":
                    case "scale":
                        break;
                    default:
                        Report(ctx, arg, SB1001, $"Unknown Transform argument '{paramName}'");
                        break;
                }
            }
        }

        // ---- Literal-shape checks (mirror BuilderParser's EvalStringLiteral/EvalBool/EvalFloat) --

        private static void CheckStringArg(ArgumentListSyntax args, RecognizerContext ctx)
        {
            if (args.Arguments.Count == 0)
            {
                Report(ctx, args, SB1001, "Expected a string literal");
                return;
            }

            var expr = args.Arguments[0].Expression;
            if (!IsStringLiteral(expr))
            {
                Report(ctx, expr, SB1001, "Expected a string literal");
            }
        }

        private static void CheckFloatArg(ArgumentListSyntax args, RecognizerContext ctx)
        {
            if (args.Arguments.Count == 0)
            {
                Report(ctx, args, SB1001, "Expected a numeric literal");
                return;
            }

            var expr = args.Arguments[0].Expression;
            if (!IsNumericLiteral(expr))
            {
                Report(ctx, expr, SB1001, "Expected a numeric literal");
            }
        }

        private static void CheckBoolArg(ArgumentListSyntax args, RecognizerContext ctx)
        {
            if (args.Arguments.Count == 0)
            {
                Report(ctx, args, SB1001, "Expected a boolean literal");
                return;
            }

            var expr = args.Arguments[0].Expression;
            if (!IsBoolLiteral(expr))
            {
                Report(ctx, expr, SB1001, "Expected a boolean literal");
            }
        }

        private static bool IsStringLiteral(ExpressionSyntax expression) =>
            expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression);

        private static bool IsBoolLiteral(ExpressionSyntax expression) =>
            expression is LiteralExpressionSyntax literal &&
            (literal.IsKind(SyntaxKind.TrueLiteralExpression) || literal.IsKind(SyntaxKind.FalseLiteralExpression));

        private static bool IsNumericLiteral(ExpressionSyntax expression)
        {
            if (expression is PrefixUnaryExpressionSyntax unary && unary.OperatorToken.IsKind(SyntaxKind.MinusToken))
            {
                return IsNumericLiteral(unary.Operand);
            }

            return expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.NumericLiteralExpression);
        }

        // ---- Scope / violation plumbing --------------------------------------------------------

        private static bool IsKnownReceiver(IdentifierNameSyntax receiver, RecognizerContext ctx) =>
            receiver.Identifier.Text == ctx.SceneParamName || ctx.Scope.Contains(receiver.Identifier.Text);

        private static void Report(RecognizerContext ctx, SyntaxNode node, string code, string message)
        {
            ctx.Violations.Add(new ShapeViolation(new SourceSpan(node.Span.Start, node.Span.Length), code, message));
        }

        private sealed class RecognizerContext
        {
            public RecognizerContext(string sceneParamName, bool isVariant = false)
            {
                SceneParamName = sceneParamName;
                IsVariant = isVariant;
            }

            public string SceneParamName { get; }
            public bool IsVariant { get; }
            public HashSet<string> Scope { get; } = new();
            public List<ShapeViolation> Violations { get; } = new();
        }
    }
}
