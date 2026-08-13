using System;

namespace SceneBuilder.Authoring
{
    /// <summary>
    /// The <c>root</c> parameter of <see cref="IPrefabVariantDefinition.Build"/> — an override-only
    /// handle onto the variant's base prefab instance. There is no <c>Add</c>: the variant's single
    /// root IS the base prefab, never a freshly authored object.
    /// </summary>
    /// <remarks>
    /// Compile-time scaffolding only — SceneBuilder parses the source text to build the variant, so
    /// these methods return handles for chaining but perform no work at runtime.
    /// </remarks>
    public sealed class VariantRoot
    {
        /// <summary>
        /// Author property overrides on the base prefab's own root components, mirroring
        /// <see cref="InstanceHandle.Override(Action{OverrideHandle})"/>.
        /// </summary>
        public VariantRoot Override(Action<OverrideHandle> configure)
        {
            configure?.Invoke(new OverrideHandle());
            return this;
        }

        /// <summary>Add a component of type <typeparamref name="T"/> to the base prefab's root with no field overrides.</summary>
        public VariantRoot AddComponent<T>() => this;

        /// <summary>Add a component of type <typeparamref name="T"/> to the base prefab's root and set its serialized fields in a closure.</summary>
        public VariantRoot AddComponent<T>(Action<ComponentHandle<T>> configure)
        {
            configure?.Invoke(new ComponentHandle<T>());
            return this;
        }

        /// <summary>Remove a component of type <typeparamref name="T"/> from the base prefab's root (must exist on the base).</summary>
        public VariantRoot RemoveComponent<T>() => this;

        /// <summary>
        /// Author overrides scoped to a nested target inside the base prefab's hierarchy, addressed
        /// by child path (e.g. <c>"Turret/Barrel"</c>), mirroring
        /// <see cref="InstanceHandle.On(string, Action{ScopedHandle})"/>.
        /// </summary>
        public VariantRoot On(string childPath, Action<ScopedHandle> closure)
        {
            closure?.Invoke(new ScopedHandle());
            return this;
        }
    }
}
