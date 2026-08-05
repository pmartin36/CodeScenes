using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
    }
}
