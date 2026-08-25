using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SceneBuilder.Grammar
{
    // Mirror of SceneBuilder.Core.Parsing.StringConstantFolder — Grammar cannot reference Core
    // (ns2.1 vs ns2.0), so the identical fold/collect logic is reimplemented here, kept in
    // lockstep so the recognizer's acceptance never drifts from the parser's folding. A `const
    // string` class field or Build-body local, a bare reference to one, or a `+`-concatenation of
    // such constants, is accepted wherever a bare string literal is required today. String-
    // constant folding only — no interpolation, no numeric/other consts, no runtime values.
    internal static class StringConstantFolder
    {
        public static IReadOnlyDictionary<string, string> Collect(BlockSyntax body)
        {
            var consts = new Dictionary<string, string>();

            var cls = body.FirstAncestorOrSelf<ClassDeclarationSyntax>();
            if (cls != null)
            {
                foreach (var field in cls.Members.OfType<FieldDeclarationSyntax>())
                {
                    if (!IsConstStringDeclaration(field.Modifiers, field.Declaration.Type))
                    {
                        continue;
                    }

                    foreach (var variable in field.Declaration.Variables)
                    {
                        if (variable.Initializer != null && TryFold(variable.Initializer.Value, consts, out var value))
                        {
                            consts[variable.Identifier.Text] = value;
                        }
                    }
                }
            }

            foreach (var statement in body.Statements)
            {
                if (statement is not LocalDeclarationStatementSyntax local ||
                    !IsConstStringDeclaration(local.Modifiers, local.Declaration.Type))
                {
                    continue;
                }

                foreach (var variable in local.Declaration.Variables)
                {
                    if (variable.Initializer != null && TryFold(variable.Initializer.Value, consts, out var value))
                    {
                        consts[variable.Identifier.Text] = value;
                    }
                }
            }

            return consts;
        }

        public static bool IsConstStringDeclaration(SyntaxTokenList modifiers, TypeSyntax type) =>
            modifiers.Any(SyntaxKind.ConstKeyword) && IsStringType(type);

        private static bool IsStringType(TypeSyntax type) =>
            (type is PredefinedTypeSyntax predefined && predefined.Keyword.IsKind(SyntaxKind.StringKeyword)) ||
            type is IdentifierNameSyntax { Identifier.Text: "String" };

        public static bool IsFoldable(ExpressionSyntax expr, IReadOnlyDictionary<string, string> consts) =>
            TryFold(expr, consts, out _);

        private static bool TryFold(ExpressionSyntax expr, IReadOnlyDictionary<string, string> consts, out string value)
        {
            switch (expr)
            {
                case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                    value = literal.Token.ValueText;
                    return true;

                case ParenthesizedExpressionSyntax parenthesized:
                    return TryFold(parenthesized.Expression, consts, out value);

                case IdentifierNameSyntax identifier:
                    return consts.TryGetValue(identifier.Identifier.Text, out value);

                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression):
                    if (TryFold(binary.Left, consts, out var left) && TryFold(binary.Right, consts, out var right))
                    {
                        value = left + right;
                        return true;
                    }

                    value = "";
                    return false;

                default:
                    value = "";
                    return false;
            }
        }
    }
}
