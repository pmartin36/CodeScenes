namespace SceneBuilder.Authoring
{
    /// <summary>
    /// A prefab variant defined in code: a base prefab plus a set of root-level overrides,
    /// authored without re-declaring the base prefab's hierarchy. SceneBuilder <b>parses</b> the
    /// <see cref="Build"/> method (it does not execute it) and materializes the variant as a
    /// single prefab-instance root over <see cref="Base"/>, then keeps code and prefab in sync.
    /// </summary>
    public interface IPrefabVariantDefinition
    {
        /// <summary>The base prefab this variant overrides, e.g. <c>Prefabs.Tank</c>.</summary>
        PrefabRef Base { get; }

        /// <summary>Authors the variant's root-level overrides onto <paramref name="root"/>.</summary>
        void Build(VariantRoot root);
    }
}
