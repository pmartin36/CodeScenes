using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
// IsKind lives in Microsoft.CodeAnalysis.CSharpExtensions (namespace Microsoft.CodeAnalysis).
using Microsoft.CodeAnalysis;

namespace SceneBuilder.Grammar
{
    // `scene.Instance("path")` / `handle.Instance("path")` recognizer arms, split out of
    // FlatShapeRecognizer.cs — mirrors BuilderParser.Instance.cs's structure and Fail sites
    // (all SB1001 per research.md's mapping table). `partial class` — shares RecognizerContext,
    // ApplyChainedCalls, ProcessComponentClosure, UnwrapChain, Report with the main file.
    public static partial class FlatShapeRecognizer
    {
        private static void ProcessInstanceChain(IdentifierNameSyntax receiver, List<(string Method, ArgumentListSyntax Args, InvocationExpressionSyntax Invocation)> calls, string? handleName, RecognizerContext ctx)
        {
            if (!IsKnownReceiver(receiver, ctx))
            {
                Report(ctx, receiver, SB1001, $"Unknown receiver '{receiver.Identifier.Text}'");
                return;
            }

            var instanceArgs = calls[0].Args.Arguments;
            if (instanceArgs.Count == 0)
            {
                Report(ctx, calls[0].Args, SB1001, "Instance requires a prefab-path argument");
                return;
            }

            if (!IsStringLiteral(instanceArgs[0].Expression))
            {
                Report(ctx, instanceArgs[0].Expression, SB1001, "Expected a string literal");
            }

            var chainedCalls = new List<(string Method, ArgumentListSyntax Args, InvocationExpressionSyntax Invocation)>();
            foreach (var call in calls.Skip(1))
            {
                switch (call.Method)
                {
                    case "Override":
                        ApplyOverride(call.Args, ctx);
                        break;
                    case "AddComponent":
                        ApplyAddComponent(call.Invocation, call.Args, ctx);
                        break;
                    case "RemoveComponent":
                        ApplyRemoveComponent(call.Invocation, ctx);
                        break;
                    default:
                        chainedCalls.Add(call);
                        break;
                }
            }

            ApplyChainedCalls(chainedCalls, ctx);

            if (handleName != null)
            {
                ctx.Scope.Add(handleName);
            }
        }

        // `.Override(e => ...)` — closure body is a block of `e.Set(...)` statements or a fluent
        // chain `e.Set(a).Set(b)`; both forms unwrap uniformly through UnwrapChain.
        private static void ApplyOverride(ArgumentListSyntax args, RecognizerContext ctx)
        {
            if (args.Arguments.Count != 1)
            {
                Report(ctx, args, SB1001, "Override(...) requires exactly one closure argument");
                return;
            }

            if (args.Arguments[0].Expression is not SimpleLambdaExpressionSyntax lambda)
            {
                Report(ctx, args.Arguments[0].Expression, SB1001, "Unsupported closure form; expected a lambda like `e => ...`");
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
                            Report(ctx, statement, SB1001, "Unsupported statement in Override closure (expected .Set(...) calls)");
                            continue;
                        }

                        ApplyOverrideSetChain(exprStatement.Expression, paramName, ctx);
                    }
                    break;

                case ExpressionSyntax exprBody:
                    ApplyOverrideSetChain(exprBody, paramName, ctx);
                    break;

                default:
                    Report(ctx, lambda.Body, SB1001, "Unsupported lambda body");
                    break;
            }
        }

        private static void ApplyOverrideSetChain(ExpressionSyntax expression, string paramName, RecognizerContext ctx)
        {
            var (receiver, calls) = UnwrapChain(expression, ctx);
            if (receiver == null)
            {
                return;
            }

            if (receiver.Identifier.Text != paramName)
            {
                Report(ctx, receiver, SB1001, $"Unknown receiver '{receiver.Identifier.Text}' in Override closure");
                return;
            }

            foreach (var (method, _, invocation) in calls)
            {
                if (method != "Set")
                {
                    Report(ctx, invocation, SB1001, "Unsupported call in Override closure (expected .Set(...))");
                    continue;
                }

                ParseOverrideSet(invocation, ctx);
            }
        }

        // Selector KEY structure only (VALUE lowering is not this recognizer's concern — total,
        // never a shape violation). Mirrors BuilderParser.ParseOverrideSet's structural checks.
        private static void ParseOverrideSet(InvocationExpressionSyntax setInvocation, RecognizerContext ctx)
        {
            var args = setInvocation.ArgumentList.Arguments;
            if (args.Count != 2)
            {
                Report(ctx, setInvocation, SB1001, "Set(...) requires exactly two arguments");
                return;
            }

            var keyExpr = args[0].Expression;
            if (keyExpr is SimpleLambdaExpressionSyntax untypedLambda)
            {
                Report(ctx, untypedLambda.Parameter, SB1001, "Override selector requires a typed parameter, e.g. `(Health x) => x.member` (component type is unrecoverable from an untyped selector)");
                return;
            }

            if (keyExpr is ParenthesizedLambdaExpressionSyntax { Body: MemberAccessExpressionSyntax } typedLambda)
            {
                var parameters = typedLambda.ParameterList.Parameters;
                if (parameters.Count != 1 || parameters[0].Type == null)
                {
                    Report(ctx, keyExpr, SB1001, "Override selector requires a typed parameter, e.g. `(Health x) => x.member`");
                }

                return;
            }

            if (keyExpr is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                if (setInvocation.Expression is not MemberAccessExpressionSyntax setMemberAccess ||
                    setMemberAccess.Name is not GenericNameSyntax generic ||
                    generic.TypeArgumentList.Arguments.Count != 1)
                {
                    Report(ctx, setInvocation, SB1001, "Set(\"path\", value) requires a generic type argument, e.g. `.Set<BoxCollider>(...)`");
                }

                return;
            }

            Report(ctx, keyExpr, SB1001, "Unsupported Override Set(...) key form (expected a typed selector or a string literal with `.Set<T>(...)`)");
        }

        // `.AddComponent<T>(cfg?)` — reuses ProcessComponentClosure (SB1003 sub-grammar), same as
        // Component<T>().
        private static void ApplyAddComponent(InvocationExpressionSyntax invocation, ArgumentListSyntax args, RecognizerContext ctx)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess ||
                memberAccess.Name is not GenericNameSyntax generic ||
                generic.TypeArgumentList.Arguments.Count != 1)
            {
                Report(ctx, invocation, SB1001, "AddComponent<T>() requires exactly one type argument");
                return;
            }

            if (args.Arguments.Count > 0)
            {
                ProcessComponentClosure(args.Arguments[0].Expression, ctx);
            }
        }

        // `.RemoveComponent<T>()` — structure-only (no value/closure to validate).
        private static void ApplyRemoveComponent(InvocationExpressionSyntax invocation, RecognizerContext ctx)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess ||
                memberAccess.Name is not GenericNameSyntax generic ||
                generic.TypeArgumentList.Arguments.Count != 1)
            {
                Report(ctx, invocation, SB1001, "RemoveComponent<T>() requires exactly one type argument");
            }
        }
    }
}
