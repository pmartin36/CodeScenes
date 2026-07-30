using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Materialize;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b4-t1: Core-only close of the spec's failure/revert paths -- a RectTransform argument that
    // reverts to its default patches back in place (the `.RectTransform(...)` call survives, Kind
    // is not lost), a rect layout that cannot be localized to a builder construct (a
    // PrefabInstanceNode root -- InstanceHandle has no `.RectTransform(...)` member) surfaces a
    // located Conflict instead of emitting source that does not compile, and the whole
    // Reconcile -> Apply -> re-parse -> Reconcile / Materialize loop is idempotent.
    public class RectTransformRoundTripTests
    {
        [Fact]
        public void RectTransform_FullRoundTrip_IsIdempotent()
        {
            const string source = @"
public class HudScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var canvas = scene.Add(""Canvas"");
        var panel = canvas.Add(""Panel"").RectTransform(anchoredPos: (-10f, -10f), sizeDelta: (200f, 120f), anchorMin: (1f, 1f), anchorMax: (1f, 1f), pivot: (1f, 1f));
    }
}
";
            var parsed = BuilderParser.Parse(source);
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "canvas", GlobalObjectId = "goid-canvas", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "panel", GlobalObjectId = "goid-panel", Kind = "GameObject", ParentLogicalId = "canvas" },
                },
            };

            // Canvas archetype: every rect field + base position/scale driven by Unity (D2); a
            // nonzero live Position/Scale must never leak into a scene op.
            var canvasRect = RectTransformFixtures.Rect(
                anchoredPos: new Vec2(640f, 360f), sizeDelta: new Vec2(1280f, 720f),
                anchorMin: Vec2.Zero, anchorMax: Vec2.Zero,
                position: new Vec3(640f, 360f, 0f), scale: Vec3.One,
                driven: ChannelMask.AllRectFields | ChannelMask.PositionX | ChannelMask.PositionY | ChannelMask.PositionZ
                    | ChannelMask.ScaleX | ChannelMask.ScaleY | ChannelMask.ScaleZ);

            SceneSnapshot Snapshot(Vec2 panelAnchoredPos, Vec3 panelDerivedPosition) => new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-canvas", Name = "Canvas", Transform = canvasRect,
                        Children = new[]
                        {
                            new SnapshotNode
                            {
                                GlobalObjectId = "goid-panel", Name = "Panel",
                                // D1: AnchoredPosition owns X/Y; Position is Unity-DERIVED and must
                                // never be independently diffed against pos:.
                                Transform = RectTransformFixtures.Rect(
                                    anchoredPos: panelAnchoredPos, sizeDelta: new Vec2(200f, 120f),
                                    anchorMin: new Vec2(1f, 1f), anchorMax: new Vec2(1f, 1f), pivot: new Vec2(1f, 1f),
                                    position: panelDerivedPosition),
                            },
                        },
                    },
                },
            };

            var settled = Snapshot(new Vec2(-10f, -10f), new Vec3(-110f, -70f, 0f));

            var settledReconcile = Reconciler.Reconcile(parsed.Model, settled, map, parsed.Anchors);
            Assert.Empty(settledReconcile.Patch.Edits);
            Assert.Empty(settledReconcile.Conflicts);
            Assert.Empty(Materializer.Materialize(parsed.Model, settled, map).Ops);

            var dragged = Snapshot(new Vec2(-40f, -25f), new Vec3(-140f, -85f, 0f));
            var recon1 = Reconciler.Reconcile(parsed.Model, dragged, map, parsed.Anchors);
            Assert.Empty(recon1.Conflicts);
            var edit = Assert.IsType<PatchArgument>(Assert.Single(recon1.Patch.Edits));
            Assert.Equal("anchoredPos", edit.ArgName);
            Assert.Equal("panel", edit.Anchor);

            var patched = SourcePatchApplier.Apply(source, recon1.Patch, parsed.Anchors);
            var reparsed = BuilderParser.Parse(patched, map);

            var recon2 = Reconciler.Reconcile(reparsed.Model, dragged, reparsed.IdentityMap, reparsed.Anchors);
            Assert.Empty(recon2.Patch.Edits);
            Assert.Empty(recon2.Conflicts);
            Assert.Empty(Materializer.Materialize(reparsed.Model, dragged, reparsed.IdentityMap).Ops);
        }

        [Fact]
        public void RectTransform_PromotedNode_FullRoundTrip_IsIdempotent()
        {
            const string source = @"
public class HudScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var icon = scene.Add(""Icon"");
    }
}
";
            var parsed = BuilderParser.Parse(source);
            var map = new IdentityMap
            {
                Entries = new[] { new IdentityMapEntry { LogicalId = "icon", GlobalObjectId = "goid-icon", Kind = "GameObject" } },
            };
            var live = RectTransformFixtures.Rect(anchoredPos: new Vec2(5f, 6f), sizeDelta: new Vec2(64f, 64f), position: new Vec3(5f, 6f, 0f));
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-icon", Name = "Icon", Transform = live } },
            };

            var recon1 = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors);
            Assert.Empty(recon1.Conflicts);
            Assert.Equal(5, recon1.Patch.Edits.Length);

            var patched = SourcePatchApplier.Apply(source, recon1.Patch, parsed.Anchors);
            var reparsed = BuilderParser.Parse(patched, map);
            var iconNode = Assert.Single(reparsed.Model.Roots, n => n.Name == "Icon");
            Assert.Equal("RectTransform", iconNode.Transform!.Kind);

            var recon2 = Reconciler.Reconcile(reparsed.Model, snapshot, reparsed.IdentityMap, reparsed.Anchors);
            Assert.Empty(recon2.Patch.Edits);
            Assert.Empty(recon2.Conflicts);
            Assert.Empty(Materializer.Materialize(reparsed.Model, snapshot, reparsed.IdentityMap).Ops);
        }

        [Fact]
        public void RectTransform_ArgumentRevertedToDefault_RewritesLiteral_KeepsCallAndReparses()
        {
            const string source = @"
public class HudScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var panel = scene.Add(""Panel"").RectTransform(anchoredPos: (50f, 0f), sizeDelta: (200f, 120f));
    }
}
";
            var parsed = BuilderParser.Parse(source);
            var map = new IdentityMap
            {
                Entries = new[] { new IdentityMapEntry { LogicalId = "panel", GlobalObjectId = "goid-panel", Kind = "GameObject" } },
            };
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-panel", Name = "Panel", Transform = RectTransformFixtures.Rect() } },
            };

            var recon = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors);
            Assert.Empty(recon.Conflicts);
            Assert.DoesNotContain(recon.Patch.Edits, e => e is not PatchArgument);

            var patched = SourcePatchApplier.Apply(source, recon.Patch, parsed.Anchors);
            Assert.Contains("anchoredPos: (0f, 0f)", patched);
            Assert.Contains("sizeDelta: (100f, 100f)", patched);
            Assert.Contains(".RectTransform(", patched);

            var reparsed = BuilderParser.Parse(patched, map);
            var panelNode = Assert.Single(reparsed.Model.Roots, n => n.Name == "Panel");
            Assert.Equal("RectTransform", panelNode.Transform!.Kind);
            Assert.Equal(0f, panelNode.Transform!.AnchoredPosition!.Value.X);
            Assert.Equal(100f, panelNode.Transform!.SizeDelta!.Value.X);

            var recon2 = Reconciler.Reconcile(reparsed.Model, snapshot, reparsed.IdentityMap, reparsed.Anchors);
            Assert.Empty(recon2.Patch.Edits);
            Assert.Empty(recon2.Conflicts);
            Assert.Empty(Materializer.Materialize(reparsed.Model, snapshot, reparsed.IdentityMap).Ops);
        }

        [Fact]
        public void RectTransform_AllArgumentsRevertedToDefault_KeepsRectTransformCall_KindPreserved()
        {
            const string source = @"
public class HudScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var panel = scene.Add(""Panel"").RectTransform(anchoredPos: (50f, 0f), sizeDelta: (200f, 120f), anchorMin: (1f, 1f), anchorMax: (1f, 1f), pivot: (1f, 1f));
    }
}
";
            var parsed = BuilderParser.Parse(source);
            var map = new IdentityMap
            {
                Entries = new[] { new IdentityMapEntry { LogicalId = "panel", GlobalObjectId = "goid-panel", Kind = "GameObject" } },
            };
            // Every field back at the RectTransform default: this is the anti-removal lock -- the
            // `.RectTransform(...)` call must SURVIVE even when every argument reverts, or Kind is
            // silently lost on the next parse.
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-panel", Name = "Panel", Transform = RectTransformFixtures.Rect() } },
            };

            var recon = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors);
            Assert.Empty(recon.Conflicts);
            Assert.NotEmpty(recon.Patch.Edits);
            Assert.All(recon.Patch.Edits, e => Assert.IsType<PatchArgument>(e));
            Assert.DoesNotContain(recon.Patch.Edits, e => e is RemoveFlagCall);

            var patched = SourcePatchApplier.Apply(source, recon.Patch, parsed.Anchors);
            Assert.Contains(".RectTransform(", patched);

            var reparsed = BuilderParser.Parse(patched, map);
            var panelNode = Assert.Single(reparsed.Model.Roots, n => n.Name == "Panel");
            Assert.Equal("RectTransform", panelNode.Transform!.Kind);
        }

        // With the prefab-asset BASELINE left unset (SourcePrefabTransform == null), the
        // divergence-narrowing rule has nothing to compare against, so this pins the UNKNOWN-baseline
        // fallback -- report every free field, regardless of value. Its premise is deliberate, not
        // accidental: a test that sets an equal baseline would legitimately silence this conflict
        // (see Reconcile_RectTransformOnPrefabInstanceRoot_LayoutMatchesPrefabAsset_NoConflictNoEdit).
        [Fact]
        public void Reconcile_RectTransformOnPrefabInstanceRoot_UnknownBaseline_YieldsLocatedConflict_AndNoUnappliableEdit()
        {
            const string source = @"
public class HudScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var panel = scene.Instance(""Assets/Prefabs/Panel.prefab"");
    }
}
";
            var parsed = BuilderParser.Parse(source);
            Assert.IsType<Model.PrefabInstanceNode>(parsed.Model.Roots[0]);

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry
                    {
                        LogicalId = "panel", GlobalObjectId = "goid-panel", Kind = "PrefabInstance",
                        SourcePrefabGuid = "guid-panel",
                    },
                },
                Assets = new[] { new AssetEntry { Guid = "guid-panel", LastKnownPath = "Assets/Prefabs/Panel.prefab" } },
            };
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-panel", Name = "Panel", SourcePrefabGuid = "guid-panel",
                        // SourcePrefabTransform deliberately left null -- unresolvable/unknown baseline.
                        Transform = RectTransformFixtures.Rect(anchoredPos: new Vec2(12f, 34f), sizeDelta: new Vec2(200f, 120f)),
                    },
                },
            };

            var result = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors);

            Assert.Empty(result.Patch.Edits.OfType<PatchArgument>());

            var conflict = Assert.Single(result.Conflicts);
            Assert.Equal(ConflictKind.UnlocalizableRectTransform, conflict.Kind);
            Assert.Equal("panel", conflict.LogicalId);
            Assert.Equal(parsed.Anchors["panel"], conflict.Location);
            Assert.Contains("panel", conflict.Reason);
            Assert.Contains("anchoredPos", conflict.Reason);
            Assert.Contains("RectTransform", conflict.Reason);

            var patched = SourcePatchApplier.Apply(source, result.Patch, parsed.Anchors);
            Assert.DoesNotContain(".RectTransform(", patched);
        }

        // A settled scene whose live layout already equals the prefab ASSET's own layout has nothing
        // unpersisted -- the asset itself is the layout's second persistence home, so this reports
        // zero Conflicts and zero edits, on every sync.
        [Fact]
        public void Reconcile_RectTransformOnPrefabInstanceRoot_LayoutMatchesPrefabAsset_NoConflictNoEdit()
        {
            const string source = @"
public class HudScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var panel = scene.Instance(""Assets/Prefabs/Panel.prefab"");
    }
}
";
            var parsed = BuilderParser.Parse(source);
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry
                    {
                        LogicalId = "panel", GlobalObjectId = "goid-panel", Kind = "PrefabInstance",
                        SourcePrefabGuid = "guid-panel",
                    },
                },
                Assets = new[] { new AssetEntry { Guid = "guid-panel", LastKnownPath = "Assets/Prefabs/Panel.prefab" } },
            };
            var baseline = RectTransformFixtures.Rect(anchoredPos: new Vec2(12f, 34f), sizeDelta: new Vec2(200f, 120f));
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-panel", Name = "Panel", SourcePrefabGuid = "guid-panel",
                        Transform = RectTransformFixtures.Rect(anchoredPos: new Vec2(12f, 34f), sizeDelta: new Vec2(200f, 120f)),
                        SourcePrefabTransform = baseline,
                    },
                },
            };

            var result = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors);
            Assert.Empty(result.Conflicts);
            Assert.Empty(result.Patch.Edits);

            // The "every sync" half of the finding: re-running on the same settled inputs must stay quiet.
            var again = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors);
            Assert.Empty(again.Conflicts);
            Assert.Empty(again.Patch.Edits);
        }

        // A genuine divergence from the prefab-asset baseline still reports, but ONLY the diverged
        // field(s) -- narrowing is the point (an unknown baseline names every free field; a known,
        // diverged baseline names only what actually differs).
        [Fact]
        public void Reconcile_RectTransformOnPrefabInstanceRoot_LayoutDivergesFromPrefabAsset_NamesOnlyDivergedFields()
        {
            const string source = @"
public class HudScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var panel = scene.Instance(""Assets/Prefabs/Panel.prefab"");
    }
}
";
            var parsed = BuilderParser.Parse(source);
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry
                    {
                        LogicalId = "panel", GlobalObjectId = "goid-panel", Kind = "PrefabInstance",
                        SourcePrefabGuid = "guid-panel",
                    },
                },
                Assets = new[] { new AssetEntry { Guid = "guid-panel", LastKnownPath = "Assets/Prefabs/Panel.prefab" } },
            };
            // Asset baseline sits at RectTransform defaults; only anchoredPos diverges on the live instance.
            var baseline = RectTransformFixtures.Rect();
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-panel", Name = "Panel", SourcePrefabGuid = "guid-panel",
                        Transform = RectTransformFixtures.Rect(anchoredPos: new Vec2(12f, 34f)),
                        SourcePrefabTransform = baseline,
                    },
                },
            };

            var result = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors);

            Assert.Empty(result.Patch.Edits.OfType<PatchArgument>());
            var conflict = Assert.Single(result.Conflicts);
            Assert.Equal(ConflictKind.UnlocalizableRectTransform, conflict.Kind);
            Assert.Contains("panel", conflict.Reason);
            Assert.Contains("anchoredPos", conflict.Reason);
            Assert.Contains("RectTransform", conflict.Reason);
            Assert.DoesNotContain("sizeDelta", conflict.Reason);
            Assert.DoesNotContain("pivot", conflict.Reason);
        }

        // A field diverging from the baseline ONLY on a driven axis is held to the baseline before
        // comparison (the same driven-axis rule the matched-node rect diff already applies), so it
        // never registers as a divergence.
        [Fact]
        public void Reconcile_RectTransformOnPrefabInstanceRoot_DivergenceOnDrivenAxisOnly_NoConflict()
        {
            const string source = @"
public class HudScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var panel = scene.Instance(""Assets/Prefabs/Panel.prefab"");
    }
}
";
            var parsed = BuilderParser.Parse(source);
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry
                    {
                        LogicalId = "panel", GlobalObjectId = "goid-panel", Kind = "PrefabInstance",
                        SourcePrefabGuid = "guid-panel",
                    },
                },
                Assets = new[] { new AssetEntry { Guid = "guid-panel", LastKnownPath = "Assets/Prefabs/Panel.prefab" } },
            };
            var baseline = RectTransformFixtures.Rect(sizeDelta: new Vec2(160f, 40f));
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-panel", Name = "Panel", SourcePrefabGuid = "guid-panel",
                        Transform = RectTransformFixtures.Rect(
                            sizeDelta: new Vec2(999f, 40f), driven: ChannelMask.SizeDeltaX),
                        SourcePrefabTransform = baseline,
                    },
                },
            };

            var result = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors);
            Assert.Empty(result.Conflicts);
        }

        [Fact]
        public void Reconcile_PrefabInstanceRoot_StillPatchesTransformArguments()
        {
            // No-over-reach lock: the rect-only guard must not swallow a genuine z/rotation drift on
            // the SAME prefab-instance root -- `InstanceHandle.Transform(...)` exists, so that path
            // stays untouched.
            const string source = @"
public class HudScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var panel = scene.Instance(""Assets/Prefabs/Panel.prefab"");
    }
}
";
            var parsed = BuilderParser.Parse(source);
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry
                    {
                        LogicalId = "panel", GlobalObjectId = "goid-panel", Kind = "PrefabInstance",
                        SourcePrefabGuid = "guid-panel",
                    },
                },
            };
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-panel", Name = "Panel", SourcePrefabGuid = "guid-panel",
                        Transform = new TransformData { Position = new Vec3(0f, 0f, 5f) },
                    },
                },
            };

            var result = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors);

            Assert.Empty(result.Conflicts);
            var edit = Assert.IsType<PatchArgument>(Assert.Single(result.Patch.Edits));
            Assert.Equal("panel", edit.Anchor);
            Assert.Equal("pos", edit.ArgName);
        }
    }
}
