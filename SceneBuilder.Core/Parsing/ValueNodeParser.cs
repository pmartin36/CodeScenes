using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Parsing
{
    // Owns ALL value-argument lowering for `.Set(key, value)` (b3-t2). Supersedes
    // BuilderParser's b3-t1 interim `ParsePrimitiveValue`. TOTAL: never throws — every
    // unrecognized form falls back to `ValueNode.Unsupported(expr.ToString())` (verbatim
    // source text of the value argument, trivia-trimmed).
    internal static class ValueNodeParser
    {
        private static readonly string[] VectorTypeNames = { "Vector2", "Vector3", "Vector4", "Quaternion", "Color" };

        public static ValueNode Parse(ExpressionSyntax expr)
        {
            switch (expr)
            {
                case PrefixUnaryExpressionSyntax unary when unary.OperatorToken.IsKind(SyntaxKind.MinusToken):
                    return ParseNegated(unary);

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

                case MemberAccessExpressionSyntax memberAccess:
                    return new ValueNode.Enum(
                        memberAccess.Expression.ToString(),
                        new[] { memberAccess.Name.Identifier.Text },
                        IsFlags: false);

                case ObjectCreationExpressionSyntax objectCreation:
                    return ParseObjectCreation(objectCreation, expr);

                case ArrayCreationExpressionSyntax { Initializer: { } arrayInitializer }:
                    return ParseList(arrayInitializer);

                case ImplicitArrayCreationExpressionSyntax implicitArray:
                    return ParseList(implicitArray.Initializer);

                default:
                    return Unsupported(expr);
            }
        }

        private static ValueNode ParseNegated(PrefixUnaryExpressionSyntax unary)
        {
            var operand = Parse(unary.Operand);
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

            var members = leaves
                .Select(leaf => leaf.Name.Identifier.Text)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(m => m, StringComparer.Ordinal)
                .ToArray();

            return new ValueNode.Enum(typeFullName, members, IsFlags: true);
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

        private static ValueNode ParseObjectCreation(ObjectCreationExpressionSyntax objectCreation, ExpressionSyntax whole)
        {
            var initializerKind = objectCreation.Initializer?.Kind();

            if (initializerKind == SyntaxKind.ObjectInitializerExpression)
            {
                return ParseNested(objectCreation.Type, objectCreation.Initializer!, whole);
            }

            if (initializerKind == SyntaxKind.CollectionInitializerExpression)
            {
                return ParseList(objectCreation.Initializer!);
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

        private static ValueNode ParseNested(TypeSyntax type, InitializerExpressionSyntax initializer, ExpressionSyntax whole)
        {
            var fields = new List<KeyValuePair<string, ValueNode>>();

            foreach (var element in initializer.Expressions)
            {
                if (element is not AssignmentExpressionSyntax { Left: IdentifierNameSyntax ident } assignment)
                {
                    return Unsupported(whole);
                }

                fields.Add(new KeyValuePair<string, ValueNode>(ident.Identifier.Text, Parse(assignment.Right)));
            }

            // Full written type text (namespace preserved), NOT TypeNameOf (drops the namespace).
            return new ValueNode.Nested(type.ToString().Trim(), new FieldMap(fields));
        }

        private static ValueNode ParseList(InitializerExpressionSyntax initializer)
        {
            var items = initializer.Expressions.Select(Parse).ToArray();
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

        private static ValueNode.Unsupported Unsupported(ExpressionSyntax expr) => new(expr.ToString());
    }
}
