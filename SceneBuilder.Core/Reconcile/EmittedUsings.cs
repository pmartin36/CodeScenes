using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SceneBuilder.Core.Reconcile
{
    // Emitted-code self-consistency: guarantees the compiled source carries the using directive its
    // emitted factory calls need. Second partial-class file so the existing private helpers on
    // SourcePatchApplier are reused directly — no visibility changes, no duplication.
    public static partial class SourcePatchApplier
    {
        // ---- Emitted-code self-consistency ------------------------------------------------------

        private const string AssetRefsTypeName = "SceneBuilder.Authoring.AssetRefs";

        /// <summary>
        /// Guarantees the compilation unit imports the <c>Asset(...)</c>/<c>Builtin(...)</c> factories
        /// whenever the patched source calls either of them in the SHORT form. Both factories live on
        /// the same <c>AssetRefs</c> static class, so a single directive covers both. Emission keeps the
        /// short, readable `Asset("path")` / `Builtin("Cube")` call — readability is the product's point —
        /// so the file must carry <c>using static SceneBuilder.Authoring.AssetRefs;</c> or it fails with
        /// CS0103. Idempotent: a file that already imports it (however it got there) is returned untouched.
        /// </summary>
        private static CompilationUnitSyntax EnsureAssetRefsUsing(CompilationUnitSyntax root)
        {
            var callsShortAssetRefsFactory = root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Any(inv => inv.Expression is IdentifierNameSyntax identifier
                    && (identifier.Identifier.Text == "Asset" || identifier.Identifier.Text == "Builtin"));

            if (!callsShortAssetRefsFactory)
            {
                return root;
            }

            // Guard against duplicates anywhere in the file (compilation-unit OR namespace scope).
            var alreadyImported = root.DescendantNodes()
                .OfType<UsingDirectiveSyntax>()
                .Any(u => u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)
                    && u.Name?.ToString() == AssetRefsTypeName);

            if (alreadyImported)
            {
                return root;
            }

            // Spacing is explicit: bare SyntaxFactory tokens carry no trivia, which renders the
            // directive as `usingstaticSceneBuilder.Authoring.AssetRefs;` — itself uncompilable.
            var directive = SyntaxFactory
                .UsingDirective(SyntaxFactory.ParseName(AssetRefsTypeName))
                .WithUsingKeyword(SyntaxFactory.Token(SyntaxKind.UsingKeyword).WithTrailingTrivia(SyntaxFactory.Space))
                .WithStaticKeyword(SyntaxFactory.Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(SyntaxFactory.Space))
                .WithTrailingTrivia(SyntaxFactory.EndOfLine("\n"));

            return root.AddUsings(directive);
        }
    }
}
