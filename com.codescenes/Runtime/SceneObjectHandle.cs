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

        /// <summary>This scene object, typed as <typeparamref name="T"/> so it can be written as a
        /// UnityEvent listener's object argument (e.g. <c>m.SetSubject(door.As&lt;UnityEngine.GameObject&gt;())</c>).
        /// <typeparamref name="T"/> is a compile-time cast for that parameter slot; it does NOT select a
        /// component (use <see cref="NodeHandle.Ref{T}(int)"/> for that) and performs no work at
        /// runtime.</summary>
        public T As<T>() => default!;
    }
}
