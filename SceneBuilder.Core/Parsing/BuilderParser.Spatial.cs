using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;

namespace SceneBuilder.Core.Parsing
{
    // Spatial-authoring-component parse arms (`.FitSize(...)`, `.SurfaceSnap(...)`), split out of
    // BuilderParser.cs for file-size discipline. Dispatch lives in BuilderParser's
    // ApplyChainedCalls switch; the resulting ComponentBuilder is an ordinary component, so
    // all downstream machinery (LogicalId synthesis, IdentityMap/anchors, BuildComponent)
    // applies unchanged.
    public static partial class BuilderParser
    {
        // `.FitSize(height: 2f)` (aspect-locked) | `.FitSize(size: (2,1,0.5f))` (explicit).
        // Total on VALUES (non-literal -> Unsupported); Fail (located) on STRUCTURE.
        private static void ApplyFitSize(NodeBuilder node, ArgumentListSyntax args, InvocationExpressionSyntax invocation, ParserContext ctx)
        {
            var fields = new List<KeyValuePair<string, ValueNode>>();
            var spans = new List<KeyValuePair<string, SourceSpan>>();
            bool hasAspect = false, hasExplicit = false;
            int aspectCount = 0;

            foreach (var arg in args.Arguments)
            {
                if (arg.NameColon == null)
                {
                    throw Unreachable();
                }

                var name = arg.NameColon.Name.Identifier.Text;
                var span = new SourceSpan(arg.Expression.SpanStart, arg.Expression.Span.Length);
                if (SpatialComponents.TryFitAspectMode(name, out var member))
                {
                    hasAspect = true;
                    aspectCount++;
                    fields.Add(new KeyValuePair<string, ValueNode>(
                        SpatialComponents.FitSizeFields.Mode,
                        ValueNode.Enum.Canonical(SpatialComponents.FitSizeEnums.ModeTypeName, new[] { member })));
                    fields.Add(new KeyValuePair<string, ValueNode>(SpatialComponents.FitSizeFields.Value, ParseSpatialScalar(arg.Expression, ctx)));
                    spans.Add(new KeyValuePair<string, SourceSpan>(SpatialComponents.FitSizeFields.Value, span));
                }
                else if (name == SpatialComponents.FitSizeFields.Size)
                {
                    hasExplicit = true;
                    fields.Add(new KeyValuePair<string, ValueNode>(
                        SpatialComponents.FitSizeFields.Mode,
                        ValueNode.Enum.Canonical(SpatialComponents.FitSizeEnums.ModeTypeName, new[] { SpatialComponents.FitSizeEnums.Explicit })));
                    fields.Add(new KeyValuePair<string, ValueNode>(SpatialComponents.FitSizeFields.Size, ParseSpatialVec3(arg.Expression, ctx)));
                    spans.Add(new KeyValuePair<string, SourceSpan>(SpatialComponents.FitSizeFields.Size, span));
                }
                else
                {
                    throw Unreachable();
                }
            }

            if (hasAspect && hasExplicit)
            {
                throw Unreachable();
            }

            if (!hasAspect && !hasExplicit)
            {
                throw Unreachable();
            }

            if (aspectCount > 1)
            {
                throw Unreachable();
            }

            var memberAccess = (MemberAccessExpressionSyntax)invocation.Expression;
            var anchorStart = memberAccess.OperatorToken.SpanStart;
            var cb = new ComponentBuilder
            {
                TypeFullName = SpatialComponents.FitSizeTypeName,
                AnchorSpan = new SourceSpan(anchorStart, invocation.Span.End - anchorStart),
            };
            foreach (var f in fields)
            {
                cb.Fields.Add(f);
            }

            foreach (var s in spans)
            {
                cb.FieldValueSpans.Add(s);
            }

            node.Components.Add(cb);
            node.DrivenChannels |= SpatialComponents.FitSizeMask;
        }

        // Scalar field: reuse ValueNodeParser, then coerce any numeric primitive to Float
        // (spec: width/height/depth are FLOAT ValueNodes). Non-numeric -> Unsupported (total).
        private static ValueNode ParseSpatialScalar(ExpressionSyntax expr, ParserContext ctx)
            => TryCoerceFloat(ValueNodeParser.Parse(expr, ctx.AssetCatalog, ctx.FacadeConflicts, ctx.ConstStrings), out var f)
                ? ValueNode.Primitive.Float(f)
                : new ValueNode.Unsupported(expr.ToString());

        // Explicit size: the authored form is a bare 3-tuple (x,y,z). ValueNodeParser does not
        // parse bare tuples, so build the Vec3 here; fall back to ValueNodeParser for any other
        // form (e.g. new Vector3(...)), which stays total.
        private static ValueNode ParseSpatialVec3(ExpressionSyntax expr, ParserContext ctx)
        {
            if (expr is TupleExpressionSyntax tuple && tuple.Arguments.Count == 3
                && TryCoerceFloat(ValueNodeParser.Parse(tuple.Arguments[0].Expression, ctx.AssetCatalog, ctx.FacadeConflicts, ctx.ConstStrings), out var x)
                && TryCoerceFloat(ValueNodeParser.Parse(tuple.Arguments[1].Expression, ctx.AssetCatalog, ctx.FacadeConflicts, ctx.ConstStrings), out var y)
                && TryCoerceFloat(ValueNodeParser.Parse(tuple.Arguments[2].Expression, ctx.AssetCatalog, ctx.FacadeConflicts, ctx.ConstStrings), out var z))
            {
                return new ValueNode.Vec3(new Vec3(x, y, z));
            }

            return ValueNodeParser.Parse(expr, ctx.AssetCatalog, ctx.FacadeConflicts, ctx.ConstStrings); // Vector3(...) -> Vec3; else Unsupported
        }

        // `.SurfaceSnap(down: true, left: true, target: floor)` — bool axis flags + optional target ObjectRef.
        // Structural errors (unnamed/unknown arg, contradictory pair, no axis) -> Fail (located).
        // Non-literal flag value -> Unsupported (total); target -> ValueNodeParser (total, ObjectRef).
        private static void ApplySurfaceSnap(NodeBuilder node, ArgumentListSyntax args, InvocationExpressionSyntax invocation, ParserContext ctx)
        {
            var fields = new List<KeyValuePair<string, ValueNode>>();
            var spans = new List<KeyValuePair<string, SourceSpan>>();
            bool up = false, down = false, left = false, right = false, forward = false, back = false;

            foreach (var arg in args.Arguments)
            {
                if (arg.NameColon == null)
                {
                    throw Unreachable();
                }

                var name = arg.NameColon.Name.Identifier.Text;
                var span = new SourceSpan(arg.Expression.SpanStart, arg.Expression.Span.Length);
                // The enum-axis field renders as an AUTHORING keyword whose text encodes the member
                // (down->up is a keyword swap, not just a value swap), so a member-flip patch must own
                // the WHOLE `down: true` argument, not the `true` value alone. target: keeps the
                // value-only span (its keyword never changes; only the ObjectRef handle is patched).
                var argSpan = new SourceSpan(arg.SpanStart, arg.Span.Length);

                if (name == SpatialComponents.SurfaceSnapFields.Target)
                {
                    fields.Add(new KeyValuePair<string, ValueNode>(SpatialComponents.SurfaceSnapFields.Target, ValueNodeParser.Parse(arg.Expression, ctx.AssetCatalog, ctx.FacadeConflicts, ctx.ConstStrings)));
                    spans.Add(new KeyValuePair<string, SourceSpan>(SpatialComponents.SurfaceSnapFields.Target, span));
                    continue;
                }

                if (!SpatialComponents.TryAxisKeyword(name, out var fieldKey, out var enumTypeName, out var member))
                {
                    throw Unreachable();
                }

                var set = ApplyAxisFlag(arg.Expression, name, fieldKey, enumTypeName, member, span, argSpan, fields, spans);
                switch (name)
                {
                    case "up": up = set; break;
                    case "down": down = set; break;
                    case "left": left = set; break;
                    case "right": right = set; break;
                    case "forward": forward = set; break;
                    case "back": back = set; break;
                }
            }

            if (up && down)
            {
                throw Unreachable();
            }

            if (left && right)
            {
                throw Unreachable();
            }

            if (forward && back)
            {
                throw Unreachable();
            }

            if (!(up || down || left || right || forward || back))
            {
                throw Unreachable();
            }

            var verticalSet = up || down;
            var horizontalSet = left || right;
            var depthSet = forward || back;

            var memberAccess = (MemberAccessExpressionSyntax)invocation.Expression;
            var anchorStart = memberAccess.OperatorToken.SpanStart;
            var cb = new ComponentBuilder
            {
                TypeFullName = SpatialComponents.SurfaceSnapTypeName,
                AnchorSpan = new SourceSpan(anchorStart, invocation.Span.End - anchorStart),
            };
            foreach (var f in fields)
            {
                cb.Fields.Add(f);
            }

            foreach (var s in spans)
            {
                cb.FieldValueSpans.Add(s);
            }

            node.Components.Add(cb);
            node.DrivenChannels |= SpatialComponents.SurfaceSnapMask(verticalSet, horizontalSet, depthSet);
        }

        // An axis keyword: a literal `true` builds the per-axis ValueNode.Enum field and marks the axis
        // SET (drives + contradiction) — returns true. A literal `false` is NOT stored and does not set
        // the axis — returns false. A non-literal value stays TOTAL: stored as Unsupported under the
        // ORIGINAL bool keyword (intent not silently dropped, never materialized/driven) — returns false.
        private static bool ApplyAxisFlag(
            ExpressionSyntax expr, string keyword, string fieldKey, string enumTypeName, string member, SourceSpan span,
            SourceSpan argSpan, List<KeyValuePair<string, ValueNode>> fields, List<KeyValuePair<string, SourceSpan>> spans)
        {
            if (expr.IsKind(SyntaxKind.TrueLiteralExpression))
            {
                fields.Add(new KeyValuePair<string, ValueNode>(fieldKey, ValueNode.Enum.Canonical(enumTypeName, new[] { member })));
                // Whole-argument span (`down: true`) — a member flip rewrites the keyword too.
                spans.Add(new KeyValuePair<string, SourceSpan>(fieldKey, argSpan));
                return true;
            }

            if (expr.IsKind(SyntaxKind.FalseLiteralExpression))
            {
                // not set, not stored (only set axes round-trip — spec §Emit "only the set flags emitted")
                return false;
            }

            fields.Add(new KeyValuePair<string, ValueNode>(keyword, new ValueNode.Unsupported(expr.ToString())));
            spans.Add(new KeyValuePair<string, SourceSpan>(keyword, span));
            return false;
        }

        // `.Between(from: a, to: b, fraction: 0.25f, axis: Between.Axis.X[, alongOrientationOf: r])`.
        // Structural errors (unnamed arg) are already ruled out by the recognizer -> Unreachable.
        // from/to/alongOrientationOf are ObjectRefs (total via ValueNodeParser); axis is parsed
        // directly from the rightmost member identifier (never the generic MemberAccess->Enum arm,
        // which would yield the runtime '+' TypeFullName) into the canonical authoring TypeFullName.
        private static void ApplyBetween(NodeBuilder node, ArgumentListSyntax args, InvocationExpressionSyntax invocation, ParserContext ctx)
        {
            var fields = new List<KeyValuePair<string, ValueNode>>();
            var spans = new List<KeyValuePair<string, SourceSpan>>();
            var oriented = false;
            var axisResolved = false;
            var resolvedAxis = SpatialAxis.X;

            foreach (var arg in args.Arguments)
            {
                if (arg.NameColon == null)
                {
                    throw Unreachable();
                }

                var name = arg.NameColon.Name.Identifier.Text;
                var span = new SourceSpan(arg.Expression.SpanStart, arg.Expression.Span.Length);

                switch (name)
                {
                    case SpatialComponents.BetweenFields.From:
                        fields.Add(new KeyValuePair<string, ValueNode>(SpatialComponents.BetweenFields.From, ValueNodeParser.Parse(arg.Expression, ctx.AssetCatalog, ctx.FacadeConflicts, ctx.ConstStrings)));
                        spans.Add(new KeyValuePair<string, SourceSpan>(SpatialComponents.BetweenFields.From, span));
                        break;
                    case SpatialComponents.BetweenFields.To:
                        fields.Add(new KeyValuePair<string, ValueNode>(SpatialComponents.BetweenFields.To, ValueNodeParser.Parse(arg.Expression, ctx.AssetCatalog, ctx.FacadeConflicts, ctx.ConstStrings)));
                        spans.Add(new KeyValuePair<string, SourceSpan>(SpatialComponents.BetweenFields.To, span));
                        break;
                    case SpatialComponents.BetweenFields.Fraction:
                        fields.Add(new KeyValuePair<string, ValueNode>(SpatialComponents.BetweenFields.Fraction, ParseSpatialScalar(arg.Expression, ctx)));
                        spans.Add(new KeyValuePair<string, SourceSpan>(SpatialComponents.BetweenFields.Fraction, span));
                        break;
                    case SpatialComponents.BetweenFields.Axis:
                        ValueNode axisValue;
                        if (arg.Expression is MemberAccessExpressionSyntax ma && SpatialComponents.TryAxisFromMember(ma.Name.Identifier.Text, out resolvedAxis))
                        {
                            axisResolved = true;
                            axisValue = ValueNode.Enum.Canonical(SpatialComponents.BetweenEnums.AxisTypeName, new[] { ma.Name.Identifier.Text });
                        }
                        else
                        {
                            axisValue = new ValueNode.Unsupported(arg.Expression.ToString());
                        }

                        fields.Add(new KeyValuePair<string, ValueNode>(SpatialComponents.BetweenFields.Axis, axisValue));
                        spans.Add(new KeyValuePair<string, SourceSpan>(SpatialComponents.BetweenFields.Axis, span));
                        break;
                    case "alongOrientationOf":
                        oriented = true;
                        fields.Add(new KeyValuePair<string, ValueNode>(SpatialComponents.BetweenFields.Orientation, ValueNodeParser.Parse(arg.Expression, ctx.AssetCatalog, ctx.FacadeConflicts, ctx.ConstStrings)));
                        spans.Add(new KeyValuePair<string, SourceSpan>(SpatialComponents.BetweenFields.Orientation, span));
                        break;
                    default:
                        throw Unreachable();
                }
            }

            var memberAccess = (MemberAccessExpressionSyntax)invocation.Expression;
            var anchorStart = memberAccess.OperatorToken.SpanStart;
            var cb = new ComponentBuilder
            {
                TypeFullName = SpatialComponents.BetweenTypeName,
                AnchorSpan = new SourceSpan(anchorStart, invocation.Span.End - anchorStart),
            };
            foreach (var f in fields)
            {
                cb.Fields.Add(f);
            }

            foreach (var s in spans)
            {
                cb.FieldValueSpans.Add(s);
            }

            node.Components.Add(cb);

            if (axisResolved)
            {
                node.DrivenChannels |= SpatialComponents.BetweenDrivenMask(resolvedAxis, oriented);
            }
        }

        private static bool TryCoerceFloat(ValueNode v, out float f)
        {
            switch (v)
            {
                case ValueNode.Primitive(PrimitiveKind.Float, float x):
                    f = x;
                    return true;
                case ValueNode.Primitive(PrimitiveKind.Int, int x):
                    f = x;
                    return true;
                case ValueNode.Primitive(PrimitiveKind.Long, long x):
                    f = x;
                    return true;
                case ValueNode.Primitive(PrimitiveKind.Double, double x):
                    f = (float)x;
                    return true;
                default:
                    f = 0f;
                    return false;
            }
        }
    }
}
