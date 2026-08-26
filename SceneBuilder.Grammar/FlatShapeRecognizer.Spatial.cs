using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SceneBuilder.Grammar
{
    // `.FitSize(...)` / `.AlignTo(...)` structural recognizer arms, split out of
    // FlatShapeRecognizer.cs — mirrors BuilderParser.Spatial.cs's structural Fail sites (all
    // SB1001 per research.md's mapping table). Grammar cannot reference Core, so the
    // SceneBuilder.Core.Model.SpatialComponents keyword tables cannot be reused; this file
    // reimplements ONLY the keyword-recognition surface (which argument names are structurally
    // valid) — it never needs the enum-member/type-name side of those tables, since it builds no
    // model. Total on VALUES (non-literal flag/scalar/`.Offset(...)`/unknown preset member -> no
    // violation, mirrors ApplyAxisAlign's Unsupported fallback being value-level, not structural);
    // Fail (located) on STRUCTURE only.
    public static partial class FlatShapeRecognizer
    {
        private static readonly string[] FitSizeAspectKeywords = { "width", "height", "depth" };
        private const string FitSizeExplicitKeyword = "size";

        private static readonly string[] AlignToAxisKeywords = { "x", "y", "z" };
        private const string AlignToTargetKeyword = "target";
        private const string AlignToFrameKeyword = "frame";
        private const string AlignToSpaceKeyword = "space";

        // Presets that need a target's extent to resolve against, unlike AbutMin/AbutMax (which fall
        // back to a world raycast/scan). Grammar cannot reference SceneBuilder.Core.Model
        // .SpatialComponents.AlignPresets, so this is a deliberate redeclaration of the subset that
        // requires a target, kept in sync with it by the parser/recognizer agreement tests.
        private static readonly string[] AlignTargetRequiringPresets = { "AlignMin", "AlignMax", "AlignCenter" };

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

        // `.AlignTo(target, x:/y:/z:/frame:/space:)`: the first argument may be unnamed (target); any
        // OTHER unnamed argument, or an unknown named argument, is SB1001. Unlike FitSize/Between there
        // is no whole-call combination rule (no axis is required — target alone is a valid call) and no
        // "requires an axis" case: target is always structurally optional.
        // A second, value-level rule layers on top: an x:/y:/z: axis carrying an AlignMin/AlignMax/
        // AlignCenter preset (mirrors the parser's ApplyAxisAlign peel/resolve) needs a target's extent
        // to resolve against, so it is SB1001 when no target argument is present at all (neither
        // positional nor `target:`). AbutMin/AbutMax are unaffected (they fall back to a world
        // raycast/scan). Reported on the first such axis argument, after every structural violation.
        private static void ApplyAlignTo(ArgumentListSyntax args, InvocationExpressionSyntax invocation, RecognizerContext ctx)
        {
            var hasTarget = false;
            ArgumentSyntax targetRequiringArg = null;
            string targetRequiringMember = null;
            string targetRequiringKeyword = null;

            for (var i = 0; i < args.Arguments.Count; i++)
            {
                var arg = args.Arguments[i];

                if (arg.NameColon == null)
                {
                    if (i != 0)
                    {
                        Report(ctx, arg, SB1001, "AlignTo arguments after target must be named (x:/y:/z:/frame:/space:)");
                    }
                    else
                    {
                        hasTarget = true;
                    }

                    continue;
                }

                var name = arg.NameColon.Name.Identifier.Text;

                if (name == AlignToTargetKeyword)
                {
                    hasTarget = true;
                    continue;
                }

                if (name == AlignToFrameKeyword || name == AlignToSpaceKeyword)
                {
                    continue;
                }

                if (System.Array.IndexOf(AlignToAxisKeywords, name) < 0)
                {
                    Report(ctx, arg, SB1001, $"Unknown AlignTo argument '{name}'");
                    continue;
                }

                if (targetRequiringArg == null)
                {
                    var member = ExtractAxisAlignMember(arg.Expression);
                    if (member != null && System.Array.IndexOf(AlignTargetRequiringPresets, member) >= 0)
                    {
                        targetRequiringArg = arg;
                        targetRequiringMember = member;
                        targetRequiringKeyword = name;
                    }
                }
            }

            if (!hasTarget && targetRequiringArg != null)
            {
                Report(ctx, targetRequiringArg, SB1001,
                    $"AlignTo {targetRequiringKeyword}: AxisAlign.{targetRequiringMember} requires a target; only AbutMin/AbutMax align without one");
            }
        }

        // An `x:`/`y:`/`z:` axis argument: `AxisAlign.<Preset>` or `AxisAlign.<Preset>.Offset(<expr>)`
        // or the bare `<Preset>` identifier (`using static AxisAlign;`). Returns null for any other
        // shape — this rule only needs to recognize a preset MEMBER NAME, not validate the argument,
        // so an unrecognized shape is simply not a target-requiring preset.
        private static string ExtractAxisAlignMember(ExpressionSyntax expr)
        {
            var baseExpr = expr;

            if (expr is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Offset" } offsetAccess })
            {
                baseExpr = offsetAccess.Expression;
            }

            return baseExpr switch
            {
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                IdentifierNameSyntax bare => bare.Identifier.Text,
                _ => null,
            };
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
