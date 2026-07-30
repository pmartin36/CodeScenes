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

        private const string InstancePrefabGuid = "guid-ui-button";
        private const string InstancePrefabPath = "Assets/Prefabs/UiButton.prefab";

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

        // Third create-payload call site (ReconcilerInstances.cs's HandleInstanceNode): a
        // prefab-INSTANCE root created under a mapped rect parent. `child` carries SourcePrefabGuid,
        // so it never recurses (HandleInstanceNode) and is routed through the SAME
        // SplitCreatedPayload rule as the plain-Add and AddChild create sites, passing
        // `canHostRectTransformCall: false` because InstanceHandle has no `.RectTransform(...)` member.
        private static (SceneSnapshot Snapshot, IdentityMap Map) CanvasWithInstanceChild(SnapshotNode child)
        {
            var (snapshot, map) = CanvasWithChild(child);
            return (snapshot, map with
            {
                Assets = new[] { new AssetEntry { Guid = InstancePrefabGuid, LastKnownPath = InstancePrefabPath, TypeHint = "GameObject" } },
            });
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

        // The instance-root create path (ReconcilerInstances.cs's HandleInstanceNode) is the THIRD
        // create-payload call site, routed through SplitCreatedPayload: the derived m_LocalPosition
        // never reaches source as `.Transform(pos:)`, and the dropped UI layout is reported as one
        // `UnlocalizableRectTransform` conflict. `child` carries no SourcePrefabTransform, so this
        // case pins the UNKNOWN-baseline fallback (every free field named). See
        // Reconcile_CreatedUiPrefabInstance_LayoutMatchesPrefabAsset_AppendsBareInstance_NoConflict
        // for the known-and-equal case that stays quiet instead.
        [Fact]
        public void Reconcile_CreatedUiPrefabInstance_AppendsInstanceWithoutDerivedPos_ReportsUnlocalizableLayout()
        {
            var buttonTransform = Rect(
                anchoredPos: new Vec2(50, 20),
                sizeDelta: new Vec2(160, 40),
                position: new Vec3(50, 20, 0));

            var child = new SnapshotNode
            {
                GlobalObjectId = "goid-button",
                Name = "UiButton",
                SourcePrefabGuid = InstancePrefabGuid,
                Transform = buttonTransform,
            };
            var (snapshot, map) = CanvasWithInstanceChild(child);
            var parsed = BuilderParser.Parse(CanvasRootSource);

            var recon1 = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors);

            var append = Assert.IsType<AppendStatement>(Assert.Single(recon1.Patch.Edits));
            Assert.Equal(InstancePrefabPath, append.SourcePrefabPath);
            Assert.Null(append.RectTransform);

            var conflict = Assert.Single(recon1.Conflicts);
            Assert.Equal(ConflictKind.UnlocalizableRectTransform, conflict.Kind);
            foreach (var argName in new[] { "anchoredPos", "sizeDelta", "anchorMin", "anchorMax", "pivot" })
            {
                Assert.Contains(argName, conflict.Reason);
            }

            var patched = SourcePatchApplier.Apply(CanvasRootSource, recon1.Patch, parsed.Anchors);

            // D1: the button's derived m_LocalPosition (50, 20) must never reach source as `pos:`.
            Assert.Contains($"canvas.Instance(\"{InstancePrefabPath}\")", patched);
            Assert.DoesNotContain(".Transform(", patched);
            Assert.DoesNotContain(".RectTransform(", patched);

            var foldedEntries = map.Entries.Concat(recon1.AddedEntries).ToArray();
            var updatedMap = map with { Entries = foldedEntries };
            var reparsed = BuilderParser.Parse(patched, updatedMap);

            var recon2 = Reconciler.Reconcile(reparsed.Model, snapshot, reparsed.IdentityMap, reparsed.Anchors);

            // A mapped UI instance whose layout diverges from its asset keeps reporting on the
            // matched path, so this assertion is on EDITS, not Conflicts.
            Assert.Empty(recon2.Patch.Edits);
        }

        // D2 on the instance-create path: every rect axis AND the base pos/scale are driven (the
        // root-Canvas archetype dragged as a prefab instance), so there is nothing author-controlled
        // to capture — no `.Transform(` and no conflict.
        [Fact]
        public void Reconcile_CreatedUiPrefabInstance_AllChannelsDriven_AppendsBareInstance_NoConflict()
        {
            var buttonTransform = Rect(
                sizeDelta: new Vec2(160, 40),
                position: new Vec3(66.67f, -40, 0),
                scale: new Vec3(2, 2, 2),
                driven: ChannelMask.AllRectFields
                    | ChannelMask.PositionX | ChannelMask.PositionY | ChannelMask.PositionZ
                    | ChannelMask.ScaleX | ChannelMask.ScaleY | ChannelMask.ScaleZ);

            var child = new SnapshotNode
            {
                GlobalObjectId = "goid-button",
                Name = "UiButton",
                SourcePrefabGuid = InstancePrefabGuid,
                Transform = buttonTransform,
            };
            var (snapshot, map) = CanvasWithInstanceChild(child);
            var parsed = BuilderParser.Parse(CanvasRootSource);

            var recon1 = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors);
            Assert.Empty(recon1.Conflicts);

            var patched = SourcePatchApplier.Apply(CanvasRootSource, recon1.Patch, parsed.Anchors);

            Assert.Contains($"canvas.Instance(\"{InstancePrefabPath}\");", patched);
            Assert.DoesNotContain(".Transform(", patched);
            Assert.DoesNotContain(".RectTransform(", patched);

            var foldedEntries = map.Entries.Concat(recon1.AddedEntries).ToArray();
            var updatedMap = map with { Entries = foldedEntries };
            var reparsed = BuilderParser.Parse(patched, updatedMap);

            var recon2 = Reconciler.Reconcile(reparsed.Model, snapshot, reparsed.IdentityMap, reparsed.Anchors);

            Assert.Empty(recon2.Patch.Edits);
        }

        // D5's per-axis rule on the instance-create path: a driven whole-field alias (both axes)
        // must be excluded from the Conflict's free-field list entirely, not just narrowed to a
        // free axis (there is none). `child` carries no SourcePrefabTransform (unknown-baseline
        // fallback) -- adding an EQUAL baseline here would legitimately silence this conflict, so
        // the baseline must stay unset for this case to keep meaning what it says.
        [Fact]
        public void Reconcile_CreatedUiPrefabInstance_PartiallyDrivenField_NamesOnlyFreeFields()
        {
            var buttonTransform = Rect(sizeDelta: new Vec2(160, 40), driven: ChannelMask.SizeDelta);

            var child = new SnapshotNode
            {
                GlobalObjectId = "goid-button",
                Name = "UiButton",
                SourcePrefabGuid = InstancePrefabGuid,
                Transform = buttonTransform,
            };
            var (snapshot, map) = CanvasWithInstanceChild(child);
            var parsed = BuilderParser.Parse(CanvasRootSource);

            var recon1 = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors);

            var conflict = Assert.Single(recon1.Conflicts);
            Assert.Equal(ConflictKind.UnlocalizableRectTransform, conflict.Kind);
            Assert.DoesNotContain("sizeDelta", conflict.Reason);
            foreach (var argName in new[] { "anchoredPos", "anchorMin", "anchorMax", "pivot" })
            {
                Assert.Contains(argName, conflict.Reason);
            }
        }

        // The SAME baseline rule applies on the create path -- a UI prefab dragged in with a
        // layout that already matches its own prefab asset has nothing unpersisted, so the append
        // carries NO `.RectTransform(...)` payload and reports NO conflict.
        [Fact]
        public void Reconcile_CreatedUiPrefabInstance_LayoutMatchesPrefabAsset_AppendsBareInstance_NoConflict()
        {
            var baseline = Rect(anchoredPos: new Vec2(50, 20), sizeDelta: new Vec2(160, 40));
            var buttonTransform = Rect(
                anchoredPos: new Vec2(50, 20),
                sizeDelta: new Vec2(160, 40),
                position: new Vec3(50, 20, 0));

            var child = new SnapshotNode
            {
                GlobalObjectId = "goid-button",
                Name = "UiButton",
                SourcePrefabGuid = InstancePrefabGuid,
                Transform = buttonTransform,
                SourcePrefabTransform = baseline,
            };
            var (snapshot, map) = CanvasWithInstanceChild(child);
            var parsed = BuilderParser.Parse(CanvasRootSource);

            var recon1 = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors);

            var append = Assert.IsType<AppendStatement>(Assert.Single(recon1.Patch.Edits));
            Assert.Equal(InstancePrefabPath, append.SourcePrefabPath);
            Assert.Null(append.RectTransform);
            Assert.Empty(recon1.Conflicts);

            var patched = SourcePatchApplier.Apply(CanvasRootSource, recon1.Patch, parsed.Anchors);
            Assert.Contains($"canvas.Instance(\"{InstancePrefabPath}\");", patched);
            Assert.DoesNotContain(".Transform(", patched);
            Assert.DoesNotContain(".RectTransform(", patched);
        }

        // Byte-identity sentinel: a plain, non-rect prefab instance never enters
        // SplitCreatedPayload's rect branch, so routing the instance-create path through the
        // shared payload rule does not regress the ordinary "genuine Z/position" case.
        [Fact]
        public void Reconcile_CreatedPlainPrefabInstance_StillAppendsTransformPayload_NoConflict()
        {
            var child = new SnapshotNode
            {
                GlobalObjectId = "goid-enemy",
                Name = "Enemy",
                SourcePrefabGuid = InstancePrefabGuid,
                Transform = new TransformData { Position = new Vec3(1, 2, 3) },
            };
            var (snapshot, map) = CanvasWithInstanceChild(child);
            var parsed = BuilderParser.Parse(CanvasRootSource);

            var recon1 = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors);
            Assert.Empty(recon1.Conflicts);

            var patched = SourcePatchApplier.Apply(CanvasRootSource, recon1.Patch, parsed.Anchors);
            Assert.Contains(".Transform(pos: (1f, 2f, 3f))", patched);
        }
    }
}
