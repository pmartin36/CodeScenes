using System;

namespace SceneBuilder.Authoring
{
    /// <summary>
    /// A handle for authoring property overrides on a prefab instance's root, via
    /// <see cref="InstanceHandle.Override(Action{OverrideHandle})"/>. Unlike
    /// <see cref="ComponentHandle{T}"/>, a single closure can target multiple different root
    /// component types — <c>TComponent</c> is inferred per-call from the selector's explicitly-typed
    /// lambda parameter, e.g. <c>e.Set((Health x) =&gt; x.health, 50)</c>.
    /// </summary>
    /// <remarks>
    /// Compile-time scaffolding only — SceneBuilder parses the source text to build the scene, so
    /// these methods return handles for chaining but perform no work at runtime.
    /// </remarks>
    public sealed class OverrideHandle
    {
        /// <summary>Set a field by typed member selector (e.g. <c>(Health x) =&gt; x.health</c>).</summary>
        public OverrideHandle Set<TComponent, TValue>(Func<TComponent, TValue> selector, TValue value) => this;

        /// <summary>
        /// Set an asset-reference field by typed member selector, e.g.
        /// <c>e.Set((MeshRenderer m) =&gt; m.sharedMaterial, Asset("Assets/Materials/Red.mat"))</c>. The
        /// selector's return type is the asset type; the value is the <see cref="AssetReference"/> factory result.
        /// </summary>
        public OverrideHandle Set<TComponent, TValue>(Func<TComponent, TValue> selector, AssetReference asset) => this;

        /// <summary>
        /// Set a cross-object-reference field by typed member selector, e.g.
        /// <c>e.Set((Joint j) =&gt; j.connectedBody, target)</c>. Pass <see cref="NodeHandle.None"/> to clear the slot.
        /// </summary>
        public OverrideHandle Set<TComponent, TValue>(Func<TComponent, TValue> selector, NodeHandle target) => this;

        /// <summary>
        /// Set a field by serialized property path (e.g. <c>e.Set&lt;BoxCollider&gt;("m_Center.x", 1.0)</c>).
        /// The target root component type is explicit since there is no selector closure to infer it from.
        /// </summary>
        public OverrideHandle Set<TComponent>(string serializedPath, object value) => this;
    }
}
