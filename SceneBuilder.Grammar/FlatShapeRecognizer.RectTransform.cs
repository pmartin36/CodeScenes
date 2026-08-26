using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SceneBuilder.Grammar
{
    // `.RectTransform(...)` structural recognizer arm. Grammar cannot reference Core (D7), so
    // SceneBuilder.Core.Model.RectTransformFields' argument-name spellings are re-declared here,
    // keyword-only, matching FlatShapeRecognizer.Spatial.cs's FitSizeAspectKeywords idiom. No
    // whole-call combination rule exists (unlike FitSize/AlignTo) — every violation is
    // per-argument, and a bare `.RectTransform()` is valid.
    public static partial class FlatShapeRecognizer
    {
        private static readonly string[] RectTransformArgKeywords =
            { "anchoredPos", "sizeDelta", "anchorMin", "anchorMax", "pivot" };

        private static void ApplyRectTransform(ArgumentListSyntax args, RecognizerContext ctx)
        {
            foreach (var arg in args.Arguments)
            {
                if (arg.NameColon == null)
                {
                    Report(ctx, arg, SB1001,
                        "RectTransform arguments must be named (anchoredPos:/sizeDelta:/anchorMin:/anchorMax:/pivot:)");
                    continue;
                }

                var name = arg.NameColon.Name.Identifier.Text;
                if (System.Array.IndexOf(RectTransformArgKeywords, name) < 0)
                {
                    Report(ctx, arg, SB1001, $"Unknown RectTransform argument '{name}'");
                    continue;
                }

                if (arg.Expression is not TupleExpressionSyntax tuple || tuple.Arguments.Count != 2)
                {
                    Report(ctx, arg.Expression, SB1001, $"RectTransform.{name} expects a 2-tuple");
                    continue;
                }

                if (!IsNumericLiteral(tuple.Arguments[0].Expression))
                {
                    Report(ctx, tuple.Arguments[0].Expression, SB1001, "Expected a numeric literal");
                }

                if (!IsNumericLiteral(tuple.Arguments[1].Expression))
                {
                    Report(ctx, tuple.Arguments[1].Expression, SB1001, "Expected a numeric literal");
                }
            }
        }
    }
}
