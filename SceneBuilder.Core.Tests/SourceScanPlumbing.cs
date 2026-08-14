using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SceneBuilder.Core.Tests
{
    // The one owner of the Roslyn source-scan plumbing shared by the descent guards
    // (ObjectRefDescentScanTests, ValueContainerDescentScanTests): the production-file walk, the
    // `qualifier.Identifier` token match, and the enclosing-member resolution. Each guard supplies
    // its own inventory and identifier set; the traversal skeleton lives here once so a change to it
    // lands in one place.
    internal static class SourceScanPlumbing
    {
        // Every production `.cs` file under `scanRoots`, relative to `repoRoot` (forward-slashed),
        // excluding obj/bin output and any file `isExempt` rejects.
        public static IEnumerable<string> ProductionFiles(
            string repoRoot, IReadOnlyList<string> scanRoots, Func<string, bool> isExempt)
        {
            foreach (var scanRoot in scanRoots)
            {
                var rootPath = Path.Combine(repoRoot, scanRoot);
                if (!Directory.Exists(rootPath))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                    if (relative.Contains("/obj/") || relative.Contains("/bin/"))
                    {
                        continue;
                    }

                    if (isExempt(relative))
                    {
                        continue;
                    }

                    yield return relative;
                }
            }
        }

        internal readonly record struct QualifiedToken(string Identifier, string Member, int Line);

        // Every `{qualifier}.{identifier}` qualified-identifier TOKEN in `sourceText` whose
        // identifier is in `identifiers`, in ANY syntactic position, with its enclosing member and
        // 1-based line. A syntax-only parse: comment / XML-doc trivia carry no tokens, so prose
        // mentioning either name can never match.
        public static IEnumerable<QualifiedToken> QualifiedTokens(
            string sourceText, string relativePath, string qualifier, ISet<string> identifiers)
        {
            var tree = CSharpSyntaxTree.ParseText(sourceText, path: relativePath);
            var root = tree.GetRoot();

            foreach (var token in root.DescendantTokens())
            {
                if (token.Kind() != SyntaxKind.IdentifierToken || !identifiers.Contains(token.Text))
                {
                    continue;
                }

                var dot = token.GetPreviousToken();
                if (dot.Kind() != SyntaxKind.DotToken)
                {
                    continue;
                }

                var qual = dot.GetPreviousToken();
                if (qual.Text != qualifier)
                {
                    continue;
                }

                var line = token.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                yield return new QualifiedToken(token.Text, EnclosingMember(token), line);
            }
        }

        public static string EnclosingMember(SyntaxToken token)
        {
            for (var node = token.Parent; node != null; node = node.Parent)
            {
                switch (node)
                {
                    case MethodDeclarationSyntax m: return m.Identifier.Text;
                    case LocalFunctionStatementSyntax l: return l.Identifier.Text;
                    case ConstructorDeclarationSyntax c: return c.Identifier.Text;
                    case PropertyDeclarationSyntax p: return p.Identifier.Text;
                    case TypeDeclarationSyntax t: return t.Identifier.Text;
                }
            }

            return "<top-level>";
        }
    }
}
