using System;
using System.Linq;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Reconcile
{
    /// <summary>
    /// Owns array-literal element typing (Rule A) and "cannot be spelled at all" detection (Rule B)
    /// for a <see cref="ValueNode.List"/> reaching <see cref="SourceExpr.ValueNodeLiteral"/>. Sibling
    /// to <see cref="NestedValueEmission"/>, which owns the equivalent decisions for a struct member.
    /// </summary>
    internal static class ListValueEmission
    {
        /// <summary>The empty list's literal — always explicit, since an empty array names no element type.</summary>
        internal const string EmptyArrayLiteral = "new object[] { }";

        /// <summary>
        /// `"new[] { "` when every item of <paramref name="list"/> renders as the same C# type,
        /// else `"new object[] { "`.
        /// </summary>
        internal static string ArrayPrefix(ValueNode.List list)
        {
            string? commonType = null;
            var seenFirst = false;

            foreach (var item in list.Items)
            {
                var token = EmittedTypeToken(item);
                if (!seenFirst)
                {
                    commonType = token;
                    seenFirst = true;
                    continue;
                }

                if (!string.Equals(commonType, token, StringComparison.Ordinal))
                {
                    return "new object[] { ";
                }
            }

            return "new[] { ";
        }

        /// <summary>
        /// True when <paramref name="value"/> reaches, INSIDE A LIST at any depth, a node with no
        /// compiling emission form.
        /// </summary>
        internal static bool HasUnemittableItem(ValueNode value) =>
            ValueWalk.Any(value, n => n is ValueNode.List list
                && list.Items.Any(item => ValueWalk.Any(item, x => x is ValueNode.Unsupported)));

        // The ordinal token naming an item's rendered C# type; null = a raw token this formatter
        // cannot name (an ObjectRef, already substituted into an Unsupported handle expression by
        // ComponentReconciler.RenderFieldValue by the time this runs — every OTHER Unsupported is
        // vetoed by HasUnemittableItem before an array literal is ever built, so every null token
        // reaching this list is one of those handle expressions).
        private static string? EmittedTypeToken(ValueNode item) => item switch
        {
            ValueNode.Primitive primitive => "p:" + primitive.Kind,
            ValueNode.Enum en => "e:" + en.TypeFullName,
            ValueNode.Vec2 => "UnityEngine.Vector2",
            ValueNode.Vec3 => "UnityEngine.Vector3",
            ValueNode.Vec4 => "UnityEngine.Vector4",
            ValueNode.Quat => "UnityEngine.Quaternion",
            ValueNode.Color => "UnityEngine.Color",
            ValueNode.Nested nested => "n:" + nested.TypeName,
            ValueNode.AssetRef => "SceneBuilder.Authoring.AssetReference",
            ValueNode.List => "l:",
            _ => null,
        };
    }
}
