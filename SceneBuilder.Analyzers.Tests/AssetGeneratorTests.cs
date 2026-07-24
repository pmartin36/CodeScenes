using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using CodeScenes.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SceneBuilder.Core.Model;
using Xunit;

namespace SceneBuilder.Analyzers.Tests
{
    // Tests for b5-t1: AssetCatalogEmit — manifest JSON (AssetCatalog shape) -> typed `Assets`
    // catalog source. Like FacadeGeneratorTests (FacadeEmit, b3-t1), there is no
    // IIncrementalGenerator yet (that is b5-t2) — AssetCatalogEmit is called directly, and the
    // emitted source is compile-checked via AssetCompileHarness. See
    // .agent_handoffs/typed-asset-catalogs/b5-t1/research.md "Test surface".
    public class AssetGeneratorTests
    {
        // Materials:[Environment/Rocks/Stone, Environment/Rocks/Rust] + Meshes:[Models/Props/Barrel]
        // — mirrors this task's DELIVERABLE probe exactly. Stone and Rust share the merged
        // Materials.Environment.Rocks folder class; Barrel sits in an independent Meshes namespace.
        internal static AssetCatalog TankAssetsCatalog() => new AssetCatalog
        {
            SchemaVersion = 1,
            Entries = new[]
            {
                new AssetCatalogEntry
                {
                    Group = "Materials",
                    FolderSegments = new[] { "Environment", "Rocks" },
                    MemberName = "Stone",
                    Guid = "11111111111111111111111111111111",
                    FileId = 0,
                    Path = "Assets/Environment/Rocks/Stone.mat",
                },
                new AssetCatalogEntry
                {
                    Group = "Materials",
                    FolderSegments = new[] { "Environment", "Rocks" },
                    MemberName = "Rust",
                    Guid = "22222222222222222222222222222222",
                    FileId = 0,
                    Path = "Assets/Environment/Rocks/Rust.mat",
                },
                new AssetCatalogEntry
                {
                    Group = "Meshes",
                    FolderSegments = new[] { "Models", "Props" },
                    MemberName = "Barrel",
                    Guid = "33333333333333333333333333333333",
                    FileId = 123,
                    Path = "Assets/Models/Props/Barrel.fbx",
                    SubAsset = "Barrel",
                },
            },
        };

        [Fact]
        public void Generator_AssetsManifest_EmitsPropertyPerEntry()
        {
            var ok = AssetCatalogEmit.TryEmitAssets(TankAssetsCatalog().Serialize(), out var source);
            Assert.True(ok);

            const string probe = @"
public class Probe
{
    public void Use()
    {
        var a = Assets.Materials.Environment.Rocks.Stone;
        var b = Assets.Materials.Environment.Rocks.Rust;
        var c = Assets.Meshes.Models.Props.Barrel;
    }
}
";
            var compilation = AssetCompileHarness.Compile(source, probe);
            var hasErrors = AssetCompileHarness.HasErrors(compilation, out var errorText);
            Assert.False(
                hasErrors,
                "expected the emitted Assets source + probe to compile clean (Stone/Rust merged into one Rocks class, Barrel in an independent Meshes namespace):\n" + errorText);
        }

        [Fact]
        public void Generator_Deterministic_ByteStable()
        {
            var json = TankAssetsCatalog().Serialize();

            var okA = AssetCatalogEmit.TryEmitAssets(json, out var sourceA);
            var okB = AssetCatalogEmit.TryEmitAssets(json, out var sourceB);

            Assert.True(okA);
            Assert.True(okB);
            // Guard against a vacuous empty-string pass: byte-stability is only a meaningful
            // contract if there IS generated source for a present, non-empty manifest.
            Assert.Contains("Assets", sourceA);
            Assert.Equal(sourceA, sourceB);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("{}")]
        [InlineData("{\"schemaVersion\":1,\"entries\":[]}")]
        public void Generator_MissingManifest_EmitsNothing_ReferenceFailsToCompile(string? json)
        {
            var ok = AssetCatalogEmit.TryEmitAssets(json, out var source);

            Assert.False(ok);
            Assert.Equal("", source);

            const string probe = @"
public class Probe
{
    public object A = Assets.Materials.Environment.Rocks.Stone;
}
";
            // No Assets source emitted, so the probe alone must fail to resolve the `Assets` symbol.
            var compilation = AssetCompileHarness.Compile(probeSource: probe);
            var hasErrors = AssetCompileHarness.HasErrors(compilation, out var errorText);
            Assert.True(hasErrors, "expected Assets.Materials... to fail to resolve when no Assets source is emitted");
            Assert.Contains("CS0103", errorText);
        }

        // b5-t1's headless half of the rename/move safety net (spec behavior #8). A REGENERATED
        // manifest with Stone removed (renamed/moved elsewhere) drops the Stone accessor from the
        // merged Rocks class while Rust (untouched) still compiles. A builder still referencing the
        // stale `Assets.Materials.Environment.Rocks.Stone` chain must fail to compile with a located
        // error at the stale `Stone` token — never a silent wrong value.
        [Fact]
        public void StaleTypedRef_AfterRegen_FailsCompileCheck()
        {
            var regenerated = new AssetCatalog
            {
                SchemaVersion = 1,
                Entries = new[]
                {
                    new AssetCatalogEntry
                    {
                        Group = "Materials",
                        FolderSegments = new[] { "Environment", "Rocks" },
                        MemberName = "Rust",
                        Guid = "22222222222222222222222222222222",
                        FileId = 0,
                        Path = "Assets/Environment/Rocks/Rust.mat",
                    },
                },
            };

            var ok = AssetCatalogEmit.TryEmitAssets(regenerated.Serialize(), out var source);
            Assert.True(ok);

            const string staleProbe = @"
public class Probe
{
    public void Use()
    {
        var s = Assets.Materials.Environment.Rocks.Stone;
    }
}
";
            var compilation = AssetCompileHarness.Compile(source, staleProbe);
            var errors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToArray();

            var staleError = Assert.Single(errors);
            var span = staleError.Location.SourceTree!.GetText().ToString(staleError.Location.SourceSpan);
            Assert.Equal("Stone", span);

            const string validProbe = @"
public class Probe
{
    public void Use()
    {
        var r = Assets.Materials.Environment.Rocks.Rust;
    }
}
";
            var validCompilation = AssetCompileHarness.Compile(source, validProbe);
            var hasErrors = AssetCompileHarness.HasErrors(validCompilation, out var errorText);
            Assert.False(
                hasErrors,
                "expected the regenerated Rust accessor to compile clean (proves the emit itself is healthy and only the stale Stone accessor fails):\n" + errorText);
        }

        // Pins the research REFINED decision: b1-t2 stores FINAL, already-sanitized,
        // keyword-`@`-escaped identifiers — AssetCatalogEmit must render them VERBATIM, never
        // re-sanitize (which would turn `@class` into `_class`, diverging from the manifest name the
        // b2-t1 parser matches against).
        [Fact]
        public void KeywordEscapedMember_EmittedVerbatim_NotResanitized()
        {
            var catalog = new AssetCatalog
            {
                SchemaVersion = 1,
                Entries = new[]
                {
                    new AssetCatalogEntry
                    {
                        Group = "Materials",
                        FolderSegments = new[] { "@class" },
                        MemberName = "@object",
                        Guid = "44444444444444444444444444444444",
                        FileId = 0,
                        Path = "Assets/class/object.mat",
                    },
                },
            };

            var ok = AssetCatalogEmit.TryEmitAssets(catalog.Serialize(), out var source);
            Assert.True(ok);
            Assert.Contains("@class", source);
            Assert.Contains("@object", source);
            Assert.DoesNotContain("_class", source);
            Assert.DoesNotContain("_object", source);

            const string probe = @"
public class Probe
{
    public void Use()
    {
        var v = Assets.Materials.@class.@object;
    }
}
";
            var compilation = AssetCompileHarness.Compile(source, probe);
            var hasErrors = AssetCompileHarness.HasErrors(compilation, out var errorText);
            Assert.False(
                hasErrors,
                "expected the keyword-escaped chain to compile clean (verbatim @-escaped identifiers, not re-sanitized):\n" + errorText);
        }
    }

    // Compiles [Assets source] + [probe] + [AssetReference stub] via a plain CSharpCompilation —
    // there is no IIncrementalGenerator to drive yet (that is b5-t2), so this does NOT use
    // GeneratorHarness/InMemoryAdditionalText; it mirrors FacadeGeneratorTests.FacadeCompileHarness.
    internal static class AssetCompileHarness
    {
        private const string AssetReferenceStub =
            "namespace SceneBuilder.Authoring { public sealed class AssetReference {} }";

        public static Compilation Compile(string? assetsSource = null, string? probeSource = null)
        {
            var syntaxTrees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(AssetReferenceStub) };
            if (assetsSource is not null)
                syntaxTrees.Add(CSharpSyntaxTree.ParseText(assetsSource));
            if (probeSource is not null)
                syntaxTrees.Add(CSharpSyntaxTree.ParseText(probeSource));

            var references = new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            };

            return CSharpCompilation.Create(
                "AssetCompileHarnessAssembly",
                syntaxTrees,
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        }

        public static bool HasErrors(Compilation compilation, out string errorText)
        {
            var errors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToArray();
            errorText = string.Join("\n", errors.Select(d => d.ToString()));
            return errors.Length > 0;
        }
    }

    // Tests for b5-t2: AssetCatalogSourceGenerator (IIncrementalGenerator) — the wiring layer that
    // keys an AdditionalTextsProvider on ManifestFileName, calls AssetCatalogEmit (b5-t1), and
    // AddSources the result into the compilation. Mirrors PrefabFacadeGeneratorTests
    // (FacadeGeneratorTests.cs), driving AssetCatalogSourceGenerator via AssetGeneratorHarness below
    // instead of calling AssetCatalogEmit directly. See
    // .agent_handoffs/typed-asset-catalogs/b5-t2/test-writer.md "Contract".
    public class AssetCatalogGeneratorTests
    {
        [Fact]
        public void AssetCatalogGenerator_PresentManifest_TypedRefsResolve()
        {
            const string probe = @"
public class Probe
{
    public void Use()
    {
        var a = Assets.Materials.Environment.Rocks.Stone;
        var b = Assets.Materials.Environment.Rocks.Rust;
        var c = Assets.Meshes.Models.Props.Barrel;
    }
}
";
            var json = AssetGeneratorTests.TankAssetsCatalog().Serialize();
            var (_, compilation) = AssetGeneratorHarness.Run(json, probe);

            var hasErrors = AssetGeneratorHarness.HasErrors(compilation, out var errorText);
            Assert.False(hasErrors, "expected zero compile errors referencing Assets.Materials.../Assets.Meshes...:\n" + errorText);
        }

        [Fact]
        public void AssetCatalogGenerator_MissingManifest_EmitsNothing_ReferenceFailsToCompile()
        {
            const string probe = @"
public class Probe
{
    public object A = Assets.Materials.Environment.Rocks.Stone;
}
";
            var (runResult, compilation) = AssetGeneratorHarness.Run(manifestJson: null, probeSource: probe);

            Assert.Empty(runResult.GeneratedTrees);

            var hasErrors = AssetGeneratorHarness.HasErrors(compilation, out var errorText);
            Assert.True(hasErrors, "expected Assets.Materials... to fail to resolve when no manifest AdditionalFile is present");
            Assert.Contains("CS0103", errorText);
        }

        [Fact]
        public void AssetCatalogGenerator_EmptyManifest_EmitsNothing()
        {
            const string emptyManifest = "{\"schemaVersion\":1,\"entries\":[]}";

            var (runResult, _) = AssetGeneratorHarness.Run(emptyManifest);

            Assert.Empty(runResult.GeneratedTrees);
        }

        [Fact]
        public void AssetCatalogGenerator_Deterministic_ByteStable()
        {
            var json = AssetGeneratorTests.TankAssetsCatalog().Serialize();

            var (runA, _) = AssetGeneratorHarness.Run(json);
            var (runB, _) = AssetGeneratorHarness.Run(json);

            var textA = AssetGeneratorHarness.CombinedGeneratedText(runA);
            var textB = AssetGeneratorHarness.CombinedGeneratedText(runB);

            // Byte-stability is only a meaningful contract if there IS generated source for a
            // present, non-empty manifest — guard against a vacuous "emits nothing" pass.
            Assert.Contains("Assets", textA);
            Assert.Equal(textA, textB);
        }
    }

    // Runs AssetCatalogSourceGenerator via CSharpGeneratorDriver — mirrors FacadeGeneratorHarness
    // (FacadeGeneratorTests.cs), which is hardwired to PrefabFacadeGenerator. Reuses the shared
    // InMemoryAdditionalText fixture (InMemoryAdditionalText.cs) and the AssetReference stub
    // convention from AssetCompileHarness above.
    internal static class AssetGeneratorHarness
    {
        private const string AssetReferenceStub =
            "namespace SceneBuilder.Authoring { public sealed class AssetReference {} }";

        public static (GeneratorDriverRunResult RunResult, Compilation OutputCompilation) Run(
            string? manifestJson,
            string? probeSource = null,
            string manifestPath = "/fake/SceneBuilders/Generated/" + AssetCatalogSourceGenerator.ManifestFileName)
        {
            var additionalTexts = manifestJson is null
                ? ImmutableArray<AdditionalText>.Empty
                : ImmutableArray.Create<AdditionalText>(new InMemoryAdditionalText(manifestPath, manifestJson));

            var syntaxTrees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(AssetReferenceStub) };
            if (probeSource is not null)
                syntaxTrees.Add(CSharpSyntaxTree.ParseText(probeSource));

            var references = new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            };

            var compilation = CSharpCompilation.Create(
                "AssetCatalogGeneratorHarnessAssembly",
                syntaxTrees,
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            GeneratorDriver driver = CSharpGeneratorDriver.Create(new AssetCatalogSourceGenerator())
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
