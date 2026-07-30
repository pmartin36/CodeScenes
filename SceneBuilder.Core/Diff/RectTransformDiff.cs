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

        // b2-t1/i3: a base Position/Scale channel the SNAPSHOT reports driven that the MODEL's own
        // DrivenChannels does NOT also claim is "driven by something the builder does not author" —
        // e.g. a CanvasScaler re-driving a Canvas's scale. The plugin cannot re-baseline a driver it
        // does not own (unlike FitSize/SurfaceSnap, which PlanExecutor re-baselines on write), so that
        // axis must be held to the live value rather than clobbered. A channel driven on BOTH sides
        // (authored FitSize/SurfaceSnap intent) is excluded — spec 23 still writes it in full.
        public static ChannelMask ForeignDrivenBase(TransformData model, TransformData snapshot)
            => snapshot.DrivenChannels & ~model.DrivenChannels & DrivenTransform.BaseChannels;

        // code->scene ONLY (the scene->code counterpart is Reconciler.MaskDriven). Composes D8's X/Y
        // anchored-position hold with the foreign-driven-base hold above; both live in one returned
        // TransformData so Differ.EmitTransformEdit's condition and its emitted payload are provably
        // the same object.
        public static TransformData HoldForWrite(TransformData model, TransformData snapshot)
            => DrivenTransform.HoldBase(HoldAnchoredXY(model, snapshot), snapshot, ForeignDrivenBase(model, snapshot));

        // Matched path (called from Differ.EmitTransformEdit). Three sibling arms, kind-mismatch
        // checked BEFORE the value compare — mirrors EmitCreate's "kind differs -> assert the whole
        // rect state" rule so a kind change can never be short-circuited by a coincidental value
        // match on either side.
        public static void EmitEdit(string logicalId, TransformData model, TransformData snapshot, List<ChangeOp> ops)
        {
            if (!model.IsRectTransform)
            {
                // R6/D6: snapshot.IsRectTransform, model is plain Transform — the promotion.
                // Unconditional: this is how b3-t2 detects a demotion and how b2-t2 knows to skip
                // lowering (Transform.Kind == "Transform"). Never compares channels; Changed carries
                // no meaning for a promotion.
                ops.Add(new SetRectTransform { LogicalId = logicalId, Transform = model, Changed = ChannelMask.None });
                return;
            }

            if (!snapshot.IsRectTransform)
            {
                // b2: model is a RectTransform, the live object still carries a plain Transform —
                // forward promotion. Unconditional, NOT value-gated: a bare `.RectTransform()` (all
                // five args at RectTransformFields.Default*) compares EQUAL to a plain snapshot's
                // unset fields (read as Default via ChangedChannels), so a value compare here would
                // emit zero ops and silently drop the promotion forever — the adapter's in-place
                // promotion (PlanExecutor.EnsureRectTransform) is reachable ONLY through a rect
                // SetField. Every value the live object is missing must be asserted, so Changed is
                // the whole rect state, not the differing subset.
                ops.Add(new SetRectTransform { LogicalId = logicalId, Transform = model, Changed = ChannelMask.AllRectFields });
                return;
            }

            var masked = RectTransformFields.MaskDrivenRect(model, snapshot);
            var changed = RectTransformFields.ChangedChannels(model, masked);
            if (changed != ChannelMask.None)
            {
                ops.Add(new SetRectTransform { LogicalId = logicalId, Transform = model, Changed = changed });
            }
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
