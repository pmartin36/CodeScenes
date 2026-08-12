using System;
using System.Collections.Generic;
using System.Linq;

namespace SceneBuilder.Core.Model
{
    /// <summary>
    /// The ONE model &lt;-&gt; serialized mapping for a <see cref="ValueNode.ManagedReference"/>: the
    /// <see cref="TypeRef"/> &lt;-&gt; Unity's <c>managedReferenceFullTypename</c> spelling
    /// (<c>"&lt;assembly&gt; &lt;namespace.type&gt;"</c>), and the node &lt;-&gt; raw
    /// (fullTypename, per-field values) shape the adapter reads/writes. Pure, static, no Unity deps
    /// — mirrors <see cref="UnityEventProjection"/>'s role for UnityEvent persistent calls.
    /// </summary>
    public static class ManagedReferenceProjection
    {
        public sealed record Projection(string? FullTypename, IReadOnlyList<KeyValuePair<string, ValueNode>> Fields);

        // Total: null ConcreteType -> null fullTypename; otherwise Unity's
        // "<assembly> <namespace.type>" shape (bare FullName when AssemblyHint is null/empty).
        public static string? ToFullTypename(TypeRef? concreteType) =>
            concreteType is null
                ? null
                : string.IsNullOrEmpty(concreteType.AssemblyHint)
                    ? concreteType.FullName
                    : concreteType.AssemblyHint + " " + concreteType.FullName;

        // Total, reversible for resolvable types: splits on the FIRST space into (assembly, type).
        // null/empty -> null.
        public static TypeRef? FromFullTypename(string? fullTypename)
        {
            if (string.IsNullOrEmpty(fullTypename))
            {
                return null;
            }

            var spaceIndex = fullTypename.IndexOf(' ');
            if (spaceIndex < 0)
            {
                return new TypeRef(fullTypename);
            }

            var assemblyPart = fullTypename[..spaceIndex];
            var typePart = fullTypename[(spaceIndex + 1)..];
            return new TypeRef(typePart, assemblyPart == "" ? null : assemblyPart);
        }

        public static Projection Project(ValueNode.ManagedReference node) =>
            new(ToFullTypename(node.ConcreteType), node.Fields.ToList());

        public static ValueNode.ManagedReference FromProjection(
            string? fullTypename, IEnumerable<KeyValuePair<string, ValueNode>> fields) =>
            new(FromFullTypename(fullTypename), new FieldMap(fields));
    }
}
