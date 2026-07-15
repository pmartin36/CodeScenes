namespace SceneBuilder.Authoring
{
    /// <summary>
    /// The value produced by the <see cref="AssetRefs.Asset(string)"/> authoring factory — an asset
    /// reference authored by a readable project path (e.g. <c>Asset("Assets/Materials/Red.mat")</c>).
    /// </summary>
    /// <remarks>
    /// Compile-time scaffolding only. SceneBuilder parses the builder SOURCE TEXT (it never runs the
    /// builder), resolving the authored path to the asset's GUID at build time; this object carries no
    /// runtime state. A cleared/None reference is authored as <c>Asset(null)</c>.
    /// </remarks>
    public sealed class AssetReference
    {
        internal AssetReference()
        {
        }
    }

    /// <summary>
    /// The <c>Asset(displayPath)</c> authoring factory. Bring it into scope with
    /// <c>using static SceneBuilder.Authoring.AssetRefs;</c> and reference a project asset from any
    /// serialized asset field: <c>c.Set("m_Materials", new[] { Asset("Assets/Materials/Red.mat") })</c>.
    /// Author a cleared field with <c>Asset(null)</c>.
    /// </summary>
    /// <remarks>
    /// Compile-time scaffolding only — the parser reads the source text, so this returns an inert
    /// handle and performs no work at runtime.
    /// </remarks>
    public static class AssetRefs
    {
        /// <summary>
        /// Reference the project asset at <paramref name="displayPath"/> (resolved to its GUID at build
        /// time). Pass <c>null</c> to author a cleared / None reference.
        /// </summary>
        public static AssetReference Asset(string displayPath) => new AssetReference();
    }
}
