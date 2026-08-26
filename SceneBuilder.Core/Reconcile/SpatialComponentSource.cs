using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Reconcile
{
    // Dedicated .FitSize(...)/.AlignTo(...) fluent-call renderer + FitSize-before-AlignTo
    // canonical ordering.
    internal static class SpatialComponentSource
    {
        internal static bool IsSpatial(string typeFullName) =>
            typeFullName == SpatialComponents.FitSizeTypeName
            || typeFullName == SpatialComponents.AlignToTypeName
            || typeFullName == SpatialComponents.BetweenTypeName;

        // Between.Axis's authoring-form prefix, derived once from BetweenEnums.AxisTypeName's
        // runtime nested-type spelling ("SceneBuilder.Authoring.Between+Axis" -> "Between.Axis")
        // rather than a separately hand-typed literal.
        private static readonly string BetweenAxisAuthoringPrefix = SpatialComponents.BetweenEnums.AxisTypeName
            .Substring(SpatialComponents.BetweenEnums.AxisTypeName.LastIndexOf('.') + 1)
            .Replace('+', '.');

        // AxisAlign's authoring-form identifier, derived once from AlignToEnums.AxisAlignTypeName
        // ("SceneBuilder.Authoring.AxisAlign" -> "AxisAlign") rather than a separately hand-typed
        // literal. Not nested ('+'-free unlike BetweenAxisAuthoringPrefix): AxisAlign is a standalone
        // authoring value type.
        private static readonly string AxisAlignAuthoringPrefix = SpatialComponents.AlignToEnums.AxisAlignTypeName
            .Substring(SpatialComponents.AlignToEnums.AxisAlignTypeName.LastIndexOf('.') + 1);

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

            if (typeFullName == SpatialComponents.BetweenTypeName)
            {
                return RenderBetweenArguments(fields, fieldExpressions);
            }

            if (typeFullName == SpatialComponents.AlignToTypeName)
            {
                return RenderAlignToArguments(fields, fieldExpressions);
            }

            return string.Join(", ", fields.Select(kv =>
                $"{RenderArgumentKeyValue(typeFullName, kv.Key, kv.Value, fieldExpressions)}"));
        }

        // Fixed argument order (from, to, fraction, axis[, alongOrientationOf]) — never the
        // FieldMap's insertion order. alongOrientationOf renders only when the Orientation field
        // is present (world case omits it entirely; no "unset" sentinel).
        private static string RenderBetweenArguments(FieldMap fields, IReadOnlyDictionary<string, string>? fieldExpressions)
        {
            var parts = new List<string>
            {
                $"from: {RenderFieldValue(SpatialComponents.BetweenFields.From, fields[SpatialComponents.BetweenFields.From], fieldExpressions)}",
                $"to: {RenderFieldValue(SpatialComponents.BetweenFields.To, fields[SpatialComponents.BetweenFields.To], fieldExpressions)}",
                $"fraction: {RenderFieldValue(SpatialComponents.BetweenFields.Fraction, fields[SpatialComponents.BetweenFields.Fraction], fieldExpressions)}",
                $"axis: {RenderBetweenAxis(fields[SpatialComponents.BetweenFields.Axis])}",
            };

            if (fields.ContainsKey(SpatialComponents.BetweenFields.Orientation))
            {
                parts.Add($"alongOrientationOf: {RenderFieldValue(SpatialComponents.BetweenFields.Orientation, fields[SpatialComponents.BetweenFields.Orientation], fieldExpressions)}");
            }

            return string.Join(", ", parts);
        }

        // Between.Axis renders in AUTHORING form ("Between.Axis.X"), never the runtime nested-type
        // FullName ("SceneBuilder.Authoring.Between+Axis.X" — the '+' is not valid C#).
        private static string RenderBetweenAxis(ValueNode value)
        {
            if (value is ValueNode.Enum(_, var members, _) && members.Count == 1)
            {
                return $"{BetweenAxisAuthoringPrefix}.{members[0]}";
            }

            throw new System.NotSupportedException($"Between field 'axis' has an unrenderable value: {value}");
        }

        // Fixed argument order (target, x, y, z[, frame][, space]) — never the FieldMap's insertion
        // order. Only a pinned (Mode != None) axis renders; an offset renders as a chained
        // `.Offset(f)` call on the axis keyword. frame:/space: render only when present (World is the
        // only Space value ever stored — TargetLocal is pruned at parse).
        private static string RenderAlignToArguments(FieldMap fields, IReadOnlyDictionary<string, string>? fieldExpressions)
        {
            var parts = new List<string>
            {
                $"target: {RenderFieldValue(SpatialComponents.AlignToFields.Target, fields[SpatialComponents.AlignToFields.Target], fieldExpressions)}",
            };

            foreach (var axis in new[] { SpatialAxis.X, SpatialAxis.Y, SpatialAxis.Z })
            {
                SpatialComponents.TryAlignAxisKeyword(axis, out var keyword);
                SpatialComponents.TryAlignAxis(keyword, out _, out var modeField, out var offsetField);

                if (!fields.TryGetValue(modeField, out var modeValue)
                    || modeValue is not ValueNode.Enum(_, var members, _)
                    || members.Count != 1)
                {
                    continue;
                }

                var axisExpr = $"{AxisAlignAuthoringPrefix}.{members[0]}";
                if (fields.TryGetValue(offsetField, out var offsetValue)
                    && offsetValue is ValueNode.Primitive(PrimitiveKind.Float, float offset)
                    && offset != 0f)
                {
                    axisExpr = $"{axisExpr}.Offset({SourceExpr.Float(offset)})";
                }

                parts.Add($"{keyword}: {axisExpr}");
            }

            if (fields.ContainsKey(SpatialComponents.AlignToFields.Frame))
            {
                parts.Add($"frame: {RenderFieldValue(SpatialComponents.AlignToFields.Frame, fields[SpatialComponents.AlignToFields.Frame], fieldExpressions)}");
            }

            if (fields.TryGetValue(SpatialComponents.AlignToFields.Space, out var spaceValue)
                && spaceValue is ValueNode.Enum(_, var spaceMembers, _) && spaceMembers.Count == 1)
            {
                parts.Add($"space: {SpatialComponents.AlignToEnums.AlignSpaceTypeName.Substring(SpatialComponents.AlignToEnums.AlignSpaceTypeName.LastIndexOf('.') + 1)}.{spaceMembers[0]}");
            }

            return string.Join(", ", parts);
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

        // An AlignTo per-axis mode field (xMode/yMode/zMode holding a ValueNode.Enum) renders as its
        // authoring axis keyword ("y: AxisAlign.AbutMax"), the single reverse mapping shared with the
        // parser via SpatialComponents.TryAlignAxisFromModeField. Every other field (target:, frame:,
        // FitSize's width/height/depth/size, or a non-literal value kept under its original keyword as
        // Unsupported) renders via the generic "key: value" form, unchanged.
        private static string RenderArgumentKeyValue(
            string typeFullName, string key, ValueNode value, IReadOnlyDictionary<string, string>? fieldExpressions) =>
            RenderKeyValue(key, value, RenderFieldValue(key, value, fieldExpressions));

        // Shared by APPEND (RenderArguments, above) and by ComponentPatchApplier's spatial
        // introduce-field arm (a scene-side field newly present, absent from source, patched into
        // an EXISTING `.AlignTo(...)` call) — same "mode field -> axis keyword" translation, so an
        // introduced axis (e.g. yMode=AbutMax set live) renders "y: AxisAlign.AbutMax", never
        // "yMode: <enum literal>".
        internal static string RenderKeyValue(string key, ValueNode value, string valueExpr)
        {
            if (value is ValueNode.Enum(_, var members, _)
                && members.Count == 1
                && SpatialComponents.TryAlignAxisFromModeField(key, out var keyword))
            {
                return $"{keyword}: {AxisAlignAuthoringPrefix}.{members[0]}";
            }

            return $"{key}: {valueExpr}";
        }

        private static string MethodName(string typeFullName)
        {
            if (typeFullName == SpatialComponents.FitSizeTypeName) return "FitSize";
            if (typeFullName == SpatialComponents.BetweenTypeName) return "Between";
            return "AlignTo";
        }

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

        // Stable canonical order: a FitSize always precedes an AlignTo; all other components keep
        // their relative positions (only the spatial pair is pinned). At most one of each per
        // node in practice; general form pins every FitSize ahead of the first AlignTo.
        internal static ComponentData[] OrderForEmit(ComponentData[] components) =>
            components.OrderBy(RankFor).ToArray();

        private static int RankFor(ComponentData component)
        {
            if (component.Type.FullName == SpatialComponents.FitSizeTypeName)
            {
                return -1;
            }

            if (component.Type.FullName == SpatialComponents.AlignToTypeName)
            {
                return 1;
            }

            if (component.Type.FullName == SpatialComponents.BetweenTypeName)
            {
                return 2;
            }

            return 0;
        }
    }
}
