namespace SceneBuilder.Core.Model
{
    /// <summary>
    /// The single Core-owned source of truth for the generated asset catalog manifest's file
    /// name. com.codescenes/SceneBuilderPaths references this const rather than redeclaring it;
    /// CodeScenes.Analyzers (ns2.0, cannot reference Core) mirrors the literal — exactly as
    /// FacadeManifest.FileName is mirrored elsewhere.
    /// </summary>
    public static class AssetManifest
    {
        public const string FileName = "Assets.sbassets.json";
    }
}
