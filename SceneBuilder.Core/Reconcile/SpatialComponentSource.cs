using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Reconcile
{
    // b4-t1: dedicated .Sizer(...)/.Snapper(...) fluent-call renderer + Sizer-before-Snapper
    // canonical ordering.
    internal static class SpatialComponentSource
    {
        internal static bool IsSpatial(string typeFullName) =>
            typeFullName == SpatialComponents.SizerTypeName
            || typeFullName == SpatialComponents.SnapperTypeName;

        internal static string RenderStatement(
            string receiver,
            string typeFullName,
            FieldMap fields,
            IReadOnlyDictionary<string, string>? fieldExpressions) =>
            $"{receiver}.{MethodName(typeFullName)}({RenderArguments(typeFullName, fields, fieldExpressions)});";

        internal static string RenderArguments(
            string typeFullName,
            FieldMap fields,
            IReadOnlyDictionary<string, string>? fieldExpressions) =>
            string.Join(", ", fields.Select(kv =>
                $"{kv.Key}: {RenderFieldValue(kv.Key, kv.Value, fieldExpressions)}"));

        private static string MethodName(string typeFullName) =>
            typeFullName == SpatialComponents.SizerTypeName ? "Sizer" : "Snapper";

        // Reuses SourceExpr so float/vec formatting is byte-identical to the parser's accepted
        // form (bare `2f`, tuple `(2f, 1f, 0.5f)`) — NOT ValueNodeLiteral's
        // `new UnityEngine.Vector3(...)`.
        private static string RenderFieldValue(
            string key, ValueNode value, IReadOnlyDictionary<string, string>? fieldExpressions)
        {
            if (fieldExpressions != null && fieldExpressions.TryGetValue(key, out var pre))
            {
                return pre; // pre-rendered ObjectRef handle (target:)
            }

            return RenderFieldValue(value);
        }

        // b4-t2: the single per-value formatter shared by APPEND (above) and PATCH
        // (ComponentReconciler.RenderFieldValue's spatial dispatch) so the two can never diverge.
        internal static string RenderFieldValue(ValueNode value) =>
            value switch
            {
                ValueNode.Vec3(var v) => SourceExpr.Vec3Literal(v),
                ValueNode.Primitive(PrimitiveKind.Float, float f) => SourceExpr.Float(f),
                ValueNode.Primitive(PrimitiveKind.Bool, bool b) => b ? "true" : "false",
                _ => SourceExpr.ValueNodeLiteral(value), // total fallback (e.g. Unsupported)
            };

        // Stable canonical order: a Sizer always precedes a Snapper; all other components keep
        // their relative positions (only the spatial pair is pinned). At most one of each per
        // node in practice; general form pins every Sizer ahead of the first Snapper.
        internal static ComponentData[] OrderForEmit(ComponentData[] components) =>
            components.OrderBy(RankFor).ToArray();

        private static int RankFor(ComponentData component)
        {
            if (component.Type.FullName == SpatialComponents.SizerTypeName)
            {
                return -1;
            }

            if (component.Type.FullName == SpatialComponents.SnapperTypeName)
            {
                return 1;
            }

            return 0;
        }
    }
}
