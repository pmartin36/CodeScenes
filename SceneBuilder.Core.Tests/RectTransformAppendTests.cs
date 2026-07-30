using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;
using static SceneBuilder.Core.Tests.RectTransformFixtures;

namespace SceneBuilder.Core.Tests
{
    // b3-t4: created-UI-node append carries the RectTransform payload in the SAME Reconcile pass
    // (§13 single-pass convergence). Covers the churn trap (a not-fully-driven UI node must ALWAYS
    // get a call, bare when every arg is default) and the all-driven guard (D2). Fixture style
    // mirrors StructuralSyncbackTests's Reconcile -> Apply -> fold map -> re-Parse -> Reconcile
    // idempotence harness.
    public class RectTransformAppendTests
    {
        private const string CanvasRootSource = @"
public class CreatedUiScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var canvas = scene.Add(""Canvas"");
    }
}
";

        private static (SceneSnapshot Snapshot, IdentityMap Map) CanvasWithChild(SnapshotNode child)
        {
            var canvas = new SnapshotNode
            {
                GlobalObjectId = "goid-canvas",
                Name = "Canvas",
                Children = new[] { child },
            };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { canvas } };
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "canvas", GlobalObjectId = "goid-canvas", Kind = "GameObject" },
                },
            };

            return (snapshot, map);
        }

        [Fact]
        public void Reconcile_CreatedUiNode_AppendsAddWithRectTransform_SecondSyncNoOp()
        {
            var panel = Rect(
                anchoredPos: new Vec2(-10, -10),
                sizeDelta: new Vec2(200, 120),
                anchorMin: new Vec2(1, 1),
                anchorMax: new Vec2(1, 1),
                pivot: new Vec2(1, 1),
                position: new Vec3(950, 530, 0));

            var (snapshot, map) = CanvasWithChild(new SnapshotNode { GlobalObjectId = "goid-panel", Name = "Panel", Transform = panel });
            var parsed = BuilderParser.Parse(CanvasRootSource);

            var recon1 = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors);

            Assert.NotEmpty(recon1.Patch.Edits);
            Assert.Empty(recon1.Conflicts);

            var patched = SourcePatchApplier.Apply(CanvasRootSource, recon1.Patch, parsed.Anchors);

            // D1: the panel's derived m_LocalPosition (950, 530) must never reach source as `.Transform(pos:)`.
            Assert.Contains(
                "canvas.Add(\"Panel\").RectTransform(anchoredPos: (-10f, -10f), sizeDelta: (200f, 120f), anchorMin: (1f, 1f), anchorMax: (1f, 1f), pivot: (1f, 1f));",
                patched);
            Assert.DoesNotContain(".Transform(", patched);

            var foldedEntries = map.Entries.Concat(recon1.AddedEntries).ToArray();
            var updatedMap = map with { Entries = foldedEntries };
            var reparsed = BuilderParser.Parse(patched, updatedMap);

            var recon2 = Reconciler.Reconcile(reparsed.Model, snapshot, reparsed.IdentityMap, reparsed.Anchors);

            Assert.Empty(recon2.Patch.Edits);
            Assert.Empty(recon2.Conflicts);
        }

        [Fact]
        public void Reconcile_CreatedUiNode_AppendsBareRectTransformCall_WhenEveryFieldIsAtItsDefault()
        {
            var panel = Rect(); // every field at RectTransformFields defaults, nothing driven.
            var (snapshot, map) = CanvasWithChild(new SnapshotNode { GlobalObjectId = "goid-panel", Name = "Panel", Transform = panel });
            var parsed = BuilderParser.Parse(CanvasRootSource);

            var recon1 = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors);
            var patched = SourcePatchApplier.Apply(CanvasRootSource, recon1.Patch, parsed.Anchors);

            Assert.Contains("canvas.Add(\"Panel\").RectTransform();", patched);

            var foldedEntries = map.Entries.Concat(recon1.AddedEntries).ToArray();
            var updatedMap = map with { Entries = foldedEntries };
            var reparsed = BuilderParser.Parse(patched, updatedMap);

            var recon2 = Reconciler.Reconcile(reparsed.Model, snapshot, reparsed.IdentityMap, reparsed.Anchors);

            // The churn trap: dropping the call re-parses as Kind=="Transform", and the promotion
            // branch then patches all five arguments back in on the very next sync.
            Assert.Empty(recon2.Patch.Edits);
        }

        [Fact]
        public void Reconcile_CreatedUiNodeAllFieldsDriven_AppendsWithoutRectTransformCall()
        {
            var overlay = Rect(
                sizeDelta: new Vec2(1920, 1080),
                position: new Vec3(960, 540, 0),
                scale: new Vec3(2, 2, 2),
                driven: ChannelMask.AllRectFields
                    | ChannelMask.PositionX | ChannelMask.PositionY | ChannelMask.PositionZ
                    | ChannelMask.ScaleX | ChannelMask.ScaleY | ChannelMask.ScaleZ);

            var (snapshot, map) = CanvasWithChild(new SnapshotNode { GlobalObjectId = "goid-overlay", Name = "Overlay", Transform = overlay });
            var parsed = BuilderParser.Parse(CanvasRootSource);

            var recon1 = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors);
            var patched = SourcePatchApplier.Apply(CanvasRootSource, recon1.Patch, parsed.Anchors);

            // D2: the root-Canvas archetype — every rect axis AND the base pos/scale are driven, so
            // there is nothing author-controlled to capture; no `.RectTransform(` and no `.Transform(`.
            Assert.Contains("canvas.Add(\"Overlay\");", patched);
            Assert.DoesNotContain(".RectTransform(", patched);
            Assert.DoesNotContain(".Transform(", patched);

            var foldedEntries = map.Entries.Concat(recon1.AddedEntries).ToArray();
            var updatedMap = map with { Entries = foldedEntries };
            var reparsed = BuilderParser.Parse(patched, updatedMap);

            var recon2 = Reconciler.Reconcile(reparsed.Model, snapshot, reparsed.IdentityMap, reparsed.Anchors);

            Assert.Empty(recon2.Patch.Edits);
        }

        [Fact]
        public void Reconcile_CreatedUiNode_PartiallyDrivenField_AppendsFreeAxisOnly()
        {
            // D5's per-axis rule on the append path: driven X falls back to the authoring default
            // (100), the free Y axis carries the snapshot's value.
            var panel = Rect(sizeDelta: new Vec2(999, 120), driven: ChannelMask.SizeDeltaX);
            var (snapshot, map) = CanvasWithChild(new SnapshotNode { GlobalObjectId = "goid-panel", Name = "Panel", Transform = panel });
            var parsed = BuilderParser.Parse(CanvasRootSource);

            var recon1 = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors);
            var patched = SourcePatchApplier.Apply(CanvasRootSource, recon1.Patch, parsed.Anchors);

            Assert.Contains("sizeDelta: (100f, 120f)", patched);
        }
    }
}
