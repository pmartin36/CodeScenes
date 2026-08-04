using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SceneBuilder.DocGen
{
    /// <summary>
    /// Reads the SB#### table out of the DiagnosticDescriptors registry. The registry is the single
    /// source of truth for id/title/message/category/severity, so the reference tracks it directly
    /// rather than restating it.
    /// </summary>
    public static class DiagnosticExtractor
    {
        public static List<ApiDiagnostic> FromFile(string path)
        {
            var diagnostics = new List<ApiDiagnostic>();
            if (!File.Exists(path)) return diagnostics;

            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path));
            var root = tree.GetRoot();

            var constants = root.DescendantNodes()
                .OfType<FieldDeclarationSyntax>()
                .Where(f => f.Modifiers.Any(SyntaxKind.ConstKeyword))
                .SelectMany(f => f.Declaration.Variables)
                .Where(v => v.Initializer?.Value is LiteralExpressionSyntax)
                .ToDictionary(
                    v => v.Identifier.ValueText,
                    v => ((LiteralExpressionSyntax)v.Initializer!.Value).Token.ValueText,
                    StringComparer.Ordinal);

            foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
            {
                if (field.Declaration.Type.ToString() != "DiagnosticDescriptor") continue;

                foreach (var variable in field.Declaration.Variables)
                {
                    var arguments = ArgumentsOf(variable.Initializer?.Value);
                    if (arguments is null || arguments.Count < 5) continue;

                    var entry = new ApiDiagnostic
                    {
                        Id = Value(arguments[0], constants) ?? variable.Identifier.ValueText,
                        Title = Value(arguments[1], constants) ?? "",
                        MessageFormat = Value(arguments[2], constants) ?? "",
                        Category = Value(arguments[3], constants) ?? "",
                        Severity = SeverityOf(arguments[4]),
                    };
                    entry.Summary.AddRange(DocComments.Read(field).Summary);
                    diagnostics.Add(entry);
                }
            }

            return diagnostics.OrderBy(d => d.Id, StringComparer.Ordinal).ToList();
        }

        private static List<ArgumentSyntax>? ArgumentsOf(ExpressionSyntax? initializer) => initializer switch
        {
            ImplicitObjectCreationExpressionSyntax implicitNew => implicitNew.ArgumentList.Arguments.ToList(),
            ObjectCreationExpressionSyntax explicitNew => explicitNew.ArgumentList?.Arguments.ToList(),
            _ => null,
        };

        private static string? Value(ArgumentSyntax argument, IReadOnlyDictionary<string, string> constants)
        {
            switch (argument.Expression)
            {
                case LiteralExpressionSyntax literal:
                    return literal.Token.ValueText;
                case IdentifierNameSyntax identifier
                    when constants.TryGetValue(identifier.Identifier.ValueText, out var constant):
                    return constant;
                default:
                    return null;
            }
        }

        private static string SeverityOf(ArgumentSyntax argument)
        {
            var text = argument.Expression.ToString();
            var dot = text.LastIndexOf('.');
            return dot < 0 ? text : text[(dot + 1)..];
        }
    }
}
