using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SceneBuilder.Grammar
{
    // `.FitSize(...)` / `.SurfaceSnap(...)` structural recognizer arms, split out of
    // FlatShapeRecognizer.cs — mirrors BuilderParser.Spatial.cs's structural Fail sites (all
    // SB1001 per research.md's mapping table). Grammar cannot reference Core, so the
    // SceneBuilder.Core.Model.SpatialComponents keyword tables cannot be reused; this file
    // reimplements ONLY the keyword-recognition surface (which argument names are structurally
    // valid) — it never needs the enum-member/type-name side of those tables, since it builds no
    // model. Total on VALUES (non-literal flag/scalar -> no violation, mirrors ApplyAxisFlag's
    // Unsupported fallback being value-level, not structural); Fail (located) on STRUCTURE only.
    public static partial class FlatShapeRecognizer
    {
        private static readonly string[] FitSizeAspectKeywords = { "width", "height", "depth" };
        private const string FitSizeExplicitKeyword = "size";

        private static readonly string[] SurfaceSnapAxisKeywords = { "up", "down", "left", "right", "forward", "back" };
        private const string SurfaceSnapTargetKeyword = "target";

        private const string BetweenFromKeyword = "from";
        private const string BetweenToKeyword = "to";
        private const string BetweenFractionKeyword = "fraction";
        private const string BetweenAxisKeyword = "axis";
        private const string BetweenOrientationKeyword = "alongOrientationOf";

        private static void ApplyFitSize(ArgumentListSyntax args, InvocationExpressionSyntax invocation, RecognizerContext ctx)
        {
            var hasAspect = false;
            var hasExplicit = false;
            var aspectCount = 0;

            foreach (var arg in args.Arguments)
            {
                if (arg.NameColon == null)
                {
                    Report(ctx, arg, SB1001, "FitSize arguments must be named (width:/height:/depth:/size:)");
                    continue;
                }

                var name = arg.NameColon.Name.Identifier.Text;
                if (System.Array.IndexOf(FitSizeAspectKeywords, name) >= 0)
                {
                    hasAspect = true;
                    aspectCount++;
                }
                else if (name == FitSizeExplicitKeyword)
                {
                    hasExplicit = true;
                }
                else
                {
                    Report(ctx, arg, SB1001, $"Unknown FitSize argument '{name}'");
                }
            }

            if (hasAspect && hasExplicit)
            {
                Report(ctx, invocation, SB1001, "FitSize cannot combine aspect (width/height/depth) with explicit size");
            }

            if (!hasAspect && !hasExplicit)
            {
                Report(ctx, invocation, SB1001, "FitSize requires one of width/height/depth, or size");
            }

            if (aspectCount > 1)
            {
                Report(ctx, invocation, SB1001, "FitSize aspect-locked form takes exactly one of width/height/depth");
            }
        }

        private static void ApplySurfaceSnap(ArgumentListSyntax args, InvocationExpressionSyntax invocation, RecognizerContext ctx)
        {
            bool up = false, down = false, left = false, right = false, forward = false, back = false;

            foreach (var arg in args.Arguments)
            {
                if (arg.NameColon == null)
                {
                    Report(ctx, arg, SB1001, "SurfaceSnap arguments must be named (up:/down:/left:/right:/forward:/back:/target:)");
                    continue;
                }

                var name = arg.NameColon.Name.Identifier.Text;

                if (name == SurfaceSnapTargetKeyword)
                {
                    continue;
                }

                if (System.Array.IndexOf(SurfaceSnapAxisKeywords, name) < 0)
                {
                    Report(ctx, arg, SB1001, $"Unknown SurfaceSnap argument '{name}'");
                    continue;
                }

                var set = arg.Expression.IsKind(SyntaxKind.TrueLiteralExpression);
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
                Report(ctx, invocation, SB1001, "SurfaceSnap cannot combine up and down");
            }

            if (left && right)
            {
                Report(ctx, invocation, SB1001, "SurfaceSnap cannot combine left and right");
            }

            if (forward && back)
            {
                Report(ctx, invocation, SB1001, "SurfaceSnap cannot combine forward and back");
            }

            if (!(up || down || left || right || forward || back))
            {
                Report(ctx, invocation, SB1001, "SurfaceSnap requires at least one snap axis (up/down/left/right/forward/back)");
            }
        }

        private static void ApplyBetween(ArgumentListSyntax args, InvocationExpressionSyntax invocation, RecognizerContext ctx)
        {
            var hasFrom = false;
            var hasTo = false;
            var hasFraction = false;
            var hasAxis = false;

            foreach (var arg in args.Arguments)
            {
                if (arg.NameColon == null)
                {
                    Report(ctx, arg, SB1001, "Between arguments must be named (from:/to:/fraction:/axis:/alongOrientationOf:)");
                    continue;
                }

                var name = arg.NameColon.Name.Identifier.Text;
                switch (name)
                {
                    case BetweenFromKeyword: hasFrom = true; break;
                    case BetweenToKeyword: hasTo = true; break;
                    case BetweenFractionKeyword: hasFraction = true; break;
                    case BetweenAxisKeyword: hasAxis = true; break;
                    case BetweenOrientationKeyword: break;
                    default:
                        Report(ctx, arg, SB1001, $"Unknown Between argument '{name}'");
                        break;
                }
            }

            if (!(hasFrom && hasTo && hasFraction && hasAxis))
            {
                Report(ctx, invocation, SB1001, "Between requires from:, to:, fraction:, and axis: (alongOrientationOf: optional)");
            }
        }
    }
}
