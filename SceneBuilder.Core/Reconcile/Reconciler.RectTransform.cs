using System.Collections.Generic;
using System.Linq;
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
        //     model's (mirrors RectTransformFields.ChangedChannels, but on rendered text, never raw
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

        // m-ui-recttransform b4-t1 (iteration 2): the ONE dispatch point for the SetRectTransform
        // case (Reconciler.cs `case SetRectTransform:`). RectTransformEdits(...) itself stays pure
        // and unchanged; this wrapper decides whether the model node's authoring construct can host
        // a `.RectTransform(...)` call at all. A PrefabInstanceNode cannot (InstanceHandle has no
        // such member, InstanceHandle.cs) — emitting the edit there would produce source that does
        // not compile. For that case, whether anything is reported at all now depends on the prefab
        // ASSET's own layout (`live.SourcePrefabTransform`, the second persistence home): a settled
        // instance whose layout already equals its own asset has nothing unpersisted and reports
        // nothing; a genuine divergence (or an unknown baseline) surfaces ONE located Conflict.
        internal static void EmitRectTransformEdits(
            string logicalId,
            string? globalObjectId,
            GameObjectNode model,
            SnapshotNode live,
            IReadOnlyDictionary<string, SourceSpan>? anchors,
            List<SourceEdit> edits,
            List<Conflict> conflicts)
        {
            var masked = MaskDriven(model.Transform, live.Transform);

            if (CanHostRectTransformCall(model))
            {
                edits.AddRange(RectTransformEdits(logicalId, model.Transform, masked));
                return;
            }

            SourceSpan? location = anchors != null && anchors.TryGetValue(logicalId, out var span) ? span : null;
            ReportUnlocalizableRect(
                logicalId,
                globalObjectId,
                UnpersistedRectFieldNames(logicalId, model.Transform, live.Transform, live.SourcePrefabTransform),
                location,
                conflicts);
        }

        // m-ui-recttransform b4-t1 (iteration 2): the ONE "which RectTransform fields does this node
        // actually LOSE?" computation, shared by the matched path (EmitRectTransformEdits) and the
        // create path (SplitCreatedPayload) — the only place the baseline rule is spelled.
        // `prefabBaseline` null (unresolvable/unknown asset) falls back to comparing against
        // `authoringModel` (never rect for a non-hostable node), which reuses RectTransformEdits'
        // PROMOTION arm to name every field with >=1 free axis regardless of value — unknown never
        // goes quiet. `prefabBaseline` set reuses the MATCHED arm (canonical-literal comparison) with
        // the baseline as the comparand, so only a genuine divergence from the asset's own layout is
        // named; `MaskDriven(baseline, live)` holds a driven axis to the baseline's value first, so a
        // driven-only divergence can never register.
        private static IEnumerable<string> UnpersistedRectFieldNames(
            string logicalId, TransformData authoringModel, TransformData live, TransformData? prefabBaseline)
            => prefabBaseline is { } baseline
                ? ArgNames(RectTransformEdits(logicalId, baseline, MaskDriven(baseline, live)))
                : ArgNames(RectTransformEdits(logicalId, authoringModel, MaskDriven(authoringModel, live)));

        // One place for the PatchArgument -> ArgName projection (RectTransformEdits' two arms yield
        // only PatchArgument today; kept as an explicit .OfType<> rather than an unguarded downcast).
        private static IEnumerable<string> ArgNames(IEnumerable<SourceEdit> edits)
            => edits.OfType<PatchArgument>().Select(e => e.ArgName);

        // A PrefabInstanceNode is authored as `.Instance(...)`, and InstanceHandle has no
        // `.RectTransform(...)` call (InstanceHandle.cs) — every other GameObjectNode is authored
        // through a NodeHandle chain (`Add(...)`, `AddChild(...)`, configure-closure), which can host
        // the call. Written as "which nodes CAN host the call" rather than a list of exceptions so a
        // future non-NodeHandle authoring host (e.g. ScopedHandle, which also has no Transform/
        // RectTransform member) extends this ONE predicate.
        internal static bool CanHostRectTransformCall(GameObjectNode node) => node is not PrefabInstanceNode;

        // m-ui-recttransform b3-t1 (iteration 2, extended iteration 2 of b3-t4): the ONE
        // created-node payload rule — every create site (plain `.Add`, instance-`.AddChild`,
        // prefab-instance `.Instance`) calls this, so the all-driven guard and the D1/D2 masking
        // exist in one place. Splits a live create candidate's TransformData into the base
        // GameObject payload (driven-masked, X/Y-held per D1/D2) and, separately, the chained
        // `.RectTransform(...)` payload — null for a non-UI node, and also null when every rect
        // axis is driven (no author-controlled layout to capture).
        // `canHostRectTransformCall` has NO default: does the statement being appended expose a
        // `.RectTransform(...)` call? `.Add(...)`/`.AddChild(...)` (NodeHandle) -> true;
        // `.Instance(...)` (InstanceHandle) -> false. A future create site must state its host
        // explicitly rather than silently inherit one that may be wrong.
        // `prefabBaseline` also has NO default (m-ui-recttransform b4-t1 iteration 2): the prefab
        // ASSET's own layout for an about-to-be-appended prefab-instance root, or null for every
        // NodeHandle-hosted create site (`.Add(...)`/`.AddChild(...)`, where the baseline is
        // unreachable — state it, do not omit it) and for an instance whose asset is unresolvable.
        internal static (TransformData Transform, TransformData? RectTransform) SplitCreatedPayload(
            TransformData live,
            bool canHostRectTransformCall,
            TransformData? prefabBaseline,
            string logicalId,
            string? globalObjectId,
            List<Conflict> conflicts)
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
                    if (canHostRectTransformCall)
                    {
                        rectTransformPayload = transformPayload;
                    }
                    else
                    {
                        // §7: never a silent drop. Reuses the SAME baseline rule the matched path
                        // uses (UnpersistedRectFieldNames) — a layout that already matches the
                        // prefab asset has nothing unpersisted and reports nothing; an unknown
                        // baseline falls back to RectTransformEdits' PROMOTION arm (model =
                        // authoring defaults => plain), naming every field with >=1 FREE axis.
                        ReportUnlocalizableRect(
                            logicalId,
                            globalObjectId,
                            UnpersistedRectFieldNames(logicalId, authoringDefaults, live, prefabBaseline),
                            location: null, // the statement does not exist in source yet
                            conflicts);
                    }
                }
            }

            return (transformPayload, rectTransformPayload);
        }

        // The ONE "this UI layout cannot be authored" report, shared by the MATCHED path
        // (EmitRectTransformEdits) and the CREATE path (SplitCreatedPayload) — the message and any
        // future gating of it live in this one method.
        private static void ReportUnlocalizableRect(
            string logicalId,
            string? globalObjectId,
            IEnumerable<string> fieldArgNames,
            SourceSpan? location,
            List<Conflict> conflicts)
        {
            var names = fieldArgNames.ToList();
            if (names.Count == 0)
            {
                return;
            }

            conflicts.Add(ConflictDetector.UnlocalizableRectTransform(logicalId, globalObjectId, names, location));
        }
    }
}
