using System.Collections.Generic;
using SceneBuilder.Core.Diff;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Reconcile
{
    // b3-t2: scene->code RectTransform field emission, sibling of TransformEdits (Reconciler.cs).
    // Split into its own partial per the project's file-size precedent (BuilderParser.RectTransform.cs,
    // SourcePatchApplier.Instances.cs) — the parent file keeps dispatch lines only.
    public static partial class Reconciler
    {
        // `snapshot` MUST already be driven-masked by Reconciler.MaskDriven (its
        // RectTransformFields.MaskDrivenRect tail) before it reaches here. Emits one PatchArgument
        // per field, never an edit that rewrites text to itself:
        //   - snapshot NOT rect (both plain, or a demotion: model rect / live object plain) -> nothing.
        //     Writing a plain Transform's absent fields back would clobber authored layout with
        //     defaults, so source stays authoritative.
        //   - promotion (model NOT rect, snapshot rect) -> one edit per field with at least one FREE
        //     axis, value = the masked snapshot value, EVEN when it equals field.Default (D6: an
        //     "omit at default" rule would silently drop the Kind change — no `.RectTransform(...)`
        //     call could ever be introduced). All axes of a field driven -> nothing for that field.
        //   - matched (both rect) -> one edit per field whose CANONICAL literal text differs from the
        //     model's (mirrors RectTransformDiff.ChangedChannels, but on rendered text, never raw
        //     floats — the same anti-loop rule TransformEdits already applies to rot:/scale:).
        private static IEnumerable<SourceEdit> RectTransformEdits(string logicalId, TransformData model, TransformData snapshot)
        {
            if (!snapshot.IsRectTransform)
            {
                yield break;
            }

            var promoting = !model.IsRectTransform;
            var driven = snapshot.DrivenChannels & ChannelMask.AllRectFields;

            foreach (var field in RectTransformFields.All)
            {
                if (promoting)
                {
                    if ((driven & field.Mask) == field.Mask)
                    {
                        continue; // every axis of this field is driven -> nothing free to author
                    }

                    yield return new PatchArgument
                    {
                        Anchor = logicalId,
                        ArgName = field.ArgName,
                        NewExpr = SourceExpr.Vec2Literal(field.Get(snapshot) ?? field.Default),
                    };

                    continue;
                }

                var modelLiteral = SourceExpr.Vec2Literal(field.Get(model) ?? field.Default);
                var snapshotLiteral = SourceExpr.Vec2Literal(field.Get(snapshot) ?? field.Default);
                if (!string.Equals(modelLiteral, snapshotLiteral, System.StringComparison.Ordinal))
                {
                    yield return new PatchArgument { Anchor = logicalId, ArgName = field.ArgName, NewExpr = snapshotLiteral };
                }
            }
        }

        // m-ui-recttransform b3-t1 (iteration 2): the ONE created-node payload rule, extracted
        // verbatim from ReconcilerAppends.cs (both the plain-append and instance-AddChild create
        // paths call this — a second copy of the all-driven guard is precisely how a
        // Canvas-archetype node starts churning). Splits a live create candidate's TransformData
        // into the base GameObject payload (driven-masked, X/Y-held per D1/D2) and, separately,
        // the chained `.RectTransform(...)` payload — null for a non-UI node, and also null when
        // every rect axis is driven (no author-controlled layout to capture).
        internal static (TransformData Transform, TransformData? RectTransform) SplitCreatedPayload(TransformData live)
        {
            var authoringDefaults = new TransformData();
            var transformPayload = live;
            TransformData? rectTransformPayload = null;

            if (transformPayload.IsRectTransform)
            {
                transformPayload = RectTransformDiff.HoldAnchoredXY(
                    MaskDriven(authoringDefaults, transformPayload),
                    authoringDefaults);

                if ((live.DrivenChannels & ChannelMask.AllRectFields) != ChannelMask.AllRectFields)
                {
                    rectTransformPayload = transformPayload;
                }
            }

            return (transformPayload, rectTransformPayload);
        }
    }
}
