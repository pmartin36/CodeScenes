using System;

namespace SceneBuilder.Authoring
{
    /// <summary>
    /// A handle to a prefab instance in a scene definition, returned by
    /// <see cref="SceneRoot.Instance"/> / <see cref="NodeHandle.Instance"/>. Chain calls to configure
    /// the instance's root transform and nest plain children.
    /// </summary>
    /// <remarks>
    /// Compile-time scaffolding only — SceneBuilder parses the source text to build the scene, so
    /// these methods return handles for chaining but perform no work at runtime.
    /// </remarks>
    /// <remarks>
    /// A prefab instance is handled as one whole unit for its hierarchy (M6): child GameObjects are
    /// not authored under an instance. Root-level property overrides and added/removed components are
    /// authored via <see cref="Override"/>, <see cref="AddComponent{T}()"/> and
    /// <see cref="RemoveComponent{T}"/> (M10).
    /// </remarks>
    public sealed class InstanceHandle
    {
        /// <summary>Set the local transform. Rotation is authored in Euler degrees.</summary>
        public InstanceHandle Transform(
            (float x, float y, float z)? pos = null,
            (float x, float y, float z)? rot = null,
            (float x, float y, float z)? scale = null) => this;

        /// <summary>Assign an explicit, stable logical id (otherwise one is derived).</summary>
        public InstanceHandle Id(string id) => this;

        /// <summary>Add a plain child GameObject alongside the instance's hierarchy.</summary>
        public NodeHandle Add(string name) => new NodeHandle();

        /// <summary>Add a plain child GameObject and configure it in a closure.</summary>
        public NodeHandle Add(string name, Action<NodeHandle> configure)
        {
            var handle = new NodeHandle();
            configure?.Invoke(handle);
            return handle;
        }

        /// <summary>
        /// Author property overrides on the instance root's own components, e.g.
        /// <c>.Override(e =&gt; e.Set((Health x) =&gt; x.health, 50))</c>. Targets are the instance
        /// root only — nested targets (including inside a nested prefab) are not authored here.
        /// </summary>
        public InstanceHandle Override(Action<OverrideHandle> configure)
        {
            configure?.Invoke(new OverrideHandle());
            return this;
        }

        /// <summary>Add a component of type <typeparamref name="T"/> to the instance root with no field overrides.</summary>
        public InstanceHandle AddComponent<T>() => this;

        /// <summary>Add a component of type <typeparamref name="T"/> to the instance root and set its serialized fields in a closure.</summary>
        public InstanceHandle AddComponent<T>(Action<ComponentHandle<T>> configure)
        {
            configure?.Invoke(new ComponentHandle<T>());
            return this;
        }

        /// <summary>Remove a component of type <typeparamref name="T"/> from the instance root (must exist on the source prefab).</summary>
        public InstanceHandle RemoveComponent<T>() => this;
    }
}
