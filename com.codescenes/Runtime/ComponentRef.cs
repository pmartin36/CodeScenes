namespace SceneBuilder.Authoring
{
    /// <summary>A reference to an already-declared component, captured with
    /// <see cref="NodeHandle.Ref{T}(int)"/> and passed as a UnityEvent listener target.
    /// Reference-only: it exposes no configuration members, so a component can only be
    /// configured inside its own <c>Component&lt;T&gt;(c =&gt; ...)</c> closure.</summary>
    /// <remarks>Compile-time scaffolding only — SceneBuilder parses the source text.</remarks>
    public sealed class ComponentRef<T>
    {
        /// <summary>This component reference, typed as <typeparamref name="T"/> so it can be written as
        /// a UnityEvent listener's object argument (e.g. <c>hud.Bind(opener.As())</c>). Compile-time
        /// cast scaffolding only — it does not select a different component and performs no work at
        /// runtime.</summary>
        public T As() => default!;
    }
}
