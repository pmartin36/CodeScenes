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
    public sealed class NodeHandle : SceneObjectHandle
    {
        /// <summary>Assign this to a reference field to clear it, leaving the slot empty in the scene.</summary>
        public static readonly NodeHandle None = new NodeHandle();

        /// <summary>Assign this to a reference field to point it at the GameObject the component is on
        /// — a Button's target graphic on its own Image, for example. It needs no variable, so it is
        /// legal inside the statement that declares the node.</summary>
        public static readonly NodeHandle Self = new NodeHandle();

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

        /// <summary>Set the UI layout (RectTransform). Presence of this call marks the node as a
        /// RectTransform node; omitted arguments stay at Unity's RectTransform defaults.</summary>
        public NodeHandle RectTransform(
            (float x, float y)? anchoredPos = null,
            (float x, float y)? sizeDelta = null,
            (float x, float y)? anchorMin = null,
            (float x, float y)? anchorMax = null,
            (float x, float y)? pivot = null) => this;

        /// <summary>
        /// Aspect-locked world size: exactly one of <paramref name="width"/>/<paramref name="height"/>/
        /// <paramref name="depth"/> drives the size, aspect preserved on the other two axes.
        /// </summary>
        public NodeHandle FitSize(float? width = null, float? height = null, float? depth = null) => this;

        /// <summary>Explicit per-axis world size (non-uniform allowed).</summary>
        public NodeHandle FitSize((float x, float y, float z) size) => this;

        /// <summary>
        /// Snap to a surface: at most one horizontal (<paramref name="left"/>/<paramref name="right"/>),
        /// one vertical (<paramref name="up"/>/<paramref name="down"/>), and one depth
        /// (<paramref name="forward"/>/<paramref name="back"/>) axis, with an optional explicit
        /// <paramref name="target"/> override (skips the raycast/fallback scan). The target is any
        /// scene object handle — a GameObject or a prefab instance root.
        /// </summary>
        public NodeHandle SurfaceSnap(bool up = false, bool down = false,
                                  bool left = false, bool right = false,
                                  bool forward = false, bool back = false,
                                  SceneObjectHandle target = null) => this;

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

        /// <summary>
        /// Instantiate a prefab instance nested under this GameObject, from <paramref name="assetPath"/>
        /// (a <c>.prefab</c> or an imported model such as a <c>.fbx</c>). The path is authoring-time
        /// convenience only — the GUID is authoritative and stored in the sidecar; two calls with the
        /// same path produce two distinct instances.
        /// </summary>
        public InstanceHandle Instance(string assetPath) => new InstanceHandle();

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
