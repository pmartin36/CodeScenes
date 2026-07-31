using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;

namespace SceneBuilder.Core.Parsing
{
    // Owns ALL value-argument lowering for `.Set(key, value)` (b3-t2). Supersedes
    // BuilderParser's b3-t1 interim `ParsePrimitiveValue`. TOTAL: never throws — every
    // unrecognized form falls back to `ValueNode.Unsupported(expr.ToString())` (verbatim
    // source text of the value argument, trivia-trimmed).
    internal static class ValueNodeParser
    {
        private static readonly string[] VectorTypeNames = { "Vector2", "Vector3", "Vector4", "Quaternion", "Color" };

        // b2-t1: `assetCatalog`/`conflicts` are optional so every pre-existing 1-arg call site
        // keeps binding unchanged. They flow only to the new Assets-root arm (TryParseAssetChain)
        // and the recursion carriers that can reach it.
        public static ValueNode Parse(ExpressionSyntax expr, AssetCatalog? assetCatalog = null, List<Conflict>? conflicts = null)
        {
            switch (expr)
            {
                case PrefixUnaryExpressionSyntax unary when unary.OperatorToken.IsKind(SyntaxKind.MinusToken):
                    return ParseNegated(unary, assetCatalog, conflicts);

                case LiteralExpressionSyntax literal:
                    return ParseLiteral(literal, expr);

                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.BitwiseOrExpression):
                    return ParseFlagsEnum(binary, expr);

                case InvocationExpressionSyntax invocation
                    when invocation.Expression is IdentifierNameSyntax id && id.Identifier.Text == "Asset":
                    return ParseAsset(invocation);

                case InvocationExpressionSyntax invocation
                    when invocation.Expression is IdentifierNameSyntax id && id.Identifier.Text == "Builtin":
                    return ParseBuiltin(invocation);

                case MemberAccessExpressionSyntax member
                    when member.Expression.ToString() == "Asset" && member.Name.Identifier.Text == "None":
                    return new ValueNode.AssetRef(null);

                case MemberAccessExpressionSyntax member
                    when member.Expression.ToString() == "NodeHandle" && member.Name.Identifier.Text == "None":
                    return new ValueNode.ObjectRef(null);

                case IdentifierNameSyntax id:
                    return new ValueNode.ObjectRef(id.Identifier.Text);

                // b2-t1: an `Assets.<Group>.<folders...>.<Leaf>` chain, claimed ONLY when
                // catalog-shaped (known Group in a non-null catalog). MUST run before the bare
                // MemberAccess->Enum arm below or a resolvable typed ref would never be reached;
                // a non-catalog-shaped chain falls through to that arm unchanged (the pinned
                // false-hijack regression guard — see research.md).
                case MemberAccessExpressionSyntax assetChain
                    when TryParseAssetChain(assetChain, assetCatalog, conflicts, out var assetNode):
                    return assetNode;

                case MemberAccessExpressionSyntax memberAccess:
                    return ValueNode.Enum.Canonical(
                        memberAccess.Expression.ToString(),
                        new[] { memberAccess.Name.Identifier.Text });

                case ObjectCreationExpressionSyntax objectCreation:
                    return ParseObjectCreation(objectCreation, expr, assetCatalog, conflicts);

                case ArrayCreationExpressionSyntax { Initializer: { } arrayInitializer }:
                    return ParseList(arrayInitializer, assetCatalog, conflicts);

                case ImplicitArrayCreationExpressionSyntax implicitArray:
                    return ParseList(implicitArray.Initializer, assetCatalog, conflicts);

                default:
                    return Unsupported(expr);
            }
        }

        private static ValueNode ParseNegated(PrefixUnaryExpressionSyntax unary, AssetCatalog? assetCatalog, List<Conflict>? conflicts)
        {
            var operand = Parse(unary.Operand, assetCatalog, conflicts);
            return operand switch
            {
                ValueNode.Primitive(PrimitiveKind.Int, int i) => ValueNode.Primitive.Int(-i),
                ValueNode.Primitive(PrimitiveKind.Long, long l) => ValueNode.Primitive.Long(-l),
                ValueNode.Primitive(PrimitiveKind.Float, float f) => ValueNode.Primitive.Float(-f),
                ValueNode.Primitive(PrimitiveKind.Double, double d) => ValueNode.Primitive.Double(-d),
                _ => Unsupported(unary),
            };
        }

        private static ValueNode ParseLiteral(LiteralExpressionSyntax literal, ExpressionSyntax expr)
        {
            if (literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                return ValueNode.Primitive.String(literal.Token.ValueText);
            }

            if (literal.IsKind(SyntaxKind.TrueLiteralExpression))
            {
                return ValueNode.Primitive.Bool(true);
            }

            if (literal.IsKind(SyntaxKind.FalseLiteralExpression))
            {
                return ValueNode.Primitive.Bool(false);
            }

            if (literal.IsKind(SyntaxKind.NumericLiteralExpression))
            {
                return literal.Token.Value switch
                {
                    int i => ValueNode.Primitive.Int(i),
                    long l => ValueNode.Primitive.Long(l),
                    float f => ValueNode.Primitive.Float(f),
                    double d => ValueNode.Primitive.Double(d),
                    _ => Unsupported(expr),
                };
            }

            return Unsupported(expr);
        }

        // `A.M1 | A.M2 | ...` — recursively flatten `|` operands; every leaf must be a
        // MemberAccessExpressionSyntax sharing ONE type FQN. Members are ordinal-sorted and
        // de-duplicated so member order never depends on source operand order (R1).
        private static ValueNode ParseFlagsEnum(BinaryExpressionSyntax binary, ExpressionSyntax whole)
        {
            var leaves = new List<MemberAccessExpressionSyntax>();
            if (!TryFlattenBitwiseOr(binary, leaves))
            {
                return Unsupported(whole);
            }

            var typeFullName = leaves[0].Expression.ToString();
            if (leaves.Any(leaf => leaf.Expression.ToString() != typeFullName))
            {
                return Unsupported(whole);
            }

            var members = leaves.Select(leaf => leaf.Name.Identifier.Text);

            // Canonical derives IsFlags from the resulting DISTINCT member count, so `A | A` — two
            // operands, one distinct member — correctly collapses to a single-member, non-flags node.
            return ValueNode.Enum.Canonical(typeFullName, members);
        }

        private static bool TryFlattenBitwiseOr(ExpressionSyntax expr, List<MemberAccessExpressionSyntax> leaves)
        {
            if (expr is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.BitwiseOrExpression))
            {
                return TryFlattenBitwiseOr(binary.Left, leaves) && TryFlattenBitwiseOr(binary.Right, leaves);
            }

            if (expr is MemberAccessExpressionSyntax memberAccess)
            {
                leaves.Add(memberAccess);
                return true;
            }

            return false;
        }

        private static ValueNode ParseObjectCreation(ObjectCreationExpressionSyntax objectCreation, ExpressionSyntax whole, AssetCatalog? assetCatalog, List<Conflict>? conflicts)
        {
            var initializerKind = objectCreation.Initializer?.Kind();

            if (initializerKind == SyntaxKind.ObjectInitializerExpression)
            {
                return ParseNested(objectCreation.Type, objectCreation.Initializer!, whole, assetCatalog, conflicts);
            }

            if (initializerKind == SyntaxKind.CollectionInitializerExpression)
            {
                return ParseList(objectCreation.Initializer!, assetCatalog, conflicts);
            }

            var typeName = TypeNameOf(objectCreation.Type);
            if (typeName != null && VectorTypeNames.Contains(typeName))
            {
                return ParseVectorLike(typeName, objectCreation, whole);
            }

            return Unsupported(whole);
        }

        private static string? TypeNameOf(TypeSyntax type) => type switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Text,
            _ => null,
        };

        private static ValueNode ParseNested(TypeSyntax type, InitializerExpressionSyntax initializer, ExpressionSyntax whole, AssetCatalog? assetCatalog, List<Conflict>? conflicts)
        {
            var fields = new List<KeyValuePair<string, ValueNode>>();

            foreach (var element in initializer.Expressions)
            {
                if (element is not AssignmentExpressionSyntax { Left: IdentifierNameSyntax ident } assignment)
                {
                    return Unsupported(whole);
                }

                fields.Add(new KeyValuePair<string, ValueNode>(ident.Identifier.Text, Parse(assignment.Right, assetCatalog, conflicts)));
            }

            // Full written type text (namespace preserved), NOT TypeNameOf (drops the namespace).
            return new ValueNode.Nested(type.ToString().Trim(), new FieldMap(fields));
        }

        private static ValueNode ParseList(InitializerExpressionSyntax initializer, AssetCatalog? assetCatalog, List<Conflict>? conflicts)
        {
            var items = initializer.Expressions.Select(e => Parse(e, assetCatalog, conflicts)).ToArray();
            return new ValueNode.List(items);
        }

        private static ValueNode ParseVectorLike(string typeName, ObjectCreationExpressionSyntax objectCreation, ExpressionSyntax whole)
        {
            var args = objectCreation.ArgumentList?.Arguments ?? default;
            var expectedArity = typeName switch
            {
                "Vector2" => 2,
                "Vector3" => 3,
                "Vector4" => 4,
                "Quaternion" => 4,
                "Color" => 4,
                _ => -1,
            };

            if (expectedArity < 0 || args.Count != expectedArity)
            {
                return Unsupported(whole);
            }

            var values = new float[expectedArity];
            for (var i = 0; i < expectedArity; i++)
            {
                if (!TryEvalFloat(args[i].Expression, out values[i]))
                {
                    return Unsupported(whole);
                }
            }

            return typeName switch
            {
                "Vector2" => new ValueNode.Vec2(new Vec2(values[0], values[1])),
                "Vector3" => new ValueNode.Vec3(new Vec3(values[0], values[1], values[2])),
                "Vector4" => new ValueNode.Vec4(new Vec4(values[0], values[1], values[2], values[3])),
                "Quaternion" => new ValueNode.Quat(new Quat(values[0], values[1], values[2], values[3])),
                "Color" => new ValueNode.Color(new Color(values[0], values[1], values[2], values[3])),
                _ => Unsupported(whole),
            };
        }

        // Non-throwing mirror of BuilderParser.EvalFloat: unary-minus + numeric literal via
        // Convert.ToSingle. Fallback here is Unsupported (the caller's decision), not Fail.
        private static bool TryEvalFloat(ExpressionSyntax expression, out float value)
        {
            if (expression is PrefixUnaryExpressionSyntax unary && unary.OperatorToken.IsKind(SyntaxKind.MinusToken))
            {
                if (TryEvalFloat(unary.Operand, out var operandValue))
                {
                    value = -operandValue;
                    return true;
                }

                value = default;
                return false;
            }

            if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.NumericLiteralExpression))
            {
                try
                {
                    value = Convert.ToSingle(literal.Token.Value, CultureInfo.InvariantCulture);
                    return true;
                }
                catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
                {
                    value = default;
                    return false;
                }
            }

            value = default;
            return false;
        }

        private static ValueNode ParseAsset(InvocationExpressionSyntax invocation)
        {
            var args = invocation.ArgumentList.Arguments;
            if (args.Count is not (1 or 2)) return Unsupported(invocation);

            var arg = args[0].Expression;
            if (args.Count == 1)
            {
                if (TryStringLiteral(arg, out var path))
                    return new ValueNode.AssetRef(new AssetRef { DisplayPath = path });
                if (arg is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.NullLiteralExpression))
                    return new ValueNode.AssetRef(null);
                return Unsupported(invocation);
            }

            // 2-arg sub-asset form: both args must be string literals.
            if (!TryStringLiteral(arg, out var displayPath)) return Unsupported(invocation);
            if (!TryStringLiteral(args[1].Expression, out var subAsset)) return Unsupported(invocation);
            return new ValueNode.AssetRef(new AssetRef { DisplayPath = displayPath, SubAsset = subAsset });
        }

        private static ValueNode ParseBuiltin(InvocationExpressionSyntax invocation)
        {
            var args = invocation.ArgumentList.Arguments;
            if (args.Count is not (1 or 2)) return Unsupported(invocation);
            if (!TryStringLiteral(args[0].Expression, out var name)) return Unsupported(invocation);
            var typeHint = "";
            if (args.Count == 2 && !TryStringLiteral(args[1].Expression, out typeHint)) return Unsupported(invocation);
            return new ValueNode.AssetRef(new AssetRef { DisplayPath = name, IsBuiltin = true, TypeHint = typeHint });
        }

        private static bool TryStringLiteral(ExpressionSyntax expr, out string value)
        {
            if (expr is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
            {
                value = lit.Token.ValueText;
                return true;
            }

            value = "";
            return false;
        }

        // b2-t1: claims an `Assets.<Group>.<folders...>.<Leaf>` chain ONLY when catalog-shaped —
        // root identifier `Assets`, a non-null catalog, AND the Group (2nd segment) is KNOWN in
        // the catalog. Mirrors BuilderParser.Instance.cs's `Instance(Prefabs.X)` arm: a hit
        // resolves straight to the string-form AssetRef shape (ParseAsset, above); a
        // catalog-shaped miss records ONE located Conflict and still claims the node (never a
        // silent Enum). A non-catalog-shaped chain (null catalog, or an unknown Group) returns
        // false so the caller falls through to the ordinary MemberAccess->Enum arm — the pinned
        // false-hijack regression guard (a user's own `enum Assets` is never hijacked).
        private static bool TryParseAssetChain(MemberAccessExpressionSyntax expr, AssetCatalog? catalog, List<Conflict>? conflicts, out ValueNode node)
        {
            node = default!;

            if (catalog == null)
            {
                return false;
            }

            if (!TryFlattenIdentifierChain(expr, out var segments))
            {
                return false;
            }

            if (segments.Count < 3 || segments[0] != "Assets")
            {
                return false;
            }

            var group = segments[1];
            var leaf = segments[^1];
            var folders = segments.GetRange(2, segments.Count - 3);

            if (catalog.TryGetEntry(group, folders, leaf, out var entry))
            {
                node = new ValueNode.AssetRef(new AssetRef { DisplayPath = entry.Path, SubAsset = entry.SubAsset });
                return true;
            }

            if (!GroupKnown(catalog, group))
            {
                return false;
            }

            conflicts?.Add(new Conflict
            {
                Kind = ConflictKind.UnknownFacadeReference,
                Reason = $"Unknown typed asset reference '{string.Join(".", segments)}'.",
                Location = new SourceSpan(expr.Span.Start, expr.Span.Length),
            });
            node = new ValueNode.AssetRef(new AssetRef { DisplayPath = "" });
            return true;
        }

        // Walks a MemberAccessExpressionSyntax spine right-to-left, requiring every `.Name` be a
        // plain IdentifierNameSyntax (rejects a generic segment / invocation / `?.`) and the
        // leftmost `.Expression` be a plain IdentifierNameSyntax (the chain root). Returns the
        // segments root-to-leaf, e.g. `Assets.Materials.Environment.Rocks.Stone` ->
        // ["Assets","Materials","Environment","Rocks","Stone"].
        private static bool TryFlattenIdentifierChain(MemberAccessExpressionSyntax expr, out List<string> segments)
        {
            var names = new List<string>();
            ExpressionSyntax current = expr;

            while (current is MemberAccessExpressionSyntax memberAccess)
            {
                if (memberAccess.Name is not IdentifierNameSyntax nameIdentifier)
                {
                    segments = new List<string>();
                    return false;
                }

                names.Add(nameIdentifier.Identifier.Text);
                current = memberAccess.Expression;
            }

            if (current is not IdentifierNameSyntax rootIdentifier)
            {
                segments = new List<string>();
                return false;
            }

            names.Add(rootIdentifier.Identifier.Text);
            names.Reverse();
            segments = names;
            return true;
        }

        private static bool GroupKnown(AssetCatalog catalog, string group)
        {
            foreach (var e in catalog.Entries)
            {
                if (string.Equals(e.Group, group, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static ValueNode.Unsupported Unsupported(ExpressionSyntax expr) => new(expr.ToString());
    }
}
