using Microsoft.CodeAnalysis.CSharp.Syntax;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Parsing
{
    // `.RectTransform(...)` parse arm, split out of BuilderParser.cs for file-size discipline.
    // Dispatch lives in BuilderParser's ApplyChainedCalls switch. Table-driven off
    // RectTransformFields (b1-t1) — zero hardcoded argument-name strings or default numbers here.
    // BuildTransformData is the ONE TransformData projection, called by BOTH BuildPlainNode and
    // BuildInstanceNode so an instance node's UI fields are never silently dropped.
    public static partial class BuilderParser
    {
        // `.RectTransform(anchoredPos: (-10,-10), sizeDelta: (200,120), ...)`. All args named,
        // all 2-tuples of numeric literals — already proven by FlatShapeRecognizer.ApplyRectTransform,
        // the single grammar source. Mere presence of the call marks the node's Kind.
        private static void ApplyRectTransform(NodeBuilder node, ArgumentListSyntax args)
        {
            node.IsRectTransform = true;

            foreach (var arg in args.Arguments)
            {
                if (arg.NameColon == null)
                {
                    throw Unreachable();
                }

                var name = arg.NameColon.Name.Identifier.Text;
                if (!RectTransformFields.TryFromArgName(name, out var field))
                {
                    throw Unreachable();
                }

                if (arg.Expression is not TupleExpressionSyntax tuple || tuple.Arguments.Count != 2)
                {
                    throw Unreachable();
                }

                var x = EvalFloat(tuple.Arguments[0].Expression);
                var y = EvalFloat(tuple.Arguments[1].Expression);
                node.RectFields[field.ArgName] = new Vec2(x, y);
            }
        }

        // The ONE TransformData projection — called by BuildPlainNode AND BuildInstanceNode.
        private static TransformData BuildTransformData(NodeBuilder builder)
        {
            var data = new TransformData
            {
                Position = builder.Position ?? Vec3.Zero,
                Rotation = builder.Rotation ?? Quat.Identity,
                Scale = builder.Scale ?? Vec3.One,
                DrivenChannels = builder.DrivenChannels,
            };

            if (!builder.IsRectTransform)
            {
                return data;
            }

            data = data with { Kind = RectTransformFields.Kind };
            foreach (var field in RectTransformFields.All)
            {
                var value = builder.RectFields.TryGetValue(field.ArgName, out var authored) ? authored : field.Default;
                data = field.With(data, value);
            }

            return data;
        }
    }
}
