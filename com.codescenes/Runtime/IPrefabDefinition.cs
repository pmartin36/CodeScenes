namespace SceneBuilder.Authoring
{
    /// <summary>
    /// A Unity prefab defined in code. SceneBuilder <b>parses</b> the <see cref="Build"/> method
    /// (it does not execute it) and materializes the described prefab, then keeps code and prefab
    /// in sync. A prefab defines exactly one root object.
    /// </summary>
    public interface IPrefabDefinition
    {
        void Build(PrefabRoot root);
    }
}
