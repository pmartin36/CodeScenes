using System;

namespace SceneBuilder.Authoring
{
    /// <summary>
    /// Root of a scene definition. Add top-level GameObjects here.
    /// </summary>
    /// <remarks>
    /// This is compile-time scaffolding so builder files type-check and autocomplete. SceneBuilder
    /// reads the source text (via Roslyn) to build the scene — these methods are never executed.
    /// </remarks>
    public sealed class SceneRoot
    {
        /// <summary>Add a root GameObject.</summary>
        public NodeHandle Add(string name) => new NodeHandle();

        /// <summary>Add a root GameObject and configure it (and its children) in a closure.</summary>
        public NodeHandle Add(string name, Action<NodeHandle> configure)
        {
            var handle = new NodeHandle();
            configure?.Invoke(handle);
            return handle;
        }

        /// <summary>
        /// Instantiate a root prefab instance from <paramref name="assetPath"/> (a <c>.prefab</c> or an
        /// imported model such as a <c>.fbx</c>). The path is authoring-time convenience only — the
        /// GUID is authoritative and stored in the sidecar; two calls with the same path produce two
        /// distinct instances.
        /// </summary>
        public InstanceHandle Instance(string assetPath) => new InstanceHandle();
    }
}
