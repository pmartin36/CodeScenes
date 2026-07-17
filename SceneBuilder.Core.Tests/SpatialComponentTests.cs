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
            Assert.Equal("SceneBuilder.Authoring.Sizer", SpatialComponents.SizerTypeName);
            Assert.Equal("SceneBuilder.Authoring.Snapper", SpatialComponents.SnapperTypeName);
        }

        [Fact]
        public void SpatialComponents_SizerFieldKeys_MatchExpectedLiterals()
        {
            Assert.Equal("width", SpatialComponents.SizerFields.Width);
            Assert.Equal("height", SpatialComponents.SizerFields.Height);
            Assert.Equal("depth", SpatialComponents.SizerFields.Depth);
            Assert.Equal("size", SpatialComponents.SizerFields.Size);
        }

        [Fact]
        public void SpatialComponents_SnapperFieldKeys_MatchExpectedLiterals()
        {
            Assert.Equal("up", SpatialComponents.SnapperFields.Up);
            Assert.Equal("down", SpatialComponents.SnapperFields.Down);
            Assert.Equal("left", SpatialComponents.SnapperFields.Left);
            Assert.Equal("right", SpatialComponents.SnapperFields.Right);
            Assert.Equal("forward", SpatialComponents.SnapperFields.Forward);
            Assert.Equal("back", SpatialComponents.SnapperFields.Back);
            Assert.Equal("target", SpatialComponents.SnapperFields.Target);
        }

        // ---- b2-t1: .Sizer(...) parse arm ------------------------------------------------

        private const string SizerHeightSource = @"
public class SizerHeightScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").Sizer(height: 2f);
    }
}
";

        private const string SizerExplicitSizeSource = @"
public class SizerExplicitSizeScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").Sizer(size: (2f, 1f, 0.5f));
    }
}
";

        private const string SizerAspectAndExplicitSource = @"
public class SizerBothScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").Sizer(height: 2f, size: (1f, 1f, 1f));
    }
}
";

        private const string SizerNoDimensionSource = @"
public class SizerNoneScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").Sizer();
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
        public void Parse_SizerHeight_YieldsSizerComponentAndDrivenScale()
        {
            var result = BuilderParser.Parse(SizerHeightSource);

            var node = Assert.Single(result.Model.Roots);
            var component = Assert.Single(node.Components);

            Assert.Equal(SpatialComponents.SizerTypeName, component.Type.FullName);
            Assert.Equal(
                ValueNode.Primitive.Float(2f),
                component.Fields[SpatialComponents.SizerFields.Height]);
            Assert.Equal(ChannelMask.Scale, node.Transform.DrivenChannels);
        }

        [Fact]
        public void Parse_SizerExplicitSize_YieldsPerAxisFieldsAndDrivenScale()
        {
            var result = BuilderParser.Parse(SizerExplicitSizeSource);

            var node = Assert.Single(result.Model.Roots);
            var component = Assert.Single(node.Components);

            Assert.Equal(SpatialComponents.SizerTypeName, component.Type.FullName);
            Assert.Equal(
                new ValueNode.Vec3(new Vec3(2f, 1f, 0.5f)),
                component.Fields[SpatialComponents.SizerFields.Size]);
            Assert.Equal(ChannelMask.Scale, node.Transform.DrivenChannels);
        }

        [Fact]
        public void Parse_SizerAspectAndExplicitTogether_YieldsLocatedError()
        {
            var ex = Assert.Throws<ParseException>(() => BuilderParser.Parse(SizerAspectAndExplicitSource));

            Assert.True(ex.Line > 0);
            Assert.Contains("combine", ex.Message);
        }

        [Fact]
        public void Parse_SizerNoDimension_YieldsLocatedError()
        {
            var ex = Assert.Throws<ParseException>(() => BuilderParser.Parse(SizerNoDimensionSource));

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

        // ---- b2-t2: .Snapper(...) parse arm ----------------------------------------------

        private const string SnapperDownLeftSource = @"
public class SnapperDownLeftScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").Snapper(down: true, left: true);
    }
}
";

        private const string SnapperDownOnlySource = @"
public class SnapperDownOnlyScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").Snapper(down: true);
    }
}
";

        private const string SnapperBackOnlySource = @"
public class SnapperBackOnlyScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").Snapper(back: true);
    }
}
";

        private const string SnapperDownBackSource = @"
public class SnapperDownBackScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").Snapper(down: true, back: true);
    }
}
";

        private const string SnapperLeftRightSource = @"
public class SnapperLeftRightScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").Snapper(left: true, right: true);
    }
}
";

        private const string SnapperUpDownSource = @"
public class SnapperUpDownScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").Snapper(up: true, down: true);
    }
}
";

        private const string SnapperForwardBackSource = @"
public class SnapperForwardBackScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").Snapper(forward: true, back: true);
    }
}
";

        private const string SnapperWithTargetSource = @"
public class SnapperTargetScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var floor = scene.Add(""Floor"");
        scene.Add(""Crate"").Snapper(down: true, target: floor);
    }
}
";

        [Fact]
        public void Parse_SnapperDownLeft_SetsFlagsAndDrivenPositionXY()
        {
            var result = BuilderParser.Parse(SnapperDownLeftSource);

            var node = Assert.Single(result.Model.Roots);
            var component = Assert.Single(node.Components);

            Assert.Equal(SpatialComponents.SnapperTypeName, component.Type.FullName);
            Assert.Equal(ValueNode.Primitive.Bool(true), component.Fields[SpatialComponents.SnapperFields.Down]);
            Assert.Equal(ValueNode.Primitive.Bool(true), component.Fields[SpatialComponents.SnapperFields.Left]);
            Assert.False(component.Fields.ContainsKey(SpatialComponents.SnapperFields.Up));
            Assert.False(component.Fields.ContainsKey(SpatialComponents.SnapperFields.Right));
            Assert.False(component.Fields.ContainsKey(SpatialComponents.SnapperFields.Forward));
            Assert.False(component.Fields.ContainsKey(SpatialComponents.SnapperFields.Back));
            Assert.Equal(ChannelMask.PositionX | ChannelMask.PositionY, node.Transform.DrivenChannels);
        }

        [Fact]
        public void Parse_SnapperDownOnly_DrivesPositionYNotX()
        {
            var result = BuilderParser.Parse(SnapperDownOnlySource);

            var node = Assert.Single(result.Model.Roots);
            var component = Assert.Single(node.Components);

            Assert.Single(component.Fields);
            Assert.Equal(ValueNode.Primitive.Bool(true), component.Fields[SpatialComponents.SnapperFields.Down]);
            Assert.Equal(ChannelMask.PositionY, node.Transform.DrivenChannels);
        }

        [Theory]
        [InlineData(SnapperLeftRightSource)]
        [InlineData(SnapperUpDownSource)]
        [InlineData(SnapperForwardBackSource)]
        public void Parse_SnapperContradictoryAxis_YieldsLocatedError(string source)
        {
            var ex = Assert.Throws<ParseException>(() => BuilderParser.Parse(source));

            Assert.True(ex.Line > 0);
            Assert.Contains("combine", ex.Message);
        }

        [Fact]
        public void Parse_SnapperWithTarget_CarriesObjectRefToHandleLogicalId()
        {
            var result = BuilderParser.Parse(SnapperWithTargetSource);

            var floor = Assert.Single(result.Model.Roots, r => r.Name == "Floor");
            var crate = Assert.Single(result.Model.Roots, r => r.Name == "Crate");
            var component = Assert.Single(crate.Components);

            var target = Assert.IsType<ValueNode.ObjectRef>(component.Fields[SpatialComponents.SnapperFields.Target]);
            Assert.Equal(floor.LogicalId, target.TargetLogicalId);
            Assert.Equal(ChannelMask.PositionY, crate.Transform.DrivenChannels & ChannelMask.PositionY);
        }

        [Fact]
        public void Parse_SnapperBack_DrivesPositionZ()
        {
            var result = BuilderParser.Parse(SnapperBackOnlySource);

            var node = Assert.Single(result.Model.Roots);
            var component = Assert.Single(node.Components);

            Assert.Single(component.Fields);
            Assert.Equal(ValueNode.Primitive.Bool(true), component.Fields[SpatialComponents.SnapperFields.Back]);
            Assert.Equal(ChannelMask.PositionZ, node.Transform.DrivenChannels);
        }

        [Fact]
        public void Parse_SnapperDownBack_DrivesPositionYAndZ()
        {
            var result = BuilderParser.Parse(SnapperDownBackSource);

            var node = Assert.Single(result.Model.Roots);

            Assert.Equal(ChannelMask.PositionY | ChannelMask.PositionZ, node.Transform.DrivenChannels);
        }

        // ---- b4-t1: dedicated .Sizer(...)/.Snapper(...) emit --------------------------------

        [Fact]
        public void Emit_Sizer_EmitsDedicatedCallNotGenericComponent()
        {
            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(SpatialComponents.SizerFields.Height, ValueNode.Primitive.Float(2f)),
            });

            var text = SpatialComponentSource.RenderStatement("crate", SpatialComponents.SizerTypeName, fields, null);

            Assert.Equal("crate.Sizer(height: 2f);", text);
            Assert.DoesNotContain(".Component<", text);
        }

        [Fact]
        public void Emit_SizerExplicit_EmitsSizeVectorFSuffixed()
        {
            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(SpatialComponents.SizerFields.Size, new ValueNode.Vec3(new Vec3(2f, 1f, 0.5f))),
            });

            var text = SpatialComponentSource.RenderStatement("crate", SpatialComponents.SizerTypeName, fields, null);

            Assert.Equal("crate.Sizer(size: (2f, 1f, 0.5f));", text);
        }

        [Fact]
        public void Emit_Snapper_EmitsOnlySetFlags()
        {
            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(SpatialComponents.SnapperFields.Down, ValueNode.Primitive.Bool(true)),
                new KeyValuePair<string, ValueNode>(SpatialComponents.SnapperFields.Left, ValueNode.Primitive.Bool(true)),
            });

            var text = SpatialComponentSource.RenderStatement("crate", SpatialComponents.SnapperTypeName, fields, null);

            Assert.Equal("crate.Snapper(down: true, left: true);", text);
            Assert.DoesNotContain("up:", text);
            Assert.DoesNotContain("right:", text);
            Assert.DoesNotContain("forward:", text);
            Assert.DoesNotContain("back:", text);
        }

        [Fact]
        public void Emit_SizerBeforeSnapper_OrderingDeterministicAndStable()
        {
            var sizer = new ComponentData { LogicalId = "x/Sizer#0", Type = new TypeRef(SpatialComponents.SizerTypeName), Fields = FieldMap.Empty };
            var snapper = new ComponentData { LogicalId = "x/Snapper#0", Type = new TypeRef(SpatialComponents.SnapperTypeName), Fields = FieldMap.Empty };

            var ordered = SpatialComponentSource.OrderForEmit(new[] { snapper, sizer });

            Assert.Equal(SpatialComponents.SizerTypeName, ordered[0].Type.FullName);
            Assert.Equal(SpatialComponents.SnapperTypeName, ordered[1].Type.FullName);

            var reordered = SpatialComponentSource.OrderForEmit(ordered);
            Assert.Equal(ordered, reordered);
        }

        [Fact]
        public void Parse_Emit_SpatialCalls_TextRoundTripsIdentically()
        {
            var heightResult = BuilderParser.Parse(SizerHeightSource);
            var heightComponent = Assert.Single(Assert.Single(heightResult.Model.Roots).Components);
            Assert.Equal(
                "x.Sizer(height: 2f);",
                SpatialComponentSource.RenderStatement("x", heightComponent.Type.FullName, heightComponent.Fields, null));

            var sizeResult = BuilderParser.Parse(SizerExplicitSizeSource);
            var sizeComponent = Assert.Single(Assert.Single(sizeResult.Model.Roots).Components);
            Assert.Equal(
                "x.Sizer(size: (2f, 1f, 0.5f));",
                SpatialComponentSource.RenderStatement("x", sizeComponent.Type.FullName, sizeComponent.Fields, null));

            var snapperResult = BuilderParser.Parse(SnapperDownBackSource);
            var snapperComponent = Assert.Single(Assert.Single(snapperResult.Model.Roots).Components);
            Assert.Equal(
                "x.Snapper(down: true, back: true);",
                SpatialComponentSource.RenderStatement("x", snapperComponent.Type.FullName, snapperComponent.Fields, null));
        }

        // ---- b4-t1: created-node (§13) attach uses the dedicated call + Sizer-before-Snapper ----

        private const string EmptySpatialScene = @"
public class EmptySpatialScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
    }
}
";

        [Fact]
        public void Reconcile_CreatedNodeWithSnapper_AppendsSnapperCall_SecondSyncNoOp()
        {
            var parsed = BuilderParser.Parse(EmptySpatialScene);
            var map = new IdentityMap { Entries = System.Array.Empty<IdentityMapEntry>() };

            var snapperFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(SpatialComponents.SnapperFields.Down, ValueNode.Primitive.Bool(true)),
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
                            new ComponentData { LogicalId = "unused", Type = new TypeRef(SpatialComponents.SnapperTypeName), Fields = snapperFields },
                        },
                    },
                },
            };

            var pass1 = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors);
            Assert.Empty(pass1.Conflicts);

            var patched = SourcePatchApplier.Apply(EmptySpatialScene, pass1.Patch, parsed.Anchors);

            Assert.Contains(".Snapper(down: true)", patched);
            Assert.DoesNotContain(".Component<", patched);

            // ---- Pass 2: reparse the applied source; unchanged scene must converge (no edits). ----
            var reparsed = BuilderParser.Parse(patched);
            var reparsedMap = new IdentityMap { Entries = pass1.AddedEntries };

            var pass2 = Reconciler.Reconcile(reparsed.Model, snapshot, reparsedMap, reparsed.Anchors);

            Assert.Empty(pass2.Patch.Edits);
            Assert.Empty(pass2.Conflicts);
        }

        [Fact]
        public void Reconcile_SnapperAndSizerOnCreatedNode_AppendsInSizerThenSnapperOrder()
        {
            var parsed = BuilderParser.Parse(EmptySpatialScene);
            var map = new IdentityMap { Entries = System.Array.Empty<IdentityMapEntry>() };

            var sizerFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(SpatialComponents.SizerFields.Height, ValueNode.Primitive.Float(2f)),
            });
            var snapperFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(SpatialComponents.SnapperFields.Down, ValueNode.Primitive.Bool(true)),
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
                        // Snapshot lists Snapper THEN Sizer — emit must still place Sizer first.
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "unused-snapper", Type = new TypeRef(SpatialComponents.SnapperTypeName), Fields = snapperFields },
                            new ComponentData { LogicalId = "unused-sizer", Type = new TypeRef(SpatialComponents.SizerTypeName), Fields = sizerFields },
                        },
                    },
                },
            };

            var pass1 = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors);
            Assert.Empty(pass1.Conflicts);

            var patched = SourcePatchApplier.Apply(EmptySpatialScene, pass1.Patch, parsed.Anchors);

            var sizerIndex = patched.IndexOf(".Sizer(", System.StringComparison.Ordinal);
            var snapperIndex = patched.IndexOf(".Snapper(", System.StringComparison.Ordinal);
            Assert.True(sizerIndex >= 0, "expected a .Sizer(...) statement in applied source");
            Assert.True(snapperIndex >= 0, "expected a .Snapper(...) statement in applied source");
            Assert.True(sizerIndex < snapperIndex, "Sizer statement must precede Snapper statement");

            // ---- Pass 2: reparse the applied source; unchanged scene must converge (no edits). ----
            var reparsed = BuilderParser.Parse(patched);
            var reparsedMap = new IdentityMap { Entries = pass1.AddedEntries };

            var pass2 = Reconciler.Reconcile(reparsed.Model, snapshot, reparsedMap, reparsed.Anchors);

            Assert.Empty(pass2.Patch.Edits);
            Assert.Empty(pass2.Conflicts);
        }

        // ---- b4-t2: field-edit reconcile -> PatchComponentField on the dedicated call ----------

        [Fact]
        public void Reconcile_EditedSizerHeightInScene_PatchesSizerArgumentOnly()
        {
            var parsed = BuilderParser.Parse(SizerHeightSource);
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
                        ComponentType = SpatialComponents.SizerTypeName,
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
                new KeyValuePair<string, ValueNode>(SpatialComponents.SizerFields.Height, ValueNode.Primitive.Float(2.34567f)),
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
                            new ComponentData { LogicalId = "unused", Type = new TypeRef(SpatialComponents.SizerTypeName), Fields = editedFields },
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

            var applied = SourcePatchApplier.Apply(SizerHeightSource, result.Patch, parsed.Anchors);
            Assert.Contains(".Sizer(height: 2.3457f)", applied);
            Assert.DoesNotContain(".Transform(", applied);
            Assert.DoesNotContain(".Component<", applied);
        }

        [Fact]
        public void Reconcile_DrivenScaleAndPosition_NotEmittedToSource()
        {
            var sizerFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(SpatialComponents.SizerFields.Height, ValueNode.Primitive.Float(2f)),
            });
            var snapperFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(SpatialComponents.SnapperFields.Down, ValueNode.Primitive.Bool(true)),
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
                            new ComponentData { LogicalId = "sizer-1/" + SpatialComponents.SizerTypeName + "#0", Type = new TypeRef(SpatialComponents.SizerTypeName), Fields = sizerFields },
                        },
                    },
                    new GameObjectNode
                    {
                        LogicalId = "snapper-1",
                        Name = "Shelf",
                        Transform = new TransformData { DrivenChannels = ChannelMask.PositionY },
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "snapper-1/" + SpatialComponents.SnapperTypeName + "#0", Type = new TypeRef(SpatialComponents.SnapperTypeName), Fields = snapperFields },
                        },
                    },
                },
            };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "sizer-1", GlobalObjectId = "goid-sizer", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "sizer-1/" + SpatialComponents.SizerTypeName + "#0", GlobalObjectId = "", Kind = "Component", ComponentType = SpatialComponents.SizerTypeName, ParentLogicalId = "sizer-1" },
                    new IdentityMapEntry { LogicalId = "snapper-1", GlobalObjectId = "goid-snapper", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "snapper-1/" + SpatialComponents.SnapperTypeName + "#0", GlobalObjectId = "", Kind = "Component", ComponentType = SpatialComponents.SnapperTypeName, ParentLogicalId = "snapper-1" },
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
                            new ComponentData { LogicalId = "unused-sizer", Type = new TypeRef(SpatialComponents.SizerTypeName), Fields = sizerFields },
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
                            new ComponentData { LogicalId = "unused-snapper", Type = new TypeRef(SpatialComponents.SnapperTypeName), Fields = snapperFields },
                        },
                    },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            Assert.Empty(result.Patch.Edits);
            Assert.Empty(result.Conflicts);
        }

        [Fact]
        public void Reconcile_SnapperDownFreeAxisDrag_PatchesFreeAxisNotDrivenY()
        {
            var snapperFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(SpatialComponents.SnapperFields.Down, ValueNode.Primitive.Bool(true)),
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
                            new ComponentData { LogicalId = "crate-1/" + SpatialComponents.SnapperTypeName + "#0", Type = new TypeRef(SpatialComponents.SnapperTypeName), Fields = snapperFields },
                        },
                    },
                },
            };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "crate-1", GlobalObjectId = "goid-crate", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "crate-1/" + SpatialComponents.SnapperTypeName + "#0", GlobalObjectId = "", Kind = "Component", ComponentType = SpatialComponents.SnapperTypeName, ParentLogicalId = "crate-1" },
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
                            new ComponentData { LogicalId = "unused", Type = new TypeRef(SpatialComponents.SnapperTypeName), Fields = snapperFields },
                        },
                    },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            Assert.Empty(result.Conflicts);
            var pos = Assert.Single(result.Patch.Edits.OfType<PatchArgument>(), e => e.ArgName == "pos");
            Assert.Equal("(5f, 0f, 0f)", pos.NewExpr);
        }

        [Fact]
        public void RoundTrip_SizerSnapper_Idempotent()
        {
            const string source = @"
public class SizerSnapperRoundTripScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var floor = scene.Add(""Floor"");
        var crate = scene.Add(""Crate"");
        crate.Sizer(height: 2f);
        crate.Snapper(down: true, target: floor);
    }
}
";

            var parsed = BuilderParser.Parse(source);
            var floor = Assert.Single(parsed.Model.Roots, r => r.Name == "Floor");
            var crate = Assert.Single(parsed.Model.Roots, r => r.Name == "Crate");
            var sizer = Assert.Single(crate.Components, c => c.Type.FullName == SpatialComponents.SizerTypeName);
            var snapper = Assert.Single(crate.Components, c => c.Type.FullName == SpatialComponents.SnapperTypeName);

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = floor.LogicalId, GlobalObjectId = "goid-floor", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = crate.LogicalId, GlobalObjectId = "goid-crate", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = sizer.LogicalId, GlobalObjectId = "", Kind = "Component", ComponentType = SpatialComponents.SizerTypeName, ParentLogicalId = crate.LogicalId },
                    new IdentityMapEntry { LogicalId = snapper.LogicalId, GlobalObjectId = "", Kind = "Component", ComponentType = SpatialComponents.SnapperTypeName, ParentLogicalId = crate.LogicalId },
                },
            };

            var editedSizerFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(SpatialComponents.SizerFields.Height, ValueNode.Primitive.Float(3f)),
            });
            var snapperFieldsUnchanged = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(SpatialComponents.SnapperFields.Down, ValueNode.Primitive.Bool(true)),
                new KeyValuePair<string, ValueNode>(SpatialComponents.SnapperFields.Target, new ValueNode.ObjectRef(floor.LogicalId)),
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
                            new ComponentData { LogicalId = "unused-sizer", Type = new TypeRef(SpatialComponents.SizerTypeName), Fields = editedSizerFields },
                            new ComponentData { LogicalId = "unused-snapper", Type = new TypeRef(SpatialComponents.SnapperTypeName), Fields = snapperFieldsUnchanged },
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
