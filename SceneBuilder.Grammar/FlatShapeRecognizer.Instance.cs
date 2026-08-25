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

            if (!IsStringLiteral(instanceArgs[0].Expression, ctx) && !IsPrefabsMemberAccess(instanceArgs[0].Expression))
            {
                Report(ctx, instanceArgs[0].Expression, SB1001, "Expected a string literal");
            }

            var chainedCalls = DispatchInstanceVerbs(calls.Skip(1), ctx);
            ApplyChainedCalls(chainedCalls, ctx);

            if (handleName != null)
            {
                ctx.Scope.Add(handleName);
                ctx.InstanceHandles.Add(handleName);
            }
        }

        // Per-verb shape check for a call chain rooted on a prefab-instance receiver — shared by the
        // INLINE form (ProcessInstanceChain, after skipping the leading `Instance(...)` call) and the
        // CAPTURED form (the instance arm in ProcessBuilderChain, where every call in the chain is a
        // verb). Mirrors BuilderParser.Instance.cs's DispatchInstanceVerbs exactly, so the two sides
        // agree on which shapes are instance verbs. Returns the leftover non-verb calls.
        private static List<(string Method, ArgumentListSyntax Args, InvocationExpressionSyntax Invocation)> DispatchInstanceVerbs(
            IEnumerable<(string Method, ArgumentListSyntax Args, InvocationExpressionSyntax Invocation)> calls, RecognizerContext ctx)
        {
            var chainedCalls = new List<(string Method, ArgumentListSyntax Args, InvocationExpressionSyntax Invocation)>();
            foreach (var call in calls)
            {
                switch (call.Method)
                {
                    case "Override":
                        ApplyOverride(call.Args, ctx);
                        break;
                    case "Component":
                    case "AddComponent":
                        ApplyAddComponent(call.Invocation, call.Args, ctx);
                        break;
                    case "RemoveComponent":
                        ApplyRemoveComponent(call.Invocation, ctx);
                        break;
                    case "On":
                        ApplyScopedOn(call.Args, ctx);
                        break;
                    case "AddChild":
                        ApplyAddChild(call.Args, ctx);
                        break;
                    case "RemoveChild":
                        ApplyRemoveChild(call.Args, ctx);
                        break;
                    default:
                        chainedCalls.Add(call);
                        break;
                }
            }

            return chainedCalls;
        }

        // A variant's root-level `.Override`/`.AddComponent`/`.RemoveComponent`/`.On`
        // verbs, authored directly on the Build param — reuses the SAME per-verb shape checks
        // ProcessInstanceChain's post-`Instance` dispatch uses below. Every call here IS a verb
        // (there is no leading `Instance` call to skip past).
        private static void ProcessVariantRootChain(List<(string Method, ArgumentListSyntax Args, InvocationExpressionSyntax Invocation)> calls, RecognizerContext ctx)
        {
            foreach (var call in calls)
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
                    case "On":
                        ApplyScopedOn(call.Args, ctx);
                        break;
                    default:
                        Report(ctx, call.Invocation, SB1001, $"Unsupported builder call '.{call.Method}(...)' on the variant root (expected .Override/.AddComponent/.RemoveComponent/.On)");
                        break;
                }
            }
        }

        // b4-t1: the typed façade form `Instance(Prefabs.Tank)` — shape-only (the recognizer is
        // ns2.0 and cannot reference Core/FacadeCatalog for the semantic catalog lookup; that
        // lives solely in BuilderParser.Instance.cs).
        private static bool IsPrefabsMemberAccess(ExpressionSyntax expr) =>
            expr is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "Prefabs" } };

        // `.On(selector, closure)` — shape-only, mirroring BuilderParser.Facade.cs's ApplyScopedOn
        // arg0 acceptance (SimpleLambda with a MemberAccess body, OR a string literal). The
        // catalog resolution + miss-conflict live solely in the parser (this recognizer cannot
        // reference Core/FacadeCatalog). arg1 (the closure) IS walked (b2-t1), mirroring the
        // parser's ParseScopedClosure dispatch, so the recognizer flags the same unsupported verb
        // the parser would `Unreachable()` on — MUST accept/reject the identical shape as the
        // parser arm or RecognizerAgreementTests/RecognizerCompletenessTests break.
        private static void ApplyScopedOn(ArgumentListSyntax args, RecognizerContext ctx)
        {
            if (args.Arguments.Count != 2)
            {
                Report(ctx, args, SB1001, "On(selector, closure) requires exactly two arguments");
                return;
            }

            var arg0 = args.Arguments[0].Expression;
            var isTypedSelector = arg0 is SimpleLambdaExpressionSyntax { Body: MemberAccessExpressionSyntax };

            if (!isTypedSelector && !IsStringLiteral(arg0, ctx))
            {
                Report(ctx, arg0, SB1001, "On(...) requires a typed member-chain selector (e.g. `t => t.A.B`) or a string path");
            }

            ProcessScopedClosure(args.Arguments[1].Expression, ctx);
        }

        // `.On(..., b => b.<ops>)` closure body — mirrors ApplyOverride's Block/fluent-chain
        // unwrap, dispatching each unwrapped call across the three nested-op verbs (reusing the
        // same shape checks as the root `.Override`/`.AddComponent`/`.RemoveComponent` verbs). An
        // empty closure (`b => { }`) is accepted (no-op); an unsupported verb is SB1001.
        private static void ProcessScopedClosure(ExpressionSyntax closureExpr, RecognizerContext ctx)
        {
            if (closureExpr is not SimpleLambdaExpressionSyntax lambda)
            {
                Report(ctx, closureExpr, SB1001, "On(...) closure must be a lambda like `b => ...`");
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
                            Report(ctx, statement, SB1001, "Unsupported statement in On(...) closure (expected .Override/.AddComponent/.RemoveComponent calls)");
                            continue;
                        }

                        ProcessScopedStatement(exprStatement.Expression, paramName, ctx);
                    }
                    break;

                case ExpressionSyntax exprBody:
                    ProcessScopedStatement(exprBody, paramName, ctx);
                    break;

                default:
                    Report(ctx, lambda.Body, SB1001, "Unsupported lambda body");
                    break;
            }
        }

        private static void ProcessScopedStatement(ExpressionSyntax expression, string paramName, RecognizerContext ctx)
        {
            var (receiver, calls) = UnwrapChain(expression, ctx);
            if (receiver == null)
            {
                return;
            }

            if (receiver.Identifier.Text != paramName)
            {
                Report(ctx, receiver, SB1001, $"Unknown receiver '{receiver.Identifier.Text}' in On(...) closure");
                return;
            }

            foreach (var (method, callArgs, invocation) in calls)
            {
                switch (method)
                {
                    case "Override":
                        ApplyOverride(callArgs, ctx);
                        break;
                    case "AddComponent":
                        ApplyAddComponent(invocation, callArgs, ctx);
                        break;
                    case "RemoveComponent":
                        ApplyRemoveComponent(invocation, ctx);
                        break;
                    default:
                        Report(ctx, invocation, SB1001, $"Unsupported call '{method}' in On(...) closure (expected .Override/.AddComponent/.RemoveComponent)");
                        break;
                }
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

        // `.AddChild(parent, name, cfg?)` — parent (arg0) is EITHER a typed member-chain selector
        // (`t => t.A.B`, compiler-checked) OR a string path; the NEW child name (arg1) is always a
        // string literal (a brand-new object has no façade type). An optional arg2 closure is walked
        // via ProcessClosure (the shared NodeHandle sub-grammar). Mirrors ApplyScopedOn's arg0
        // acceptance (specs/27).
        private static void ApplyAddChild(ArgumentListSyntax args, RecognizerContext ctx)
        {
            if (args.Arguments.Count < 2)
            {
                Report(ctx, args, SB1001, "AddChild(parent, name, cfg?) requires at least two arguments");
                return;
            }

            var arg0 = args.Arguments[0].Expression;
            if (!IsTypedChildSelector(arg0) && !IsStringLiteral(arg0, ctx))
            {
                Report(ctx, arg0, SB1001, "AddChild parent requires a typed member-chain selector (e.g. `t => t.A.B`) or a string path");
            }

            if (!IsStringLiteral(args.Arguments[1].Expression, ctx))
            {
                Report(ctx, args.Arguments[1].Expression, SB1001, "Expected a string literal");
            }

            if (args.Arguments.Count > 2)
            {
                ProcessClosure(args.Arguments[2].Expression, ctx);
            }
        }

        // `.RemoveChild(child)` — child (arg0) is EITHER a typed member-chain selector
        // (`t => t.A.B`, compiler-checked) OR a string path. Structure-only otherwise.
        private static void ApplyRemoveChild(ArgumentListSyntax args, RecognizerContext ctx)
        {
            if (args.Arguments.Count != 1)
            {
                Report(ctx, args, SB1001, "RemoveChild(child) requires exactly one argument");
                return;
            }

            var arg0 = args.Arguments[0].Expression;
            if (!IsTypedChildSelector(arg0) && !IsStringLiteral(arg0, ctx))
            {
                Report(ctx, arg0, SB1001, "RemoveChild(...) requires a typed member-chain selector (e.g. `t => t.A.B`) or a string path");
            }
        }

        // A typed façade child selector `t => t.A.B` — a lambda whose body is a member-access spine.
        // Same shape ApplyScopedOn accepts for `.On`.
        private static bool IsTypedChildSelector(ExpressionSyntax expr) =>
            expr is SimpleLambdaExpressionSyntax { Body: MemberAccessExpressionSyntax };
    }
}
