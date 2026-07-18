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
    /// A prefab instance is handled as one whole unit in v1 (M6): this handle deliberately has no
    /// <c>Component&lt;T&gt;()</c> or field-setter methods — per-property overrides are out of scope
    /// until M10, and the absence of those members is a compile-time guarantee, not just a convention.
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
    }
}
