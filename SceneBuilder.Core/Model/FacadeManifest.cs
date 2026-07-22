namespace SceneBuilder.Core.Model
{
    /// <summary>
    /// The single Core-owned source of truth for the generated façade manifest's file name.
    /// com.codescenes/SceneBuilderPaths references this const rather than redeclaring it;
    /// CodeScenes.Analyzers (ns2.0, cannot reference Core) mirrors the literal — exactly as
    /// CatalogSourceGenerator.ManifestFileName mirrors SceneBuilderPaths.ProjectCatalogFileName.
    /// </summary>
    public static class FacadeManifest
    {
        public const string FileName = "Prefabs.sbfacade.json";
    }
}
