using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using SceneBuilder.Core.Parsing;

namespace SceneBuilder.Core.Reconcile
{
    // Source-only Roslyn rewrite pass that heals a colliding `.Id(...)`/handle before Sync
    // writes back: for every LogicalId that ParseResult.Ambiguities flags as DuplicateLogicalId,
    // the FIRST occurrence (document order) is the incumbent and stays untouched — it keeps the
    // sidecar's GlobalObjectId — while every LATER occurrence with no authored handle is
    // re-minted into `var <handle> = <chain-without-.Id>;`. A later occurrence that already has an
    // authored handle cannot be safely renamed (every reference in its scope would need
    // rewriting too), so it is left as report-only and the conflict survives a re-parse.
    //
    // Reuses StatementText.BuildHandleDeclaration (the one handle-declaration path) and
    // SourcePatchApplier.RemoveTrailingInvocation (the chained-call-removal shape shared with
    // ResolveRemoveFlagCall) rather than cloning either. See
    // .agent_handoffs/duplicate-sibling-identity/b3-t1/research.md for the full blueprint.
    public static class IdCollisionHealer
    {
        public static string Heal(string source, ParseResult parse)
        {
            var collidingIds = new HashSet<string>(
                parse.Ambiguities
                    .Where(c => c.Kind == ConflictKind.DuplicateLogicalId && c.LogicalId != null)
                    .Select(c => c.LogicalId!),
                StringComparer.Ordinal);

            if (collidingIds.Count == 0)
            {
                return source;
            }

            var reserved = new HashSet<string>(StringComparer.Ordinal);
            foreach (var anchor in parse.NodeAnchors)
            {
                reserved.Add(anchor.LogicalId);
                if (anchor.Handle != null)
                {
                    reserved.Add(anchor.Handle);
                }
            }

            var tree = CSharpSyntaxTree.ParseText(source);
            var root = (CompilationUnitSyntax)tree.GetRoot();

            var replacements = new Dictionary<ExpressionStatementSyntax, StatementSyntax>();

            var groups = parse.NodeAnchors
                .GroupBy(a => a.LogicalId, StringComparer.Ordinal)
                .Where(g => collidingIds.Contains(g.Key));

            foreach (var group in groups)
            {
                foreach (var anchor in group.Skip(1))
                {
                    if (anchor.Handle != null)
                    {
                        // Report-only: renaming an authored `var` would need every reference in
                        // its scope rewritten too. Leave it — the conflict survives the re-parse.
                        continue;
                    }

                    var statement = FindStatement(root, anchor.Span);
                    if (statement == null || replacements.ContainsKey(statement))
                    {
                        continue;
                    }

                    var handle = HandleNaming.Derive(anchor.Name, reserved);
                    reserved.Add(handle);

                    var target = statement;
                    if (anchor.IdCallSpan is SourceSpan idCallSpan)
                    {
                        var idCall = FindIdCall(statement, idCallSpan);
                        if (idCall != null)
                        {
                            target = (ExpressionStatementSyntax)SourcePatchApplier.RemoveTrailingInvocation(statement, idCall);
                        }
                    }

                    replacements[statement] = SourcePatchApplier.BuildHandleDeclaration(target, handle);
                }
            }

            if (replacements.Count == 0)
            {
                return source;
            }

            var newRoot = root.ReplaceNodes(replacements.Keys, (orig, _) => replacements[orig]);
            return newRoot.ToFullString();
        }

        private static ExpressionStatementSyntax? FindStatement(SyntaxNode root, SourceSpan span)
        {
            var textSpan = TextSpan.FromBounds(span.Start, span.Start + span.Length);
            var node = root.FindNode(textSpan, getInnermostNodeForTie: true);
            return node.FirstAncestorOrSelf<ExpressionStatementSyntax>();
        }

        private static InvocationExpressionSyntax? FindIdCall(SyntaxNode statement, SourceSpan span)
        {
            var textSpan = TextSpan.FromBounds(span.Start, span.Start + span.Length);
            var node = statement.FindNode(textSpan, getInnermostNodeForTie: true);
            return node as InvocationExpressionSyntax ?? node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
        }
    }
}
