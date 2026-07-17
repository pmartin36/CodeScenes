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
    public sealed class ComponentHandle<T>
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
        /// <c>c.Set(r =&gt; r.target, door)</c>. Pass <see cref="NodeHandle.None"/> to clear the slot.
        /// </summary>
        public ComponentHandle<T> Set<TValue>(Func<T, TValue> selector, NodeHandle target) => this;
    }
}
