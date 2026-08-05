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
        /// The C# type every substituted scene-object handle expression has: the base of NodeHandle
        /// and InstanceHandle, so it names a list of MIXED handle kinds as well as a uniform one. An
        /// implicitly-typed array infers its element type from the elements, never their common base,
        /// so `new[] { door, tank }` (a plain node beside a prefab-instance root) does not compile even
        /// though both are SceneObjectHandle — this names that shared base explicitly instead.
        /// </summary>
        internal const string HandleElementTypeName = "SceneBuilder.Authoring.SceneObjectHandle";

        private const string ObjectArrayPrefix = "new object[] { ";

        /// <summary>
        /// `"new SceneBuilder.Authoring.SceneObjectHandle[] { "` when every item of
        /// <paramref name="list"/> is a substituted scene-object-handle expression (same or differing
        /// concrete kind); `"new[] { "` when every item shares some other single nameable C# type;
        /// else `"new object[] { "` (an item's type cannot be named at all, or named types genuinely
        /// differ).
        /// </summary>
        internal static string ArrayPrefix(ValueNode.List list)
        {
            string? commonType = null;
            var seenFirst = false;

            foreach (var item in list.Items)
            {
                var token = EmittedTypeToken(item);
                if (token == null)
                {
                    return ObjectArrayPrefix;
                }

                if (!seenFirst)
                {
                    commonType = token;
                    seenFirst = true;
                    continue;
                }

                if (!string.Equals(commonType, token, StringComparison.Ordinal))
                {
                    return ObjectArrayPrefix;
                }
            }

            if (!seenFirst)
            {
                return ObjectArrayPrefix;
            }

            return commonType == HandleElementTypeName
                ? "new " + HandleElementTypeName + "[] { "
                : "new[] { ";
        }

        /// <summary>
        /// True when <paramref name="value"/> reaches, INSIDE A LIST at any depth, a node with no
        /// compiling emission form.
        /// </summary>
        internal static bool HasUnemittableItem(ValueNode value) =>
            ValueWalk.Any(value, n => n is ValueNode.List list
                && list.Items.Any(item => ValueWalk.Any(item, x => x is ValueNode.Unsupported)));

        // The ordinal token naming an item's rendered C# type; null = a raw token this formatter
        // cannot name at all, which forces the explicit `object[]` form so the compiler is never asked
        // to infer an element type from a value whose type is unknown here. A substituted scene-object
        // handle expression (ComponentReconciler.RenderFieldValue turns every ObjectRef reached inside
        // a list into a ValueNode.Unsupported handle token before this runs — every OTHER Unsupported
        // is vetoed by HasUnemittableItem before an array literal is ever built) always has C# type
        // SceneObjectHandle or a subtype of it, so it is named positively rather than left null.
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
            ValueNode.Unsupported => HandleElementTypeName,
            _ => null,
        };
    }
}
