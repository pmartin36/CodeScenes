using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Facades
{
    // b3-t1: formatting-preserving Roslyn edit that rewrites `Assets.<Group>.<folders...>.<Member>`
    // member-access chains keyed on the durable (Guid, FileId), covering both a rename (leaf
    // differs) and a move (folder segments differ/added/removed). Mirrors FacadeReferencePatcher's
    // shape but replaces the WHOLE chain node (ReplaceNodes), not a single token, since a move can
    // change arity. See .agent_handoffs/typed-asset-catalogs/b3-t1/research.md.
    public static class AssetReferencePatcher
    {
        public static string Patch(string builderSource, AssetCatalog oldCatalog, AssetCatalog newCatalog)
        {
            var tree = CSharpSyntaxTree.ParseText(builderSource);
            var root = (CompilationUnitSyntax)tree.GetRoot();

            var replacements = new Dictionary<MemberAccessExpressionSyntax, ExpressionSyntax>();

            foreach (var memberAccess in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                // Only the OUTERMOST link of a chain — inner sub-chains (`Assets.Materials`, ...)
                // have a MemberAccess parent and are handled as part of the outer node.
                if (memberAccess.Parent is MemberAccessExpressionSyntax)
                {
                    continue;
                }

                if (!TryReadChain(memberAccess, out var names))
                {
                    continue; // not an `Assets.…` chain (root isn't the bare `Assets` identifier).
                }

                if (names.Count < 2)
                {
                    continue; // just `Assets.Foo` (group alone) — not an asset reference.
                }

                var group = names[0];
                var member = names[^1];
                var folderSegments = names.GetRange(1, names.Count - 2);

                if (!oldCatalog.TryGetEntry(group, folderSegments, member, out var entry))
                {
                    continue; // unknown to the old catalog — leave untouched (conservative).
                }

                if (!newCatalog.TryGetMember(entry.Guid, entry.FileId, out var newGroup, out var newFolders, out var newMember))
                {
                    continue; // durable target vanished from the new catalog — safety net.
                }

                var newNames = new List<string> { newGroup };
                newNames.AddRange(newFolders);
                newNames.Add(newMember);

                if (newNames.SequenceEqual(names))
                {
                    continue; // unchanged — no replacement queued.
                }

                var replacement = BuildChain(newNames)
                    .WithLeadingTrivia(memberAccess.GetLeadingTrivia())
                    .WithTrailingTrivia(memberAccess.GetTrailingTrivia());

                replacements[memberAccess] = replacement;
            }

            if (replacements.Count == 0)
            {
                return builderSource;
            }

            var newRoot = root.ReplaceNodes(replacements.Keys, (orig, _) => replacements[orig]);
            return newRoot.ToFullString();
        }

        // Walks a member-access chain leaf->root, collecting identifier names. Returns false
        // unless the terminal expression is exactly the bare `Assets` identifier (rejects
        // `this.Assets.…`, `Foo.Assets.…`).
        private static bool TryReadChain(MemberAccessExpressionSyntax outermost, out List<string> names)
        {
            names = new List<string>();

            ExpressionSyntax current = outermost;
            while (current is MemberAccessExpressionSyntax memberAccess)
            {
                names.Insert(0, memberAccess.Name.Identifier.Text);
                current = memberAccess.Expression;
            }

            return current is IdentifierNameSyntax { Identifier.Text: "Assets" };
        }

        private static ExpressionSyntax BuildChain(IReadOnlyList<string> names)
        {
            ExpressionSyntax expr = SyntaxFactory.IdentifierName("Assets");
            foreach (var name in names)
            {
                expr = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    expr,
                    SyntaxFactory.IdentifierName(name));
            }

            return expr;
        }
    }
}
