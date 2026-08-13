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

        public static ParseResult Parse(string source) => ParseCore(source, null, null, null);

        public static ParseResult Parse(string source, IdentityMap? existingMap = null) => ParseCore(source, existingMap, null, null);

        // b4-t1: the FacadeCatalog-aware overload — Instance(Prefabs.X) resolves X via the
        // catalog straight to a Guid; an unknown entry (or a null catalog) is a located Conflict
        // (never a throw, never a silent drop). See research.md.
        // b2-t1: `assetCatalog` is optional (=null) so the 3-arg callers (ComponentTypeNormalizer,
        // SceneBuilderBuild) keep binding unchanged. See research.md.
        public static ParseResult Parse(string source, IdentityMap? existingMap, FacadeCatalog? facadeCatalog, AssetCatalog? assetCatalog = null) =>
            ParseCore(source, existingMap, facadeCatalog, assetCatalog);

        private static ParseResult ParseCore(string source, IdentityMap? existingMap, FacadeCatalog? facadeCatalog, AssetCatalog? assetCatalog)
        {
            var tree = CSharpSyntaxTree.ParseText(source);
            var root = (CompilationUnitSyntax)tree.GetRoot();

            var (buildMethod, sceneParamName, isVariant, variantBaseExpression, variantClassName) = FindBuildMethod(root);
            var body = buildMethod.Body;
            if (body == null)
            {
                throw Fail(buildMethod, "Build method must have a block body");
            }

            RecognizeOrThrow(tree, body, sceneParamName, isVariant);

            var ctx = new ParserContext(sceneParamName, new LogicalIdResolver(existingMap), facadeCatalog, assetCatalog, isVariant);

            // A variant's single root IS the base prefab instance — pre-seeded BEFORE the
            // statement walk so root-level `.Override`/`.AddComponent`/`.RemoveComponent`/`.On`
            // verbs (routed by ProcessBuilderChain's variant arm) land on it, reusing the same
            // lowering a nested Instance uses. A catalog miss (or no catalog) is a located
            // Conflict, never a throw, never a silent drop — the overrides still land.
            NodeBuilder? variantRootNode = null;
            if (isVariant)
            {
                var basePropertyName = variantBaseExpression?.Name.Identifier.Text ?? "";
                // The persisted root name must match the variant's own prefab filename stem
                // (== the variant class name), not the base facade property name: SaveAsPrefabAsset
                // forces the saved root to the filename stem regardless, so naming it anything else
                // creates a permanent desired-vs-live mismatch that a build rejects (see
                // PrefabBuildSyncTarget's single-root-name check).
                variantRootNode = new NodeBuilder { Name = variantClassName ?? basePropertyName, IsInstance = true };

                if (facadeCatalog != null && variantBaseExpression != null && facadeCatalog.TryGetGuid(basePropertyName, out var guid))
                {
                    variantRootNode.SourcePrefabGuid = guid;
                }
                else if (variantBaseExpression != null)
                {
                    var argSpan = new SourceSpan(variantBaseExpression.Span.Start, variantBaseExpression.Span.Length);
                    ctx.FacadeConflicts.Add(new Conflict
                    {
                        Kind = ConflictKind.UnknownFacadeReference,
                        Reason = $"Unknown facade reference 'Prefabs.{basePropertyName}'.",
                        Location = argSpan,
                    });
                }

                variantRootNode.LogicalId = ctx.Resolver.Resolve(null, null, null, variantRootNode.Name, 0);
                ctx.Roots.Add(variantRootNode);
                ctx.VariantRootNode = variantRootNode;
            }

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
                VariantBase = isVariant ? new AssetRef { Guid = variantRootNode!.SourcePrefabGuid ?? "" } : null,
            };

            var identityMap = BuildIdentityMap(ctx.Roots, existingMap);

            // Merge-only: the base prefab's AssetEntry joins whatever existingMap.Assets already
            // carried, never replacing it.
            if (isVariant && variantRootNode != null && !string.IsNullOrEmpty(variantRootNode.SourcePrefabGuid) &&
                !identityMap.Assets.Any(a => a.Guid == variantRootNode.SourcePrefabGuid))
            {
                identityMap = identityMap with
                {
                    Assets = identityMap.Assets.Append(new AssetEntry { Guid = variantRootNode.SourcePrefabGuid }).ToArray(),
                };
            }
            var anchors = BuildAnchors(ctx.Roots);
            var nodeAnchors = BuildNodeAnchors(ctx.Roots);
            var componentAnchors = BuildComponentAnchors(ctx.Roots);
            var chainedComponents = BuildChainedComponents(ctx.Roots);
            var flagPresence = BuildFlagPresence(ctx.Roots);
            var fieldArgumentSpans = BuildFieldArgumentSpans(ctx.Roots);
            var listenerCallSpans = BuildListenerCallSpans(ctx.Roots);
            var handles = BuildHandles(ctx.Roots);

            // Invert LogicalId->handle-name into name->LogicalId for ObjectRef resolution.
            // Handle var-names are unique C# locals, so no value collision; built defensively
            // (skip duplicate names rather than throw) in case that ever changes.
            var handlesByName = new Dictionary<string, string>();
            foreach (var kv in handles)
            {
                handlesByName.TryAdd(kv.Value, kv.Key);
            }

            // `.Ref<T>()`-captured component vars, resolved now that every component's
            // LogicalId is final. Kept as its own name->LogicalId map (never merged into
            // handlesByName) so a listener target always resolves against the component space
            // FIRST -- the invariant that a listener target is a component LogicalId, never the
            // owning GameObject's, holds by construction.
            var componentRefsByName = ResolveComponentRefs(ctx);
            var componentHandles = new Dictionary<string, string>();
            foreach (var kv in componentRefsByName)
            {
                componentHandles.TryAdd(kv.Value, kv.Key);
            }

            model = ObjectRefLowering.Lower(model, name =>
                componentRefsByName.TryGetValue(name, out var componentId) ? componentId :
                handlesByName.TryGetValue(name, out var nodeId) ? nodeId : null);

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
                .Concat(ctx.FacadeConflicts)
                .ToList();

            return new ParseResult { Model = model, IdentityMap = identityMap, Anchors = anchors, NodeAnchors = nodeAnchors, ComponentAnchors = componentAnchors, FlagPresence = flagPresence, FieldArgumentSpans = fieldArgumentSpans, ListenerCallSpans = listenerCallSpans, Handles = handles, ComponentHandles = componentHandles, Ambiguities = ambiguities, Usings = usings, ChainedComponents = chainedComponents };
        }

        // ---- Build-method discovery -------------------------------------------------

        private static (MethodDeclarationSyntax Method, string SceneParamName, bool IsVariant, MemberAccessExpressionSyntax? VariantBaseExpression, string? VariantClassName) FindBuildMethod(CompilationUnitSyntax root)
        {
            MethodDeclarationSyntax? candidate = null;
            ClassDeclarationSyntax? matchedClass = null;
            var isVariant = false;

            foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var matchedInterface = cls.BaseList?.Types
                    .Select(t => t.Type)
                    .OfType<IdentifierNameSyntax>()
                    .FirstOrDefault(id => id.Identifier.Text is "ISceneDefinition" or "IPrefabDefinition" or "IPrefabVariantDefinition");
                if (matchedInterface == null)
                {
                    continue;
                }

                var method = cls.Members.OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m => m.Identifier.Text == "Build");
                if (method != null)
                {
                    candidate = method;
                    matchedClass = cls;
                    isVariant = matchedInterface.Identifier.Text == "IPrefabVariantDefinition";
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

            MemberAccessExpressionSyntax? variantBaseExpression = null;
            if (isVariant && matchedClass != null)
            {
                var baseProperty = matchedClass.Members.OfType<PropertyDeclarationSyntax>()
                    .FirstOrDefault(pd => pd.Identifier.Text == "Base");
                variantBaseExpression = baseProperty?.ExpressionBody?.Expression as MemberAccessExpressionSyntax;
            }

            var variantClassName = isVariant ? matchedClass?.Identifier.Text : null;

            return (candidate, param.Identifier.Text, isVariant, variantBaseExpression, variantClassName);
        }

        // ---- Statement walk -----------------------------------------------------------

        private static void ProcessStatement(StatementSyntax statement, ParserContext ctx)
        {
            switch (statement)
            {
                case LocalDeclarationStatementSyntax local:
                    if (local.Declaration.Variables.Count != 1)
                    {
                        throw Unreachable();
                    }

                    var declarator = local.Declaration.Variables[0];
                    if (declarator.Initializer == null)
                    {
                        throw Unreachable();
                    }

                    ProcessBuilderChain(declarator.Initializer.Value, declarator.Identifier.Text, ctx, statementLevel: true);
                    break;

                case ExpressionStatementSyntax exprStatement:
                    ProcessBuilderChain(exprStatement.Expression, null, ctx, statementLevel: true);
                    break;

                default:
                    throw Unreachable();
            }
        }

        // b3-t5: `statementLevel` is true only when `expression` IS the statement's whole RHS/
        // expression (the two ProcessStatement arms above) — never for a same-statement closure
        // body (ProcessClosure's expression-body arm keeps the default FALSE: that shape has no
        // statement list of its own, so a component reached through it can never be its own
        // statement). Feeds the setter-only arm's StatementAnchored marking below.
        private static void ProcessBuilderChain(ExpressionSyntax expression, string? handleName, ParserContext ctx, bool statementLevel = false)
        {
            var (receiver, calls) = UnwrapChain(expression);

            if (calls.Count == 0)
            {
                throw Unreachable();
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

            // A variant's root-level override verbs (`root.Override(...)`/`.AddComponent<T>()`/
            // `.RemoveComponent<T>()`/`.On(...)`), authored directly on the Build param — routes
            // onto the pre-seeded VariantRootNode via ProcessVariantRootChain (the same per-verb
            // lowering ProcessInstanceChain uses), never a fresh node.
            if (ctx.IsVariant && receiver.Identifier.Text == ctx.SceneParamName &&
                calls[0].Method is "Override" or "AddComponent" or "RemoveComponent" or "On")
            {
                ProcessVariantRootChain(calls, ctx);
                return;
            }

            // Setter-only chain applied directly to an already-created node — the closure form
            // `m => m.Transform(...)` where `m` is the node just created by the enclosing Add.
            if (receiver.Identifier.Text == ctx.SceneParamName || !ctx.Handles.TryGetValue(receiver.Identifier.Text, out var node))
            {
                throw Unreachable();
            }

            var (remainingCalls, refCall) = SplitTrailingComponentRef(calls);

            var explicitId = ApplyChainedCalls(node, remainingCalls, ctx);
            if (explicitId != null)
            {
                node.LogicalId = explicitId;
            }

            // b3-t5: a lone `handle.Component<T>(...)`/`.FitSize(...)`/`.SurfaceSnap(...)` call
            // that IS this statement's whole expression is the "own statement" shape
            // (`crate.Component<T>(...);`) — the ONLY case a RemoveStatement/ReorderStatement
            // anchored on it may safely resolve against the ENCLOSING statement. Two or more
            // chained calls on one statement (`crate.Component<A>().Component<B>();`) each still
            // live partly inside a chain another call also occupies, so none of them qualifies;
            // they stay StatementAnchored = false (the pinned default) like every other chained
            // shape. Reads the ORIGINAL unsplit `calls` so a trailing `.Ref<T>()` keeps this
            // conservative false.
            if (statementLevel && calls.Count == 1 && calls[0].Method is "Component" or "FitSize" or "SurfaceSnap")
            {
                node.Components[node.Components.Count - 1].StatementAnchored = true;
            }

            BindChainHandle(node, handleName, refCall, ctx);
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
                throw Unreachable();
            }

            var addArgs = calls[0].Args.Arguments;
            if (addArgs.Count == 0)
            {
                throw Unreachable();
            }

            var name = EvalStringLiteral(addArgs[0].Expression);
            var node = new NodeBuilder { Name = name };
            node.AnchorSpan = new SourceSpan(calls[0].Invocation.Span.Start, calls[0].Invocation.Span.Length);

            var (remainingCalls, refCall) = SplitTrailingComponentRef(calls.Skip(1).ToList());
            var explicitId = ApplyChainedCalls(node, remainingCalls, ctx);

            var siblingIndex = targetList.Count;
            var parentLogicalId = parentNode?.LogicalId;
            // A trailing `.Ref<T>()` names a COMPONENT, not this GameObject, so the var is withheld
            // from LogicalId resolution -- the node still gets its synthesized/explicit id.
            node.LogicalId = ctx.Resolver.Resolve(refCall == null ? handleName : null, explicitId, parentLogicalId, name, siblingIndex);

            targetList.Add(node);
            BindChainHandle(node, handleName, refCall, ctx);

            if (addArgs.Count > 1)
            {
                ProcessClosure(addArgs[1].Expression, node, ctx);
            }
        }

        // Applies non-Add chained calls as property setters on `node`; returns the explicit
        // `.Id(...)` value if present (caller decides how it factors into LogicalId priority).
        private static string? ApplyChainedCalls(NodeBuilder node, List<(string Method, ArgumentListSyntax Args, InvocationExpressionSyntax Invocation)> calls, ParserContext ctx)
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
                        ApplyComponent(node, args, invocation, ctx);
                        break;
                    case "FitSize":
                        ApplyFitSize(node, args, invocation, ctx);
                        break;
                    case "SurfaceSnap":
                        ApplySurfaceSnap(node, args, invocation, ctx);
                        break;
                    case "RectTransform":
                        ApplyRectTransform(node, args);
                        break;
                    default:
                        throw Unreachable();
                }
            }

            return explicitId;
        }

        private static void ProcessClosure(ExpressionSyntax closureExpression, NodeBuilder parentNode, ParserContext ctx)
        {
            if (closureExpression is not SimpleLambdaExpressionSyntax lambda)
            {
                throw Unreachable();
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
                        throw Unreachable();
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
        private static void ApplyComponent(NodeBuilder node, ArgumentListSyntax args, InvocationExpressionSyntax invocation, ParserContext ctx)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess ||
                memberAccess.Name is not GenericNameSyntax generic ||
                generic.TypeArgumentList.Arguments.Count != 1)
            {
                throw Unreachable();
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
                ProcessComponentClosure(args.Arguments[0].Expression, cb, ctx);
            }

            node.Components.Add(cb);
        }

        // Mirrors ProcessClosure but self-contained: a component closure only contains
        // `x.Set(...)`/`x.OnClick(...)`/`x.OnEvent(...)` calls on the lambda parameter, no node
        // handles / nested Add.
        private static void ProcessComponentClosure(ExpressionSyntax closureExpression, ComponentBuilder cb, ParserContext ctx)
        {
            if (closureExpression is not SimpleLambdaExpressionSyntax lambda)
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

                        ProcessComponentSetCall(exprStatement.Expression, paramName, cb, ctx);
                    }
                    break;

                case ExpressionSyntax exprBody:
                    ProcessComponentSetCall(exprBody, paramName, cb, ctx);
                    break;

                default:
                    throw Unreachable();
            }
        }

        private static void ProcessComponentSetCall(ExpressionSyntax expression, string paramName, ComponentBuilder cb, ParserContext ctx)
        {
            if (expression is not InvocationExpressionSyntax invocation ||
                invocation.Expression is not MemberAccessExpressionSyntax memberAccess ||
                memberAccess.Expression is not IdentifierNameSyntax receiver ||
                receiver.Identifier.Text != paramName)
            {
                throw Unreachable();
            }

            switch (memberAccess.Name.Identifier.Text)
            {
                case "Set":
                    var (key, value, valueSpan) = ParseSetCall(invocation, ctx);
                    cb.Fields.Add(new KeyValuePair<string, ValueNode>(key, value));
                    cb.FieldValueSpans.Add(new KeyValuePair<string, SourceSpan>(key, valueSpan));
                    break;
                case "OnClick":
                    ParseListenerCall(invocation, cb, ctx, isOnEvent: false);
                    break;
                case "OnEvent":
                    ParseListenerCall(invocation, cb, ctx, isOnEvent: true);
                    break;
                case "SetRef":
                {
                    var (refKey, refValue, refValueSpan) = ParseSetRefCall(invocation, ctx);
                    cb.Fields.Add(new KeyValuePair<string, ValueNode>(refKey, refValue));
                    cb.FieldValueSpans.Add(new KeyValuePair<string, SourceSpan>(refKey, refValueSpan));
                    break;
                }
                default:
                    throw Unreachable();
            }
        }

        // The ONE composer of the transient `member:<name>` field key (§ field-key convention):
        // every `r => r.member`-shaped selector — `.Set(x => x.field, ...)`, `.Override(...).Set(...)`,
        // and `.OnEvent(x => x.field, ...)` alike — routes through this so the key spelling can never
        // drift between the three call sites.
        private static string MemberFieldKey(MemberAccessExpressionSyntax selectorBody) =>
            "member:" + selectorBody.Name.Identifier.Text;

        // Field-key convention (§ field-key convention): string-literal arg0 -> verbatim key
        // (no m_/accessibility mangling); `r => r.member` lambda arg0 -> provisional
        // "member:"+memberName key (unresolved — Core never maps member->serialized-path).
        // KEY handling stays fail-loud (keys are not values); VALUE lowering is delegated to
        // ValueNodeParser (b3-t2), which is total and never throws.
        private static (string Key, ValueNode Value, SourceSpan ValueSpan) ParseSetCall(InvocationExpressionSyntax setInvocation, ParserContext ctx)
        {
            var args = setInvocation.ArgumentList.Arguments;
            if (args.Count != 2)
            {
                throw Unreachable();
            }

            string key;
            var keyExpr = args[0].Expression;
            if (keyExpr is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                key = literal.Token.ValueText;
            }
            else if (keyExpr is SimpleLambdaExpressionSyntax { Body: MemberAccessExpressionSyntax memberAccess })
            {
                key = MemberFieldKey(memberAccess);
            }
            else
            {
                throw Unreachable();
            }

            var valueExpr = args[1].Expression;
            var value = ValueNodeParser.Parse(valueExpr, ctx.AssetCatalog, ctx.FacadeConflicts);
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

                    throw Unreachable();
                }

                if (current is IdentifierNameSyntax identifier)
                {
                    calls.Reverse();
                    return (identifier, calls);
                }

                throw Unreachable();
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
                    throw Unreachable();
                }

                if (arg.Expression is not TupleExpressionSyntax tuple || tuple.Arguments.Count != 3)
                {
                    throw Unreachable();
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
                        throw Unreachable();
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

            throw Unreachable();
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

            throw Unreachable();
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

            throw Unreachable();
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
                component.LogicalId = ComponentTargetResolution.ComposeLogicalId(node.LogicalId, component.TypeFullName, ordinal);
            }

            foreach (var child in node.Children)
            {
                AssignComponentLogicalIds(child);
            }
        }

        // ---- Materialization --------------------------------------------------------------

        private static GameObjectNode BuildNode(NodeBuilder builder)
        {
            if (builder.IsInstance)
            {
                return BuildInstanceNode(builder);
            }

            return BuildPlainNode(builder);
        }

        private static GameObjectNode BuildPlainNode(NodeBuilder builder) => new()
        {
            LogicalId = builder.LogicalId,
            Name = builder.Name,
            Tag = builder.Tag,
            Layer = builder.Layer,
            Active = builder.Active,
            IsStatic = builder.IsStatic,
            Transform = BuildTransformData(builder),
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

        // Reserved for the pre-recognition discovery layer (FindBuildMethod / block-body check),
        // which runs BEFORE RecognizeOrThrow and is NOT part of the flat-shape body grammar. Almost
        // every body-shape rejection originates SOLELY from SceneBuilder.Grammar.FlatShapeRecognizer
        // (via RecognizeOrThrow) — the single grammar source. The one exception is ReadCallState's
        // unknown-name arm (BuilderParser.UnityEvents.cs), which calls Fail as defense-in-depth: the
        // recognizer already rejects an unknown callState name before the body walk runs, so that arm
        // is unreachable via Parse() and exists only so recognizer/parser drift surfaces as a located
        // ParseException instead of a crash.
        private static ParseException Fail(SyntaxNode node, string message)
        {
            var position = node.GetLocation().GetLineSpan().StartLinePosition;
            var line = position.Line + 1;
            var column = position.Character + 1;
            return new ParseException($"{message} at line {line}, column {column}.", line, column);
        }

        // The flat-shape grammar lives in exactly ONE place: SceneBuilder.Grammar.FlatShapeRecognizer,
        // run up front by RecognizeOrThrow, which fails loud on the first violation. By the time the
        // body walk runs, the shape is already proven a recognized flat builder, so the extraction
        // paths below only ever meet recognized constructs. An else/default reaching here would mean
        // the recognizer accepted a shape the extraction cannot handle — recognizer/parser drift, not
        // a user error. RecognizerAgreementTests + RecognizerCompletenessTests pin the two so this
        // never fires; it exists only to keep the extraction total.
        private static System.InvalidOperationException Unreachable() =>
            new System.InvalidOperationException(
                "BuilderParser reached a flat-shape construct that FlatShapeRecognizer (the single " +
                "grammar source) did not reject; recognizer and parser have drifted.");

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
            public bool IsRectTransform;
            public readonly Dictionary<string, Vec2> RectFields = new();
            public SourceSpan AnchorSpan;
            public string? Handle;
            public SourceSpan? IdCallSpan;
            public ChannelMask DrivenChannels;
            public bool IsInstance;
            public string? SourcePrefabPath;
            public string? SourcePrefabGuid;
            public readonly List<NodeBuilder> Children = new();
            public readonly List<ComponentBuilder> Components = new();

            // b2-t2: instance-only override collections (`.Override`/`.AddComponent`/
            // `.RemoveComponent`), carried until BuildInstanceNode maps them onto
            // PrefabInstanceNode.{Overrides,AddedComponents,RemovedComponents}.
            public readonly List<PropertyOverride> Overrides = new();
            public readonly List<ComponentBuilder> AddedComponents = new();
            public readonly List<OverrideTarget> RemovedComponents = new();

            // b4-t2: `.On(selector, ...)` resolved targets, carried until BuildInstanceNode maps
            // them onto PrefabInstanceNode.ScopedOverrides.
            public readonly List<ScopedOverride> ScopedOverrides = new();

            // b2-t2: `.AddChild`/`.RemoveChild`, carried until BuildInstanceNode maps them onto
            // PrefabInstanceNode.{AddedGameObjects,RemovedGameObjects}.
            public readonly List<(string ParentPath, NodeBuilder Node)> AddedGameObjects = new();
            public readonly List<OverrideTarget> RemovedGameObjects = new();
        }

        private sealed class ComponentBuilder
        {
            public string TypeFullName = "";
            public string LogicalId = "";
            public string ChildPath = "";
            public SourceSpan AnchorSpan;
            public readonly List<KeyValuePair<string, ValueNode>> Fields = new();
            public readonly List<KeyValuePair<string, SourceSpan>> FieldValueSpans = new();

            // m8: one entry per parsed `.OnClick(...)`/`.OnEvent(...)` call on this component, in
            // source order — every listener, unlike FieldValueSpans above (which records only the
            // FIRST listener's span per field key). Feeds ParseResult.ListenerCallSpans, the
            // per-listener span table Reconcile's in-place patch/remove index against.
            public readonly List<KeyValuePair<string, SourceSpan>> ListenerCallSpans = new();

            // b3-t5: true only for a lone `handle.Component<T>(...)`/`.FitSize(...)`/
            // `.SurfaceSnap(...)` call that is its OWN statement — set by ProcessBuilderChain's
            // setter-only arm. DEFAULT FALSE (pinned): every other source shape (chained onto an
            // `Add`/`Instance` chain, a multi-component chain, an expression-bodied configure
            // lambda) is treated conservatively as chained, because ONLY the statement-form case
            // has a source construct a RemoveStatement/ReorderStatement can safely resolve
            // against its enclosing statement for.
            public bool StatementAnchored;
        }

        private sealed class ParserContext
        {
            public ParserContext(string sceneParamName, LogicalIdResolver resolver, FacadeCatalog? facadeCatalog = null, AssetCatalog? assetCatalog = null, bool isVariant = false)
            {
                SceneParamName = sceneParamName;
                Resolver = resolver;
                FacadeCatalog = facadeCatalog;
                AssetCatalog = assetCatalog;
                IsVariant = isVariant;
            }

            public string SceneParamName { get; }
            public LogicalIdResolver Resolver { get; }
            public FacadeCatalog? FacadeCatalog { get; }
            public AssetCatalog? AssetCatalog { get; }
            public Dictionary<string, NodeBuilder> Handles { get; } = new();

            // The component-ref address space -- a `.Ref<T>()`-terminated chain's declared var,
            // mapping to the owning node + the captured type/ordinal (final composition is
            // deferred to ResolveComponentRefs, once every component LogicalId is final). Disjoint
            // from Handles by construction: a chain binds its var to exactly one of the two.
            public Dictionary<string, (NodeBuilder Node, string TypeFullName, int Ordinal)> ComponentRefs { get; } = new();

            public List<NodeBuilder> Roots { get; } = new();
            public List<Conflict> FacadeConflicts { get; } = new();
            public bool IsVariant { get; }
            public NodeBuilder? VariantRootNode { get; set; }
        }
    }
}
