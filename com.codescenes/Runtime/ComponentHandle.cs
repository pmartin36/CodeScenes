using System;

namespace SceneBuilder.Authoring
{
    /// <summary>
    /// A handle to a component being authored on a GameObject (via <see cref="NodeHandle.Component{T}(Action{ComponentHandle{T}})"/>).
    /// Set serialized fields either by serialized property path (<c>c.Set("m_Mass", 5f)</c>) or by a
    /// typed member selector (<c>c.Set(r =&gt; r.mass, 5f)</c>).
    /// </summary>
    /// <remarks>
    /// Compile-time scaffolding only — SceneBuilder parses the source text to build the scene, so
    /// these methods return handles for chaining but perform no work at runtime.
    /// </remarks>
    public sealed partial class ComponentHandle<T>
    {
        /// <summary>Set a serialized field by its serialized property path (e.g. "m_Mass").</summary>
        public ComponentHandle<T> Set(string serializedPath, object value) => this;

        /// <summary>Set a field by typed member selector (e.g. <c>r =&gt; r.mass</c>).</summary>
        public ComponentHandle<T> Set<TValue>(Func<T, TValue> selector, TValue value) => this;

        /// <summary>
        /// Set an asset-reference field by typed member selector, e.g.
        /// <c>c.Set(r =&gt; r.sharedMaterial, Asset("Assets/Materials/Red.mat"))</c>. The selector's
        /// return type is the asset type; the value is the <see cref="AssetReference"/> factory result.
        /// </summary>
        public ComponentHandle<T> Set<TValue>(Func<T, TValue> selector, AssetReference asset) => this;

        /// <summary>
        /// Set a cross-object-reference field by typed member selector, e.g.
        /// <c>c.Set(r =&gt; r.target, door)</c>. The target is any scene object handle — a GameObject
        /// or a prefab instance root. Pass <see cref="NodeHandle.None"/> to clear the slot.
        /// </summary>
        public ComponentHandle<T> Set<TValue>(Func<T, TValue> selector, SceneObjectHandle target) => this;

        /// <summary>Wire a persistent listener onto this component's <c>OnClick</c> UnityEvent
        /// (<c>m_OnClick</c>): when it fires, call <paramref name="method"/> on <paramref name="target"/>.
        /// The method is a typed lambda on the target component, so a method it does not have fails to
        /// compile. The lambda's own call carries the static argument, if any (e.g.
        /// <c>x =&gt; x.SetLevel(3)</c>); a zero-argument call wires a void listener.</summary>
        public ComponentHandle<T> OnClick<TTarget>(ComponentRef<TTarget> target, Action<TTarget> method) => this;

        /// <summary>Wire a persistent listener onto this component's <c>OnClick</c> UnityEvent, with an
        /// explicit <paramref name="callState"/> governing whether it fires in edit mode, play mode, or
        /// both. Omitting this argument defaults to <see cref="UnityEngine.Events.UnityEventCallState.RuntimeOnly"/>.</summary>
        public ComponentHandle<T> OnClick<TTarget>(ComponentRef<TTarget> target, Action<TTarget> method,
                                                   UnityEngine.Events.UnityEventCallState callState) => this;

        /// <summary>Wire a persistent listener whose target is a project asset (resolved through the
        /// typed asset catalog, e.g. <c>Assets.Prefabs.Speakers.Speaker</c>) rather than a scene
        /// reference. <typeparamref name="TTarget"/> is not inferable from an asset reference, so the
        /// call site supplies it explicitly (e.g. <c>b.OnClick&lt;AudioSource&gt;(Assets.Prefabs.Speaker, a =&gt; a.Play())</c>).</summary>
        public ComponentHandle<T> OnClick<TTarget>(AssetReference target, Action<TTarget> method) => this;

        /// <summary>The asset-target form of <see cref="OnClick{TTarget}(AssetReference, Action{TTarget})"/>
        /// with an explicit <paramref name="callState"/>.</summary>
        public ComponentHandle<T> OnClick<TTarget>(AssetReference target, Action<TTarget> method,
                                                   UnityEngine.Events.UnityEventCallState callState) => this;

        /// <summary>Wire a persistent listener whose target is a GameObject (a scene node, not one of
        /// its components) — the shape a <c>Button.onClick =&gt; GameObject.SetActive(false)</c> wiring
        /// takes.</summary>
        public ComponentHandle<T> OnClick(SceneObjectHandle target, Action<UnityEngine.GameObject> method) => this;

        /// <summary>The GameObject-target form of <see cref="OnClick(SceneObjectHandle, Action{UnityEngine.GameObject})"/>
        /// with an explicit <paramref name="callState"/>.</summary>
        public ComponentHandle<T> OnClick(SceneObjectHandle target, Action<UnityEngine.GameObject> method,
                                          UnityEngine.Events.UnityEventCallState callState) => this;

        /// <summary>Wire a persistent listener onto an arbitrary <c>UnityEvent</c>-typed field selected
        /// by <paramref name="unityEvent"/> (e.g. <c>b.OnEvent(x =&gt; x.onValueChanged, target, t =&gt; t.Method())</c>),
        /// rather than the fixed <c>OnClick</c> shortcut. <typeparamref name="TEvent"/> is unconstrained,
        /// so any <c>UnityEvent</c>-typed field on <typeparamref name="T"/> is authorable this way.</summary>
        public ComponentHandle<T> OnEvent<TEvent, TTarget>(Func<T, TEvent> unityEvent, ComponentRef<TTarget> target,
                                                           Action<TTarget> method) => this;

        /// <summary>The component-target form of
        /// <see cref="OnEvent{TEvent, TTarget}(Func{T, TEvent}, ComponentRef{TTarget}, Action{TTarget})"/>
        /// with an explicit <paramref name="callState"/>.</summary>
        public ComponentHandle<T> OnEvent<TEvent, TTarget>(Func<T, TEvent> unityEvent, ComponentRef<TTarget> target,
                                                           Action<TTarget> method,
                                                           UnityEngine.Events.UnityEventCallState callState) => this;

        /// <summary>The asset-target form of <see cref="OnEvent{TEvent, TTarget}(Func{T, TEvent}, ComponentRef{TTarget}, Action{TTarget})"/>:
        /// wires the selected event field to a method on an asset reference rather than a scene
        /// component.</summary>
        public ComponentHandle<T> OnEvent<TEvent, TTarget>(Func<T, TEvent> unityEvent, AssetReference target,
                                                           Action<TTarget> method) => this;

        /// <summary>The GameObject-target form of <see cref="OnEvent{TEvent, TTarget}(Func{T, TEvent}, ComponentRef{TTarget}, Action{TTarget})"/>:
        /// wires the selected event field to a method on the target GameObject itself (e.g.
        /// <c>SetActive</c>).</summary>
        public ComponentHandle<T> OnEvent<TEvent>(Func<T, TEvent> unityEvent, SceneObjectHandle target,
                                                  Action<UnityEngine.GameObject> method) => this;

        /// <summary>Wire a "dynamic" persistent listener: the argument the event fires with is passed
        /// straight through to <paramref name="method"/> at runtime, rather than a static value baked in
        /// at authoring time. <paramref name="method"/> is a method GROUP on the target (e.g.
        /// <c>h =&gt; h.SetValue</c>, not <c>h =&gt; h.SetValue(...)</c>) whose parameter type is
        /// <typeparamref name="TArg"/> — the selected event's own generic argument.</summary>
        public ComponentHandle<T> OnEvent<TArg, TTarget>(Func<T, UnityEngine.Events.UnityEvent<TArg>> unityEvent,
                                                         ComponentRef<TTarget> target,
                                                         Func<TTarget, Action<TArg>> method, bool dynamic) => this;

        /// <summary>The two-argument-event form of
        /// <see cref="OnEvent{TArg, TTarget}(Func{T, UnityEngine.Events.UnityEvent{TArg}}, ComponentRef{TTarget}, Func{TTarget, Action{TArg}}, bool)"/>.</summary>
        public ComponentHandle<T> OnEvent<TArg0, TArg1, TTarget>(
            Func<T, UnityEngine.Events.UnityEvent<TArg0, TArg1>> unityEvent, ComponentRef<TTarget> target,
            Func<TTarget, Action<TArg0, TArg1>> method, bool dynamic) => this;
    }
}
