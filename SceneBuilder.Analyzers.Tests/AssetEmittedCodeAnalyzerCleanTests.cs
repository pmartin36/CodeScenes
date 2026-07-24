using System.Collections.Immutable;
using System.Linq;
using CodeScenes.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace SceneBuilder.Analyzers.Tests
{
    // Test #13 (b5-t1): the emitted `Assets` typed-catalog source must be analyzer-clean, mirroring
    // EmittedCodeAnalyzerCleanTests' AnalyzerErrors pattern (b4-t1) but driving AssetCatalogEmit's
    // real output instead of the Reconcile/SourcePatch emit path.
    public class AssetEmittedCodeAnalyzerCleanTests
    {
        private static ImmutableArray<Diagnostic> AnalyzerErrors(string source)
        {
            var compilation = CSharpCompilation.Create(
                "AssetEmitCleanCorpus",
                new[] { CSharpSyntaxTree.ParseText(source) },
                references: System.Array.Empty<MetadataReference>(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var withAnalyzers = compilation.WithAnalyzers(
                ImmutableArray.Create<DiagnosticAnalyzer>(new BuilderAnalyzer()));

            var diagnostics = withAnalyzers.GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();

            return diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();
        }

        [Fact]
        public void EmittedTypedAssetRef_IsAnalyzerClean()
        {
            var ok = AssetCatalogEmit.TryEmitAssets(
                AssetGeneratorTests.TankAssetsCatalog().Serialize(), out var source);
            Assert.True(ok);

            var errors = AnalyzerErrors(source);

            Assert.True(
                errors.IsEmpty,
                $"expected the emitted Assets source to be analyzer-clean, tripped {errors.Length} Error diagnostic(s): " +
                string.Join("; ", errors.Select(d => $"{d.Id} @ {d.Location.GetLineSpan()}")) +
                $"\nEmitted:\n{source}");
        }
    }
}
