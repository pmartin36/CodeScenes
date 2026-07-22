using System.IO;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace CodeScenes.Analyzers
{
    /// <summary>
    /// Keys on the `Prefabs.sbfacade.json` AdditionalFile and emits the façade types
    /// (`Prefabs` static class + per-prefab ref types, via FacadeEmit) into the compilation
    /// (b3-t2). Mirrors CatalogSourceGenerator verbatim. See
    /// .agent_handoffs/typed-prefab-facades/b3-t2/test-writer.md "Contract".
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class PrefabFacadeGenerator : IIncrementalGenerator
    {
        // Mirrored literal — ns2.0 CodeScenes.Analyzers cannot reference SceneBuilder.Core, so this
        // cannot be a direct reference to SceneBuilder.Core.Model.FacadeManifest.FileName. Must stay
        // byte-identical to that const (b1-t1) and to the editor writer's output file name (b5-t2).
        public const string ManifestFileName = "Prefabs.sbfacade.json";

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

                if (!FacadeEmit.TryEmitFacades(manifestJson, out var source))
                    return;

                spc.AddSource("CodeScenesFacades.Prefabs.g.cs", SourceText.From(NormalizeNewlines(source), Encoding.UTF8));
            });
        }

        private static string NormalizeNewlines(string text) =>
            text.Replace("\r\n", "\n").Replace("\r", "\n");
    }
}
