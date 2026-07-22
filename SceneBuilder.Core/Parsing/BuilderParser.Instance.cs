using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;

namespace SceneBuilder.Core.Parsing
{
    // `scene.Instance("path")` / `handle.Instance("path")` parse arms, split out of
    // BuilderParser.cs for file-size discipline (BuilderParser.cs is near its 1000-line budget).
    // Mirrors ProcessAddChain's receiver-resolution + handle-registration shape (b2-t2); the
    // instance's `name`/LogicalId derives from the prefab-path STEM ONLY (never a live prefab
    // root name — that lands at snapshot-read, b5-t2).
    public static partial class BuilderParser
    {
        private static void ProcessInstanceChain(IdentifierNameSyntax receiver, List<(string Method, ArgumentListSyntax Args, InvocationExpressionSyntax Invocation)> calls, string? handleName, ParserContext ctx)
        {
            NodeBuilder? parentNode = null;
            List<NodeBuilder> targetList;

            if (receiver.Identifier.Text == ctx.SceneParamName)
            {
                targetList = ctx.Roots;
            }
            else if (ctx.Handles.TryGetValue(receiver.Identifier.Text, out parentNode))
            {
                targetList = parentNode.Children;
            }
            else
            {
                throw Fail(receiver, $"Unknown receiver '{receiver.Identifier.Text}'");
            }

            var instanceArgs = calls[0].Args.Arguments;
            if (instanceArgs.Count == 0)
            {
                throw Fail(calls[0].Args, "Instance requires a prefab-path argument");
            }

            var path = EvalStringLiteral(instanceArgs[0].Expression);
            var stem = Path.GetFileNameWithoutExtension(path);

            var node = new NodeBuilder { Name = stem, IsInstance = true, SourcePrefabPath = path };
            node.AnchorSpan = new SourceSpan(calls[0].Invocation.Span.Start, calls[0].Invocation.Span.Length);

            var chainedCalls = new List<(string Method, ArgumentListSyntax Args, InvocationExpressionSyntax Invocation)>();
            foreach (var call in calls.Skip(1))
            {
                switch (call.Method)
                {
                    case "Override":
                        ApplyOverride(node, call.Args);
                        break;
                    case "AddComponent":
                        ApplyAddComponent(node, call.Invocation, call.Args);
                        break;
                    case "RemoveComponent":
                        ApplyRemoveComponent(node, call.Invocation);
                        break;
                    default:
                        chainedCalls.Add(call);
                        break;
                }
            }

            var explicitId = ApplyChainedCalls(node, chainedCalls);

            var siblingIndex = targetList.Count;
            var parentLogicalId = parentNode?.LogicalId;
            node.LogicalId = ctx.Resolver.Resolve(handleName, explicitId, parentLogicalId, stem, siblingIndex);

            targetList.Add(node);
            if (handleName != null)
            {
                ctx.Handles[handleName] = node;
                node.Handle = handleName;
            }
        }

        // Mirrors BuildPlainNode's base-field copy, but emits a PrefabInstanceNode: Components
        // stays empty (v1 — whole-instance only, no per-component authoring) and SourcePrefab
        // carries the unresolved DisplayPath (GUID lowering is b2-t3's job, not parse's).
        // Overrides/RemovedComponents map straight from the NodeBuilder collections; AddedComponents
        // get their LogicalId (`{instanceLogicalId}/{TypeFullName}#{ordinal}`) assigned HERE, since
        // this only runs once the instance's own LogicalId is final (see ProcessInstanceChain).
        private static PrefabInstanceNode BuildInstanceNode(NodeBuilder builder) => new()
        {
            LogicalId = builder.LogicalId,
            Name = builder.Name,
            Tag = builder.Tag,
            Layer = builder.Layer,
            Active = builder.Active,
            IsStatic = builder.IsStatic,
            Transform = new TransformData
            {
                Position = builder.Position ?? Vec3.Zero,
                Rotation = builder.Rotation ?? Quat.Identity,
                Scale = builder.Scale ?? Vec3.One,
                DrivenChannels = builder.DrivenChannels,
            },
            Components = System.Array.Empty<ComponentData>(),
            Children = builder.Children.Select(BuildNode).ToArray(),
            SourcePrefab = new AssetRef { DisplayPath = builder.SourcePrefabPath ?? "" },
            OpaqueOverrides = null,
            Overrides = builder.Overrides.ToArray(),
            AddedComponents = BuildAddedComponents(builder),
            RemovedComponents = builder.RemovedComponentTypes
                .Select(typeFullName => new OverrideTarget { PrefabId = "type:" + typeFullName, ObjectId = 0 })
                .ToArray(),
        };

        private static AddedComponent[] BuildAddedComponents(NodeBuilder builder)
        {
            var ordinalByType = new Dictionary<string, int>();
            var result = new AddedComponent[builder.AddedComponents.Count];

            for (var i = 0; i < builder.AddedComponents.Count; i++)
            {
                var cb = builder.AddedComponents[i];
                var ordinal = ordinalByType.TryGetValue(cb.TypeFullName, out var count) ? count : 0;
                ordinalByType[cb.TypeFullName] = ordinal + 1;
                cb.LogicalId = $"{builder.LogicalId}/{cb.TypeFullName}#{ordinal}";

                result[i] = new AddedComponent { Target = new OverrideTarget(), Component = BuildComponent(cb) };
            }

            return result;
        }

        // `.Override(e => ...)` — closure body is a block of `e.Set(...)` statements or a fluent
        // chain `e.Set(a).Set(b)`; both forms unwrap uniformly through UnwrapChain (a lone
        // `.Set(...)` call unwraps to a single-element chain).
        private static void ApplyOverride(NodeBuilder node, ArgumentListSyntax args)
        {
            if (args.Arguments.Count != 1)
            {
                throw Fail(args, "Override(...) requires exactly one closure argument");
            }

            if (args.Arguments[0].Expression is not SimpleLambdaExpressionSyntax lambda)
            {
                throw Fail(args.Arguments[0].Expression, "Unsupported closure form; expected a lambda like `e => ...`");
            }

            var paramName = lambda.Parameter.Identifier.Text;

            switch (lambda.Body)
            {
                case BlockSyntax block:
                    foreach (var statement in block.Statements)
                    {
                        if (statement is not ExpressionStatementSyntax exprStatement)
                        {
                            throw Fail(statement, "Unsupported statement in Override closure (expected .Set(...) calls)");
                        }

                        ApplyOverrideSetChain(exprStatement.Expression, paramName, node);
                    }
                    break;

                case ExpressionSyntax exprBody:
                    ApplyOverrideSetChain(exprBody, paramName, node);
                    break;

                default:
                    throw Fail(lambda.Body, "Unsupported lambda body");
            }
        }

        private static void ApplyOverrideSetChain(ExpressionSyntax expression, string paramName, NodeBuilder node)
        {
            var (receiver, calls) = UnwrapChain(expression);
            if (receiver.Identifier.Text != paramName)
            {
                throw Fail(receiver, $"Unknown receiver '{receiver.Identifier.Text}' in Override closure");
            }

            foreach (var (method, _, invocation) in calls)
            {
                if (method != "Set")
                {
                    throw Fail(invocation, "Unsupported call in Override closure (expected .Set(...))");
                }

                node.Overrides.Add(ParseOverrideSet(invocation));
            }
        }

        // Selector KEY handling is fail-loud (mirrors ParseSetCall's key/value split); VALUE
        // lowering reuses ValueNodeParser.Parse verbatim (b3-t2, total). A reference value
        // (AssetRef/ObjectRef) lands in ObjectReference, leaving Value at its model default;
        // every other value lands in Value, leaving ObjectReference null.
        private static PropertyOverride ParseOverrideSet(InvocationExpressionSyntax setInvocation)
        {
            var args = setInvocation.ArgumentList.Arguments;
            if (args.Count != 2)
            {
                throw Fail(setInvocation, "Set(...) requires exactly two arguments");
            }

            string typeFullName;
            string propertyPath;

            var keyExpr = args[0].Expression;
            if (keyExpr is SimpleLambdaExpressionSyntax untypedLambda)
            {
                throw Fail(untypedLambda.Parameter, "Override selector requires a typed parameter, e.g. `(Health x) => x.member` (component type is unrecoverable from an untyped selector)");
            }

            if (keyExpr is ParenthesizedLambdaExpressionSyntax { Body: MemberAccessExpressionSyntax memberAccess } typedLambda)
            {
                var parameter = typedLambda.ParameterList.Parameters.Single();
                if (parameter.Type == null)
                {
                    throw Fail(parameter, "Override selector requires a typed parameter, e.g. `(Health x) => x.member`");
                }

                typeFullName = parameter.Type.ToString().Trim();
                propertyPath = "member:" + memberAccess.Name.Identifier.Text;
            }
            else if (keyExpr is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                if (setInvocation.Expression is not MemberAccessExpressionSyntax setMemberAccess ||
                    setMemberAccess.Name is not GenericNameSyntax generic ||
                    generic.TypeArgumentList.Arguments.Count != 1)
                {
                    throw Fail(setInvocation, "Set(\"path\", value) requires a generic type argument, e.g. `.Set<BoxCollider>(...)`");
                }

                typeFullName = generic.TypeArgumentList.Arguments[0].ToString().Trim();
                propertyPath = literal.Token.ValueText;
            }
            else
            {
                throw Fail(keyExpr, "Unsupported Override Set(...) key form (expected a typed selector or a string literal with `.Set<T>(...)`)");
            }

            var value = ValueNodeParser.Parse(args[1].Expression);
            var target = new OverrideTarget { PrefabId = "type:" + typeFullName, ObjectId = 0 };

            return value is ValueNode.AssetRef or ValueNode.ObjectRef
                ? new PropertyOverride { Target = target, PropertyPath = propertyPath, ObjectReference = value }
                : new PropertyOverride { Target = target, PropertyPath = propertyPath, Value = value };
        }

        // `.AddComponent<T>(cfg?)` — mirrors ApplyComponent's generic-type-arg extraction and
        // reuses ProcessComponentClosure verbatim for the `c => c.Set(...)` closure. The owner
        // (instance root) and this component's ordinal LogicalId are resolved later, in
        // BuildInstanceNode.
        private static void ApplyAddComponent(NodeBuilder node, InvocationExpressionSyntax invocation, ArgumentListSyntax args)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess ||
                memberAccess.Name is not GenericNameSyntax generic ||
                generic.TypeArgumentList.Arguments.Count != 1)
            {
                throw Fail(invocation, "AddComponent<T>() requires exactly one type argument");
            }

            var typeFullName = generic.TypeArgumentList.Arguments[0].ToString().Trim();
            var cb = new ComponentBuilder { TypeFullName = typeFullName };

            if (args.Arguments.Count > 0)
            {
                ProcessComponentClosure(args.Arguments[0].Expression, cb);
            }

            node.AddedComponents.Add(cb);
        }

        // `.RemoveComponent<T>()` — records the provisional `type:<FullName>` target sigil;
        // the adapter resolves it to the real (GUID:fileID, ObjectId) pair before diff.
        private static void ApplyRemoveComponent(NodeBuilder node, InvocationExpressionSyntax invocation)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess ||
                memberAccess.Name is not GenericNameSyntax generic ||
                generic.TypeArgumentList.Arguments.Count != 1)
            {
                throw Fail(invocation, "RemoveComponent<T>() requires exactly one type argument");
            }

            var typeFullName = generic.TypeArgumentList.Arguments[0].ToString().Trim();
            node.RemovedComponentTypes.Add(typeFullName);
        }

        // Mirrors the plain-GameObject IdentityMapEntry construction in CollectIdentityEntries,
        // but with Kind="PrefabInstance". PrefabKey/SourcePrefabGuid are unresolved at parse time
        // (filled in by b2-t3 lowering / b5-t3 build) UNLESS `existingEntry` (this LogicalId's entry
        // in the map passed into BuilderParser.Parse) already carries them — a re-parse after a
        // structural move (b4-t2) rebuilds every entry from scratch, so those fields must be
        // re-fetched from the prior map the same way GlobalObjectId already is, or a moved-but-not-
        // renamed instance loses its prefab identity on every syncback pass.
        private static IdentityMapEntry BuildInstanceIdentityEntry(NodeBuilder node, string? parentLogicalId, int siblingIndex, Dictionary<string, string> globalObjectIdByLogicalId, IdentityMapEntry? existingEntry) => new()
        {
            LogicalId = node.LogicalId,
            GlobalObjectId = globalObjectIdByLogicalId.TryGetValue(node.LogicalId, out var globalObjectId) ? globalObjectId : "",
            Kind = "PrefabInstance",
            ComponentType = null,
            ParentLogicalId = parentLogicalId,
            Name = node.Name,
            SiblingIndex = siblingIndex,
            PrefabKey = existingEntry?.PrefabKey,
            SourcePrefabGuid = existingEntry?.SourcePrefabGuid,
        };
    }
}
