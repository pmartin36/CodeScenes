using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SceneBuilder.Grammar
{
    // Non-throwing mirror of SceneBuilder.Core.Parsing.BuilderParser's private FindBuildMethod
    // (BuilderParser.cs:98). Grammar cannot reference Core (ns2.0 vs ns2.1), and the analyzer must
    // never throw on ambiguous/missing Build methods (it just skips), so this locator encodes the
    // SAME discovery decision but returns false instead of throwing.
    public static partial class FlatShapeRecognizer
    {
        public static bool TryFindBuildMethod(CompilationUnitSyntax root, out BlockSyntax? buildBody, out string? sceneParamName)
        {
            buildBody = null;
            sceneParamName = null;

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

                if (allBuildMethods.Count != 1)
                {
                    return false;
                }

                candidate = allBuildMethods[0];
            }

            var param = candidate.ParameterList.Parameters.FirstOrDefault();
            if (param == null)
            {
                return false;
            }

            if (candidate.Body is not BlockSyntax body)
            {
                return false;
            }

            buildBody = body;
            sceneParamName = param.Identifier.Text;
            return true;
        }
    }
}
