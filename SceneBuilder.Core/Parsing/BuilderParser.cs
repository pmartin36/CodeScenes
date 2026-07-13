using System;
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
    // Syntax-only Roslyn parser for the M1 builder-file authoring surface (§6).
    // No semantic binding: fixture/builder source references types (ISceneDefinition, SceneRoot)
    // that do not exist in Core, so only CSharpSyntaxTree.ParseText is used.
    public static class BuilderParser
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

            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = ctx.Roots.Select(BuildNode).ToArray(),
            };

            var identityMap = BuildIdentityMap(ctx.Roots, existingMap);
            var anchors = BuildAnchors(ctx.Roots);

            return new ParseResult { Model = model, IdentityMap = identityMap, Anchors = anchors };
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

            foreach (var (method, args, _) in calls)
            {
                switch (method)
                {
                    case "Transform":
                        ApplyTransform(node, args);
                        break;
                    case "Tag":
                        node.Tag = EvalStringLiteral(args.Arguments[0].Expression);
                        break;
                    case "Layer":
                        node.Layer = (int)EvalFloat(args.Arguments[0].Expression);
                        break;
                    case "Active":
                        node.Active = EvalBool(args.Arguments[0].Expression);
                        break;
                    case "Static":
                        node.IsStatic = true;
                        break;
                    case "Id":
                        explicitId = EvalStringLiteral(args.Arguments[0].Expression);
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
            foreach (var root in roots)
            {
                CollectIdentityEntries(root, null, globalObjectIdByLogicalId, entries);
            }

            return new IdentityMap
            {
                SchemaVersion = 1,
                Scene = existingMap?.Scene ?? "",
                Entries = entries.ToArray(),
                Assets = existingMap?.Assets ?? Array.Empty<AssetEntry>(),
            };
        }

        private static void CollectIdentityEntries(NodeBuilder node, string? parentLogicalId, Dictionary<string, string> globalObjectIdByLogicalId, List<IdentityMapEntry> entries)
        {
            entries.Add(new IdentityMapEntry
            {
                LogicalId = node.LogicalId,
                GlobalObjectId = globalObjectIdByLogicalId.TryGetValue(node.LogicalId, out var globalObjectId) ? globalObjectId : "",
                Kind = "GameObject",
                ComponentType = null,
                ParentLogicalId = parentLogicalId,
            });

            foreach (var child in node.Children)
            {
                CollectIdentityEntries(child, node.LogicalId, globalObjectIdByLogicalId, entries);
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
            },
            Children = builder.Children.Select(BuildNode).ToArray(),
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
            public Vec3? Position;
            public Quat? Rotation;
            public Vec3? Scale;
            public SourceSpan AnchorSpan;
            public readonly List<NodeBuilder> Children = new();
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
