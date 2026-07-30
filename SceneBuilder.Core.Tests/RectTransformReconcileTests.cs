using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;
using static SceneBuilder.Core.Tests.RectTransformFixtures;

namespace SceneBuilder.Core.Tests
{
    // b3-t2: scene->code RectTransform reconcile. Covers matched-node per-field PatchArguments,
    // per-axis driven masking (D5), the anti-loop canonical-literal rule, the D1 double-authority
    // hold on `pos:` for a rect node, Transform->RectTransform promotion emission (D6) and its
    // demotion counterpart, and the promotion's applied-without-throwing seam (SourcePatchApplier
    // plumbing this task also lands). Fixture style mirrors RectTransformDiffTests.cs;
    // Rect(...)/ReconcileMatchedNode(...) come from RectTransformFixtures via `using static`.
    public class RectTransformReconcileTests
    {
        [Fact]
        public void Reconcile_MovedRect_ProducesAnchoredPosArgumentPatch_FSuffixed()
        {
            var model = Rect(anchoredPos: new Vec2(10, 20));
            var snapshot = Rect(anchoredPos: new Vec2(30, 40));

            var result = ReconcileMatchedNode(model, snapshot);

            var edit = Assert.Single(result.Patch.Edits);
            var patchArg = Assert.IsType<PatchArgument>(edit);
            Assert.Equal("anchoredPos", patchArg.ArgName);
            Assert.Equal("(30f, 40f)", patchArg.NewExpr);
        }

        [Fact]
        public void Reconcile_ResizedRect_ProducesSizeDeltaArgumentPatch()
        {
            var model = Rect(sizeDelta: new Vec2(100, 100));
            var snapshot = Rect(sizeDelta: new Vec2(150, 80));

            var result = ReconcileMatchedNode(model, snapshot);

            var edit = Assert.Single(result.Patch.Edits);
            var patchArg = Assert.IsType<PatchArgument>(edit);
            Assert.Equal("sizeDelta", patchArg.ArgName);
            Assert.Equal("(150f, 80f)", patchArg.NewExpr);
        }

        [Fact]
        public void Reconcile_AnchorsChanged_ProducesOnlyAnchorMinMaxPatches()
        {
            var model = Rect(anchorMin: new Vec2(0, 0), anchorMax: new Vec2(1, 1));
            var snapshot = Rect(anchorMin: new Vec2(0.5f, 0), anchorMax: new Vec2(1, 0.5f));

            var result = ReconcileMatchedNode(model, snapshot);

            var patchArgs = result.Patch.Edits.Cast<PatchArgument>().ToList();
            Assert.Equal(2, patchArgs.Count);
            Assert.Contains(patchArgs, p => p.ArgName == "anchorMin" && p.NewExpr == "(0.5f, 0f)");
            Assert.Contains(patchArgs, p => p.ArgName == "anchorMax" && p.NewExpr == "(1f, 0.5f)");
        }

        [Fact]
        public void Reconcile_EqualRectLayout_ProducesNoEdits()
        {
            var t = Rect();

            var result = ReconcileMatchedNode(t, t);

            Assert.Empty(result.Patch.Edits);
        }

        [Fact]
        public void Reconcile_RectSubRoundingDrift_ProducesNoArgumentPatch()
        {
            // Anti-loop rule (mirrors `rot:`, Reconciler.cs:792-807): a sub-0.0001 drift renders to
            // the SAME canonical literal, so emitting it would be a perpetual self-rewrite.
            var model = Rect(anchoredPos: new Vec2(10, 20));
            var snapshot = Rect(anchoredPos: new Vec2(10.00001f, 20));

            var result = ReconcileMatchedNode(model, snapshot);

            Assert.Empty(result.Patch.Edits);
        }

        [Fact]
        public void Reconcile_DrivenRectAxis_FreeAxisChanged_ComposesModelDrivenAxisWithSnapshotFreeAxis()
        {
            var model = Rect(sizeDelta: new Vec2(100, 100));
            var snapshot = Rect(sizeDelta: new Vec2(200, 300), driven: ChannelMask.SizeDeltaX);

            var result = ReconcileMatchedNode(model, snapshot);

            var edit = Assert.Single(result.Patch.Edits);
            var patchArg = Assert.IsType<PatchArgument>(edit);
            Assert.Equal("sizeDelta", patchArg.ArgName);
            Assert.Equal("(100f, 300f)", patchArg.NewExpr);
        }

        [Fact]
        public void Reconcile_RectTransform_LocalPositionXYDrift_ProducesNoPosPatch()
        {
            // An X/Y-only drift never reaches the Reconciler at all — Differ.cs suppresses the
            // SetTransform op for a rect node.
            var model = Rect(position: new Vec3(0, 0, 0));
            var xyOnlyDrift = Rect(position: new Vec3(5, 7, 0));

            Assert.Empty(ReconcileMatchedNode(model, xyOnlyDrift).Patch.Edits);

            // A genuine Z drift must still hold X/Y to the MODEL's values, not the snapshot's
            // anchor-derived m_LocalPosition.
            var zDrift = Rect(position: new Vec3(5, 7, 3));

            var result = ReconcileMatchedNode(model, zDrift);

            var edit = Assert.Single(result.Patch.Edits);
            var patchArg = Assert.IsType<PatchArgument>(edit);
            Assert.Equal("pos", patchArg.ArgName);
            Assert.Equal("(0f, 0f, 3f)", patchArg.NewExpr);
        }

        [Fact]
        public void Reconcile_TransformPromotedToRect_AppendsRectTransformCall_NotDropped()
        {
            // The "Image added to a GameObject at the origin" archetype: model authored `.Transform(...)`
            // only (Kind "Transform"); the live object is now a RectTransform sitting exactly on the
            // RectTransformFields defaults, with Position/Rotation/Scale unchanged from the model.
            var model = new TransformData();
            var snapshot = Rect();

            var result = ReconcileMatchedNode(model, snapshot);

            var patchArgs = result.Patch.Edits.Cast<PatchArgument>().ToList();
            Assert.Equal(
                new[] { "anchoredPos", "anchorMax", "anchorMin", "pivot", "sizeDelta" },
                patchArgs.Select(p => p.ArgName).OrderBy(n => n));
            Assert.DoesNotContain(patchArgs, p => p.ArgName is "pos" or "rot" or "scale");
        }

        [Fact]
        public void Reconcile_AllFieldsDriven_DoesNotAppendRectTransform()
        {
            var model = new TransformData();
            var snapshot = Rect(driven: ChannelMask.AllRectFields);

            var result = ReconcileMatchedNode(model, snapshot);

            Assert.Empty(result.Patch.Edits);
        }

        [Fact]
        public void Reconcile_DemotedRectNode_ProducesNoRectEdits()
        {
            // Model still authored as a RectTransform; the live object reverted to a plain Transform
            // (e.g. the Image component was removed). Source stays authoritative: writing the plain
            // Transform's absent fields back would clobber the authored layout with defaults.
            var model = Rect(anchoredPos: new Vec2(10, 20));
            var snapshot = new TransformData();

            var result = ReconcileMatchedNode(model, snapshot);

            Assert.DoesNotContain(
                result.Patch.Edits.Cast<PatchArgument>(),
                p => p.ArgName is "anchoredPos" or "sizeDelta" or "anchorMin" or "anchorMax" or "pivot");
        }

        [Fact]
        public void Reconcile_PromotedNode_EditsApplyWithoutPatchException()
        {
            var model = new TransformData();
            var snapshot = Rect();

            var root = new GameObjectNode { LogicalId = "icon", Name = "Icon", Transform = model };
            var sceneModel = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };
            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-icon", Name = "Icon", Transform = snapshot };
            var sceneSnapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };
            var map = new IdentityMap
            {
                Scene = "Assets/Scenes/Demo.unity",
                Entries = new[] { new IdentityMapEntry { LogicalId = "icon", GlobalObjectId = "goid-icon", Kind = "GameObject" } },
            };

            const string source = @"
public class IconScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var icon = scene.Add(""Icon"");
    }
}
";
            var anchors = BuilderParser.Parse(source).Anchors;

            var result = Reconciler.Reconcile(sceneModel, sceneSnapshot, map, anchors);
            var applied = SourcePatchApplier.Apply(source, result.Patch, anchors);

            Assert.Contains(".RectTransform(", applied);
        }
    }
}
