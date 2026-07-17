using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;

namespace SceneBuilder.Core.Parsing
{
    // Spatial-authoring-component parse arms (`.Sizer(...)`, `.Snapper(...)`), split out of
    // BuilderParser.cs for file-size discipline. Dispatch lives in BuilderParser's
    // ApplyChainedCalls switch; the resulting ComponentBuilder is an ordinary component, so
    // all downstream machinery (LogicalId synthesis, IdentityMap/anchors, BuildComponent)
    // applies unchanged.
    public static partial class BuilderParser
    {
        // `.Sizer(height: 2f)` (aspect-locked) | `.Sizer(size: (2,1,0.5f))` (explicit).
        // Total on VALUES (non-literal -> Unsupported); Fail (located) on STRUCTURE.
        private static void ApplySizer(NodeBuilder node, ArgumentListSyntax args, InvocationExpressionSyntax invocation)
        {
            var fields = new List<KeyValuePair<string, ValueNode>>();
            var spans = new List<KeyValuePair<string, SourceSpan>>();
            bool hasAspect = false, hasExplicit = false;
            int aspectCount = 0;

            foreach (var arg in args.Arguments)
            {
                if (arg.NameColon == null)
                {
                    throw Fail(arg, "Sizer arguments must be named (width:/height:/depth:/size:)");
                }

                var name = arg.NameColon.Name.Identifier.Text;
                var span = new SourceSpan(arg.Expression.SpanStart, arg.Expression.Span.Length);
                switch (name)
                {
                    case SpatialComponents.SizerFields.Width:
                    case SpatialComponents.SizerFields.Height:
                    case SpatialComponents.SizerFields.Depth:
                        hasAspect = true;
                        aspectCount++;
                        fields.Add(new KeyValuePair<string, ValueNode>(name, ParseSpatialScalar(arg.Expression)));
                        spans.Add(new KeyValuePair<string, SourceSpan>(name, span));
                        break;
                    case SpatialComponents.SizerFields.Size:
                        hasExplicit = true;
                        fields.Add(new KeyValuePair<string, ValueNode>(SpatialComponents.SizerFields.Size, ParseSpatialVec3(arg.Expression)));
                        spans.Add(new KeyValuePair<string, SourceSpan>(SpatialComponents.SizerFields.Size, span));
                        break;
                    default:
                        throw Fail(arg, $"Unknown Sizer argument '{name}'");
                }
            }

            if (hasAspect && hasExplicit)
            {
                throw Fail(invocation, "Sizer cannot combine aspect (width/height/depth) with explicit size");
            }

            if (!hasAspect && !hasExplicit)
            {
                throw Fail(invocation, "Sizer requires one of width/height/depth, or size");
            }

            if (aspectCount > 1)
            {
                throw Fail(invocation, "Sizer aspect-locked form takes exactly one of width/height/depth");
            }

            var memberAccess = (MemberAccessExpressionSyntax)invocation.Expression;
            var anchorStart = memberAccess.OperatorToken.SpanStart;
            var cb = new ComponentBuilder
            {
                TypeFullName = SpatialComponents.SizerTypeName,
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
            node.DrivenChannels |= ChannelMask.Scale;
        }

        // Scalar field: reuse ValueNodeParser, then coerce any numeric primitive to Float
        // (spec: width/height/depth are FLOAT ValueNodes). Non-numeric -> Unsupported (total).
        private static ValueNode ParseSpatialScalar(ExpressionSyntax expr)
            => TryCoerceFloat(ValueNodeParser.Parse(expr), out var f)
                ? ValueNode.Primitive.Float(f)
                : new ValueNode.Unsupported(expr.ToString());

        // Explicit size: the authored form is a bare 3-tuple (x,y,z). ValueNodeParser does not
        // parse bare tuples, so build the Vec3 here; fall back to ValueNodeParser for any other
        // form (e.g. new Vector3(...)), which stays total.
        private static ValueNode ParseSpatialVec3(ExpressionSyntax expr)
        {
            if (expr is TupleExpressionSyntax tuple && tuple.Arguments.Count == 3
                && TryCoerceFloat(ValueNodeParser.Parse(tuple.Arguments[0].Expression), out var x)
                && TryCoerceFloat(ValueNodeParser.Parse(tuple.Arguments[1].Expression), out var y)
                && TryCoerceFloat(ValueNodeParser.Parse(tuple.Arguments[2].Expression), out var z))
            {
                return new ValueNode.Vec3(new Vec3(x, y, z));
            }

            return ValueNodeParser.Parse(expr); // Vector3(...) -> Vec3; else Unsupported
        }

        // `.Snapper(down: true, left: true, target: floor)` — bool axis flags + optional target ObjectRef.
        // Structural errors (unnamed/unknown arg, contradictory pair, no axis) -> Fail (located).
        // Non-literal flag value -> Unsupported (total); target -> ValueNodeParser (total, ObjectRef).
        private static void ApplySnapper(NodeBuilder node, ArgumentListSyntax args, InvocationExpressionSyntax invocation)
        {
            var fields = new List<KeyValuePair<string, ValueNode>>();
            var spans = new List<KeyValuePair<string, SourceSpan>>();
            bool up = false, down = false, left = false, right = false, forward = false, back = false;

            foreach (var arg in args.Arguments)
            {
                if (arg.NameColon == null)
                {
                    throw Fail(arg, "Snapper arguments must be named (up:/down:/left:/right:/forward:/back:/target:)");
                }

                var name = arg.NameColon.Name.Identifier.Text;
                var span = new SourceSpan(arg.Expression.SpanStart, arg.Expression.Span.Length);
                switch (name)
                {
                    case SpatialComponents.SnapperFields.Up:
                        ApplyFlag(arg.Expression, name, span, ref up, fields, spans);
                        break;
                    case SpatialComponents.SnapperFields.Down:
                        ApplyFlag(arg.Expression, name, span, ref down, fields, spans);
                        break;
                    case SpatialComponents.SnapperFields.Left:
                        ApplyFlag(arg.Expression, name, span, ref left, fields, spans);
                        break;
                    case SpatialComponents.SnapperFields.Right:
                        ApplyFlag(arg.Expression, name, span, ref right, fields, spans);
                        break;
                    case SpatialComponents.SnapperFields.Forward:
                        ApplyFlag(arg.Expression, name, span, ref forward, fields, spans);
                        break;
                    case SpatialComponents.SnapperFields.Back:
                        ApplyFlag(arg.Expression, name, span, ref back, fields, spans);
                        break;
                    case SpatialComponents.SnapperFields.Target:
                        fields.Add(new KeyValuePair<string, ValueNode>(SpatialComponents.SnapperFields.Target, ValueNodeParser.Parse(arg.Expression)));
                        spans.Add(new KeyValuePair<string, SourceSpan>(SpatialComponents.SnapperFields.Target, span));
                        break;
                    default:
                        throw Fail(arg, $"Unknown Snapper argument '{name}'");
                }
            }

            if (up && down)
            {
                throw Fail(invocation, "Snapper cannot combine up and down");
            }

            if (left && right)
            {
                throw Fail(invocation, "Snapper cannot combine left and right");
            }

            if (forward && back)
            {
                throw Fail(invocation, "Snapper cannot combine forward and back");
            }

            if (!(up || down || left || right || forward || back))
            {
                throw Fail(invocation, "Snapper requires at least one snap axis (up/down/left/right/forward/back)");
            }

            var memberAccess = (MemberAccessExpressionSyntax)invocation.Expression;
            var anchorStart = memberAccess.OperatorToken.SpanStart;
            var cb = new ComponentBuilder
            {
                TypeFullName = SpatialComponents.SnapperTypeName,
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

            if (left || right)
            {
                node.DrivenChannels |= ChannelMask.PositionX;
            }

            if (up || down)
            {
                node.DrivenChannels |= ChannelMask.PositionY;
            }

            if (forward || back)
            {
                node.DrivenChannels |= ChannelMask.PositionZ;
            }
        }

        // A bool axis flag: a literal `true` is stored `Bool(true)` and marks the axis SET (drives + contradiction).
        // A literal `false` is NOT stored and does not set the axis. A non-literal value stays TOTAL: stored as
        // Unsupported (intent not silently dropped) and does NOT set the axis.
        private static void ApplyFlag(
            ExpressionSyntax expr, string name, SourceSpan span, ref bool set,
            List<KeyValuePair<string, ValueNode>> fields, List<KeyValuePair<string, SourceSpan>> spans)
        {
            if (expr.IsKind(SyntaxKind.TrueLiteralExpression))
            {
                set = true;
                fields.Add(new KeyValuePair<string, ValueNode>(name, ValueNode.Primitive.Bool(true)));
                spans.Add(new KeyValuePair<string, SourceSpan>(name, span));
            }
            else if (expr.IsKind(SyntaxKind.FalseLiteralExpression))
            {
                // not set, not stored (only set flags round-trip — spec §Emit "only the set flags emitted")
            }
            else
            {
                fields.Add(new KeyValuePair<string, ValueNode>(name, new ValueNode.Unsupported(expr.ToString())));
                spans.Add(new KeyValuePair<string, SourceSpan>(name, span));
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
