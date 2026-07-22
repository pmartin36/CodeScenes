using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SceneBuilder.Analyzers.Tests
{
    // Tests #15-#19 (b3-t2): CatalogSourceGenerator must deserialize the ProjectCatalog.sbcatalog.json
    // AdditionalFile (CanonicalJson shape: schemaVersion/tags/layers[index,name]) and emit Tags/Layers
    // static classes into the compilation — byte-stable, sanitized/deduplicated, fail-safe on missing
    // input. See .agent_handoffs/codescenes-analyzers/b3-t2/research.md "Test surface".
    public class GeneratorTests
    {
        private const string TagsAndLayersManifest = @"{
  ""schemaVersion"": 1,
  ""tags"": [""Untagged"", ""Player"", ""Enemy""],
  ""layers"": [
    { ""index"": 0, ""name"": ""Default"" },
    { ""index"": 6, ""name"": ""Ground"" }
  ]
}";

        [Fact]
        public void Generator_TagsManifest_EmitsConstPerTag()
        {
            const string probe = @"
public class Probe
{
    public string A = Tags.Untagged;
    public string B = Tags.Player;
    public string C = Tags.Enemy;
}
";
            var (_, compilation) = GeneratorHarness.Run(TagsAndLayersManifest, probe);

            var hasErrors = GeneratorHarness.HasErrors(compilation, out var errorText);
            Assert.False(hasErrors, "expected zero compile errors referencing Tags.Untagged/Player/Enemy:\n" + errorText);
        }

        [Fact]
        public void Generator_LayersManifest_EmitsIndexAndName()
        {
            const string probe = @"
public class Probe
{
    public int Index = Layers.Ground;
    public string Name = Layers.Name.Ground;
}
";
            var (runResult, compilation) = GeneratorHarness.Run(TagsAndLayersManifest, probe);

            var hasErrors = GeneratorHarness.HasErrors(compilation, out var errorText);
            Assert.False(hasErrors, "expected zero compile errors referencing Layers.Ground / Layers.Name.Ground:\n" + errorText);

            var generated = GeneratorHarness.CombinedGeneratedText(runResult);
            Assert.Contains("Ground = 6", generated);
            Assert.Contains("Ground = \"Ground\"", generated);
        }

        [Fact]
        public void Generator_Deterministic_ByteStable()
        {
            var (runA, _) = GeneratorHarness.Run(TagsAndLayersManifest);
            var (runB, _) = GeneratorHarness.Run(TagsAndLayersManifest);

            var textA = GeneratorHarness.CombinedGeneratedText(runA);
            var textB = GeneratorHarness.CombinedGeneratedText(runB);

            // Byte-stability is only a meaningful contract if there IS generated source for a
            // present, non-empty manifest — guard against a vacuous "emits nothing" pass.
            Assert.Contains("Tags", textA);
            Assert.Equal(textA, textB);
        }

        [Fact]
        public void Generator_NonIdentifierTagName_SanitizedDeterministically()
        {
            const string manifest = @"{
  ""schemaVersion"": 1,
  ""tags"": [""Big Rock!"", ""Big Rock?""],
  ""layers"": []
}";
            var (runResult, compilation) = GeneratorHarness.Run(manifest);

            var hasErrors = GeneratorHarness.HasErrors(compilation, out var errorText);
            Assert.False(hasErrors, "sanitized/deduplicated names must not collide (no CS0102 duplicate-member):\n" + errorText);

            var generated = GeneratorHarness.CombinedGeneratedText(runResult);
            var constNames = Regex.Matches(generated, @"public const string (\w+) = ""Big Rock[!?]"";")
                .Select(m => m.Groups[1].Value)
                .ToArray();

            Assert.Equal(2, constNames.Length);
            Assert.NotEqual(constNames[0], constNames[1]);

            // Determinism: sanitized/deduplicated identifiers are stable across runs for the SAME
            // colliding input.
            var (runResultB, _) = GeneratorHarness.Run(manifest);
            var generatedB = GeneratorHarness.CombinedGeneratedText(runResultB);
            Assert.Equal(generated, generatedB);
        }

        [Fact]
        public void Generator_MissingManifest_EmitsNothing_ReferenceFailsToCompile()
        {
            const string probe = @"
public class Probe
{
    public string A = Tags.Player;
}
";
            var (runResult, compilation) = GeneratorHarness.Run(manifestJson: null, probeSource: probe);

            Assert.Empty(runResult.GeneratedTrees);

            var hasErrors = GeneratorHarness.HasErrors(compilation, out var errorText);
            Assert.True(hasErrors, "expected Tags.Player to fail to resolve when no manifest AdditionalFile is present");
            Assert.Contains("CS0103", errorText);
        }
    }
}
