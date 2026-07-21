using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Reconcile
{
    // b4-t1: dedicated .FitSize(...)/.SurfaceSnap(...) fluent-call renderer + FitSize-before-SurfaceSnap
    // canonical ordering.
    internal static class SpatialComponentSource
    {
        internal static bool IsSpatial(string typeFullName) =>
            typeFullName == SpatialComponents.FitSizeTypeName
            || typeFullName == SpatialComponents.SurfaceSnapTypeName;

        internal static string RenderStatement(
            string receiver,
            string typeFullName,
            FieldMap fields,
            IReadOnlyDictionary<string, string>? fieldExpressions) =>
            $"{receiver}.{MethodName(typeFullName)}({RenderArguments(typeFullName, fields, fieldExpressions)});";

        internal static string RenderArguments(
            string typeFullName,
            FieldMap fields,
            IReadOnlyDictionary<string, string>? fieldExpressions)
        {
            if (typeFullName == SpatialComponents.FitSizeTypeName)
            {
                return RenderFitSizeArguments(fields, fieldExpressions);
            }

            return string.Join(", ", fields.Select(kv =>
                $"{RenderArgumentKeyValue(typeFullName, kv.Key, kv.Value, fieldExpressions)}"));
        }

        // b3-t1: FitSize's `mode` field discriminates which of `value` (aspect: width/height/depth)
        // or `size` (Explicit) is the authored dimension — the generic per-field renderer above can't
        // express that coupling, so FitSize gets its own arm. Never emits a bare `mode:`/`value:`
        // literal; always the authoring keyword (width/height/depth/size).
        private static string RenderFitSizeArguments(FieldMap fields, IReadOnlyDictionary<string, string>? fieldExpressions)
        {
            var mode = fields[SpatialComponents.FitSizeFields.Mode];
            if (mode is ValueNode.Enum(_, var members, _) && members.Count == 1)
            {
                var member = members[0];
                if (SpatialComponents.TryFitAspectKeyword(member, out var keyword))
                {
                    var valueField = fields[SpatialComponents.FitSizeFields.Value];
                    return $"{keyword}: {RenderFieldValue(SpatialComponents.FitSizeFields.Value, valueField, fieldExpressions)}";
                }

                if (member == SpatialComponents.FitSizeEnums.Explicit)
                {
                    var sizeField = fields[SpatialComponents.FitSizeFields.Size];
                    return $"size: {RenderFieldValue(SpatialComponents.FitSizeFields.Size, sizeField, fieldExpressions)}";
                }
            }

            throw new System.NotSupportedException($"FitSize field 'mode' has an unrenderable value: {mode}");
        }

        // A SurfaceSnap per-axis enum field (vertical/horizontal/depth holding a ValueNode.Enum)
        // renders as its authoring bool keyword ("down: true"), the single reverse mapping shared
        // with the parser via SpatialComponents.TryAxisFromEnumField. Every other field (target:,
        // FitSize's width/height/depth/size, or a non-literal flag kept under its original bool
        // keyword as Unsupported) renders via the generic "key: value" form, unchanged.
        private static string RenderArgumentKeyValue(
            string typeFullName, string key, ValueNode value, IReadOnlyDictionary<string, string>? fieldExpressions) =>
            RenderKeyValue(key, value, RenderFieldValue(key, value, fieldExpressions));

        // Shared by APPEND (RenderArguments, above) and by ComponentPatchApplier's spatial
        // introduce-field arm (a scene-side field newly present, absent from source, patched into
        // an EXISTING `.SurfaceSnap(...)` call) — same "enum field -> bool keyword" translation, so
        // an introduced axis (e.g. horizontal=Left set live) renders "left: true", never
        // "horizontal: <enum literal>".
        internal static string RenderKeyValue(string key, ValueNode value, string valueExpr)
        {
            if (value is ValueNode.Enum(_, var members, _)
                && members.Count == 1
                && SpatialComponents.TryAxisFromEnumField(key, members[0], out var keyword))
            {
                return $"{keyword}: true";
            }

            return $"{key}: {valueExpr}";
        }

        private static string MethodName(string typeFullName) =>
            typeFullName == SpatialComponents.FitSizeTypeName ? "FitSize" : "SurfaceSnap";

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

        // Stable canonical order: a FitSize always precedes a SurfaceSnap; all other components keep
        // their relative positions (only the spatial pair is pinned). At most one of each per
        // node in practice; general form pins every FitSize ahead of the first SurfaceSnap.
        internal static ComponentData[] OrderForEmit(ComponentData[] components) =>
            components.OrderBy(RankFor).ToArray();

        private static int RankFor(ComponentData component)
        {
            if (component.Type.FullName == SpatialComponents.FitSizeTypeName)
            {
                return -1;
            }

            if (component.Type.FullName == SpatialComponents.SurfaceSnapTypeName)
            {
                return 1;
            }

            return 0;
        }
    }
}
