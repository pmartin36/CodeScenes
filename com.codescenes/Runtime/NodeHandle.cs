using System;

namespace SceneBuilder.Authoring
{
    /// <summary>
    /// A handle to a GameObject in a scene definition. Chain calls to configure it and nest children.
    /// </summary>
    /// <remarks>
    /// Compile-time scaffolding only — SceneBuilder parses the source text to build the scene, so
    /// these methods return handles for chaining but perform no work at runtime.
    /// </remarks>
    public sealed class NodeHandle
    {
        /// <summary>The cleared/no-target sentinel for a <see cref="NodeHandle"/> reference field.</summary>
        public static readonly NodeHandle None = new NodeHandle();

        /// <summary>Add a child GameObject.</summary>
        public NodeHandle Add(string name) => new NodeHandle();

        /// <summary>Add a child GameObject and configure it in a closure.</summary>
        public NodeHandle Add(string name, Action<NodeHandle> configure)
        {
            var handle = new NodeHandle();
            configure?.Invoke(handle);
            return handle;
        }

        /// <summary>Set the local transform. Rotation is authored in Euler degrees.</summary>
        public NodeHandle Transform(
            (float x, float y, float z)? pos = null,
            (float x, float y, float z)? rot = null,
            (float x, float y, float z)? scale = null) => this;

        /// <summary>
        /// Aspect-locked world size: exactly one of <paramref name="width"/>/<paramref name="height"/>/
        /// <paramref name="depth"/> drives the size, aspect preserved on the other two axes.
        /// </summary>
        public NodeHandle Sizer(float? width = null, float? height = null, float? depth = null) => this;

        /// <summary>Explicit per-axis world size (non-uniform allowed).</summary>
        public NodeHandle Sizer((float x, float y, float z) size) => this;

        /// <summary>
        /// Snap to a surface: at most one horizontal (<paramref name="left"/>/<paramref name="right"/>),
        /// one vertical (<paramref name="up"/>/<paramref name="down"/>), and one depth
        /// (<paramref name="forward"/>/<paramref name="back"/>) axis, with an optional explicit
        /// <paramref name="target"/> override (skips the raycast/fallback scan).
        /// </summary>
        public NodeHandle Snapper(bool up = false, bool down = false,
                                  bool left = false, bool right = false,
                                  bool forward = false, bool back = false,
                                  NodeHandle target = null) => this;

        /// <summary>Set the GameObject tag.</summary>
        public NodeHandle Tag(string tag) => this;

        /// <summary>Set the GameObject layer.</summary>
        public NodeHandle Layer(int layer) => this;

        /// <summary>Set the active state.</summary>
        public NodeHandle Active(bool active) => this;

        /// <summary>Mark the GameObject static.</summary>
        public NodeHandle Static(bool value = true) => this;

        /// <summary>Assign an explicit, stable logical id (otherwise one is derived).</summary>
        public NodeHandle Id(string id) => this;

        /// <summary>Attach a component of type <typeparamref name="T"/> with no field overrides.</summary>
        public NodeHandle Component<T>() => this;

        /// <summary>
        /// Attach a component of type <typeparamref name="T"/> and set its serialized fields in a
        /// closure — <c>c.Set("m_Mass", 5f)</c> (serialized path) or <c>c.Set(r =&gt; r.mass, 5f)</c>
        /// (typed member selector).
        /// </summary>
        public NodeHandle Component<T>(Action<ComponentHandle<T>> configure) => this;
    }
}
