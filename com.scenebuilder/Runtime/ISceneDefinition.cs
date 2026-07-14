namespace SceneBuilder.Authoring
{
    /// <summary>
    /// A Unity scene defined in code. SceneBuilder <b>parses</b> the <see cref="Build"/> method
    /// (it does not execute it) and materializes the described scene, then keeps code and scene in sync.
    /// </summary>
    public interface ISceneDefinition
    {
        void Build(SceneRoot scene);
    }
}
