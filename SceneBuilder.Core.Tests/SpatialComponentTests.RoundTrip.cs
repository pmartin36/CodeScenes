using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Materialize;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using SceneBuilder.Core.Serialization;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public partial class SpatialComponentTests
    {
        // ---- Created-node (§13) attach uses the dedicated call + FitSize-before-SurfaceSnap ----

        private const string EmptySpatialScene = @"
public class EmptySpatialScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
    }
}
";

        [Fact]
        public void Reconcile_CreatedNodeWithSurfaceSnap_AppendsSurfaceSnapCall_SecondSyncNoOp()
        {
            var parsed = BuilderParser.Parse(EmptySpatialScene);
            var map = new IdentityMap { Entries = System.Array.Empty<IdentityMapEntry>() };

            var snapperFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    SpatialComponents.SurfaceSnapFields.Vertical,
                    new ValueNode.Enum(SpatialComponents.SurfaceSnapEnums.VerticalTypeName, new[] { SpatialComponents.SurfaceSnapEnums.Down }, false)),
            });

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-crate",
                        Name = "Crate",
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "unused", Type = new TypeRef(SpatialComponents.SurfaceSnapTypeName), Fields = snapperFields },
                        },
                    },
                },
            };

            var pass1 = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors);
            Assert.Empty(pass1.Conflicts);

            var patched = SourcePatchApplier.Apply(EmptySpatialScene, pass1.Patch, parsed.Anchors);

            Assert.Contains(".SurfaceSnap(down: true)", patched);
            Assert.DoesNotContain(".Component<", patched);

            // ---- Pass 2: reparse the applied source; unchanged scene must converge (no edits). ----
            var reparsed = BuilderParser.Parse(patched);
            var reparsedMap = new IdentityMap { Entries = pass1.AddedEntries };

            var pass2 = Reconciler.Reconcile(reparsed.Model, snapshot, reparsedMap, reparsed.Anchors);

            Assert.Empty(pass2.Patch.Edits);
            Assert.Empty(pass2.Conflicts);
        }

        [Fact]
        public void Reconcile_SurfaceSnapAndFitSizeOnCreatedNode_AppendsInFitSizeThenSurfaceSnapOrder()
        {
            var parsed = BuilderParser.Parse(EmptySpatialScene);
            var map = new IdentityMap { Entries = System.Array.Empty<IdentityMapEntry>() };

            var sizerFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    SpatialComponents.FitSizeFields.Mode,
                    new ValueNode.Enum(SpatialComponents.FitSizeEnums.ModeTypeName, new[] { SpatialComponents.FitSizeEnums.Height }, false)),
                new KeyValuePair<string, ValueNode>(SpatialComponents.FitSizeFields.Value, ValueNode.Primitive.Float(2f)),
            });
            var snapperFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    SpatialComponents.SurfaceSnapFields.Vertical,
                    new ValueNode.Enum(SpatialComponents.SurfaceSnapEnums.VerticalTypeName, new[] { SpatialComponents.SurfaceSnapEnums.Down }, false)),
            });

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-crate",
                        Name = "Crate",
                        // Snapshot lists SurfaceSnap THEN FitSize — emit must still place FitSize first.
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "unused-snapper", Type = new TypeRef(SpatialComponents.SurfaceSnapTypeName), Fields = snapperFields },
                            new ComponentData { LogicalId = "unused-sizer", Type = new TypeRef(SpatialComponents.FitSizeTypeName), Fields = sizerFields },
                        },
                    },
                },
            };

            var pass1 = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors);
            Assert.Empty(pass1.Conflicts);

            var patched = SourcePatchApplier.Apply(EmptySpatialScene, pass1.Patch, parsed.Anchors);

            var sizerIndex = patched.IndexOf(".FitSize(", System.StringComparison.Ordinal);
            var snapperIndex = patched.IndexOf(".SurfaceSnap(", System.StringComparison.Ordinal);
            Assert.True(sizerIndex >= 0, "expected a .FitSize(...) statement in applied source");
            Assert.True(snapperIndex >= 0, "expected a .SurfaceSnap(...) statement in applied source");
            Assert.True(sizerIndex < snapperIndex, "FitSize statement must precede SurfaceSnap statement");

            // ---- Pass 2: reparse the applied source; unchanged scene must converge (no edits). ----
            var reparsed = BuilderParser.Parse(patched);
            var reparsedMap = new IdentityMap { Entries = pass1.AddedEntries };

            var pass2 = Reconciler.Reconcile(reparsed.Model, snapshot, reparsedMap, reparsed.Anchors);

            Assert.Empty(pass2.Patch.Edits);
            Assert.Empty(pass2.Conflicts);
        }

        // ---- Field-edit reconcile -> PatchComponentField on the dedicated call ----------

        [Fact]
        public void Reconcile_EditedFitSizeHeightInScene_PatchesFitSizeArgumentOnly()
        {
            var parsed = BuilderParser.Parse(FitSizeHeightSource);
            var crate = Assert.Single(parsed.Model.Roots);
            var sizer = Assert.Single(crate.Components);

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = crate.LogicalId, GlobalObjectId = "goid-crate", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = sizer.LogicalId,
                        GlobalObjectId = "",
                        Kind = "Component",
                        ComponentType = SpatialComponents.FitSizeTypeName,
                        ParentLogicalId = crate.LogicalId,
                    },
                },
            };

            // Non-terminating-decimal height so the dedicated renderer's rounded "2.3457f" is
            // distinguishable from the generic fallback's "2.34567f". Mode is UNCHANGED (still
            // Height) — only `value` differs, so this must diff/patch, not introduce.
            var editedFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    SpatialComponents.FitSizeFields.Mode,
                    new ValueNode.Enum(SpatialComponents.FitSizeEnums.ModeTypeName, new[] { SpatialComponents.FitSizeEnums.Height }, false)),
                new KeyValuePair<string, ValueNode>(SpatialComponents.FitSizeFields.Value, ValueNode.Primitive.Float(2.34567f)),
            });

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-crate",
                        Name = "Crate",
                        Transform = new TransformData { DrivenChannels = ChannelMask.Scale },
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "unused", Type = new TypeRef(SpatialComponents.FitSizeTypeName), Fields = editedFields },
                        },
                    },
                },
            };

            var result = Reconciler.Reconcile(
                parsed.Model,
                snapshot,
                map,
                parsed.Anchors,
                componentAnchors: parsed.ComponentAnchors,
                fieldArgumentSpans: parsed.FieldArgumentSpans);

            Assert.Empty(result.Conflicts);
            var edit = Assert.Single(result.Patch.Edits);
            var patch = Assert.IsType<PatchComponentField>(edit);
            Assert.Equal(sizer.LogicalId, patch.Anchor);
            Assert.Equal("2.3457f", patch.NewExpr);

            var applied = SourcePatchApplier.Apply(FitSizeHeightSource, result.Patch, parsed.Anchors);
            Assert.Contains(".FitSize(height: 2.3457f)", applied);
            Assert.DoesNotContain(".Transform(", applied);
            Assert.DoesNotContain(".Component<", applied);
        }

        [Fact]
        public void Reconcile_DrivenScaleAndPosition_NotEmittedToSource()
        {
            var sizerFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    SpatialComponents.FitSizeFields.Mode,
                    new ValueNode.Enum(SpatialComponents.FitSizeEnums.ModeTypeName, new[] { SpatialComponents.FitSizeEnums.Height }, false)),
                new KeyValuePair<string, ValueNode>(SpatialComponents.FitSizeFields.Value, ValueNode.Primitive.Float(2f)),
            });
            var snapperFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(SpatialComponents.SurfaceSnapFields.Down, ValueNode.Primitive.Bool(true)),
            });

            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new GameObjectNode
                    {
                        LogicalId = "sizer-1",
                        Name = "Crate",
                        Transform = new TransformData { DrivenChannels = ChannelMask.Scale },
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "sizer-1/" + SpatialComponents.FitSizeTypeName + "#0", Type = new TypeRef(SpatialComponents.FitSizeTypeName), Fields = sizerFields },
                        },
                    },
                    new GameObjectNode
                    {
                        LogicalId = "snapper-1",
                        Name = "Shelf",
                        Transform = new TransformData { DrivenChannels = ChannelMask.PositionY },
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "snapper-1/" + SpatialComponents.SurfaceSnapTypeName + "#0", Type = new TypeRef(SpatialComponents.SurfaceSnapTypeName), Fields = snapperFields },
                        },
                    },
                },
            };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "sizer-1", GlobalObjectId = "goid-sizer", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "sizer-1/" + SpatialComponents.FitSizeTypeName + "#0", GlobalObjectId = "", Kind = "Component", ComponentType = SpatialComponents.FitSizeTypeName, ParentLogicalId = "sizer-1" },
                    new IdentityMapEntry { LogicalId = "snapper-1", GlobalObjectId = "goid-snapper", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "snapper-1/" + SpatialComponents.SurfaceSnapTypeName + "#0", GlobalObjectId = "", Kind = "Component", ComponentType = SpatialComponents.SurfaceSnapTypeName, ParentLogicalId = "snapper-1" },
                },
            };

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-sizer",
                        Name = "Crate",
                        // Fully driven scale drift (a solved geometry scale far from the model's
                        // untouched default) must never reach the source as a `.Transform(scale:)`
                        // patch or leak into any field.
                        Transform = new TransformData { DrivenChannels = ChannelMask.Scale, Scale = new Vec3(4f, 2f, 1f) },
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "unused-sizer", Type = new TypeRef(SpatialComponents.FitSizeTypeName), Fields = sizerFields },
                        },
                    },
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-snapper",
                        Name = "Shelf",
                        // Driven position-Y drift from the snap solve.
                        Transform = new TransformData { DrivenChannels = ChannelMask.PositionY, Position = new Vec3(0f, 7f, 0f) },
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "unused-snapper", Type = new TypeRef(SpatialComponents.SurfaceSnapTypeName), Fields = snapperFields },
                        },
                    },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            Assert.Empty(result.Patch.Edits);
            Assert.Empty(result.Conflicts);
        }

        // Rebuilds the component from Fields[vertical]=Enum(Down), asserting the free axis is
        // patched and the driven Y axis is not.
        [Fact]
        public void Reconcile_SurfaceSnapDownFreeAxisDrag_PatchesFreeAxisNotDrivenY()
        {
            var snapperFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    SpatialComponents.SurfaceSnapFields.Vertical,
                    new ValueNode.Enum(SpatialComponents.SurfaceSnapEnums.VerticalTypeName, new[] { SpatialComponents.SurfaceSnapEnums.Down }, false)),
            });

            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new GameObjectNode
                    {
                        LogicalId = "crate-1",
                        Name = "Crate",
                        Transform = new TransformData { DrivenChannels = ChannelMask.PositionY },
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "crate-1/" + SpatialComponents.SurfaceSnapTypeName + "#0", Type = new TypeRef(SpatialComponents.SurfaceSnapTypeName), Fields = snapperFields },
                        },
                    },
                },
            };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "crate-1", GlobalObjectId = "goid-crate", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "crate-1/" + SpatialComponents.SurfaceSnapTypeName + "#0", GlobalObjectId = "", Kind = "Component", ComponentType = SpatialComponents.SurfaceSnapTypeName, ParentLogicalId = "crate-1" },
                },
            };

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-crate",
                        Name = "Crate",
                        // X is a FREE axis dragged by hand; Y is DRIVEN by the snap solve and must
                        // never leak into the emitted `pos:` patch even though the SAME transform
                        // op fires (because the free X axis genuinely changed).
                        Transform = new TransformData { DrivenChannels = ChannelMask.PositionY, Position = new Vec3(5f, 3f, 0f) },
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "unused", Type = new TypeRef(SpatialComponents.SurfaceSnapTypeName), Fields = snapperFields },
                        },
                    },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            Assert.Empty(result.Conflicts);
            var pos = Assert.Single(result.Patch.Edits.OfType<PatchArgument>(), e => e.ArgName == "pos");
            Assert.Equal("(5f, 0f, 0f)", pos.NewExpr);
        }

        // Scope-FINAL regression #2: re-authoring an EXISTING SurfaceSnap axis in-scene (flip the
        // Vertical member Down -> Up, e.g. via the inspector dropdown) is a field-VALUE diff
        // whose keyword itself carries the member — so the patch must replace the WHOLE `down: true`
        // argument with the authoring form `up: true`, NEVER splice the enum literal
        // `SceneBuilder.Authoring.SurfaceSnap+Vertical.Up` into the `down:` slot (invalid C# — the
        // '+' nested-type separator — violating "Generated C# must compile").
        [Fact]
        public void Reconcile_SurfaceSnapAxisMemberFlip_DownToUp_EmitsUpTrue_NotEnumLiteral()
        {
            var parsed = BuilderParser.Parse(SurfaceSnapDownOnlySource);
            var crate = Assert.Single(parsed.Model.Roots);
            var snapper = Assert.Single(crate.Components);

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = crate.LogicalId, GlobalObjectId = "goid-crate", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = snapper.LogicalId,
                        GlobalObjectId = "",
                        Kind = "Component",
                        ComponentType = SpatialComponents.SurfaceSnapTypeName,
                        ParentLogicalId = crate.LogicalId,
                    },
                },
            };

            var editedFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    SpatialComponents.SurfaceSnapFields.Vertical,
                    new ValueNode.Enum(SpatialComponents.SurfaceSnapEnums.VerticalTypeName, new[] { SpatialComponents.SurfaceSnapEnums.Up }, false)),
            });

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-crate",
                        Name = "Crate",
                        Transform = new TransformData { DrivenChannels = ChannelMask.PositionY },
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "unused", Type = new TypeRef(SpatialComponents.SurfaceSnapTypeName), Fields = editedFields },
                        },
                    },
                },
            };

            var result = Reconciler.Reconcile(
                parsed.Model,
                snapshot,
                map,
                parsed.Anchors,
                componentAnchors: parsed.ComponentAnchors,
                fieldArgumentSpans: parsed.FieldArgumentSpans);

            Assert.Empty(result.Conflicts);
            var patch = Assert.IsType<PatchComponentField>(Assert.Single(result.Patch.Edits));
            Assert.Equal("up: true", patch.NewExpr);

            var applied = SourcePatchApplier.Apply(SurfaceSnapDownOnlySource, result.Patch, parsed.Anchors);
            Assert.Contains(".SurfaceSnap(up: true)", applied);
            Assert.DoesNotContain("Vertical", applied);
            Assert.DoesNotContain("SurfaceSnap+", applied);
        }

        // An authored `.Transform(scale:)` co-existing with `.FitSize` on the
        // same node must never re-emit the driven scale as a `.Transform(scale:)` patch, even when
        // the scene-write side does not suppress the channel: the code-emit direction exercised
        // here leaves the driven scale out regardless.
        [Fact]
        public void Reconcile_AuthoredTransformScaleAndFitSize_DrivenScaleNotReEmitted()
        {
            var sizerFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    SpatialComponents.FitSizeFields.Mode,
                    new ValueNode.Enum(SpatialComponents.FitSizeEnums.ModeTypeName, new[] { SpatialComponents.FitSizeEnums.Height }, false)),
                new KeyValuePair<string, ValueNode>(SpatialComponents.FitSizeFields.Value, ValueNode.Primitive.Float(2f)),
            });

            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new GameObjectNode
                    {
                        LogicalId = "sizer-1",
                        Name = "Crate",
                        Transform = new TransformData { Scale = new Vec3(2, 2, 2), DrivenChannels = ChannelMask.Scale },
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "sizer-1/" + SpatialComponents.FitSizeTypeName + "#0", Type = new TypeRef(SpatialComponents.FitSizeTypeName), Fields = sizerFields },
                        },
                    },
                },
            };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "sizer-1", GlobalObjectId = "goid-sizer", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "sizer-1/" + SpatialComponents.FitSizeTypeName + "#0", GlobalObjectId = "", Kind = "Component", ComponentType = SpatialComponents.FitSizeTypeName, ParentLogicalId = "sizer-1" },
                },
            };

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-sizer",
                        Name = "Crate",
                        // Solved geometry scale far from the authored (2,2,2) — must never reach
                        // source as a `.Transform(scale:)` patch.
                        Transform = new TransformData { DrivenChannels = ChannelMask.Scale, Scale = new Vec3(4f, 4f, 4f) },
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "unused-sizer", Type = new TypeRef(SpatialComponents.FitSizeTypeName), Fields = sizerFields },
                        },
                    },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            Assert.Empty(result.Patch.Edits.OfType<PatchArgument>().Where(e => e.ArgName == "scale"));
            Assert.Empty(result.Conflicts);
        }

        [Fact]
        public void RoundTrip_FitSizeSurfaceSnap_Idempotent()
        {
            const string source = @"
public class FitSizeSurfaceSnapRoundTripScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var floor = scene.Add(""Floor"");
        var crate = scene.Add(""Crate"");
        crate.FitSize(height: 2f);
        crate.SurfaceSnap(down: true, target: floor);
    }
}
";

            var parsed = BuilderParser.Parse(source);
            var floor = Assert.Single(parsed.Model.Roots, r => r.Name == "Floor");
            var crate = Assert.Single(parsed.Model.Roots, r => r.Name == "Crate");
            var sizer = Assert.Single(crate.Components, c => c.Type.FullName == SpatialComponents.FitSizeTypeName);
            var snapper = Assert.Single(crate.Components, c => c.Type.FullName == SpatialComponents.SurfaceSnapTypeName);

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = floor.LogicalId, GlobalObjectId = "goid-floor", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = crate.LogicalId, GlobalObjectId = "goid-crate", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = sizer.LogicalId, GlobalObjectId = "", Kind = "Component", ComponentType = SpatialComponents.FitSizeTypeName, ParentLogicalId = crate.LogicalId },
                    new IdentityMapEntry { LogicalId = snapper.LogicalId, GlobalObjectId = "", Kind = "Component", ComponentType = SpatialComponents.SurfaceSnapTypeName, ParentLogicalId = crate.LogicalId },
                },
            };

            var editedFitSizeFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    SpatialComponents.FitSizeFields.Mode,
                    new ValueNode.Enum(SpatialComponents.FitSizeEnums.ModeTypeName, new[] { SpatialComponents.FitSizeEnums.Height }, false)),
                new KeyValuePair<string, ValueNode>(SpatialComponents.FitSizeFields.Value, ValueNode.Primitive.Float(3f)),
            });
            var snapperFieldsUnchanged = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    SpatialComponents.SurfaceSnapFields.Vertical,
                    new ValueNode.Enum(SpatialComponents.SurfaceSnapEnums.VerticalTypeName, new[] { SpatialComponents.SurfaceSnapEnums.Down }, false)),
                new KeyValuePair<string, ValueNode>(SpatialComponents.SurfaceSnapFields.Target, new ValueNode.ObjectRef(floor.LogicalId)),
            });

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode { GlobalObjectId = "goid-floor", Name = "Floor" },
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-crate",
                        Name = "Crate",
                        Transform = new TransformData { DrivenChannels = ChannelMask.Scale | ChannelMask.PositionY },
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "unused-sizer", Type = new TypeRef(SpatialComponents.FitSizeTypeName), Fields = editedFitSizeFields },
                            new ComponentData { LogicalId = "unused-snapper", Type = new TypeRef(SpatialComponents.SurfaceSnapTypeName), Fields = snapperFieldsUnchanged },
                        },
                    },
                },
            };

            var pass1 = Reconciler.Reconcile(
                parsed.Model,
                snapshot,
                map,
                parsed.Anchors,
                componentAnchors: parsed.ComponentAnchors,
                fieldArgumentSpans: parsed.FieldArgumentSpans);

            Assert.NotEmpty(pass1.Patch.Edits);
            Assert.Empty(pass1.Conflicts);

            var patched = SourcePatchApplier.Apply(source, pass1.Patch, parsed.Anchors);

            var reparsed = BuilderParser.Parse(patched, map);

            var pass2 = Reconciler.Reconcile(
                reparsed.Model,
                snapshot,
                reparsed.IdentityMap,
                reparsed.Anchors,
                componentAnchors: reparsed.ComponentAnchors,
                fieldArgumentSpans: reparsed.FieldArgumentSpans);

            Assert.Empty(pass2.Patch.Edits);
            Assert.Empty(pass2.Conflicts);

            var plan = Materializer.Materialize(reparsed.Model, snapshot, reparsed.IdentityMap);
            Assert.Empty(plan.Ops);
        }
    }
}
