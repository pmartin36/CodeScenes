using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    internal static class SourcePatchTestHelpers
    {
        // Merges GameObject Anchors with component Anchors, mirroring the merged dict the
        // Reconcile caller passes to Apply (research DATA_FLOW).
        internal static Dictionary<string, SourceSpan> MergeAnchors(SceneBuilder.Core.Parsing.ParseResult parsed)
        {
            var merged = new Dictionary<string, SourceSpan>(parsed.Anchors);
            foreach (var kv in parsed.ComponentAnchors)
            {
                merged[kv.Key] = kv.Value;
            }
            return merged;
        }

        // Fails if the given source has any C# syntax error, so an emitted patch is asserted to
        // actually parse rather than merely to contain an expected substring.
        internal static void AssertNoSyntaxErrors(string source)
        {
            var diagnostics = CSharpSyntaxTree.ParseText(source).GetDiagnostics();
            Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        }

        // For every block, for every statement at index i, every identifier it uses that matches an
        // in-block local declaration must be declared strictly before i. Independent of the
        // production placement code (StatementPlacement.cs) so it is a real oracle, not a
        // tautology. Shared by every test asserting declare-before-use on emitted source.
        internal static void AssertNoForwardLocalReference(string source)
        {
            var root = CSharpSyntaxTree.ParseText(source).GetRoot();

            foreach (var block in root.DescendantNodes().OfType<BlockSyntax>())
            {
                var statements = block.Statements;
                var declIndex = new Dictionary<string, int>();
                for (var i = 0; i < statements.Count; i++)
                {
                    if (statements[i] is LocalDeclarationStatementSyntax local)
                    {
                        foreach (var variable in local.Declaration.Variables)
                        {
                            declIndex[variable.Identifier.Text] = i;
                        }
                    }
                }

                for (var i = 0; i < statements.Count; i++)
                {
                    foreach (var id in statements[i].DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                    {
                        if (declIndex.TryGetValue(id.Identifier.Text, out var declaredAt))
                        {
                            Assert.True(
                                declaredAt < i,
                                $"'{id.Identifier.Text}' used at statement {i} before its declaration at {declaredAt}: {statements[i]}");
                        }
                    }
                }
            }
        }
    }
}
