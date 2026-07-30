using SceneBuilder.Core.Diff;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Materialize;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Plan;
using SceneBuilder.Core.Reconcile;

namespace SceneBuilder.Core.Tests
{
    // b2-t2/R4: hoisted from RectTransformDiffTests.cs (b2-t1) so the diff, materialize and
    // (future) reconcile tests share one source of the rect TransformData/model/snapshot/map
    // fixtures. Assertions in RectTransformDiffTests.cs / RectTransformMaterializeTests.cs are
    // UNCHANGED by this hoist.
    internal static class RectTransformFixtures
    {
        // The model/snapshot/map trio every rect Core test drives Differ/Materializer with.
        public readonly record struct RectScene(SceneModel Model, SceneSnapshot Snapshot, IdentityMap Map);

        public static TransformData Rect(
            Vec2? anchoredPos = null,
            Vec2? sizeDelta = null,
            Vec2? anchorMin = null,
            Vec2? anchorMax = null,
            Vec2? pivot = null,
            Vec3? position = null,
            Vec3? scale = null,
            ChannelMask driven = ChannelMask.None)
        {
            return new TransformData
            {
                Kind = RectTransformFields.Kind,
                Position = position ?? Vec3.Zero,
                Scale = scale ?? Vec3.One,
                AnchoredPosition = anchoredPos ?? RectTransformFields.DefaultAnchoredPosition,
                SizeDelta = sizeDelta ?? RectTransformFields.DefaultSizeDelta,
                AnchorMin = anchorMin ?? RectTransformFields.DefaultAnchorMin,
                AnchorMax = anchorMax ?? RectTransformFields.DefaultAnchorMax,
                Pivot = pivot ?? RectTransformFields.DefaultPivot,
                DrivenChannels = driven,
            };
        }

        public static RectScene MatchedNode(TransformData modelTransform, TransformData snapshotTransform)
        {
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Transform = modelTransform };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root", Transform = snapshotTransform };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var map = new IdentityMap
            {
                Scene = "Assets/Scenes/Demo.unity",
                Entries = new[] { new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" } },
            };

            return new RectScene(model, snapshot, map);
        }

        public static RectScene CreatedNode(TransformData modelTransform)
        {
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Transform = modelTransform };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = System.Array.Empty<SnapshotNode>() };
            var map = new IdentityMap { Scene = "Assets/Scenes/Demo.unity" };

            return new RectScene(model, snapshot, map);
        }

        public static ChangeSet DiffSingleMatchedNode(TransformData modelTransform, TransformData snapshotTransform)
        {
            var scene = MatchedNode(modelTransform, snapshotTransform);
            return Differ.Diff(scene.Model, scene.Snapshot, scene.Map);
        }

        public static Plan.Plan MaterializeMatchedNode(TransformData modelTransform, TransformData snapshotTransform)
        {
            var scene = MatchedNode(modelTransform, snapshotTransform);
            return Materializer.Materialize(scene.Model, scene.Snapshot, scene.Map);
        }

        public static Plan.Plan MaterializeCreatedNode(TransformData modelTransform)
        {
            var scene = CreatedNode(modelTransform);
            return Materializer.Materialize(scene.Model, scene.Snapshot, scene.Map);
        }

        // b3-t2: the scene->code counterpart of DiffSingleMatchedNode/MaterializeMatchedNode. Anchors
        // omitted (Reconciler.Reconcile treats a null anchors dictionary as "skip the MissingAnchor
        // guard"), so this is only for tests that assert on the emitted SourceEdits themselves, never
        // on SourcePatchApplier.Apply — a test that also needs to APPLY the edits needs its own
        // anchor-bearing source and cannot use this helper.
        public static ReconcileResult ReconcileMatchedNode(TransformData modelTransform, TransformData snapshotTransform)
        {
            var scene = MatchedNode(modelTransform, snapshotTransform);
            return Reconciler.Reconcile(scene.Model, scene.Snapshot, scene.Map);
        }
    }
}
