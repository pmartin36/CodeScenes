using System.IO;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace CodeScenes.Analyzers
{
    /// <summary>
    /// Keys on the `ProjectCatalog.sbcatalog.json` AdditionalFile and emits `Tags`/`Layers` static
    /// classes into the compilation (b3-t2). See
    /// .agent_handoffs/codescenes-analyzers/b3-t2/research.md for the pipeline shape.
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class CatalogSourceGenerator : IIncrementalGenerator
    {
        public const string ManifestFileName = "ProjectCatalog.sbcatalog.json";

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

                if (!CatalogEmit.TryParseManifest(manifestJson, out var tags, out var layers))
                    return;

                if (tags.Count > 0)
                {
                    var source = NormalizeNewlines(CatalogEmit.EmitTags(tags));
                    spc.AddSource("CodeScenesCatalog.Tags.g.cs", SourceText.From(source, Encoding.UTF8));
                }

                if (layers.Count > 0)
                {
                    var source = NormalizeNewlines(CatalogEmit.EmitLayers(layers));
                    spc.AddSource("CodeScenesCatalog.Layers.g.cs", SourceText.From(source, Encoding.UTF8));
                }
            });
        }

        private static string NormalizeNewlines(string text) =>
            text.Replace("\r\n", "\n").Replace("\r", "\n");
    }
}
