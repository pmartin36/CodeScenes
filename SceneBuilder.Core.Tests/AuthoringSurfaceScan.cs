using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SceneBuilder.Core.Tests
{
    // The concrete handle types reached transitively from SceneObjectHandle in [sources]' base
    // lists, and one offender string per parameter typed as one of them. SceneObjectHandle itself
    // is never an offender -- it is the required parameter type, not a banned one.
    internal static class AuthoringSurfaceScan
    {
        private const string RootTypeName = "SceneObjectHandle";

        internal static (IReadOnlyCollection<string> HandleTypes, IReadOnlyList<string> Offenders)
            Scan(IEnumerable<(string FileName, string Text)> sources)
        {
            var parsed = sources
                .Select(source => (source.FileName, Root: CSharpSyntaxTree.ParseText(source.Text, path: source.FileName).GetRoot()))
                .ToArray();

            var declarations = parsed
                .SelectMany(p => p.Root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                .ToArray();

            var handleTypes = new HashSet<string> { RootTypeName };
            bool grew;
            do
            {
                grew = false;
                foreach (var declaration in declarations)
                {
                    var name = declaration.Identifier.Text;
                    if (handleTypes.Contains(name))
                    {
                        continue;
                    }

                    var baseNames = declaration.BaseList?.Types
                        .Select(baseType => SimpleName(baseType.Type))
                        .Where(baseName => baseName != null) ?? Enumerable.Empty<string?>();

                    if (baseNames.Any(baseName => handleTypes.Contains(baseName!)))
                    {
                        handleTypes.Add(name);
                        grew = true;
                    }
                }
            } while (grew);

            handleTypes.Remove(RootTypeName);

            var offenders = new List<string>();
            foreach (var (fileName, root) in parsed)
            {
                foreach (var parameter in root.DescendantNodes().OfType<ParameterSyntax>())
                {
                    var simpleName = SimpleName(parameter.Type);
                    if (simpleName != null && handleTypes.Contains(simpleName))
                    {
                        var line = parameter.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        var member = EnclosingMemberName(parameter) ?? "?";
                        offenders.Add($"{Path.GetFileName(fileName)}:{line} {member}({parameter})");
                    }
                }
            }

            return (handleTypes, offenders);
        }

        private static string? SimpleName(TypeSyntax? type) => type switch
        {
            NullableTypeSyntax nullable => SimpleName(nullable.ElementType),
            QualifiedNameSyntax qualified => SimpleName(qualified.Right),
            GenericNameSyntax generic => generic.Identifier.Text,
            IdentifierNameSyntax id => id.Identifier.Text,
            _ => null,
        };

        // The name of the member declaring [parameter]'s parameter list -- carried in the offender
        // string so a failure names the offending member, not just its parameter text.
        private static string? EnclosingMemberName(ParameterSyntax parameter) => parameter.Parent?.Parent switch
        {
            MethodDeclarationSyntax method => method.Identifier.Text,
            ConstructorDeclarationSyntax ctor => ctor.Identifier.Text,
            LocalFunctionStatementSyntax local => local.Identifier.Text,
            DelegateDeclarationSyntax del => del.Identifier.Text,
            _ => null,
        };
    }
}
