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
    public class SpatialComponentTests
    {
        [Fact]
        public void ChannelMask_Scale_EqualsXYZScaleBits()
        {
            Assert.Equal(
                ChannelMask.ScaleX | ChannelMask.ScaleY | ChannelMask.ScaleZ,
                ChannelMask.Scale);
        }

        [Fact]
        public void ChannelMask_AxisFlags_AreDistinctSingleBitsAndNoneIsZero()
        {
            Assert.Equal(ChannelMask.None, (ChannelMask)0);

            var flags = new HashSet<ChannelMask>
            {
                ChannelMask.PositionX,
                ChannelMask.PositionY,
                ChannelMask.PositionZ,
                ChannelMask.ScaleX,
                ChannelMask.ScaleY,
                ChannelMask.ScaleZ,
            };

            Assert.Equal(6, flags.Count);
        }

        [Fact]
        public void TransformData_Default_DrivenChannelsNone()
        {
            var transform = new TransformData();

            Assert.Equal(ChannelMask.None, transform.DrivenChannels);
        }

        [Fact]
        public void TransformData_Serialize_OmitsDrivenChannels()
        {
            var driven = new TransformData { DrivenChannels = ChannelMask.Scale, Position = new Vec3(1, 2, 3) };
            var plain = driven with { DrivenChannels = ChannelMask.None };

            var drivenJson = CanonicalJson.Serialize(driven);
            var plainJson = CanonicalJson.Serialize(plain);

            Assert.DoesNotContain("drivenChannels", drivenJson);
            Assert.Equal(plainJson, drivenJson);
        }

        [Fact]
        public void SpatialComponents_TypeNames_MatchRuntimeFqns()
        {
            Assert.Equal("SceneBuilder.Authoring.FitSize", SpatialComponents.FitSizeTypeName);
            Assert.Equal("SceneBuilder.Authoring.SurfaceSnap", SpatialComponents.SurfaceSnapTypeName);
        }

        [Fact]
        public void SpatialComponents_FitSizeFieldKeys_MatchExpectedLiterals()
        {
            Assert.Equal("width", SpatialComponents.FitSizeFields.Width);
            Assert.Equal("height", SpatialComponents.FitSizeFields.Height);
            Assert.Equal("depth", SpatialComponents.FitSizeFields.Depth);
            Assert.Equal("size", SpatialComponents.FitSizeFields.Size);
        }

        [Fact]
        public void SpatialComponents_SurfaceSnapFieldKeys_MatchExpectedLiterals()
        {
            Assert.Equal("up", SpatialComponents.SurfaceSnapFields.Up);
            Assert.Equal("down", SpatialComponents.SurfaceSnapFields.Down);
            Assert.Equal("left", SpatialComponents.SurfaceSnapFields.Left);
            Assert.Equal("right", SpatialComponents.SurfaceSnapFields.Right);
            Assert.Equal("forward", SpatialComponents.SurfaceSnapFields.Forward);
            Assert.Equal("back", SpatialComponents.SurfaceSnapFields.Back);
            Assert.Equal("target", SpatialComponents.SurfaceSnapFields.Target);
        }

        // ---- b2-t1: .FitSize(...) parse arm ------------------------------------------------

        private const string FitSizeHeightSource = @"
public class FitSizeHeightScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").FitSize(height: 2f);
    }
}
";

        private const string FitSizeExplicitSizeSource = @"
public class FitSizeExplicitSizeScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").FitSize(size: (2f, 1f, 0.5f));
    }
}
";

        private const string FitSizeAspectAndExplicitSource = @"
public class FitSizeBothScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").FitSize(height: 2f, size: (1f, 1f, 1f));
    }
}
";

        private const string FitSizeNoDimensionSource = @"
public class FitSizeNoneScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").FitSize();
    }
}
";

        private const string TransformOnlyNodeSource = @"
public class TransformOnlyScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Plain"").Transform(pos: (1f, 2f, 3f));
    }
}
";

        [Fact]
        public void Parse_FitSizeHeight_YieldsFitSizeComponentAndDrivenScale()
        {
            var result = BuilderParser.Parse(FitSizeHeightSource);

            var node = Assert.Single(result.Model.Roots);
            var component = Assert.Single(node.Components);

            Assert.Equal(SpatialComponents.FitSizeTypeName, component.Type.FullName);
            Assert.Equal(
                ValueNode.Primitive.Float(2f),
                component.Fields[SpatialComponents.FitSizeFields.Height]);
            Assert.Equal(ChannelMask.Scale, node.Transform.DrivenChannels);
        }

        [Fact]
        public void Parse_FitSizeExplicitSize_YieldsPerAxisFieldsAndDrivenScale()
        {
            var result = BuilderParser.Parse(FitSizeExplicitSizeSource);

            var node = Assert.Single(result.Model.Roots);
            var component = Assert.Single(node.Components);

            Assert.Equal(SpatialComponents.FitSizeTypeName, component.Type.FullName);
            Assert.Equal(
                new ValueNode.Vec3(new Vec3(2f, 1f, 0.5f)),
                component.Fields[SpatialComponents.FitSizeFields.Size]);
            Assert.Equal(ChannelMask.Scale, node.Transform.DrivenChannels);
        }

        [Fact]
        public void Parse_FitSizeAspectAndExplicitTogether_YieldsLocatedError()
        {
            var ex = Assert.Throws<ParseException>(() => BuilderParser.Parse(FitSizeAspectAndExplicitSource));

            Assert.True(ex.Line > 0);
            Assert.Contains("combine", ex.Message);
        }

        [Fact]
        public void Parse_FitSizeNoDimension_YieldsLocatedError()
        {
            var ex = Assert.Throws<ParseException>(() => BuilderParser.Parse(FitSizeNoDimensionSource));

            Assert.True(ex.Line > 0);
            Assert.Contains("requires", ex.Message);
        }

        [Fact]
        public void Parse_NodeWithoutSpatialComponent_DrivenChannelsNone()
        {
            var result = BuilderParser.Parse(TransformOnlyNodeSource);

            var node = Assert.Single(result.Model.Roots);
            Assert.Empty(node.Components);
            Assert.Equal(ChannelMask.None, node.Transform.DrivenChannels);
        }

        // ---- b2-t2: .SurfaceSnap(...) parse arm ----------------------------------------------

        private const string SurfaceSnapDownLeftSource = @"
public class SurfaceSnapDownLeftScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").SurfaceSnap(down: true, left: true);
    }
}
";

        private const string SurfaceSnapDownOnlySource = @"
public class SurfaceSnapDownOnlyScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").SurfaceSnap(down: true);
    }
}
";

        private const string SurfaceSnapBackOnlySource = @"
public class SurfaceSnapBackOnlyScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").SurfaceSnap(back: true);
    }
}
";

        private const string SurfaceSnapDownBackSource = @"
public class SurfaceSnapDownBackScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").SurfaceSnap(down: true, back: true);
    }
}
";

        private const string SurfaceSnapLeftRightSource = @"
public class SurfaceSnapLeftRightScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").SurfaceSnap(left: true, right: true);
    }
}
";

        private const string SurfaceSnapUpDownSource = @"
public class SurfaceSnapUpDownScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").SurfaceSnap(up: true, down: true);
    }
}
";

        private const string SurfaceSnapForwardBackSource = @"
public class SurfaceSnapForwardBackScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").SurfaceSnap(forward: true, back: true);
    }
}
";

        private const string SurfaceSnapWithTargetSource = @"
public class SurfaceSnapTargetScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var floor = scene.Add(""Floor"");
        scene.Add(""Crate"").SurfaceSnap(down: true, target: floor);
    }
}
";

        [Fact]
        public void Parse_SurfaceSnapDownLeft_SetsFlagsAndDrivenPositionXY()
        {
            var result = BuilderParser.Parse(SurfaceSnapDownLeftSource);

            var node = Assert.Single(result.Model.Roots);
            var component = Assert.Single(node.Components);

            Assert.Equal(SpatialComponents.SurfaceSnapTypeName, component.Type.FullName);
            Assert.Equal(ValueNode.Primitive.Bool(true), component.Fields[SpatialComponents.SurfaceSnapFields.Down]);
            Assert.Equal(ValueNode.Primitive.Bool(true), component.Fields[SpatialComponents.SurfaceSnapFields.Left]);
            Assert.False(component.Fields.ContainsKey(SpatialComponents.SurfaceSnapFields.Up));
            Assert.False(component.Fields.ContainsKey(SpatialComponents.SurfaceSnapFields.Right));
            Assert.False(component.Fields.ContainsKey(SpatialComponents.SurfaceSnapFields.Forward));
            Assert.False(component.Fields.ContainsKey(SpatialComponents.SurfaceSnapFields.Back));
            Assert.Equal(ChannelMask.PositionX | ChannelMask.PositionY, node.Transform.DrivenChannels);
        }

        [Fact]
        public void Parse_SurfaceSnapDownOnly_DrivesPositionYNotX()
        {
            var result = BuilderParser.Parse(SurfaceSnapDownOnlySource);

            var node = Assert.Single(result.Model.Roots);
            var component = Assert.Single(node.Components);

            Assert.Single(component.Fields);
            Assert.Equal(ValueNode.Primitive.Bool(true), component.Fields[SpatialComponents.SurfaceSnapFields.Down]);
            Assert.Equal(ChannelMask.PositionY, node.Transform.DrivenChannels);
        }

        [Theory]
        [InlineData(SurfaceSnapLeftRightSource)]
        [InlineData(SurfaceSnapUpDownSource)]
        [InlineData(SurfaceSnapForwardBackSource)]
        public void Parse_SurfaceSnapContradictoryAxis_YieldsLocatedError(string source)
        {
            var ex = Assert.Throws<ParseException>(() => BuilderParser.Parse(source));

            Assert.True(ex.Line > 0);
            Assert.Contains("combine", ex.Message);
        }

        [Fact]
        public void Parse_SurfaceSnapWithTarget_CarriesObjectRefToHandleLogicalId()
        {
            var result = BuilderParser.Parse(SurfaceSnapWithTargetSource);

            var floor = Assert.Single(result.Model.Roots, r => r.Name == "Floor");
            var crate = Assert.Single(result.Model.Roots, r => r.Name == "Crate");
            var component = Assert.Single(crate.Components);

            var target = Assert.IsType<ValueNode.ObjectRef>(component.Fields[SpatialComponents.SurfaceSnapFields.Target]);
            Assert.Equal(floor.LogicalId, target.TargetLogicalId);
            Assert.Equal(ChannelMask.PositionY, crate.Transform.DrivenChannels & ChannelMask.PositionY);
        }

        [Fact]
        public void Parse_SurfaceSnapBack_DrivesPositionZ()
        {
            var result = BuilderParser.Parse(SurfaceSnapBackOnlySource);

            var node = Assert.Single(result.Model.Roots);
            var component = Assert.Single(node.Components);

            Assert.Single(component.Fields);
            Assert.Equal(ValueNode.Primitive.Bool(true), component.Fields[SpatialComponents.SurfaceSnapFields.Back]);
            Assert.Equal(ChannelMask.PositionZ, node.Transform.DrivenChannels);
        }

        [Fact]
        public void Parse_SurfaceSnapDownBack_DrivesPositionYAndZ()
        {
            var result = BuilderParser.Parse(SurfaceSnapDownBackSource);

            var node = Assert.Single(result.Model.Roots);

            Assert.Equal(ChannelMask.PositionY | ChannelMask.PositionZ, node.Transform.DrivenChannels);
        }

        // ---- b4-t1: dedicated .FitSize(...)/.SurfaceSnap(...) emit --------------------------------

        [Fact]
        public void Emit_FitSize_EmitsDedicatedCallNotGenericComponent()
        {
            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(SpatialComponents.FitSizeFields.Height, ValueNode.Primitive.Float(2f)),
            });

            var text = SpatialComponentSource.RenderStatement("crate", SpatialComponents.FitSizeTypeName, fields, null);

            Assert.Equal("crate.FitSize(height: 2f);", text);
            Assert.DoesNotContain(".Component<", text);
        }

        [Fact]
        public void Emit_FitSizeExplicit_EmitsSizeVectorFSuffixed()
        {
            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(SpatialComponents.FitSizeFields.Size, new ValueNode.Vec3(new Vec3(2f, 1f, 0.5f))),
            });

            var text = SpatialComponentSource.RenderStatement("crate", SpatialComponents.FitSizeTypeName, fields, null);

            Assert.Equal("crate.FitSize(size: (2f, 1f, 0.5f));", text);
        }

        [Fact]
        public void Emit_SurfaceSnap_EmitsOnlySetFlags()
        {
            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(SpatialComponents.SurfaceSnapFields.Down, ValueNode.Primitive.Bool(true)),
                new KeyValuePair<string, ValueNode>(SpatialComponents.SurfaceSnapFields.Left, ValueNode.Primitive.Bool(true)),
            });

            var text = SpatialComponentSource.RenderStatement("crate", SpatialComponents.SurfaceSnapTypeName, fields, null);

            Assert.Equal("crate.SurfaceSnap(down: true, left: true);", text);
            Assert.DoesNotContain("up:", text);
            Assert.DoesNotContain("right:", text);
            Assert.DoesNotContain("forward:", text);
            Assert.DoesNotContain("back:", text);
        }

        [Fact]
        public void Emit_FitSizeBeforeSurfaceSnap_OrderingDeterministicAndStable()
        {
            var sizer = new ComponentData { LogicalId = "x/FitSize#0", Type = new TypeRef(SpatialComponents.FitSizeTypeName), Fields = FieldMap.Empty };
            var snapper = new ComponentData { LogicalId = "x/SurfaceSnap#0", Type = new TypeRef(SpatialComponents.SurfaceSnapTypeName), Fields = FieldMap.Empty };

            var ordered = SpatialComponentSource.OrderForEmit(new[] { snapper, sizer });

            Assert.Equal(SpatialComponents.FitSizeTypeName, ordered[0].Type.FullName);
            Assert.Equal(SpatialComponents.SurfaceSnapTypeName, ordered[1].Type.FullName);

            var reordered = SpatialComponentSource.OrderForEmit(ordered);
            Assert.Equal(ordered, reordered);
        }

        [Fact]
        public void Parse_Emit_SpatialCalls_TextRoundTripsIdentically()
        {
            var heightResult = BuilderParser.Parse(FitSizeHeightSource);
            var heightComponent = Assert.Single(Assert.Single(heightResult.Model.Roots).Components);
            Assert.Equal(
                "x.FitSize(height: 2f);",
                SpatialComponentSource.RenderStatement("x", heightComponent.Type.FullName, heightComponent.Fields, null));

            var sizeResult = BuilderParser.Parse(FitSizeExplicitSizeSource);
            var sizeComponent = Assert.Single(Assert.Single(sizeResult.Model.Roots).Components);
            Assert.Equal(
                "x.FitSize(size: (2f, 1f, 0.5f));",
                SpatialComponentSource.RenderStatement("x", sizeComponent.Type.FullName, sizeComponent.Fields, null));

            var snapperResult = BuilderParser.Parse(SurfaceSnapDownBackSource);
            var snapperComponent = Assert.Single(Assert.Single(snapperResult.Model.Roots).Components);
            Assert.Equal(
                "x.SurfaceSnap(down: true, back: true);",
                SpatialComponentSource.RenderStatement("x", snapperComponent.Type.FullName, snapperComponent.Fields, null));
        }

        // ---- b4-t1: created-node (§13) attach uses the dedicated call + FitSize-before-SurfaceSnap ----

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
                new KeyValuePair<string, ValueNode>(SpatialComponents.SurfaceSnapFields.Down, ValueNode.Primitive.Bool(true)),
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
                new KeyValuePair<string, ValueNode>(SpatialComponents.FitSizeFields.Height, ValueNode.Primitive.Float(2f)),
            });
            var snapperFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(SpatialComponents.SurfaceSnapFields.Down, ValueNode.Primitive.Bool(true)),
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

        // ---- b4-t2: field-edit reconcile -> PatchComponentField on the dedicated call ----------

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

            // A non-terminating-decimal height (not a round test-author's number) so the
            // dedicated renderer's 4dp-rounded SourceExpr.Float ("2.3457f") is DISTINGUISHABLE
            // from the generic ValueNodeLiteral fallback's unrounded literal ("2.34567f") — a
            // round value would happen to format identically under both paths and prove nothing.
            var editedFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(SpatialComponents.FitSizeFields.Height, ValueNode.Primitive.Float(2.34567f)),
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
                new KeyValuePair<string, ValueNode>(SpatialComponents.FitSizeFields.Height, ValueNode.Primitive.Float(2f)),
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

        [Fact]
        public void Reconcile_SurfaceSnapDownFreeAxisDrag_PatchesFreeAxisNotDrivenY()
        {
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

        // b1-t1 / Resolved #1: an authored `.Transform(scale:)` co-existing with `.FitSize` on the
        // same node must never re-emit the driven scale as a `.Transform(scale:)` patch, even when
        // the scene-write side no longer suppresses the channel (this task removes that suppression
        // on the WRITE side only — the code-emit direction, exercised here, is untouched).
        [Fact]
        public void Reconcile_AuthoredTransformScaleAndFitSize_DrivenScaleNotReEmitted()
        {
            var sizerFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(SpatialComponents.FitSizeFields.Height, ValueNode.Primitive.Float(2f)),
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
                new KeyValuePair<string, ValueNode>(SpatialComponents.FitSizeFields.Height, ValueNode.Primitive.Float(3f)),
            });
            var snapperFieldsUnchanged = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(SpatialComponents.SurfaceSnapFields.Down, ValueNode.Primitive.Bool(true)),
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
