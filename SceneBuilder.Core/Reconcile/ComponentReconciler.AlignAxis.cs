using System.Collections.Generic;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Reconcile
{
    // AlignTo per-axis reconcile: the ONE place the incremental (scene->code) path folds an axis's
    // paired (mode, offset) fields into a single rendered argument. Split out of ComponentReconciler
    // .cs for file-size discipline; the field-value-diff loop calls TryReconcileAlignAxis first for
    // every matched AlignTo field.
    internal static partial class ComponentReconciler
    {
        // Reconciles ONE AlignTo axis (its paired mode + offset fields) as a single rendered
        // argument. Returns true when the field belongs to an AlignTo axis this handler owns (an
        // edit was emitted, a no-op was recognized, or the sibling field already handled the axis) —
        // the caller then skips its generic per-field arms. Returns false to fall through for any
        // non-AlignTo-axis field, or a snapshot axis whose mode is unrenderable (left to the generic
        // path, unchanged).
        private static bool TryReconcileAlignAxis(
            ComponentData sourceComp,
            ComponentData snapshotComp,
            string fieldKey,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, SourceSpan>>? fieldArgumentSpans,
            HashSet<string> handledAlignAxes,
            List<SourceEdit> edits,
            List<Conflict> conflicts)
        {
            if (sourceComp.Type.FullName != SpatialComponents.AlignToTypeName
                || !SpatialComponents.TryAlignAxisGroupFromField(fieldKey, out var keyword, out var modeField, out var offsetField))
            {
                return false;
            }

            // Process each axis once — the sibling field (mode vs offset) is subsumed here.
            if (!handledAlignAxes.Add(keyword))
            {
                return true;
            }

            snapshotComp.Fields.TryGetValue(modeField, out var snapMode);
            snapshotComp.Fields.TryGetValue(offsetField, out var snapOffset);

            // Only a snapshot axis pinned to a single recognized Mode member folds here. An
            // unpinned/Unsupported snapshot axis (RenderAlignAxisArgument == null) is not owned by
            // this handler — release the axis and let the generic per-field path run.
            var snapArg = SpatialComponentSource.RenderAlignAxisArgument(keyword, snapMode, snapOffset);
            if (snapArg == null)
            {
                handledAlignAxes.Remove(keyword);
                return false;
            }

            sourceComp.Fields.TryGetValue(modeField, out var srcMode);
            sourceComp.Fields.TryGetValue(offsetField, out var srcOffset);
            var srcArg = SpatialComponentSource.RenderAlignAxisArgument(keyword, srcMode, srcOffset);

            // Byte-stable: the axis already renders identically — emit nothing.
            if (srcArg == snapArg)
            {
                return true;
            }

            if (srcArg != null)
            {
                // Source already pins this axis: rewrite its WHOLE argument in place (mode + offset
                // together). Both paired fields carry the same whole-argument span; the mode field is
                // always present when the axis is pinned, so key the patch off it.
                if (fieldArgumentSpans != null
                    && fieldArgumentSpans.TryGetValue(sourceComp.LogicalId, out var compSpans)
                    && compSpans.TryGetValue(modeField, out var axisSpan))
                {
                    edits.Add(new PatchComponentField
                    {
                        Anchor = sourceComp.LogicalId,
                        ValueSpan = axisSpan,
                        NewExpr = snapArg,
                    });
                }
                else if (fieldArgumentSpans != null)
                {
                    conflicts.Add(ConflictDetector.UnanchorableComponentEdit(sourceComp.LogicalId, $"patch axis '{keyword}'"));
                }

                // fieldArgumentSpans == null: legacy no-op (parity with the generic patch arm).
                return true;
            }

            // Source does not pin this axis at all: append the folded argument as a NEW keyword. The
            // whole-argument text is pre-rendered here (where both paired fields are visible) and
            // carried in NewExpr — the applier's spatial introduce arm appends it verbatim.
            edits.Add(new IntroduceComponentField
            {
                Anchor = sourceComp.LogicalId,
                FieldKey = modeField,
                Value = snapMode!,
                NewExpr = snapArg,
            });
            return true;
        }
    }
}
