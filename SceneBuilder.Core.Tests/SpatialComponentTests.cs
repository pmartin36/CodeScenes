using System.Collections.Generic;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
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
    }
}
