using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Reconcile
{
    // b3-t2: generalizes the introduce/patch machinery below (SourcePatchApplier.cs) by an
    // ArgumentCall descriptor, so a rect PatchArgument travels the SAME code path as a Transform
    // one instead of a second, rect-specific resolver. Without this every RectTransformEdits
    // (Reconciler.RectTransform.cs) edit is unapplicable — the applier would throw PatchException,
    // or worse, splice `anchoredPos:` into an unrelated `.Transform(...)` call. See research.md's
    // REFINED scope note: this plumbing lands here, in b3-t2, not b3-t3.
    public static partial class SourcePatchApplier
    {
        private sealed record ArgumentCall(string MethodName, string[] PositionalArgs);

        private static readonly ArgumentCall TransformCall = new("Transform", TransformPositionalArgs);

        private static readonly ArgumentCall RectTransformCall =
            new("RectTransform", RectTransformFields.All.Select(f => f.ArgName).ToArray());

        // null for "name" (patched at the Add(...) argument, not a chained call — see
        // ResolvePatchArgument).
        private static ArgumentCall? CallFor(string argName)
        {
            if (Array.IndexOf(TransformPositionalArgs, argName) >= 0)
            {
                return TransformCall;
            }

            return RectTransformFields.TryFromArgName(argName, out _) ? RectTransformCall : null;
        }

        // m-ui-recttransform b3-t1 (iteration 2): the rect argument-list emitter, EXTRACTED from
        // StatementText.cs's BuildAppendStatementText (omit-at-default rule preserved character
        // for character — a not-fully-driven UI node whose five values are all default still
        // yields a BARE "RectTransform()" call text) so both the top-level append and the
        // instance-AddChild closure render the exact same text, never two spellings.
        internal static string RenderRectTransformCall(TransformData rect)
        {
            var rectParts = new List<string>();
            foreach (var field in RectTransformFields.All)
            {
                var literal = SourceExpr.Vec2Literal(field.Get(rect) ?? field.Default);
                if (literal != SourceExpr.Vec2Literal(field.Default))
                {
                    rectParts.Add(field.ArgName + ": " + literal);
                }
            }

            return "RectTransform(" + string.Join(", ", rectParts) + ")";
        }
    }
}
