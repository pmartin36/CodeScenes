using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using CodeScenes.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace SceneBuilder.Analyzers.Tests
{
    // Minimal AdditionalText fixture — CatalogSourceGenerator (b3-t2) is driven directly via
    // CSharpGeneratorDriver (NOT CSharpAnalyzerTest/VerifierSetup.cs — that harness is for
    // DiagnosticAnalyzer, not IIncrementalGenerator). See
    // .agent_handoffs/codescenes-analyzers/b3-t2/research.md "Duplicate / reuse check".
    internal sealed class InMemoryAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        public InMemoryAdditionalText(string path, string content)
        {
            Path = path;
            _text = SourceText.From(content, Encoding.UTF8);
        }

        public override string Path { get; }

        public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }

    // Runs CatalogSourceGenerator over an optional manifest AdditionalText + optional probe source,
    // returning both the raw generator run result (for inspecting generated source text/hint names)
    // and the compile-asserted output compilation (for CS-diagnostic checks against the probe).
    internal static class GeneratorHarness
    {
        public static (GeneratorDriverRunResult RunResult, Compilation OutputCompilation) Run(
            string? manifestJson,
            string? probeSource = null,
            string manifestPath = "/fake/SceneBuilders/Generated/" + CatalogSourceGenerator.ManifestFileName)
        {
            var additionalTexts = manifestJson is null
                ? ImmutableArray<AdditionalText>.Empty
                : ImmutableArray.Create<AdditionalText>(new InMemoryAdditionalText(manifestPath, manifestJson));

            var syntaxTrees = probeSource is null
                ? Array.Empty<SyntaxTree>()
                : new[] { CSharpSyntaxTree.ParseText(probeSource) };

            var references = new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            };

            var compilation = CSharpCompilation.Create(
                "CatalogGeneratorHarnessAssembly",
                syntaxTrees,
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            GeneratorDriver driver = CSharpGeneratorDriver.Create(new CatalogSourceGenerator())
                .AddAdditionalTexts(additionalTexts);

            driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

            return (driver.GetRunResult(), outputCompilation);
        }

        public static string CombinedGeneratedText(GeneratorDriverRunResult runResult) =>
            string.Join(
                "\n",
                runResult.Results
                    .SelectMany(r => r.GeneratedSources)
                    .Select(s => s.SourceText.ToString()));

        public static bool HasErrors(Compilation compilation, out string errorText)
        {
            var errors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToArray();
            errorText = string.Join("\n", errors.Select(d => d.ToString()));
            return errors.Length > 0;
        }
    }
}
