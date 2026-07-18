using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Lowering;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;

namespace SceneBuilder.Core.Parsing
{
    // Syntax-only Roslyn parser for the M1 builder-file authoring surface (§6).
    // No semantic binding: fixture/builder source references types (ISceneDefinition, SceneRoot)
    // that do not exist in Core, so only CSharpSyntaxTree.ParseText is used.
    public static partial class BuilderParser
    {
        private static readonly string[] TransformPositionalArgs = { "pos", "rot", "scale" };

        public static ParseResult Parse(string source) => ParseCore(source, null);

        public static ParseResult Parse(string source, IdentityMap? existingMap = null) => ParseCore(source, existingMap);

        private static ParseResult ParseCore(string source, IdentityMap? existingMap)
        {
            var tree = CSharpSyntaxTree.ParseText(source);
            var root = (CompilationUnitSyntax)tree.GetRoot();

            var (buildMethod, sceneParamName) = FindBuildMethod(root);
            var body = buildMethod.Body;
            if (body == null)
            {
                throw Fail(buildMethod, "Build method must have a block body");
            }

            var ctx = new ParserContext(sceneParamName, new LogicalIdResolver(existingMap));

            foreach (var statement in body.Statements)
            {
                ProcessStatement(statement, ctx);
            }

            // Component LogicalIds depend on their owning node's FINAL LogicalId, which is
            // only assigned once ProcessAddChain finishes (after ApplyChainedCalls, which is
            // where Components are collected) — so this pass runs once the whole tree is built.
            AssignComponentLogicalIds(ctx.Roots);

            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = ctx.Roots.Select(BuildNode).ToArray(),
            };

            var identityMap = BuildIdentityMap(ctx.Roots, existingMap);
            var anchors = BuildAnchors(ctx.Roots);
            var nodeAnchors = BuildNodeAnchors(ctx.Roots);
            var componentAnchors = BuildComponentAnchors(ctx.Roots);
            var flagPresence = BuildFlagPresence(ctx.Roots);
            var fieldArgumentSpans = BuildFieldArgumentSpans(ctx.Roots);
            var handles = BuildHandles(ctx.Roots);

            // Invert LogicalId->handle-name into name->LogicalId for ObjectRef resolution.
            // Handle var-names are unique C# locals, so no value collision; built defensively
            // (skip duplicate names rather than throw) in case that ever changes.
            var handlesByName = new Dictionary<string, string>();
            foreach (var kv in handles)
            {
                handlesByName.TryAdd(kv.Value, kv.Key);
            }

            model = ObjectRefLowering.Lower(model, name => handlesByName.TryGetValue(name, out var id) ? id : null);

            // File-scope PLAIN `using` directives (no Alias, no static keyword), in document
            // order. `root.Usings` is file-scope-only by construction (namespace-nested and
            // other-file `global using` directives are never in this list). Source-level only —
            // resolves nothing here (b2's job).
            var usings = root.Usings
                .Where(u => u.Alias == null && u.StaticKeyword.IsKind(SyntaxKind.None) && u.Name != null)
                .Select(u => u.Name!.ToString())
                .ToList();

            // Unconditional: every parse reports which sibling groups are distinguishable only by
            // position, and which hand-authored ids collide. Both directions come through here, so
            // neither can skip the check. Order matters: SceneBuilderBuild.FormatAmbiguities renders
            // in list order.
            var ambiguities = ConflictDetector.DuplicateNameConflicts(model, anchors)
                .Concat(ConflictDetector.DuplicateLogicalIdConflicts(nodeAnchors))
                .ToList();

            return new ParseResult { Model = model, IdentityMap = identityMap, Anchors = anchors, NodeAnchors = nodeAnchors, ComponentAnchors = componentAnchors, FlagPresence = flagPresence, FieldArgumentSpans = fieldArgumentSpans, Handles = handles, Ambiguities = ambiguities, Usings = usings };
        }

        // ---- Build-method discovery -------------------------------------------------

        private static (MethodDeclarationSyntax Method, string SceneParamName) FindBuildMethod(CompilationUnitSyntax root)
        {
            MethodDeclarationSyntax? candidate = null;

            foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var implementsSceneDefinition = cls.BaseList?.Types
                    .Any(t => t.Type is IdentifierNameSyntax id && id.Identifier.Text == "ISceneDefinition") == true;
                if (!implementsSceneDefinition)
                {
                    continue;
                }

                var method = cls.Members.OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m => m.Identifier.Text == "Build");
                if (method != null)
                {
                    candidate = method;
                    break;
                }
            }

            if (candidate == null)
            {
                var allBuildMethods = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                    .Where(m => m.Identifier.Text == "Build")
                    .ToList();

                if (allBuildMethods.Count == 1)
                {
                    candidate = allBuildMethods[0];
                }
                else if (allBuildMethods.Count == 0)
                {
                    throw Fail(root, "No Build method found");
                }
                else
                {
                    throw Fail(root, "Multiple Build methods found; ambiguous");
                }
            }

            var param = candidate.ParameterList.Parameters.FirstOrDefault();
            if (param == null)
            {
                throw Fail(candidate, "Build method must declare a scene-root parameter");
            }

            return (candidate, param.Identifier.Text);
        }

        // ---- Statement walk -----------------------------------------------------------

        private static void ProcessStatement(StatementSyntax statement, ParserContext ctx)
        {
            switch (statement)
            {
                case LocalDeclarationStatementSyntax local:
                    if (local.Declaration.Variables.Count != 1)
                    {
                        throw Fail(local, "Unsupported local declaration (expected a single builder handle)");
                    }

                    var declarator = local.Declaration.Variables[0];
                    if (declarator.Initializer == null)
                    {
                        throw Fail(local, "Unsupported local declaration (expected a builder call initializer)");
                    }

                    ProcessBuilderChain(declarator.Initializer.Value, declarator.Identifier.Text, ctx);
                    break;

                case ExpressionStatementSyntax exprStatement:
                    ProcessBuilderChain(exprStatement.Expression, null, ctx);
                    break;

                default:
                    throw Fail(statement, $"Unsupported interleaved control flow ({statement.Kind()})");
            }
        }

        private static void ProcessBuilderChain(ExpressionSyntax expression, string? handleName, ParserContext ctx)
        {
            var (receiver, calls) = UnwrapChain(expression);

            if (calls.Count == 0)
            {
                throw Fail(expression, "Expected a builder call chain");
            }

            if (calls[0].Method == "Add")
            {
                ProcessAddChain(receiver, calls, handleName, ctx);
                return;
            }

            // Setter-only chain applied directly to an already-created node — the closure form
            // `m => m.Transform(...)` where `m` is the node just created by the enclosing Add.
            if (receiver.Identifier.Text == ctx.SceneParamName || !ctx.Handles.TryGetValue(receiver.Identifier.Text, out var node))
            {
                throw Fail(receiver, $"Unknown receiver '{receiver.Identifier.Text}'");
            }

            var explicitId = ApplyChainedCalls(node, calls);
            if (explicitId != null)
            {
                node.LogicalId = explicitId;
            }

            if (handleName != null)
            {
                ctx.Handles[handleName] = node;
                node.Handle = handleName;
            }
        }

        private static void ProcessAddChain(IdentifierNameSyntax receiver, List<(string Method, ArgumentListSyntax Args, InvocationExpressionSyntax Invocation)> calls, string? handleName, ParserContext ctx)
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

            var addArgs = calls[0].Args.Arguments;
            if (addArgs.Count == 0)
            {
                throw Fail(calls[0].Args, "Add requires a name argument");
            }

            var name = EvalStringLiteral(addArgs[0].Expression);
            var node = new NodeBuilder { Name = name };
            node.AnchorSpan = new SourceSpan(calls[0].Invocation.Span.Start, calls[0].Invocation.Span.Length);

            var explicitId = ApplyChainedCalls(node, calls.Skip(1).ToList());

            var siblingIndex = targetList.Count;
            var parentLogicalId = parentNode?.LogicalId;
            node.LogicalId = ctx.Resolver.Resolve(handleName, explicitId, parentLogicalId, name, siblingIndex);

            targetList.Add(node);
            if (handleName != null)
            {
                ctx.Handles[handleName] = node;
                node.Handle = handleName;
            }

            if (addArgs.Count > 1)
            {
                ProcessClosure(addArgs[1].Expression, node, ctx);
            }
        }

        // Applies non-Add chained calls as property setters on `node`; returns the explicit
        // `.Id(...)` value if present (caller decides how it factors into LogicalId priority).
        private static string? ApplyChainedCalls(NodeBuilder node, List<(string Method, ArgumentListSyntax Args, InvocationExpressionSyntax Invocation)> calls)
        {
            string? explicitId = null;

            foreach (var (method, args, invocation) in calls)
            {
                switch (method)
                {
                    case "Transform":
                        ApplyTransform(node, args);
                        break;
                    case "Tag":
                        node.Tag = EvalStringLiteral(args.Arguments[0].Expression);
                        node.HasTag = true;
                        break;
                    case "Layer":
                        node.Layer = (int)EvalFloat(args.Arguments[0].Expression);
                        node.HasLayer = true;
                        break;
                    case "Active":
                        node.Active = EvalBool(args.Arguments[0].Expression);
                        node.HasActive = true;
                        break;
                    case "Static":
                        node.IsStatic = true;
                        node.HasStatic = true;
                        break;
                    case "Id":
                        explicitId = EvalStringLiteral(args.Arguments[0].Expression);
                        var idMemberAccess = (MemberAccessExpressionSyntax)invocation.Expression;
                        var idAnchorStart = idMemberAccess.OperatorToken.SpanStart;
                        node.IdCallSpan = new SourceSpan(idAnchorStart, invocation.Span.End - idAnchorStart);
                        break;
                    case "Component":
                        ApplyComponent(node, args, invocation);
                        break;
                    case "FitSize":
                        ApplyFitSize(node, args, invocation);
                        break;
                    case "SurfaceSnap":
                        ApplySurfaceSnap(node, args, invocation);
                        break;
                    default:
                        throw Fail(args, $"Unsupported builder call '.{method}(...)'");
                }
            }

            return explicitId;
        }

        private static void ProcessClosure(ExpressionSyntax closureExpression, NodeBuilder parentNode, ParserContext ctx)
        {
            if (closureExpression is not SimpleLambdaExpressionSyntax lambda)
            {
                throw Fail(closureExpression, "Unsupported closure form; expected a lambda like `m => ...`");
            }

            var paramName = lambda.Parameter.Identifier.Text;
            var hadPrevious = ctx.Handles.TryGetValue(paramName, out var previous);
            ctx.Handles[paramName] = parentNode;

            try
            {
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
                        throw Fail(lambda.Body, "Unsupported lambda body");
                }
            }
            finally
            {
                if (hadPrevious)
                {
                    ctx.Handles[paramName] = previous!;
                }
                else
                {
                    ctx.Handles.Remove(paramName);
                }
            }
        }

        // ---- Component parsing (b3-t1) ---------------------------------------------------

        // `.Component<T>(configure)` — T's FullName is recorded VERBATIM from the generic
        // type-argument syntax (Core does no namespace resolution; fixtures author FQNs).
        // The AnchorSpan slices ONLY this `.Component<T>(...)` call (dot through this call's
        // own closing paren) — NOT the whole preceding chain.
        private static void ApplyComponent(NodeBuilder node, ArgumentListSyntax args, InvocationExpressionSyntax invocation)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess ||
                memberAccess.Name is not GenericNameSyntax generic ||
                generic.TypeArgumentList.Arguments.Count != 1)
            {
                throw Fail(invocation, "Component<T>() requires exactly one type argument");
            }

            var typeFullName = generic.TypeArgumentList.Arguments[0].ToString().Trim();
            var anchorStart = memberAccess.OperatorToken.SpanStart;

            var cb = new ComponentBuilder
            {
                TypeFullName = typeFullName,
                AnchorSpan = new SourceSpan(anchorStart, invocation.Span.End - anchorStart),
            };

            if (args.Arguments.Count > 0)
            {
                ProcessComponentClosure(args.Arguments[0].Expression, cb);
            }

            node.Components.Add(cb);
        }

        // Mirrors ProcessClosure but self-contained: a component closure only contains
        // `x.Set(...)` calls on the lambda parameter, no node handles / nested Add.
        private static void ProcessComponentClosure(ExpressionSyntax closureExpression, ComponentBuilder cb)
        {
            if (closureExpression is not SimpleLambdaExpressionSyntax lambda)
            {
                throw Fail(closureExpression, "Unsupported closure form; expected a lambda like `c => ...`");
            }

            var paramName = lambda.Parameter.Identifier.Text;

            switch (lambda.Body)
            {
                case BlockSyntax block:
                    foreach (var statement in block.Statements)
                    {
                        if (statement is not ExpressionStatementSyntax exprStatement)
                        {
                            throw Fail(statement, "Unsupported statement in component closure (expected .Set(...) calls)");
                        }

                        ProcessComponentSetCall(exprStatement.Expression, paramName, cb);
                    }
                    break;

                case ExpressionSyntax exprBody:
                    ProcessComponentSetCall(exprBody, paramName, cb);
                    break;

                default:
                    throw Fail(lambda.Body, "Unsupported lambda body");
            }
        }

        private static void ProcessComponentSetCall(ExpressionSyntax expression, string paramName, ComponentBuilder cb)
        {
            if (expression is not InvocationExpressionSyntax setInvocation ||
                setInvocation.Expression is not MemberAccessExpressionSyntax setMemberAccess ||
                setMemberAccess.Name.Identifier.Text != "Set" ||
                setMemberAccess.Expression is not IdentifierNameSyntax setReceiver ||
                setReceiver.Identifier.Text != paramName)
            {
                throw Fail(expression, "Expected a `.Set(...)` call in component closure");
            }

            var (key, value, valueSpan) = ParseSetCall(setInvocation);
            cb.Fields.Add(new KeyValuePair<string, ValueNode>(key, value));
            cb.FieldValueSpans.Add(new KeyValuePair<string, SourceSpan>(key, valueSpan));
        }

        // Field-key convention (§ field-key convention): string-literal arg0 -> verbatim key
        // (no m_/accessibility mangling); `r => r.member` lambda arg0 -> provisional
        // "member:"+memberName key (unresolved — Core never maps member->serialized-path).
        // KEY handling stays fail-loud (keys are not values); VALUE lowering is delegated to
        // ValueNodeParser (b3-t2), which is total and never throws.
        private static (string Key, ValueNode Value, SourceSpan ValueSpan) ParseSetCall(InvocationExpressionSyntax setInvocation)
        {
            var args = setInvocation.ArgumentList.Arguments;
            if (args.Count != 2)
            {
                throw Fail(setInvocation, "Set(...) requires exactly two arguments");
            }

            string key;
            var keyExpr = args[0].Expression;
            if (keyExpr is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                key = literal.Token.ValueText;
            }
            else if (keyExpr is SimpleLambdaExpressionSyntax { Body: MemberAccessExpressionSyntax memberAccess })
            {
                key = "member:" + memberAccess.Name.Identifier.Text;
            }
            else
            {
                throw Fail(keyExpr, "Unsupported Set(...) key form (expected a string literal or `r => r.member`)");
            }

            var valueExpr = args[1].Expression;
            var value = ValueNodeParser.Parse(valueExpr);
            var valueSpan = new SourceSpan(valueExpr.SpanStart, valueExpr.Span.Length);
            return (key, value, valueSpan);
        }

        // ---- Invocation-chain unwrap ----------------------------------------------------

        private static (IdentifierNameSyntax Receiver, List<(string Method, ArgumentListSyntax Args, InvocationExpressionSyntax Invocation)> Calls) UnwrapChain(ExpressionSyntax expression)
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

                    throw Fail(invocation, "Unsupported invocation form");
                }

                if (current is IdentifierNameSyntax identifier)
                {
                    calls.Reverse();
                    return (identifier, calls);
                }

                throw Fail(current, "Unsupported receiver expression");
            }
        }

        // ---- Transform argument lowering -----------------------------------------------

        private static void ApplyTransform(NodeBuilder node, ArgumentListSyntax args)
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
                    throw Fail(arg, "Too many Transform arguments");
                }

                if (arg.Expression is not TupleExpressionSyntax tuple || tuple.Arguments.Count != 3)
                {
                    throw Fail(arg.Expression, $"Transform.{paramName} expects a 3-tuple");
                }

                var x = EvalFloat(tuple.Arguments[0].Expression);
                var y = EvalFloat(tuple.Arguments[1].Expression);
                var z = EvalFloat(tuple.Arguments[2].Expression);

                switch (paramName)
                {
                    case "pos":
                        node.Position = new Vec3(x, y, z);
                        break;
                    case "rot":
                        node.Rotation = Rotation.EulerToQuat(x, y, z);
                        break;
                    case "scale":
                        node.Scale = new Vec3(x, y, z);
                        break;
                    default:
                        throw Fail(arg, $"Unknown Transform argument '{paramName}'");
                }
            }
        }

        // ---- Literal evaluation ---------------------------------------------------------

        private static string EvalStringLiteral(ExpressionSyntax expression)
        {
            if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                return literal.Token.ValueText;
            }

            throw Fail(expression, "Expected a string literal");
        }

        private static bool EvalBool(ExpressionSyntax expression)
        {
            if (expression is LiteralExpressionSyntax literal)
            {
                if (literal.IsKind(SyntaxKind.TrueLiteralExpression))
                {
                    return true;
                }

                if (literal.IsKind(SyntaxKind.FalseLiteralExpression))
                {
                    return false;
                }
            }

            throw Fail(expression, "Expected a boolean literal");
        }

        private static float EvalFloat(ExpressionSyntax expression)
        {
            if (expression is PrefixUnaryExpressionSyntax unary && unary.OperatorToken.IsKind(SyntaxKind.MinusToken))
            {
                return -EvalFloat(unary.Operand);
            }

            if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.NumericLiteralExpression))
            {
                return Convert.ToSingle(literal.Token.Value, System.Globalization.CultureInfo.InvariantCulture);
            }

            throw Fail(expression, "Expected a numeric literal");
        }

        // ---- Component LogicalId synthesis ------------------------------------------------

        // Synthesizes each component's LogicalId as `{ownerLogicalId}/{Type.FullName}#{ordinal}`
        // (ordinal among same-typed components on that owner) — deterministic-from-source, so
        // an identical re-parse yields the identical id (second-Sync no-op holds), and stable
        // against insertion of other-typed components. Runs once the whole tree's LogicalIds
        // are finalized (see ParseCore).
        private static void AssignComponentLogicalIds(List<NodeBuilder> roots)
        {
            foreach (var root in roots)
            {
                AssignComponentLogicalIds(root);
            }
        }

        private static void AssignComponentLogicalIds(NodeBuilder node)
        {
            var ordinalByType = new Dictionary<string, int>();
            foreach (var component in node.Components)
            {
                var ordinal = ordinalByType.TryGetValue(component.TypeFullName, out var count) ? count : 0;
                ordinalByType[component.TypeFullName] = ordinal + 1;
                component.LogicalId = $"{node.LogicalId}/{component.TypeFullName}#{ordinal}";
            }

            foreach (var child in node.Children)
            {
                AssignComponentLogicalIds(child);
            }
        }

        // ---- IdentityMap construction -----------------------------------------------------

        // Builds one IdentityMapEntry per parsed node, pre-order (document/declared order),
        // carrying over each node's GlobalObjectId from `existingMap` when its resolved
        // LogicalId matches a persisted entry (so a re-parse never wipes saved ids).
        private static IdentityMap BuildIdentityMap(List<NodeBuilder> roots, IdentityMap? existingMap)
        {
            var globalObjectIdByLogicalId = existingMap?.Entries
                .ToDictionary(e => e.LogicalId, e => e.GlobalObjectId)
                ?? new Dictionary<string, string>();

            var entries = new List<IdentityMapEntry>();
            for (var i = 0; i < roots.Count; i++)
            {
                CollectIdentityEntries(roots[i], null, i, globalObjectIdByLogicalId, entries);
            }

            return new IdentityMap
            {
                SchemaVersion = 1,
                Scene = existingMap?.Scene ?? "",
                Entries = entries.ToArray(),
                Assets = existingMap?.Assets ?? Array.Empty<AssetEntry>(),
            };
        }

        private static void CollectIdentityEntries(NodeBuilder node, string? parentLogicalId, int siblingIndex, Dictionary<string, string> globalObjectIdByLogicalId, List<IdentityMapEntry> entries)
        {
            entries.Add(new IdentityMapEntry
            {
                LogicalId = node.LogicalId,
                GlobalObjectId = globalObjectIdByLogicalId.TryGetValue(node.LogicalId, out var globalObjectId) ? globalObjectId : "",
                Kind = "GameObject",
                ComponentType = null,
                ParentLogicalId = parentLogicalId,
                Name = node.Name,
                SiblingIndex = siblingIndex,
            });

            foreach (var component in node.Components)
            {
                entries.Add(new IdentityMapEntry
                {
                    LogicalId = component.LogicalId,
                    GlobalObjectId = globalObjectIdByLogicalId.TryGetValue(component.LogicalId, out var componentGlobalObjectId) ? componentGlobalObjectId : "",
                    Kind = "Component",
                    ComponentType = component.TypeFullName,
                    ParentLogicalId = node.LogicalId,
                });
            }

            for (var i = 0; i < node.Children.Count; i++)
            {
                CollectIdentityEntries(node.Children[i], node.LogicalId, i, globalObjectIdByLogicalId, entries);
            }
        }

        // ---- Anchor construction -----------------------------------------------------------

        // Builds one LogicalId->SourceSpan entry per parsed node, pre-order, keyed by each node's
        // FINAL LogicalId (post `.Id(...)` resolution) — mirrors BuildIdentityMap/CollectIdentityEntries.
        private static IReadOnlyDictionary<string, SourceSpan> BuildAnchors(List<NodeBuilder> roots)
        {
            var anchors = new Dictionary<string, SourceSpan>();
            foreach (var root in roots)
            {
                CollectAnchors(root, anchors);
            }

            return anchors;
        }

        private static void CollectAnchors(NodeBuilder node, Dictionary<string, SourceSpan> anchors)
        {
            anchors[node.LogicalId] = node.AnchorSpan;

            foreach (var child in node.Children)
            {
                CollectAnchors(child, anchors);
            }
        }

        // Builds one NodeAnchor per parsed node, pre-order, NEVER collapsed by LogicalId — mirrors
        // BuildAnchors/CollectAnchors but appends to a List (not a dict-indexer assignment), so two
        // nodes resolving to the same LogicalId (a colliding hand-authored `.Id(...)`) both survive.
        private static IReadOnlyList<NodeAnchor> BuildNodeAnchors(List<NodeBuilder> roots)
        {
            var nodeAnchors = new List<NodeAnchor>();
            foreach (var root in roots)
            {
                CollectNodeAnchors(root, nodeAnchors);
            }

            return nodeAnchors;
        }

        private static void CollectNodeAnchors(NodeBuilder node, List<NodeAnchor> nodeAnchors)
        {
            nodeAnchors.Add(new NodeAnchor
            {
                LogicalId = node.LogicalId,
                Name = node.Name,
                Span = node.AnchorSpan,
                Handle = node.Handle,
                IdCallSpan = node.IdCallSpan,
            });

            foreach (var child in node.Children)
            {
                CollectNodeAnchors(child, nodeAnchors);
            }
        }

        // Builds one LogicalId->handle(var) name entry per parsed node with an AUTHORED handle
        // (NodeBuilder.Handle set only at the two ctx.Handles[handleName]=node registration
        // spots), pre-order, keyed by each node's FINAL LogicalId — mirrors
        // BuildAnchors/CollectAnchors, but OMITS nodes without an authored Handle.
        private static IReadOnlyDictionary<string, string> BuildHandles(List<NodeBuilder> roots)
        {
            var handles = new Dictionary<string, string>();
            foreach (var root in roots)
            {
                CollectHandles(root, handles);
            }

            return handles;
        }

        private static void CollectHandles(NodeBuilder node, Dictionary<string, string> handles)
        {
            if (node.Handle != null)
            {
                handles[node.LogicalId] = node.Handle;
            }

            foreach (var child in node.Children)
            {
                CollectHandles(child, handles);
            }
        }

        // Builds one component-LogicalId->SourceSpan entry per parsed component, pre-order,
        // kept SEPARATE from BuildAnchors/Anchors (GameObject-only).
        private static IReadOnlyDictionary<string, SourceSpan> BuildComponentAnchors(List<NodeBuilder> roots)
        {
            var anchors = new Dictionary<string, SourceSpan>();
            foreach (var root in roots)
            {
                CollectComponentAnchors(root, anchors);
            }

            return anchors;
        }

        private static void CollectComponentAnchors(NodeBuilder node, Dictionary<string, SourceSpan> anchors)
        {
            foreach (var component in node.Components)
            {
                anchors[component.LogicalId] = component.AnchorSpan;
            }

            foreach (var child in node.Children)
            {
                CollectComponentAnchors(child, anchors);
            }
        }

        // Builds one componentLogicalId -> (fieldKey -> value SourceSpan) entry per parsed
        // component, pre-order (mirrors BuildComponentAnchors/CollectComponentAnchors) — feed-
        // forward for b5's span-local field-argument patching.
        private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, SourceSpan>> BuildFieldArgumentSpans(List<NodeBuilder> roots)
        {
            var spans = new Dictionary<string, IReadOnlyDictionary<string, SourceSpan>>();
            foreach (var root in roots)
            {
                CollectFieldArgumentSpans(root, spans);
            }

            return spans;
        }

        private static void CollectFieldArgumentSpans(NodeBuilder node, Dictionary<string, IReadOnlyDictionary<string, SourceSpan>> spans)
        {
            foreach (var component in node.Components)
            {
                var fieldSpans = new Dictionary<string, SourceSpan>();
                foreach (var (key, span) in component.FieldValueSpans)
                {
                    fieldSpans[key] = span;
                }

                spans[component.LogicalId] = fieldSpans;
            }

            foreach (var child in node.Children)
            {
                CollectFieldArgumentSpans(child, spans);
            }
        }

        // ---- Flag presence construction -----------------------------------------------------

        // Builds one LogicalId->FlagPresence entry per parsed node, pre-order, keyed by each
        // node's FINAL LogicalId (post `.Id(...)` resolution) — mirrors BuildAnchors/CollectAnchors.
        private static IReadOnlyDictionary<string, FlagPresence> BuildFlagPresence(List<NodeBuilder> roots)
        {
            var presence = new Dictionary<string, FlagPresence>();
            foreach (var root in roots)
            {
                CollectFlagPresence(root, presence);
            }

            return presence;
        }

        private static void CollectFlagPresence(NodeBuilder node, Dictionary<string, FlagPresence> presence)
        {
            presence[node.LogicalId] = new FlagPresence(node.HasTag, node.HasLayer, node.HasActive, node.HasStatic);

            foreach (var child in node.Children)
            {
                CollectFlagPresence(child, presence);
            }
        }

        // ---- Materialization --------------------------------------------------------------

        private static GameObjectNode BuildNode(NodeBuilder builder) => new()
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
            Components = builder.Components.Select(BuildComponent).ToArray(),
            Children = builder.Children.Select(BuildNode).ToArray(),
        };

        private static ComponentData BuildComponent(ComponentBuilder cb) => new()
        {
            LogicalId = cb.LogicalId,
            Type = new TypeRef(cb.TypeFullName),
            Fields = new FieldMap(cb.Fields),
        };

        // ---- Fail-loud helper -----------------------------------------------------------

        private static ParseException Fail(SyntaxNode node, string message)
        {
            var position = node.GetLocation().GetLineSpan().StartLinePosition;
            var line = position.Line + 1;
            var column = position.Character + 1;
            return new ParseException($"{message} at line {line}, column {column}.", line, column);
        }

        // ---- Mutable intermediate tree ---------------------------------------------------

        private sealed class NodeBuilder
        {
            public string Name = "";
            public string LogicalId = "";
            public string Tag = "Untagged";
            public int Layer;
            public bool Active = true;
            public bool IsStatic;
            public bool HasTag;
            public bool HasLayer;
            public bool HasActive;
            public bool HasStatic;
            public Vec3? Position;
            public Quat? Rotation;
            public Vec3? Scale;
            public SourceSpan AnchorSpan;
            public string? Handle;
            public SourceSpan? IdCallSpan;
            public ChannelMask DrivenChannels;
            public readonly List<NodeBuilder> Children = new();
            public readonly List<ComponentBuilder> Components = new();
        }

        private sealed class ComponentBuilder
        {
            public string TypeFullName = "";
            public string LogicalId = "";
            public SourceSpan AnchorSpan;
            public readonly List<KeyValuePair<string, ValueNode>> Fields = new();
            public readonly List<KeyValuePair<string, SourceSpan>> FieldValueSpans = new();
        }

        private sealed class ParserContext
        {
            public ParserContext(string sceneParamName, LogicalIdResolver resolver)
            {
                SceneParamName = sceneParamName;
                Resolver = resolver;
            }

            public string SceneParamName { get; }
            public LogicalIdResolver Resolver { get; }
            public Dictionary<string, NodeBuilder> Handles { get; } = new();
            public List<NodeBuilder> Roots { get; } = new();
        }
    }
}
