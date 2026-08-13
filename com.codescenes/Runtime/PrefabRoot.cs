using System;

namespace SceneBuilder.Authoring
{
    /// <summary>
    /// Root of a prefab definition. Add the prefab's single root GameObject here.
    /// </summary>
    /// <remarks>
    /// This is compile-time scaffolding so builder files type-check and autocomplete. SceneBuilder
    /// reads the source text (via Roslyn) to build the prefab — these methods are never executed.
    /// Call <see cref="Add(string)"/> (or its closure overload) exactly once; a second call
    /// produces a located build error, since a prefab has exactly one root.
    /// </remarks>
    public sealed class PrefabRoot
    {
        /// <summary>Add the prefab's root GameObject. Call this once.</summary>
        public NodeHandle Add(string name) => new NodeHandle();

        /// <summary>Add the prefab's root GameObject and configure it (and its children) in a closure.</summary>
        public NodeHandle Add(string name, Action<NodeHandle> configure)
        {
            var handle = new NodeHandle();
            configure?.Invoke(handle);
            return handle;
        }
    }
}
