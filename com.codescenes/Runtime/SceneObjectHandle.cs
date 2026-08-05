namespace SceneBuilder.Authoring
{
    /// <summary>
    /// A handle to an object in a scene definition: a plain GameObject
    /// (<see cref="NodeHandle"/>) or a prefab instance root (<see cref="InstanceHandle"/>).
    /// Every authoring call that takes a cross-object reference takes this, so either kind of
    /// target can be assigned.
    /// </summary>
    /// <remarks>
    /// Compile-time scaffolding only — SceneBuilder parses the source text to build the scene, so
    /// this carries no state and performs no work at runtime.
    /// </remarks>
    public abstract class SceneObjectHandle
    {
        internal SceneObjectHandle() { }
    }
}
