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
                throw Unreachable();
            }

            var instanceArgs = calls[0].Args.Arguments;
            if (instanceArgs.Count == 0)
            {
                throw Unreachable();
            }

            var arg0 = instanceArgs[0].Expression;
            NodeBuilder node;

            if (arg0 is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "Prefabs" } } prefabsMemberAccess)
            {
                // b4-t1: typed façade form `Instance(Prefabs.X)` — resolve X via the catalog
                // straight to a Guid (DisplayPath left empty). A catalog miss (or a null
                // catalog) is a located Conflict, never a throw, never a silent drop.
                var propertyName = prefabsMemberAccess.Name.Identifier.Text;
                node = new NodeBuilder { Name = propertyName, IsInstance = true };
                node.AnchorSpan = new SourceSpan(calls[0].Invocation.Span.Start, calls[0].Invocation.Span.Length);

                if (ctx.FacadeCatalog != null && ctx.FacadeCatalog.TryGetGuid(propertyName, out var guid))
                {
                    node.SourcePrefabGuid = guid;
                }
                else
                {
                    var argSpan = new SourceSpan(arg0.Span.Start, arg0.Span.Length);
                    ctx.FacadeConflicts.Add(new Conflict
                    {
                        Kind = ConflictKind.UnknownFacadeReference,
                        Reason = $"Unknown facade reference 'Prefabs.{propertyName}'.",
                        Location = argSpan,
                    });
                }
            }
            else
            {
                var path = EvalStringLiteral(arg0);
                var stem = Path.GetFileNameWithoutExtension(path);

                node = new NodeBuilder { Name = stem, IsInstance = true, SourcePrefabPath = path };
                node.AnchorSpan = new SourceSpan(calls[0].Invocation.Span.Start, calls[0].Invocation.Span.Length);
            }

            var chainedCalls = new List<(string Method, ArgumentListSyntax Args, InvocationExpressionSyntax Invocation)>();
            var addChildCalls = new List<(ArgumentListSyntax Args, InvocationExpressionSyntax Invocation)>();
            foreach (var call in calls.Skip(1))
            {
                switch (call.Method)
                {
                    case "Override":
                        ApplyOverride(node, call.Args, ctx);
                        break;
                    case "AddComponent":
                        ApplyAddComponent(node, call.Invocation, call.Args, ctx);
                        break;
                    case "RemoveComponent":
                        ApplyRemoveComponent(node, call.Invocation);
                        break;
                    case "On":
                        // b4-t2 (test-writer stub): real resolution lands in BuilderParser.Facade.cs.
                        ApplyScopedOn(node, call.Args, ctx);
                        break;
                    case "RemoveChild":
                        ApplyRemoveChild(node, call.Args, ctx);
                        break;
                    case "AddChild":
                        // Deferred: the added child's LogicalId is parented on `node.LogicalId`,
                        // which is only final after resolution below.
                        addChildCalls.Add((call.Args, call.Invocation));
                        break;
                    default:
                        chainedCalls.Add(call);
                        break;
                }
            }

            var explicitId = ApplyChainedCalls(node, chainedCalls, ctx);

            var siblingIndex = targetList.Count;
            var parentLogicalId = parentNode?.LogicalId;
            node.LogicalId = ctx.Resolver.Resolve(handleName, explicitId, parentLogicalId, node.Name, siblingIndex);

            targetList.Add(node);
            if (handleName != null)
            {
                ctx.Handles[handleName] = node;
                node.Handle = handleName;
            }

            foreach (var (args, invocation) in addChildCalls)
            {
                ApplyAddChild(node, args, invocation, ctx);
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
            Transform = BuildTransformData(builder),
            Components = System.Array.Empty<ComponentData>(),
            Children = builder.Children.Where(c => c.IsInstance).Select(BuildNode).ToArray(),
            SourcePrefab = new AssetRef { DisplayPath = builder.SourcePrefabPath ?? "", Guid = builder.SourcePrefabGuid ?? "" },
            OpaqueOverrides = null,
            Overrides = builder.Overrides.ToArray(),
            AddedComponents = BuildAddedComponents(builder),
            RemovedComponents = builder.RemovedComponents.ToArray(),
            ScopedOverrides = builder.ScopedOverrides.Count == 0 ? null : builder.ScopedOverrides.ToArray(),
            AddedGameObjects = builder.AddedGameObjects.Select(a => new AddedGameObject
            {
                Parent = a.ParentPath == "" ? new OverrideTarget() : new OverrideTarget { ChildPath = a.ParentPath },
                Node = BuildNode(a.Node),
            }).Concat(builder.Children.Where(c => !c.IsInstance).Select(c => new AddedGameObject
            {
                Parent = new OverrideTarget(),
                Node = BuildNode(c),
            })).ToArray(),
            RemovedGameObjects = builder.RemovedGameObjects.ToArray(),
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
                cb.LogicalId = ComponentTargetResolution.ComposeLogicalId(builder.LogicalId, cb.TypeFullName, ordinal);

                var target = cb.ChildPath == ""
                    ? new OverrideTarget()
                    : new OverrideTarget { ComponentType = cb.TypeFullName, ChildPath = cb.ChildPath };
                result[i] = new AddedComponent { Target = target, Component = BuildComponent(cb) };
            }

            return result;
        }

        // `.Override(e => ...)` — closure body is a block of `e.Set(...)` statements or a fluent
        // chain `e.Set(a).Set(b)`; both forms unwrap uniformly through UnwrapChain (a lone
        // `.Set(...)` call unwraps to a single-element chain).
        private static void ApplyOverride(NodeBuilder node, ArgumentListSyntax args, ParserContext ctx, string childPath = "")
        {
            if (args.Arguments.Count != 1)
            {
                throw Unreachable();
            }

            if (args.Arguments[0].Expression is not SimpleLambdaExpressionSyntax lambda)
            {
                throw Unreachable();
            }

            var paramName = lambda.Parameter.Identifier.Text;

            switch (lambda.Body)
            {
                case BlockSyntax block:
                    foreach (var statement in block.Statements)
                    {
                        if (statement is not ExpressionStatementSyntax exprStatement)
                        {
                            throw Unreachable();
                        }

                        ApplyOverrideSetChain(exprStatement.Expression, paramName, node, ctx, childPath);
                    }
                    break;

                case ExpressionSyntax exprBody:
                    ApplyOverrideSetChain(exprBody, paramName, node, ctx, childPath);
                    break;

                default:
                    throw Unreachable();
            }
        }

        private static void ApplyOverrideSetChain(ExpressionSyntax expression, string paramName, NodeBuilder node, ParserContext ctx, string childPath = "")
        {
            var (receiver, calls) = UnwrapChain(expression);
            if (receiver.Identifier.Text != paramName)
            {
                throw Unreachable();
            }

            foreach (var (method, _, invocation) in calls)
            {
                if (method != "Set")
                {
                    throw Unreachable();
                }

                node.Overrides.Add(ParseOverrideSet(invocation, ctx, childPath));
            }
        }

        // Selector KEY handling is fail-loud (mirrors ParseSetCall's key/value split); VALUE
        // lowering reuses ValueNodeParser.Parse verbatim (b3-t2, total). A reference value
        // (AssetRef/ObjectRef) lands in ObjectReference, leaving Value at its model default;
        // every other value lands in Value, leaving ObjectReference null.
        private static PropertyOverride ParseOverrideSet(InvocationExpressionSyntax setInvocation, ParserContext ctx, string childPath = "")
        {
            var args = setInvocation.ArgumentList.Arguments;
            if (args.Count != 2)
            {
                throw Unreachable();
            }

            string typeFullName;
            string propertyPath;

            var keyExpr = args[0].Expression;
            if (keyExpr is SimpleLambdaExpressionSyntax untypedLambda)
            {
                throw Unreachable();
            }

            if (keyExpr is ParenthesizedLambdaExpressionSyntax { Body: MemberAccessExpressionSyntax memberAccess } typedLambda)
            {
                var parameter = typedLambda.ParameterList.Parameters.Single();
                if (parameter.Type == null)
                {
                    throw Unreachable();
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
                    throw Unreachable();
                }

                typeFullName = generic.TypeArgumentList.Arguments[0].ToString().Trim();
                propertyPath = literal.Token.ValueText;
            }
            else
            {
                throw Unreachable();
            }

            var value = ValueNodeParser.Parse(args[1].Expression, ctx.AssetCatalog, ctx.FacadeConflicts);
            var target = new OverrideTarget { ComponentType = typeFullName, ChildPath = childPath };

            return value is ValueNode.AssetRef or ValueNode.ObjectRef
                ? new PropertyOverride { Target = target, PropertyPath = propertyPath, ObjectReference = value }
                : new PropertyOverride { Target = target, PropertyPath = propertyPath, Value = value };
        }

        // `.AddComponent<T>(cfg?)` — mirrors ApplyComponent's generic-type-arg extraction and
        // reuses ProcessComponentClosure verbatim for the `c => c.Set(...)` closure. The owner
        // (instance root) and this component's ordinal LogicalId are resolved later, in
        // BuildInstanceNode.
        private static void ApplyAddComponent(NodeBuilder node, InvocationExpressionSyntax invocation, ArgumentListSyntax args, ParserContext ctx, string childPath = "")
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess ||
                memberAccess.Name is not GenericNameSyntax generic ||
                generic.TypeArgumentList.Arguments.Count != 1)
            {
                throw Unreachable();
            }

            var typeFullName = generic.TypeArgumentList.Arguments[0].ToString().Trim();
            var cb = new ComponentBuilder { TypeFullName = typeFullName, ChildPath = childPath };

            if (args.Arguments.Count > 0)
            {
                ProcessComponentClosure(args.Arguments[0].Expression, cb, ctx);
            }

            node.AddedComponents.Add(cb);
        }

        // `.RemoveComponent<T>()` — records a root target (SubKey defaults to (0,0)) unless
        // `childPath` is non-empty (nested, via `.On`); the adapter resolves SubKey to the real
        // (GUID:fileID, ObjectId) pair before diff.
        private static void ApplyRemoveComponent(NodeBuilder node, InvocationExpressionSyntax invocation, string childPath = "")
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess ||
                memberAccess.Name is not GenericNameSyntax generic ||
                generic.TypeArgumentList.Arguments.Count != 1)
            {
                throw Unreachable();
            }

            var typeFullName = generic.TypeArgumentList.Arguments[0].ToString().Trim();
            node.RemovedComponents.Add(new OverrideTarget { ComponentType = typeFullName, ChildPath = childPath });
        }

        // `.AddChild(parentPath, name, cfg?)` — deferred (see ProcessInstanceChain) until the
        // instance's own LogicalId is final, since the added child is parented on it. Builds a
        // child NodeBuilder and reuses ProcessClosure verbatim for the optional closure — the
        // full NodeHandle authoring grammar (components/fields/nested children).
        private static void ApplyAddChild(NodeBuilder node, ArgumentListSyntax args, InvocationExpressionSyntax invocation, ParserContext ctx)
        {
            // arg0 (the PARENT) is a typed façade selector (`t => t.A.B`) or a string path; the child
            // NAME (arg1) is always a string. A typed selector that misses the catalog is a located
            // Conflict, and the child is NOT added — mirrors ApplyScopedOn (specs/27).
            if (!TryResolveChildSelectorArg(args.Arguments[0].Expression, node, ctx, out var parentPath))
            {
                return;
            }

            var name = EvalStringLiteral(args.Arguments[1].Expression);

            var child = new NodeBuilder { Name = name };
            child.AnchorSpan = new SourceSpan(invocation.Span.Start, invocation.Span.Length);
            child.LogicalId = ctx.Resolver.Resolve(null, null, node.LogicalId, name, node.AddedGameObjects.Count);

            if (args.Arguments.Count > 2)
            {
                ProcessClosure(args.Arguments[2].Expression, child, ctx);
            }

            node.AddedGameObjects.Add((parentPath, child));
        }

        // `.RemoveChild(child)` — inline (no LogicalId needed, the target is identified by ChildPath
        // only; the adapter resolves SubKey before diff, same as RemovedComponents). The removed
        // source child is addressed by a typed façade selector (`t => t.A.B`, compiler-checked +
        // rename-auto-syncing) or a string path. A typed selector that misses the catalog is a
        // located Conflict, and nothing is removed — mirrors ApplyScopedOn (specs/27).
        private static void ApplyRemoveChild(NodeBuilder node, ArgumentListSyntax args, ParserContext ctx)
        {
            if (!TryResolveChildSelectorArg(args.Arguments[0].Expression, node, ctx, out var childPath))
            {
                return;
            }

            node.RemovedGameObjects.Add(new OverrideTarget { ChildPath = childPath });
        }

        // Resolves a `.RemoveChild`/`.AddChild` child/parent argument to a RealName-joined child path.
        // A string literal is taken verbatim. A typed selector (`t => t.A.B`) resolves through the
        // FacadeCatalog exactly like ApplyScopedOn's `.On` selector (TryReadSelectorSegments +
        // FacadeCatalog.TryResolveSelector) — a miss records a located UnknownFacadeReference Conflict
        // and returns false, so the caller drops the op (never a silent wrong-target edit).
        private static bool TryResolveChildSelectorArg(ExpressionSyntax arg0, NodeBuilder node, ParserContext ctx, out string childPath)
        {
            if (arg0 is not SimpleLambdaExpressionSyntax)
            {
                childPath = EvalStringLiteral(arg0);
                return true;
            }

            TryReadSelectorSegments(arg0, out var segments, out var byPropertyName);

            if (ctx.FacadeCatalog != null &&
                ctx.FacadeCatalog.TryResolveSelector(node.SourcePrefabGuid ?? "", segments, byPropertyName, out childPath, out _))
            {
                return true;
            }

            var argSpan = new SourceSpan(arg0.Span.Start, arg0.Span.Length);
            ctx.FacadeConflicts.Add(new Conflict
            {
                Kind = ConflictKind.UnknownFacadeReference,
                Reason = $"Unknown facade reference '{string.Join("/", segments)}'.",
                Location = argSpan,
            });
            childPath = "";
            return false;
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
