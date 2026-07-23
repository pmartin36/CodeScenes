using System;

namespace SceneBuilder.Authoring
{
    /// <summary>
    /// The closure argument passed to <see cref="InstanceHandle.On(string, Action{ScopedHandle})"/> /
    /// <see cref="InstanceHandle{TRef}.On{TNode}"/>, used to author overrides scoped to a nested target
    /// inside a prefab instance's hierarchy (e.g. the turret's barrel).
    /// </summary>
    /// <remarks>
    /// Compile-time scaffolding only — SceneBuilder parses the source text to build the scene, so
    /// these methods return handles for chaining but perform no work at runtime.
    /// </remarks>
    public sealed class ScopedHandle
    {
        /// <summary>
        /// Author property overrides on the scoped target's own components, mirroring
        /// <see cref="InstanceHandle.Override(Action{OverrideHandle})"/>.
        /// </summary>
        public ScopedHandle Override(Action<OverrideHandle> configure)
        {
            configure?.Invoke(new OverrideHandle());
            return this;
        }

        /// <summary>Add a component of type <typeparamref name="T"/> to the scoped target with no field overrides.</summary>
        public ScopedHandle AddComponent<T>() => this;

        /// <summary>Add a component of type <typeparamref name="T"/> to the scoped target and set its serialized fields in a closure.</summary>
        public ScopedHandle AddComponent<T>(Action<ComponentHandle<T>> configure)
        {
            configure?.Invoke(new ComponentHandle<T>());
            return this;
        }

        /// <summary>Remove a component of type <typeparamref name="T"/> from the scoped target (must exist on the source prefab).</summary>
        public ScopedHandle RemoveComponent<T>() => this;
    }
}
