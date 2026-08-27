using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;

namespace SceneBuilder.Core.Parsing
{
    // Spatial-authoring-component parse arms (`.FitSize(...)`, `.AlignTo(...)`), split out of
    // BuilderParser.cs for file-size discipline. Dispatch lives in BuilderParser's
    // ApplyChainedCalls switch; the resulting ComponentBuilder is an ordinary component, so
    // all downstream machinery (LogicalId synthesis, IdentityMap/anchors, BuildComponent)
    // applies unchanged.
    public static partial class BuilderParser
    {
        // AxisAlign's authoring-form identifier ("SceneBuilder.Authoring.AxisAlign" -> "AxisAlign"),
        // derived once from the runtime FullName rather than a separately hand-typed literal.
        private static readonly string AxisAlignAuthoringName = SpatialComponents.AlignToEnums.AxisAlignTypeName
            .Substring(SpatialComponents.AlignToEnums.AxisAlignTypeName.LastIndexOf('.') + 1);

        // `.FitSize(height: 2f)` (aspect-locked) | `.FitSize(size: (2,1,0.5f))` (explicit).
        // Total on VALUES (non-literal -> Unsupported); Fail (located) on STRUCTURE.
        private static void ApplyFitSize(NodeBuilder node, ArgumentListSyntax args, InvocationExpressionSyntax invocation, ParserContext ctx, List<ComponentBuilder> target)
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

            target.Add(cb);
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

        // `.AlignTo(target, x: AxisAlign.AbutMin, y: AxisAlign.AbutMax.Offset(0.5f), frame: rig, space: AlignSpace.World)`.
        // Structural errors (a later unnamed arg, an unknown named arg) -> Fail (located; the recognizer
        // agrees). Value-level facts (non-literal .Offset(...), an unknown AxisAlign/AlignSpace member) are
        // TOTAL -> Unsupported (never a throw). `target` is the sole positional-or-named arg; every other
        // argument must be named. There is no "requires an axis"/empty-arg problem: target is always present.
        private static void ApplyAlignTo(NodeBuilder node, ArgumentListSyntax args, InvocationExpressionSyntax invocation, ParserContext ctx, List<ComponentBuilder> target)
        {
            var fields = new List<KeyValuePair<string, ValueNode>>();
            var spans = new List<KeyValuePair<string, SourceSpan>>();
            bool xPinned = false, yPinned = false, zPinned = false;
            var sawPositional = false;

            for (var i = 0; i < args.Arguments.Count; i++)
            {
                var arg = args.Arguments[i];

                if (arg.NameColon == null)
                {
                    if (i != 0 || sawPositional)
                    {
                        throw Unreachable();
                    }

                    sawPositional = true;
                    var targetSpan = new SourceSpan(arg.Expression.SpanStart, arg.Expression.Span.Length);
                    fields.Add(new KeyValuePair<string, ValueNode>(SpatialComponents.AlignToFields.Target, ValueNodeParser.Parse(arg.Expression, ctx.AssetCatalog, ctx.FacadeConflicts, ctx.ConstStrings)));
                    spans.Add(new KeyValuePair<string, SourceSpan>(SpatialComponents.AlignToFields.Target, targetSpan));
                    continue;
                }

                var name = arg.NameColon.Name.Identifier.Text;
                var span = new SourceSpan(arg.Expression.SpanStart, arg.Expression.Span.Length);

                switch (name)
                {
                    case SpatialComponents.AlignToFields.Target:
                        fields.Add(new KeyValuePair<string, ValueNode>(SpatialComponents.AlignToFields.Target, ValueNodeParser.Parse(arg.Expression, ctx.AssetCatalog, ctx.FacadeConflicts, ctx.ConstStrings)));
                        spans.Add(new KeyValuePair<string, SourceSpan>(SpatialComponents.AlignToFields.Target, span));
                        break;

                    case SpatialComponents.AlignToFields.Frame:
                        fields.Add(new KeyValuePair<string, ValueNode>(SpatialComponents.AlignToFields.Frame, ValueNodeParser.Parse(arg.Expression, ctx.AssetCatalog, ctx.FacadeConflicts, ctx.ConstStrings)));
                        spans.Add(new KeyValuePair<string, SourceSpan>(SpatialComponents.AlignToFields.Frame, span));
                        break;

                    case SpatialComponents.AlignToFields.Space:
                        ValueNode spaceValue = arg.Expression is MemberAccessExpressionSyntax spaceMember
                            && System.Array.IndexOf(SpatialComponents.AlignToEnums.SpaceMembers, spaceMember.Name.Identifier.Text) >= 0
                                ? ValueNode.Enum.Canonical(SpatialComponents.AlignToEnums.AlignSpaceTypeName, new[] { spaceMember.Name.Identifier.Text })
                                : new ValueNode.Unsupported(arg.Expression.ToString());
                        // TargetLocal is the default; do not store it (default-value pruning parity
                        // with every other AlignTo axis).
                        if (spaceValue is not ValueNode.Enum(_, var spaceMembers, _) || spaceMembers[0] != SpatialComponents.AlignToEnums.TargetLocal)
                        {
                            fields.Add(new KeyValuePair<string, ValueNode>(SpatialComponents.AlignToFields.Space, spaceValue));
                            spans.Add(new KeyValuePair<string, SourceSpan>(SpatialComponents.AlignToFields.Space, span));
                        }
                        break;

                    case "x":
                    case "y":
                    case "z":
                        if (!SpatialComponents.TryAlignAxis(name, out _, out var modeField, out var offsetField))
                        {
                            throw Unreachable();
                        }

                        var argSpan = new SourceSpan(arg.SpanStart, arg.Span.Length);
                        var pinned = ApplyAxisAlign(arg.Expression, modeField, offsetField, argSpan, fields, spans);
                        switch (name)
                        {
                            case "x": xPinned = pinned; break;
                            case "y": yPinned = pinned; break;
                            case "z": zPinned = pinned; break;
                        }
                        break;

                    default:
                        throw Unreachable();
                }
            }

            var memberAccess = (MemberAccessExpressionSyntax)invocation.Expression;
            var anchorStart = memberAccess.OperatorToken.SpanStart;
            var cb = new ComponentBuilder
            {
                TypeFullName = SpatialComponents.AlignToTypeName,
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

            target.Add(cb);
            node.DrivenChannels |= SpatialComponents.AlignToDrivenMask(xPinned, yPinned, zPinned);
        }

        // An `x:`/`y:`/`z:` axis argument: `AxisAlign.<Preset>` or `AxisAlign.<Preset>.Offset(<literal>)`.
        // A recognized preset stores the mode field (whole-argument span — a member flip rewrites the
        // keyword too, mirroring the old ApplyAxisFlag) and marks the axis pinned; only a non-zero literal
        // offset also stores the paired offset field. An unrecognized member, or a non-literal `.Offset`
        // argument, stays TOTAL: stored as Unsupported under the axis's mode field key (never a throw).
        private static bool ApplyAxisAlign(
            ExpressionSyntax expr, string modeField, string offsetField, SourceSpan argSpan,
            List<KeyValuePair<string, ValueNode>> fields, List<KeyValuePair<string, SourceSpan>> spans)
        {
            var offset = 0f;
            var baseExpr = expr;

            if (expr is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Offset" } offsetAccess } offsetInvocation)
            {
                if (offsetInvocation.ArgumentList.Arguments.Count != 1
                    || !TryEvalFloatLiteral(offsetInvocation.ArgumentList.Arguments[0].Expression, out offset))
                {
                    fields.Add(new KeyValuePair<string, ValueNode>(modeField, new ValueNode.Unsupported(expr.ToString())));
                    spans.Add(new KeyValuePair<string, SourceSpan>(modeField, argSpan));
                    return false;
                }

                baseExpr = offsetAccess.Expression;
            }

            string? member = baseExpr switch
            {
                MemberAccessExpressionSyntax ma when ma.Expression is IdentifierNameSyntax id
                    && id.Identifier.Text == AxisAlignAuthoringName
                    => ma.Name.Identifier.Text,
                IdentifierNameSyntax bare => bare.Identifier.Text, // `using static AxisAlign;`
                _ => null,
            };

            if (member == SpatialComponents.AlignToEnums.None)
            {
                // The inert default, authored explicitly — not set, not stored (mirrors the old
                // ApplyAxisFlag's literal-`false` handling: only a pinned axis round-trips).
                return false;
            }

            if (member == null || !SpatialComponents.IsAlignPreset(member))
            {
                fields.Add(new KeyValuePair<string, ValueNode>(modeField, new ValueNode.Unsupported(expr.ToString())));
                spans.Add(new KeyValuePair<string, SourceSpan>(modeField, argSpan));
                return false;
            }

            fields.Add(new KeyValuePair<string, ValueNode>(modeField, ValueNode.Enum.Canonical(SpatialComponents.AlignToEnums.ModeTypeName, new[] { member })));
            spans.Add(new KeyValuePair<string, SourceSpan>(modeField, argSpan));

            if (offset != 0f)
            {
                fields.Add(new KeyValuePair<string, ValueNode>(offsetField, ValueNode.Primitive.Float(offset)));
                spans.Add(new KeyValuePair<string, SourceSpan>(offsetField, argSpan));
            }

            return true;
        }

        private static bool TryEvalFloatLiteral(ExpressionSyntax expr, out float value)
        {
            if (expr is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.NumericLiteralExpression))
            {
                var token = literal.Token.Value;
                switch (token)
                {
                    case float f: value = f; return true;
                    case double d: value = (float)d; return true;
                    case int i: value = i; return true;
                    case long l: value = l; return true;
                }
            }

            value = 0f;
            return false;
        }

        // `.Between(from: a, to: b, fraction: 0.25f, axis: Between.Axis.X[, alongOrientationOf: r])`.
        // Structural errors (unnamed arg) are already ruled out by the recognizer -> Unreachable.
        // from/to/alongOrientationOf are ObjectRefs (total via ValueNodeParser); axis is parsed
        // directly from the rightmost member identifier (never the generic MemberAccess->Enum arm,
        // which would yield the runtime '+' TypeFullName) into the canonical authoring TypeFullName.
        private static void ApplyBetween(NodeBuilder node, ArgumentListSyntax args, InvocationExpressionSyntax invocation, ParserContext ctx, List<ComponentBuilder> target)
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

            target.Add(cb);

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
