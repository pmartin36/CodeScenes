using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;

namespace SceneBuilder.Core.Parsing
{
    // `.Ref<T>()` (a second, component-only address space alongside node handles) and the
    // `.OnClick(...)`/`.OnEvent(...)` UnityEvent listener-wiring surface: a static argument
    // selected by the wired method's own arity, a `callState:` argument, the `dynamic:`
    // method-group form, and the `.OnEvent` event-field selector. Both sides of the
    // recognizer/parser mirror are total: by the time this file's extraction runs, RecognizeOrThrow
    // has already proven the source a recognized flat builder, so a malformed `.Ref`/listener-call
    // shape is never reached here — see BuilderParser.cs's Unreachable() doc comment.
    public static partial class BuilderParser
    {
        private const string OnClickFieldKey = "m_OnClick";
        private const string CallStateArgName = "callState";
        private const string DynamicArgName = "dynamic";

        private readonly record struct ComponentRefCapture(string TypeFullName, int Ordinal);

        // Splits a trailing `.Ref<T>(...)` off a chain: `.Ref` is only ever meaningful as the LAST
        // call, so a `.Ref` anywhere else stays in the returned chain and falls to
        // ApplyChainedCalls' existing `default` (SB1002) unchanged.
        private static (List<(string Method, ArgumentListSyntax Args, InvocationExpressionSyntax Invocation)> Chain, InvocationExpressionSyntax? RefCall)
            SplitTrailingComponentRef(List<(string Method, ArgumentListSyntax Args, InvocationExpressionSyntax Invocation)> calls)
        {
            if (calls.Count == 0 || calls[^1].Method != "Ref")
            {
                return (calls, null);
            }

            return (calls.GetRange(0, calls.Count - 1), calls[^1].Invocation);
        }

        // Shape-validates a split-off `.Ref<T>(ordinal)` call and yields its capture.
        // Unreachable() on a bad shape (already rejected up front by the recognizer's mirror).
        private static ComponentRefCapture ReadComponentRef(InvocationExpressionSyntax refCall)
        {
            if (refCall.Expression is not MemberAccessExpressionSyntax memberAccess ||
                memberAccess.Name is not GenericNameSyntax generic ||
                generic.TypeArgumentList.Arguments.Count != 1)
            {
                throw Unreachable();
            }

            var typeFullName = generic.TypeArgumentList.Arguments[0].ToString().Trim();
            var args = refCall.ArgumentList.Arguments;

            int ordinal;
            if (args.Count == 0)
            {
                ordinal = 0;
            }
            else if (args.Count == 1)
            {
                ordinal = (int)EvalFloat(args[0].Expression);
            }
            else
            {
                throw Unreachable();
            }

            return new ComponentRefCapture(typeFullName, ordinal);
        }

        // The ONE place a chain's declared var is bound: component-ref space (a trailing `.Ref<T>()`)
        // or node space, never both. A component-ref var is NOT written to ctx.Handles/node.Handle, so
        // it never becomes the owning GameObject's LogicalId.
        private static void BindChainHandle(NodeBuilder node, string? handleName, InvocationExpressionSyntax? refCall, ParserContext ctx)
        {
            if (handleName == null)
            {
                return;
            }

            if (refCall != null)
            {
                var capture = ReadComponentRef(refCall);
                ctx.ComponentRefs[handleName] = (node, capture.TypeFullName, capture.Ordinal);
                return;
            }

            ctx.Handles[handleName] = node;
            node.Handle = handleName;
        }

        // Resolves every captured `.Ref<T>()` var, once every component LogicalId is final
        // (AssignComponentLogicalIds has run): composes the expected id via the shipped
        // ComponentTargetResolution.ComposeLogicalId and keeps it ONLY if some component on that
        // node already carries it. A miss is simply absent here -- the caller's resolveHandle then
        // falls through to Unsupported, never a guessed id.
        private static Dictionary<string, string> ResolveComponentRefs(ParserContext ctx)
        {
            var resolved = new Dictionary<string, string>();
            foreach (var (varName, capture) in ctx.ComponentRefs)
            {
                var composedId = ComponentTargetResolution.ComposeLogicalId(capture.Node.LogicalId, capture.TypeFullName, capture.Ordinal);
                if (capture.Node.Components.Any(c => c.LogicalId == composedId))
                {
                    resolved[varName] = composedId;
                }
            }

            return resolved;
        }

        // `.OnClick(target, x => x.Method(...)[, callState: ...])` / `.OnEvent(selector, target,
        // method[, callState: ...][, dynamic: true])` inside a `.Component<T>(...)` closure.
        // `isOnEvent` shifts the positional layout by one (the leading event-field selector).
        // Both the TARGET and any static ARGUMENT lower through the shipped ValueNodeParser.Parse
        // (an identifier -> ObjectRef(name), an Assets.<...> chain -> AssetRef), so this file
        // constructs zero new ValueNode.ObjectRef/AssetRef tokens. By the time this runs,
        // RecognizeOrThrow has already proven the call shape valid (see this file's header note),
        // so every shape check below is defensive, not reachable in practice.
        private static void ParseListenerCall(InvocationExpressionSyntax invocation, ComponentBuilder cb, ParserContext ctx, bool isOnEvent)
        {
            var positional = new List<ArgumentSyntax>();
            ExpressionSyntax? callStateExpr = null;
            ExpressionSyntax? dynamicExpr = null;

            foreach (var arg in invocation.ArgumentList.Arguments)
            {
                if (arg.NameColon == null)
                {
                    positional.Add(arg);
                    continue;
                }

                switch (arg.NameColon.Name.Identifier.Text)
                {
                    case CallStateArgName:
                        callStateExpr = arg.Expression;
                        break;
                    case DynamicArgName:
                        dynamicExpr = arg.Expression;
                        break;
                    default:
                        throw Unreachable();
                }
            }

            var expectedPositional = isOnEvent ? 3 : 2;
            if (positional.Count != expectedPositional)
            {
                throw Unreachable();
            }

            var fieldKey = OnClickFieldKey;
            if (isOnEvent)
            {
                if (positional[0].Expression is not SimpleLambdaExpressionSyntax selectorLambda ||
                    selectorLambda.Body is not MemberAccessExpressionSyntax selectorBody)
                {
                    throw Unreachable();
                }

                fieldKey = MemberFieldKey(selectorBody);
            }

            var targetExpr = positional[isOnEvent ? 1 : 0].Expression;
            // A listener target is never a literal (IsListenerTargetShape), so ClassifyStaticArg's
            // Primitive arms never fire here -- this reuses the SAME one-reference normalization
            // its catch-all applies to a static argument, the ONE site ObjectRefDescentScanTests
            // pins for this test.
            var target = ClassifyStaticArg(ValueNodeParser.Parse(targetExpr, ctx.AssetCatalog, ctx.FacadeConflicts, ctx.ConstStrings), targetExpr).Value;

            var isDynamic = dynamicExpr is LiteralExpressionSyntax dynamicLiteral &&
                             dynamicLiteral.IsKind(SyntaxKind.TrueLiteralExpression);

            if (positional[isOnEvent ? 2 : 1].Expression is not SimpleLambdaExpressionSyntax methodLambda)
            {
                throw Unreachable();
            }

            string methodName;
            ListenerArgMode mode;
            ValueNode? argValue = null;

            if (isDynamic)
            {
                // A method GROUP (`h => h.SetValue`), not an invocation: the runtime argument
                // Unity's event fires with is passed straight through, so no static ArgValue.
                if (methodLambda.Body is not MemberAccessExpressionSyntax methodGroupAccess)
                {
                    throw Unreachable();
                }

                methodName = methodGroupAccess.Name.Identifier.Text;
                mode = ListenerArgMode.Dynamic;
            }
            else
            {
                if (methodLambda.Body is not InvocationExpressionSyntax methodInvocation ||
                    methodInvocation.Expression is not MemberAccessExpressionSyntax methodAccess)
                {
                    throw Unreachable();
                }

                methodName = methodAccess.Name.Identifier.Text;
                var methodArgs = methodInvocation.ArgumentList.Arguments;

                if (methodArgs.Count == 0)
                {
                    mode = ListenerArgMode.Void;
                }
                else if (methodArgs.Count == 1)
                {
                    var argExpr = UnwrapTypedRef(methodArgs[0].Expression);
                    var lowered = ValueNodeParser.Parse(argExpr, ctx.AssetCatalog, ctx.FacadeConflicts, ctx.ConstStrings);
                    (mode, argValue) = ClassifyStaticArg(lowered, argExpr);
                }
                else
                {
                    throw Unreachable();
                }
            }

            var callState = callStateExpr != null ? ReadCallState(callStateExpr) : ListenerCallState.RuntimeOnly;

            var listener = new UnityEventListener(target, methodName, mode, argValue, callState);

            var memberAccess = (MemberAccessExpressionSyntax)invocation.Expression;
            var anchorStart = memberAccess.OperatorToken.SpanStart;
            var callSpan = new SourceSpan(anchorStart, invocation.Span.End - anchorStart);

            AppendListener(cb, fieldKey, listener, callSpan);
        }

        // `<ref>.As<T>()` / `<ref>.As()` is authoring-only typing scaffolding (ComponentRef<T>.As,
        // AssetReference.As<T>, SceneObjectHandle.As<T>) that exists only to give a static argument
        // expression the wired method's parameter type. Strips it and returns the underlying
        // receiver; returns `expr` unchanged when it is not that shape.
        private static ExpressionSyntax UnwrapTypedRef(ExpressionSyntax expr)
        {
            if (expr is InvocationExpressionSyntax invocation &&
                invocation.ArgumentList.Arguments.Count == 0 &&
                invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Name.Identifier.Text == "As")
            {
                return memberAccess.Expression;
            }

            return expr;
        }

        // The ONE place a lowered value at a reference-only slot picks its final shape -- a static
        // argument, or (reused as-is by this file's target-parsing code above; a listener target
        // is never a literal, so the Primitive arms below never fire for it) a listener TARGET. A
        // Primitive of a Unity-supported kind maps to its matching ListenerArgMode; everything else
        // -- an already-whole reference (ObjectRef/AssetRef, from a scene target or a typed-catalog
        // asset), or a value ValueNodeParser could not resolve to a persistable kind at all (an
        // out-of-catalog Assets chain the shape check cannot see past, an enum member access, a
        // uint/ulong/decimal/char/null literal) -- maps to Object, degrading anything that is not
        // already ObjectRef/AssetRef/Unsupported to ValueNode.Unsupported(expr's own source text)
        // rather than reaching the validating UnityEventListener constructor with a shape it
        // rejects. A Long/Double primitive (a literal Unity's persistent-call argument types cannot
        // express) collapses to the nearest supported mode rather than producing an unconstructible
        // UnityEventListener. The single site ObjectRefDescentScanTests pins for this one-reference
        // test, the same one ValidateTarget/ValidateArgValue apply at the model boundary.
        private static (ListenerArgMode Mode, ValueNode? Value) ClassifyStaticArg(ValueNode value, ExpressionSyntax expr) => value switch
        {
            ValueNode.Primitive(PrimitiveKind.Int, _) => (ListenerArgMode.Int, value),
            ValueNode.Primitive(PrimitiveKind.Float, _) => (ListenerArgMode.Float, value),
            ValueNode.Primitive(PrimitiveKind.String, _) => (ListenerArgMode.String, value),
            ValueNode.Primitive(PrimitiveKind.Bool, _) => (ListenerArgMode.Bool, value),
            ValueNode.Primitive(PrimitiveKind.Long, long l) => (ListenerArgMode.Int, ValueNode.Primitive.Int(checked((int)l))),
            ValueNode.Primitive(PrimitiveKind.Double, double d) => (ListenerArgMode.Float, ValueNode.Primitive.Float((float)d)),
            _ => (ListenerArgMode.Object, value is ValueNode.ObjectRef or ValueNode.AssetRef or ValueNode.Unsupported
                ? value
                : new ValueNode.Unsupported(expr.ToString())),
        };

        // `callState: <MemberAccess>` reads the member NAME only, regardless of qualifier depth —
        // `UnityEventCallState.Off` and `UnityEngine.Events.UnityEventCallState.Off` both lower the
        // same way. The recognizer rejects an unknown callState name before the body walk runs, so
        // both throw sites below are unreachable via Parse(); they throw a located ParseException
        // (not Unreachable()) purely so recognizer/parser drift fails loud instead of crashing.
        private static ListenerCallState ReadCallState(ExpressionSyntax expr)
        {
            if (expr is MemberAccessExpressionSyntax memberAccess)
            {
                return memberAccess.Name.Identifier.Text switch
                {
                    "Off" => ListenerCallState.Off,
                    "EditorAndRuntime" => ListenerCallState.EditorAndRuntime,
                    "RuntimeOnly" => ListenerCallState.RuntimeOnly,
                    _ => throw Fail(expr, $"Unknown callState '{memberAccess.Name.Identifier.Text}'; expected Off, RuntimeOnly, or EditorAndRuntime"),
                };
            }

            throw Fail(expr, "Expected a member access like `UnityEventCallState.Off`");
        }

        // The ONE place a parsed listener is added to a component field: appends to an existing
        // same-key entry (preserving authored order) rather than adding a duplicate key to
        // cb.Fields, so a later listener-wiring form cannot silently bypass the merge rule.
        private static void AppendListener(ComponentBuilder cb, string fieldKey, UnityEventListener listener, SourceSpan callSpan)
        {
            // Every listener's own span, in source order — unlike FieldValueSpans below, recorded
            // unconditionally (not just on the field key's first appearance).
            cb.ListenerCallSpans.Add(new KeyValuePair<string, SourceSpan>(fieldKey, callSpan));

            var existingIndex = cb.Fields.FindIndex(kv => kv.Key == fieldKey);
            if (existingIndex >= 0 && cb.Fields[existingIndex].Value is ValueNode.UnityEventListeners existing)
            {
                var merged = existing.Listeners.Append(listener).ToArray();
                cb.Fields[existingIndex] = new KeyValuePair<string, ValueNode>(fieldKey, new ValueNode.UnityEventListeners(merged));
                return;
            }

            cb.Fields.Add(new KeyValuePair<string, ValueNode>(fieldKey, new ValueNode.UnityEventListeners(new[] { listener })));
            cb.FieldValueSpans.Add(new KeyValuePair<string, SourceSpan>(fieldKey, callSpan));
        }
    }
}
