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
        /// Align to <paramref name="target"/>'s extent on any of <paramref name="x"/>/<paramref name="y"/>/
        /// <paramref name="z"/>: <see cref="AxisAlign.AbutMin"/> lands this object's max face against the
        /// target's min face (and <see cref="AxisAlign.AbutMax"/> the mirror), each optionally offset an
        /// extra world-unit distance via <c>.Offset(f)</c>. Each axis resolves in the target's own local
        /// space by default, a <paramref name="frame"/> override's local space, or world space
        /// (<paramref name="space"/>). <paramref name="target"/> is any scene object handle — a
        /// GameObject or a prefab instance root.
        /// </summary>
        public NodeHandle AlignTo(SceneObjectHandle target, AxisAlign x = default, AxisAlign y = default,
            AxisAlign z = default, SceneObjectHandle frame = null, AlignSpace space = AlignSpace.TargetLocal) => this;

        /// <summary>
        /// Single-axis corridor placement: lands this object's bounds flush against
        /// <paramref name="from"/> at <paramref name="fraction"/> 0 and flush against
        /// <paramref name="to"/> at <paramref name="fraction"/> 1, moving only along
        /// <paramref name="axis"/>. <paramref name="fraction"/> is unclamped, so a value outside
        /// [0, 1] places past the respective anchor. <paramref name="axis"/> is a world direction by
        /// default, or the matching local axis of <paramref name="alongOrientationOf"/> when given, so
        /// placement can follow a tilted reference. Which anchor owns the fraction-0 end is fixed by
        /// argument order (<paramref name="from"/>), not by which anchor sits at the higher world
        /// coordinate. Hierarchy-independent — anchors and self may live under unrelated parents.
        /// Chaining more than two <c>Between</c> calls into a longer anchor cycle is undefined: each
        /// evaluates independently and the last one to run wins.
        /// </summary>
        public NodeHandle Between(SceneObjectHandle from, SceneObjectHandle to, float fraction,
            Between.Axis axis, SceneObjectHandle alongOrientationOf = null) => this;

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

        /// <summary>
        /// Instantiate a prefab instance nested under this GameObject from a generated typed ref
        /// (e.g. <c>Prefabs.Gun</c>). Returns a typed handle so nested targets can be addressed via a
        /// compiler-checked selector (<c>.On(t =&gt; t.Turret.Barrel, ...)</c>) and overrides authored at
        /// existing-vocabulary depth (the instance root and its direct children).
        /// </summary>
        public InstanceHandle<TRef> Instance<TRef>(TRef prefab) where TRef : PrefabRef => new InstanceHandle<TRef>();

        /// <summary>Attach a component of type <typeparamref name="T"/> with no field overrides.</summary>
        public NodeHandle Component<T>() => this;

        /// <summary>
        /// Attach a component of type <typeparamref name="T"/> and set its serialized fields in a
        /// closure — <c>c.Set("m_Mass", 5f)</c> (serialized path) or <c>c.Set(r =&gt; r.mass, 5f)</c>
        /// (typed member selector).
        /// </summary>
        public NodeHandle Component<T>(Action<ComponentHandle<T>> configure) => this;

        /// <summary>Capture a reference to a component already added to this GameObject, to pass as a
        /// UnityEvent listener target. <paramref name="ordinal"/> selects among several components of the
        /// same type on this GameObject (0 = the first).</summary>
        public ComponentRef<T> Ref<T>(int ordinal = 0) => new ComponentRef<T>();
    }
}
