using System.Collections.Generic;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Diff
{
    // b2-t1: every RectTransform layout diff rule in one place — mirrors the InstanceOverrideDiff
    // precedent (a sibling internal static helper called from Differ, not a partial class). All
    // five-way field logic is driven off RectTransformFields.All; nothing here hardcodes an
    // argument name, serialized path, ChannelMask spelling or default number.
    internal static class RectTransformDiff
    {
        // D1/D6 branch predicate: EITHER side is Kind=="RectTransform".
        public static bool Applies(TransformData model, TransformData snapshot)
            => model.IsRectTransform || snapshot.IsRectTransform;

        // D8: `target`'s transform with Position X/Y held to `xyFrom`'s values, Z from `target`.
        // AnchoredPosition owns X/Y for a rect node; base Position.X/Y is never independently
        // diffed. Both directions call this with the roles swapped: code->scene passes
        // (desired, actual) (Differ.cs:203) so the materialized transform's X/Y follows the live
        // anchor-derived position; scene->code passes (snapshot, model) (Reconciler.TransformEdits)
        // so a UI node's derived m_LocalPosition.x/y can never reach `.Transform(pos:)` — only a
        // genuine Z drift can. Only call site per direction is positional, so the rename is
        // source-compatible.
        public static TransformData HoldAnchoredXY(TransformData target, TransformData xyFrom)
            => target with { Position = new Vec3(xyFrom.Position.X, xyFrom.Position.Y, target.Position.Z) };

        // Per-axis channels that differ, reading an unset field on either side as its
        // RectTransformFields.Default* value (R3). Callers pass an ALREADY driven-masked snapshot
        // (RectTransformFields.MaskDrivenRect), so a driven axis compares equal by construction.
        public static ChannelMask ChangedChannels(TransformData model, TransformData snapshot)
        {
            var changed = ChannelMask.None;
            foreach (var field in RectTransformFields.All)
            {
                var m = field.Get(model) ?? field.Default;
                var s = field.Get(snapshot) ?? field.Default;

                if (m.X != s.X)
                {
                    changed |= field.MaskX;
                }

                if (m.Y != s.Y)
                {
                    changed |= field.MaskY;
                }
            }

            return changed;
        }

        // Matched path (called from Differ.EmitTransformEdit).
        public static void EmitEdit(string logicalId, TransformData model, TransformData snapshot, List<ChangeOp> ops)
        {
            if (model.IsRectTransform)
            {
                var masked = RectTransformFields.MaskDrivenRect(model, snapshot);
                var changed = ChangedChannels(model, masked);
                if (changed != ChannelMask.None)
                {
                    ops.Add(new SetRectTransform { LogicalId = logicalId, Transform = model, Changed = changed });
                }

                return;
            }

            // R6/D6: snapshot.IsRectTransform, model is plain Transform — the promotion. Unconditional:
            // this is how b3-t2 detects a demotion and how b2-t2 knows to skip lowering (Transform.Kind
            // == "Transform"). Never compares channels; Changed carries no meaning for a promotion.
            ops.Add(new SetRectTransform { LogicalId = logicalId, Transform = model, Changed = ChannelMask.None });
        }

        // Create path (called from Differ.EmitCreate / EmitCreateInstance). No snapshot; ALWAYS emits
        // for a rect model node so the node's Kind survives creation.
        public static void EmitCreate(string logicalId, TransformData model, List<ChangeOp> ops)
        {
            if (!model.IsRectTransform)
            {
                return;
            }

            // The create plan writes m_LocalPosition BEFORE these fields (D8 ordering), and on a
            // RectTransform that write RE-DERIVES anchoredPosition from the parent's rect, so "equal
            // to the RectTransform default" does NOT mean "already correct on the live object". A
            // created node owns all five fields.
            ops.Add(new SetRectTransform { LogicalId = logicalId, Transform = model, Changed = ChannelMask.AllRectFields });
        }
    }
}
