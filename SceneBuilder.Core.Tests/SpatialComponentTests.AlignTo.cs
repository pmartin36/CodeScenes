using System.Collections.Generic;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // .AlignTo(...) parse/emit round-trip pins, split into a sibling partial to keep
    // SpatialComponentTests.cs under the file-size budget.
    public partial class SpatialComponentTests
    {
        private const string AlignToSingleAxisSource = @"
public class AlignToSingleAxisScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var floor = scene.Add(""Floor"");
        scene.Add(""Crate"").AlignTo(floor, y: AxisAlign.AbutMax);
    }
}
";

        private const string AlignToTwoAxisSource = @"
public class AlignToTwoAxisScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var floor = scene.Add(""Floor"");
        scene.Add(""Crate"").AlignTo(floor, x: AxisAlign.AbutMin, y: AxisAlign.AbutMax);
    }
}
";

        private const string AlignToOffsetSource = @"
public class AlignToOffsetScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var floor = scene.Add(""Floor"");
        scene.Add(""Crate"").AlignTo(floor, z: AxisAlign.AbutMax.Offset(0.5f));
    }
}
";

        private const string AlignToWorldSpaceSource = @"
public class AlignToWorldSpaceScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var floor = scene.Add(""Floor"");
        scene.Add(""Crate"").AlignTo(floor, y: AxisAlign.AbutMax, space: AlignSpace.World);
    }
}
";

        private const string AlignToFrameOverrideSource = @"
public class AlignToFrameOverrideScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var floor = scene.Add(""Floor"");
        var rig = scene.Add(""Rig"");
        scene.Add(""Crate"").AlignTo(floor, y: AxisAlign.AbutMax, frame: rig);
    }
}
";

        [Fact]
        public void Parse_AlignToSingleAxis_SetsModeFieldTargetAndDrivenPositionY()
        {
            var result = BuilderParser.Parse(AlignToSingleAxisSource);

            var floor = Assert.Single(result.Model.Roots, r => r.Name == "Floor");
            var crate = Assert.Single(result.Model.Roots, r => r.Name == "Crate");
            var component = Assert.Single(crate.Components);

            Assert.Equal(SpatialComponents.AlignToTypeName, component.Type.FullName);

            var target = Assert.IsType<ValueNode.ObjectRef>(component.Fields[SpatialComponents.AlignToFields.Target]);
            Assert.Equal(floor.LogicalId, target.TargetLogicalId);

            Assert.Equal(
                new ValueNode.Enum(SpatialComponents.AlignToEnums.ModeTypeName, new[] { SpatialComponents.AlignToEnums.AbutMax }, false),
                component.Fields[SpatialComponents.AlignToFields.YMode]);
            Assert.False(component.Fields.ContainsKey(SpatialComponents.AlignToFields.XMode));
            Assert.False(component.Fields.ContainsKey(SpatialComponents.AlignToFields.ZMode));
            Assert.Equal(ChannelMask.PositionY, crate.Transform.DrivenChannels);
        }

        [Fact]
        public void Parse_AlignToTwoAxis_SetsBothModeFieldsAndDrivenXY()
        {
            var result = BuilderParser.Parse(AlignToTwoAxisSource);

            var crate = Assert.Single(result.Model.Roots, r => r.Name == "Crate");
            var component = Assert.Single(crate.Components);

            Assert.Equal(
                new ValueNode.Enum(SpatialComponents.AlignToEnums.ModeTypeName, new[] { SpatialComponents.AlignToEnums.AbutMin }, false),
                component.Fields[SpatialComponents.AlignToFields.XMode]);
            Assert.Equal(
                new ValueNode.Enum(SpatialComponents.AlignToEnums.ModeTypeName, new[] { SpatialComponents.AlignToEnums.AbutMax }, false),
                component.Fields[SpatialComponents.AlignToFields.YMode]);
            Assert.Equal(ChannelMask.PositionX | ChannelMask.PositionY, crate.Transform.DrivenChannels);
        }

        [Fact]
        public void Parse_AlignToOffset_SetsPairedOffsetFieldAndDrivenZ()
        {
            var result = BuilderParser.Parse(AlignToOffsetSource);

            var crate = Assert.Single(result.Model.Roots, r => r.Name == "Crate");
            var component = Assert.Single(crate.Components);

            Assert.Equal(
                new ValueNode.Enum(SpatialComponents.AlignToEnums.ModeTypeName, new[] { SpatialComponents.AlignToEnums.AbutMax }, false),
                component.Fields[SpatialComponents.AlignToFields.ZMode]);
            Assert.Equal(ValueNode.Primitive.Float(0.5f), component.Fields[SpatialComponents.AlignToFields.ZOffset]);
            Assert.Equal(ChannelMask.PositionZ, crate.Transform.DrivenChannels);
        }

        [Fact]
        public void Parse_AlignToWorldSpace_SetsSpaceField()
        {
            var result = BuilderParser.Parse(AlignToWorldSpaceSource);

            var crate = Assert.Single(result.Model.Roots, r => r.Name == "Crate");
            var component = Assert.Single(crate.Components);

            Assert.Equal(
                new ValueNode.Enum(SpatialComponents.AlignToEnums.AlignSpaceTypeName, new[] { SpatialComponents.AlignToEnums.World }, false),
                component.Fields[SpatialComponents.AlignToFields.Space]);
        }

        // TargetLocal is the default frame; an authored call that never mentions `space:` must not
        // carry a Space field at all (default-value pruning parity with every other AlignTo axis).
        [Fact]
        public void Parse_AlignToDefaultSpace_DoesNotStoreSpaceField()
        {
            var result = BuilderParser.Parse(AlignToSingleAxisSource);

            var crate = Assert.Single(result.Model.Roots, r => r.Name == "Crate");
            var component = Assert.Single(crate.Components);

            Assert.False(component.Fields.ContainsKey(SpatialComponents.AlignToFields.Space));
        }

        [Fact]
        public void Parse_AlignToFrameOverride_CarriesObjectRefToHandleLogicalId()
        {
            var result = BuilderParser.Parse(AlignToFrameOverrideSource);

            var rig = Assert.Single(result.Model.Roots, r => r.Name == "Rig");
            var crate = Assert.Single(result.Model.Roots, r => r.Name == "Crate");
            var component = Assert.Single(crate.Components);

            var frame = Assert.IsType<ValueNode.ObjectRef>(component.Fields[SpatialComponents.AlignToFields.Frame]);
            Assert.Equal(rig.LogicalId, frame.TargetLogicalId);
        }

        // ---- Emit ------------------------------------------------------------------------------

        [Fact]
        public void IsSpatial_AlignToTypeName_ReturnsTrue()
        {
            Assert.True(SpatialComponentSource.IsSpatial(SpatialComponents.AlignToTypeName));
        }

        [Fact]
        public void Emit_AlignTo_RendersTargetFirstThenModeKeywordGrouped()
        {
            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(SpatialComponents.AlignToFields.Target, new ValueNode.ObjectRef("floor")),
                new KeyValuePair<string, ValueNode>(
                    SpatialComponents.AlignToFields.YMode,
                    new ValueNode.Enum(SpatialComponents.AlignToEnums.ModeTypeName, new[] { SpatialComponents.AlignToEnums.AbutMax }, false)),
            });
            var fieldExpressions = new Dictionary<string, string> { [SpatialComponents.AlignToFields.Target] = "floor" };

            var text = SpatialComponentSource.RenderStatement("crate", SpatialComponents.AlignToTypeName, fields, fieldExpressions);

            Assert.Equal("crate.AlignTo(target: floor, y: AxisAlign.AbutMax);", text);
        }

        [Fact]
        public void Emit_AlignToWithOffset_RendersOffsetCallOnTheAxisKeyword()
        {
            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(SpatialComponents.AlignToFields.Target, new ValueNode.ObjectRef("floor")),
                new KeyValuePair<string, ValueNode>(
                    SpatialComponents.AlignToFields.ZMode,
                    new ValueNode.Enum(SpatialComponents.AlignToEnums.ModeTypeName, new[] { SpatialComponents.AlignToEnums.AbutMax }, false)),
                new KeyValuePair<string, ValueNode>(SpatialComponents.AlignToFields.ZOffset, ValueNode.Primitive.Float(0.5f)),
            });
            var fieldExpressions = new Dictionary<string, string> { [SpatialComponents.AlignToFields.Target] = "floor" };

            var text = SpatialComponentSource.RenderStatement("crate", SpatialComponents.AlignToTypeName, fields, fieldExpressions);

            Assert.Equal("crate.AlignTo(target: floor, z: AxisAlign.AbutMax.Offset(0.5f));", text);
        }

        [Fact]
        public void RoundTrip_AlignTo_Idempotent()
        {
            var parsed = BuilderParser.Parse(AlignToSingleAxisSource);
            var floor = Assert.Single(parsed.Model.Roots, r => r.Name == "Floor");
            var crate = Assert.Single(parsed.Model.Roots, r => r.Name == "Crate");
            var aligner = Assert.Single(crate.Components);

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = floor.LogicalId, GlobalObjectId = "goid-floor", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = crate.LogicalId, GlobalObjectId = "goid-crate", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = aligner.LogicalId,
                        GlobalObjectId = "",
                        Kind = "Component",
                        ComponentType = SpatialComponents.AlignToTypeName,
                        ParentLogicalId = crate.LogicalId,
                    },
                },
            };

            var alignerFieldsUnchanged = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    SpatialComponents.AlignToFields.YMode,
                    new ValueNode.Enum(SpatialComponents.AlignToEnums.ModeTypeName, new[] { SpatialComponents.AlignToEnums.AbutMax }, false)),
                new KeyValuePair<string, ValueNode>(SpatialComponents.AlignToFields.Target, new ValueNode.ObjectRef(floor.LogicalId)),
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
                        Transform = new TransformData { DrivenChannels = ChannelMask.PositionY },
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "unused", Type = new TypeRef(SpatialComponents.AlignToTypeName), Fields = alignerFieldsUnchanged },
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

            Assert.Empty(result.Patch.Edits);
            Assert.Empty(result.Conflicts);
        }
    }
}
