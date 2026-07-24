using System.IO;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace CodeScenes.Analyzers
{
    /// <summary>
    /// Keys on the `Assets.sbassets.json` AdditionalFile and emits the typed `Assets`
    /// catalog source (via AssetCatalogEmit) into the compilation. Mirrors PrefabFacadeGenerator
    /// verbatim (AdditionalTextsProvider.Where -> Collect -> RegisterSourceOutput -> AssetCatalogEmit
    /// -> AddSource, normalized newlines). See
    /// .agent_handoffs/typed-asset-catalogs/b5-t2/test-writer.md "Contract".
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class AssetCatalogSourceGenerator : IIncrementalGenerator
    {
        // Mirrored literal — must stay byte-identical to Core's AssetManifest.FileName (b1-t1) and
        // the editor producer's output file name (b6-t1).
        public const string ManifestFileName = "Assets.sbassets.json";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var manifestTexts = context.AdditionalTextsProvider
                .Where(static text => Path.GetFileName(text.Path) == ManifestFileName)
                .Select(static (text, ct) => text.GetText(ct)?.ToString())
                .Collect();

            context.RegisterSourceOutput(manifestTexts, static (spc, texts) =>
            {
                string? manifestJson = null;
                foreach (var text in texts)
                {
                    if (!string.IsNullOrEmpty(text))
                    {
                        manifestJson = text;
                        break;
                    }
                }

                if (manifestJson is null)
                    return;

                if (!AssetCatalogEmit.TryEmitAssets(manifestJson, out var source))
                    return;

                spc.AddSource("CodeScenesAssets.Assets.g.cs", SourceText.From(NormalizeNewlines(source), Encoding.UTF8));
            });
        }

        private static string NormalizeNewlines(string text) =>
            text.Replace("\r\n", "\n").Replace("\r", "\n");
    }
}
