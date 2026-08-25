using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SceneBuilder.Core.Parsing
{
    // Compile-time string-constant folding shared by every literal-demanding parser site: a
    // `const string` class field or Build-body local, a bare reference to one, or a
    // `+`-concatenation of such constants, resolves to its value wherever a bare string literal
    // is required (Instance path, Add name, Tag/Id, On/AddChild/RemoveChild string paths, and the
    // Asset/Builtin value positions). String-constant folding only — no interpolation, no
    // numeric/other consts, no runtime values.
    internal static class StringConstantFolder
    {
        // Collects every `const string` binding reachable from a Build-method body: class-level
        // fields (document order) followed by Build-body top-level locals (document order), each
        // folded against the dict-so-far so a const built from an earlier const resolves too.
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

        // A `const string` field/local declaration — the shared predicate both the collection
        // pass above and the Build-body statement walk's const-local skip consult, so the two
        // agree by construction. A non-string const (`const int N`) is NOT claimed here (left to
        // the ordinary rejection path, out of scope for this folder).
        public static bool IsConstStringDeclaration(SyntaxTokenList modifiers, TypeSyntax type) =>
            modifiers.Any(SyntaxKind.ConstKeyword) && IsStringType(type);

        private static bool IsStringType(TypeSyntax type) =>
            (type is PredefinedTypeSyntax predefined && predefined.Keyword.IsKind(SyntaxKind.StringKeyword)) ||
            type is IdentifierNameSyntax { Identifier.Text: "String" };

        // A string literal, a reference to a known const, or a `+`-concatenation of such — the
        // ONLY foldable shapes. Anything else (runtime variable, method call, interpolation,
        // unregistered identifier) is not foldable.
        public static bool TryFold(ExpressionSyntax expr, IReadOnlyDictionary<string, string> consts, out string value)
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
