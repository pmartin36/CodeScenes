namespace SceneBuilder.Authoring
{
    /// <summary>
    /// The value produced by the <see cref="AssetRefs.Asset(string)"/>, <see cref="AssetRefs.Asset(string, string)"/>,
    /// <see cref="AssetRefs.Builtin(string)"/>, or <see cref="AssetRefs.Builtin(string, string)"/> authoring
    /// factories — an asset reference authored by either a readable project path (e.g.
    /// <c>Asset("Assets/Materials/Red.mat")</c>), a sub-object of an imported project asset (e.g.
    /// <c>Asset("Assets/Models/Barrel.fbx", "BarrelMesh")</c>), or the name of a Unity built-in resource
    /// (e.g. <c>Builtin("Cube")</c>).
    /// </summary>
    /// <remarks>
    /// Compile-time scaffolding only. SceneBuilder parses the builder SOURCE TEXT (it never runs the
    /// builder), resolving the authored path/name to the asset's GUID at build time; this object carries no
    /// runtime state. A cleared/None reference is authored as <c>Asset(null)</c>.
    /// </remarks>
    public sealed class AssetReference
    {
        internal AssetReference()
        {
        }
    }

    /// <summary>
    /// The <c>Asset(displayPath[, subAssetName])</c> and <c>Builtin(name[, typeHint])</c> authoring
    /// factories. Bring them into scope with <c>using static SceneBuilder.Authoring.AssetRefs;</c> and
    /// reference an asset from any serialized asset field:
    /// <c>c.Set("m_Materials", new[] { Asset("Assets/Materials/Red.mat") })</c>,
    /// <c>c.Set("m_Mesh", Asset("Assets/Models/Barrel.fbx", "BarrelMesh"))</c>, or
    /// <c>c.Set("m_Mesh", Builtin("Cube"))</c>. Author a cleared field with <c>Asset(null)</c>.
    /// </summary>
    /// <remarks>
    /// Compile-time scaffolding only — the parser reads the source text, so these return an inert
    /// handle and perform no work at runtime.
    /// </remarks>
    public static class AssetRefs
    {
        /// <summary>
        /// Reference the project asset at <paramref name="displayPath"/> (resolved to its GUID at build
        /// time). Pass <c>null</c> to author a cleared / None reference.
        /// </summary>
        public static AssetReference Asset(string displayPath) => new AssetReference();

        /// <summary>
        /// Reference the sub-object named <paramref name="subAssetName"/> inside the imported project asset
        /// at <paramref name="displayPath"/> (e.g. a Mesh inside an FBX, a sub-material, a sliced Sprite),
        /// resolved to the sub-object's GUID + local file identifier at build time.
        /// </summary>
        public static AssetReference Asset(string displayPath, string subAssetName) => new AssetReference();

        /// <summary>
        /// Reference the Unity built-in resource named <paramref name="name"/> (resolved from the editor's
        /// built-in resource containers at build time).
        /// </summary>
        public static AssetReference Builtin(string name) => new AssetReference();

        /// <summary>
        /// Reference the Unity built-in resource named <paramref name="name"/>, qualified by
        /// <paramref name="typeHint"/> (the concrete type name, e.g. <c>"Sprite"</c>) to disambiguate names
        /// shared by more than one built-in object.
        /// </summary>
        public static AssetReference Builtin(string name, string typeHint) => new AssetReference();
    }
}
