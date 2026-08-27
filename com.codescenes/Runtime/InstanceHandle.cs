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
    /// A prefab instance is authored as one whole unit: you do not author child GameObjects inside its
    /// hierarchy. Change what the instance carries with <see cref="Override"/>,
    /// <see cref="AddComponent{T}()"/> (or its <see cref="Component{T}()"/> alias) and
    /// <see cref="RemoveComponent{T}"/>, and reach a nested target with
    /// <see cref="On(string, Action{ScopedHandle})"/>.
    /// </remarks>
    public class InstanceHandle : SceneObjectHandle
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

        /// <summary>Alias for <see cref="AddComponent{T}()"/>.</summary>
        public InstanceHandle Component<T>() => AddComponent<T>();

        /// <summary>Alias for <see cref="AddComponent{T}(Action{ComponentHandle{T}})"/>.</summary>
        public InstanceHandle Component<T>(Action<ComponentHandle<T>> configure) => AddComponent(configure);

        /// <summary>Remove a component of type <typeparamref name="T"/> from the instance root (must exist on the source prefab).</summary>
        public InstanceHandle RemoveComponent<T>() => this;

        /// <summary>
        /// Aspect-locked world size: exactly one of <paramref name="width"/>/<paramref name="height"/>/
        /// <paramref name="depth"/> drives the size, aspect preserved on the other two axes.
        /// </summary>
        public InstanceHandle FitSize(float? width = null, float? height = null, float? depth = null) => this;

        /// <summary>Explicit per-axis world size (non-uniform allowed).</summary>
        public InstanceHandle FitSize((float x, float y, float z) size) => this;

        /// <summary>
        /// Align to <paramref name="target"/>'s extent on any of <paramref name="x"/>/<paramref name="y"/>/
        /// <paramref name="z"/>: <see cref="AxisAlign.AbutMin"/> lands this object's max face against the
        /// target's min face (and <see cref="AxisAlign.AbutMax"/> the mirror), each optionally offset an
        /// extra world-unit distance via <c>.Offset(f)</c>. Each axis resolves in the target's own local
        /// space by default, a <paramref name="frame"/> override's local space, or world space
        /// (<paramref name="space"/>). <paramref name="target"/> is any scene object handle — a
        /// GameObject or a prefab instance root.
        /// </summary>
        public InstanceHandle AlignTo(SceneObjectHandle target, AxisAlign x = default, AxisAlign y = default,
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
        public InstanceHandle Between(SceneObjectHandle from, SceneObjectHandle to, float fraction,
            Between.Axis axis, SceneObjectHandle alongOrientationOf = null) => this;

        /// <summary>
        /// Author overrides scoped to a nested target inside the instance's hierarchy, addressed by
        /// child path (e.g. <c>"Turret/Barrel"</c>). Fallback for the typed selector overload on
        /// <see cref="InstanceHandle{TRef}"/> when no generated ref type is available.
        /// </summary>
        public InstanceHandle On(string childPath, Action<ScopedHandle> closure)
        {
            closure?.Invoke(new ScopedHandle());
            return this;
        }

        /// <summary>
        /// Add a new child GameObject nested under <paramref name="parentPath"/> in the instance's
        /// hierarchy (<c>""</c> = the instance root).
        /// </summary>
        public NodeHandle AddChild(string parentPath, string name) => new NodeHandle();

        /// <summary>
        /// Add a new child GameObject nested under <paramref name="parentPath"/> and configure it in
        /// a closure (the full <see cref="NodeHandle"/> authoring surface).
        /// </summary>
        public NodeHandle AddChild(string parentPath, string name, Action<NodeHandle> configure)
        {
            var handle = new NodeHandle();
            configure?.Invoke(handle);
            return handle;
        }

        /// <summary>Remove an existing child GameObject at <paramref name="childPath"/> from the instance's hierarchy.</summary>
        public InstanceHandle RemoveChild(string childPath) => this;
    }

    /// <summary>
    /// A typed handle to a prefab instance, returned by <see cref="SceneRoot.Instance{TRef}"/>. Carries
    /// the generated ref type <typeparamref name="TRef"/> through the chain so
    /// <see cref="On{TNode}(Func{TRef, TNode}, Action{ScopedHandle})"/>'s selector can be type-checked
    /// against the instance's actual nested hierarchy.
    /// </summary>
    /// <remarks>
    /// Compile-time scaffolding only — SceneBuilder parses the source text to build the scene, so
    /// these methods return handles for chaining but perform no work at runtime.
    /// </remarks>
    public sealed class InstanceHandle<TRef> : InstanceHandle where TRef : PrefabRef
    {
        /// <summary>Set the local transform. Rotation is authored in Euler degrees.</summary>
        public new InstanceHandle<TRef> Transform(
            (float x, float y, float z)? pos = null,
            (float x, float y, float z)? rot = null,
            (float x, float y, float z)? scale = null) => this;

        /// <summary>Assign an explicit, stable logical id (otherwise one is derived).</summary>
        public new InstanceHandle<TRef> Id(string id) => this;

        /// <summary>
        /// Author property overrides on the instance root's own components, e.g.
        /// <c>.Override(e =&gt; e.Set((Health x) =&gt; x.health, 50))</c>. Targets are the instance
        /// root only — nested targets (including inside a nested prefab) are not authored here.
        /// </summary>
        public new InstanceHandle<TRef> Override(Action<OverrideHandle> configure)
        {
            configure?.Invoke(new OverrideHandle());
            return this;
        }

        /// <summary>Add a component of type <typeparamref name="T"/> to the instance root with no field overrides.</summary>
        public new InstanceHandle<TRef> AddComponent<T>() => this;

        /// <summary>Add a component of type <typeparamref name="T"/> to the instance root and set its serialized fields in a closure.</summary>
        public new InstanceHandle<TRef> AddComponent<T>(Action<ComponentHandle<T>> configure)
        {
            configure?.Invoke(new ComponentHandle<T>());
            return this;
        }

        /// <summary>Alias for <see cref="AddComponent{T}()"/>.</summary>
        public new InstanceHandle<TRef> Component<T>() => AddComponent<T>();

        /// <summary>Alias for <see cref="AddComponent{T}(Action{ComponentHandle{T}})"/>.</summary>
        public new InstanceHandle<TRef> Component<T>(Action<ComponentHandle<T>> configure) => AddComponent(configure);

        /// <summary>Remove a component of type <typeparamref name="T"/> from the instance root (must exist on the source prefab).</summary>
        public new InstanceHandle<TRef> RemoveComponent<T>() => this;

        /// <summary>
        /// Aspect-locked world size: exactly one of <paramref name="width"/>/<paramref name="height"/>/
        /// <paramref name="depth"/> drives the size, aspect preserved on the other two axes.
        /// </summary>
        public new InstanceHandle<TRef> FitSize(float? width = null, float? height = null, float? depth = null) => this;

        /// <summary>Explicit per-axis world size (non-uniform allowed).</summary>
        public new InstanceHandle<TRef> FitSize((float x, float y, float z) size) => this;

        /// <summary>
        /// Align to <paramref name="target"/>'s extent on any of <paramref name="x"/>/<paramref name="y"/>/
        /// <paramref name="z"/>: <see cref="AxisAlign.AbutMin"/> lands this object's max face against the
        /// target's min face (and <see cref="AxisAlign.AbutMax"/> the mirror), each optionally offset an
        /// extra world-unit distance via <c>.Offset(f)</c>. Each axis resolves in the target's own local
        /// space by default, a <paramref name="frame"/> override's local space, or world space
        /// (<paramref name="space"/>). <paramref name="target"/> is any scene object handle — a
        /// GameObject or a prefab instance root.
        /// </summary>
        public new InstanceHandle<TRef> AlignTo(SceneObjectHandle target, AxisAlign x = default, AxisAlign y = default,
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
        public new InstanceHandle<TRef> Between(SceneObjectHandle from, SceneObjectHandle to, float fraction,
            Between.Axis axis, SceneObjectHandle alongOrientationOf = null) => this;

        /// <summary>
        /// Author overrides scoped to a nested target inside the instance's hierarchy, addressed by a
        /// compiler-checked member selector (e.g. <c>t =&gt; t.Turret.Barrel</c>). <typeparamref name="TNode"/>
        /// is inferred from the selector's leaf property type.
        /// </summary>
        public InstanceHandle<TRef> On<TNode>(Func<TRef, TNode> selector, Action<ScopedHandle> closure)
        {
            closure?.Invoke(new ScopedHandle());
            return this;
        }

        /// <summary>String fallback that keeps <typeparamref name="TRef"/> for chaining continuity.</summary>
        public new InstanceHandle<TRef> On(string childPath, Action<ScopedHandle> closure)
        {
            closure?.Invoke(new ScopedHandle());
            return this;
        }

        /// <summary>
        /// Remove an existing source child GameObject addressed by a compiler-checked member selector
        /// (e.g. <c>t =&gt; t.LeftTurret.Antenna</c>). A typo or a stale name is a COMPILE error, and a
        /// hierarchy rename auto-syncs the accessor — exactly like <see cref="On{TNode}"/>. Use the
        /// string overload for a child with no generated ref type.
        /// </summary>
        public InstanceHandle<TRef> RemoveChild<TNode>(Func<TRef, TNode> selector) => this;

        /// <summary>Remove an existing child GameObject at <paramref name="childPath"/> from the instance's hierarchy.</summary>
        public new InstanceHandle<TRef> RemoveChild(string childPath) => this;

        /// <summary>
        /// Add a new child GameObject nested under a compiler-checked parent selector (e.g.
        /// <c>t =&gt; t.LeftTurret</c>). The PARENT is façade-checked (typo/stale = compile error,
        /// rename auto-syncs); the new child <paramref name="name"/> stays a string — it is a
        /// genuinely new object with no façade type yet.
        /// </summary>
        public NodeHandle AddChild<TNode>(Func<TRef, TNode> parentSelector, string name) => new NodeHandle();

        /// <summary>
        /// Add a new child GameObject under a compiler-checked parent selector and configure it in a
        /// closure (the full <see cref="NodeHandle"/> authoring surface).
        /// </summary>
        public NodeHandle AddChild<TNode>(Func<TRef, TNode> parentSelector, string name, Action<NodeHandle> configure)
        {
            var handle = new NodeHandle();
            configure?.Invoke(handle);
            return handle;
        }
    }
}
