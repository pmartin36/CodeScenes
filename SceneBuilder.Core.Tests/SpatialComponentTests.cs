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
            Assert.Equal("SceneBuilder.Authoring.AlignTo", SpatialComponents.AlignToTypeName);
        }

        // b3-t1: field constants migrated from NaN-sentinel width/height/depth to mode/value/size.
        [Fact]
        public void SpatialComponents_FitSizeFieldKeys_MatchExpectedLiterals()
        {
            Assert.Equal("mode", SpatialComponents.FitSizeFields.Mode);
            Assert.Equal("value", SpatialComponents.FitSizeFields.Value);
            Assert.Equal("size", SpatialComponents.FitSizeFields.Size);
        }

        // The AlignTo per-axis mode/offset field keys, plus target/frame/space/captureThreshold.
        [Fact]
        public void SpatialComponents_AlignToFieldKeys_MatchExpectedLiterals()
        {
            Assert.Equal("xMode", SpatialComponents.AlignToFields.XMode);
            Assert.Equal("xOffset", SpatialComponents.AlignToFields.XOffset);
            Assert.Equal("yMode", SpatialComponents.AlignToFields.YMode);
            Assert.Equal("yOffset", SpatialComponents.AlignToFields.YOffset);
            Assert.Equal("zMode", SpatialComponents.AlignToFields.ZMode);
            Assert.Equal("zOffset", SpatialComponents.AlignToFields.ZOffset);
            Assert.Equal("target", SpatialComponents.AlignToFields.Target);
            Assert.Equal("frame", SpatialComponents.AlignToFields.Frame);
            Assert.Equal("space", SpatialComponents.AlignToFields.Space);
            Assert.Equal("captureThreshold", SpatialComponents.AlignToFields.CaptureThreshold);
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
                new ValueNode.Enum(SpatialComponents.FitSizeEnums.ModeTypeName, new[] { SpatialComponents.FitSizeEnums.Height }, false),
                component.Fields[SpatialComponents.FitSizeFields.Mode]);
            Assert.Equal(
                ValueNode.Primitive.Float(2f),
                component.Fields[SpatialComponents.FitSizeFields.Value]);
            Assert.False(component.Fields.ContainsKey(SpatialComponents.FitSizeFields.Size), "aspect-locked FitSize must not carry a size field");
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
                new ValueNode.Enum(SpatialComponents.FitSizeEnums.ModeTypeName, new[] { SpatialComponents.FitSizeEnums.Explicit }, false),
                component.Fields[SpatialComponents.FitSizeFields.Mode]);
            Assert.Equal(
                new ValueNode.Vec3(new Vec3(2f, 1f, 0.5f)),
                component.Fields[SpatialComponents.FitSizeFields.Size]);
            Assert.False(component.Fields.ContainsKey(SpatialComponents.FitSizeFields.Value), "explicit FitSize must not carry a value field");
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

        // ---- .AlignTo(...) parse arm (Down/Left/Forward/Back regression cases; the single/two-axis/
        // offset/space/frame cases live in SpatialComponentTests.AlignTo.cs) --------------------------

        private const string AlignToDownLeftSource = @"
public class AlignToDownLeftScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var floor = scene.Add(""Floor"");
        scene.Add(""Crate"").AlignTo(floor, x: AxisAlign.AbutMax, y: AxisAlign.AbutMax);
    }
}
";

        private const string AlignToDownOnlySource = @"
public class AlignToDownOnlyScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var floor = scene.Add(""Floor"");
        scene.Add(""Crate"").AlignTo(floor, y: AxisAlign.AbutMax);
    }
}
";

        private const string AlignToBackOnlySource = @"
public class AlignToBackOnlyScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var floor = scene.Add(""Floor"");
        scene.Add(""Crate"").AlignTo(floor, z: AxisAlign.AbutMax);
    }
}
";

        private const string AlignToDownBackSource = @"
public class AlignToDownBackScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var floor = scene.Add(""Floor"");
        scene.Add(""Crate"").AlignTo(floor, y: AxisAlign.AbutMax, z: AxisAlign.AbutMax);
    }
}
";

        // A set axis is carried as ValueNode.Enum(AlignToEnums.ModeTypeName, [<MemberName>], false) — the
        // EXACT shape SerializedFieldBridge.ReadEnum yields, so reconcile diffs by value-equality and
        // stays idempotent.
        [Fact]
        public void Parse_AlignToDownLeft_SetsModeFieldsAndDrivenPositionXY()
        {
            var result = BuilderParser.Parse(AlignToDownLeftSource);

            var crate = Assert.Single(result.Model.Roots, r => r.Name == "Crate");
            var component = Assert.Single(crate.Components);

            Assert.Equal(SpatialComponents.AlignToTypeName, component.Type.FullName);
            Assert.Equal(
                new ValueNode.Enum(SpatialComponents.AlignToEnums.ModeTypeName, new[] { SpatialComponents.AlignToEnums.AbutMax }, false),
                component.Fields[SpatialComponents.AlignToFields.XMode]);
            Assert.Equal(
                new ValueNode.Enum(SpatialComponents.AlignToEnums.ModeTypeName, new[] { SpatialComponents.AlignToEnums.AbutMax }, false),
                component.Fields[SpatialComponents.AlignToFields.YMode]);
            Assert.False(component.Fields.ContainsKey(SpatialComponents.AlignToFields.ZMode));
            Assert.Equal(ChannelMask.PositionX | ChannelMask.PositionY, crate.Transform.DrivenChannels);
        }

        [Fact]
        public void Parse_AlignToDownOnly_DrivesPositionYNotX()
        {
            var result = BuilderParser.Parse(AlignToDownOnlySource);

            var crate = Assert.Single(result.Model.Roots, r => r.Name == "Crate");
            var component = Assert.Single(crate.Components);

            Assert.Equal(
                new ValueNode.Enum(SpatialComponents.AlignToEnums.ModeTypeName, new[] { SpatialComponents.AlignToEnums.AbutMax }, false),
                component.Fields[SpatialComponents.AlignToFields.YMode]);
            Assert.False(component.Fields.ContainsKey(SpatialComponents.AlignToFields.XMode));
            Assert.Equal(ChannelMask.PositionY, crate.Transform.DrivenChannels);
        }

        // The exact ValueNode.Enum shape a `.AlignTo(floor, y: AxisAlign.AbutMax)` parse produces must
        // value-equal what SerializedFieldBridge.ReadEnum yields from the live component — this is what
        // makes reconcile a no-op on an unchanged scene.
        [Fact]
        public void Parse_AlignToDown_ProducesReaderShapedEnumValue()
        {
            var result = BuilderParser.Parse(AlignToDownOnlySource);
            var component = Assert.Single(Assert.Single(result.Model.Roots, r => r.Name == "Crate").Components);

            var expected = new ValueNode.Enum(SpatialComponents.AlignToEnums.ModeTypeName, new[] { SpatialComponents.AlignToEnums.AbutMax }, false);
            Assert.Equal(expected, component.Fields[SpatialComponents.AlignToFields.YMode]);
            Assert.IsType<ValueNode.Enum>(component.Fields[SpatialComponents.AlignToFields.YMode]);
        }

        [Fact]
        public void Parse_AlignToBack_DrivesPositionZ()
        {
            var result = BuilderParser.Parse(AlignToBackOnlySource);

            var crate = Assert.Single(result.Model.Roots, r => r.Name == "Crate");
            var component = Assert.Single(crate.Components);

            Assert.Equal(
                new ValueNode.Enum(SpatialComponents.AlignToEnums.ModeTypeName, new[] { SpatialComponents.AlignToEnums.AbutMax }, false),
                component.Fields[SpatialComponents.AlignToFields.ZMode]);
            Assert.Equal(ChannelMask.PositionZ, crate.Transform.DrivenChannels);
        }

        [Fact]
        public void Parse_AlignToDownBack_DrivesPositionYAndZ()
        {
            var result = BuilderParser.Parse(AlignToDownBackSource);

            var crate = Assert.Single(result.Model.Roots, r => r.Name == "Crate");

            Assert.Equal(ChannelMask.PositionY | ChannelMask.PositionZ, crate.Transform.DrivenChannels);
        }

        // ---- dedicated .FitSize(...)/.AlignTo(...) emit -------------------------------------

        [Fact]
        public void Emit_FitSize_EmitsDedicatedCallNotGenericComponent()
        {
            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    SpatialComponents.FitSizeFields.Mode,
                    new ValueNode.Enum(SpatialComponents.FitSizeEnums.ModeTypeName, new[] { SpatialComponents.FitSizeEnums.Height }, false)),
                new KeyValuePair<string, ValueNode>(SpatialComponents.FitSizeFields.Value, ValueNode.Primitive.Float(2f)),
            });

            var text = SpatialComponentSource.RenderStatement("crate", SpatialComponents.FitSizeTypeName, fields, null);

            Assert.Equal("crate.FitSize(height: 2f);", text);
            Assert.DoesNotContain(".Component<", text);
            Assert.DoesNotContain("mode:", text);
            Assert.DoesNotContain("value:", text);
        }

        [Fact]
        public void Emit_FitSizeExplicit_EmitsSizeVectorFSuffixed()
        {
            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    SpatialComponents.FitSizeFields.Mode,
                    new ValueNode.Enum(SpatialComponents.FitSizeEnums.ModeTypeName, new[] { SpatialComponents.FitSizeEnums.Explicit }, false)),
                new KeyValuePair<string, ValueNode>(SpatialComponents.FitSizeFields.Size, new ValueNode.Vec3(new Vec3(2f, 1f, 0.5f))),
            });

            var text = SpatialComponentSource.RenderStatement("crate", SpatialComponents.FitSizeTypeName, fields, null);

            Assert.Equal("crate.FitSize(size: (2f, 1f, 0.5f));", text);
            Assert.DoesNotContain("mode:", text);
        }

        // The emitted TEXT groups target first, then each pinned axis keyword in x/y/z order.
        [Fact]
        public void Emit_AlignTo_EmitsTargetThenOnlySetAxes()
        {
            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(SpatialComponents.AlignToFields.Target, new ValueNode.ObjectRef("floor")),
                new KeyValuePair<string, ValueNode>(
                    SpatialComponents.AlignToFields.XMode,
                    new ValueNode.Enum(SpatialComponents.AlignToEnums.ModeTypeName, new[] { SpatialComponents.AlignToEnums.AbutMax }, false)),
                new KeyValuePair<string, ValueNode>(
                    SpatialComponents.AlignToFields.YMode,
                    new ValueNode.Enum(SpatialComponents.AlignToEnums.ModeTypeName, new[] { SpatialComponents.AlignToEnums.AbutMax }, false)),
            });
            var fieldExpressions = new Dictionary<string, string> { [SpatialComponents.AlignToFields.Target] = "floor" };

            var text = SpatialComponentSource.RenderStatement("crate", SpatialComponents.AlignToTypeName, fields, fieldExpressions);

            Assert.Equal("crate.AlignTo(target: floor, x: AxisAlign.AbutMax, y: AxisAlign.AbutMax);", text);
            Assert.DoesNotContain("z:", text);
        }

        [Fact]
        public void Emit_FitSizeBeforeAlignTo_OrderingDeterministicAndStable()
        {
            var sizer = new ComponentData { LogicalId = "x/FitSize#0", Type = new TypeRef(SpatialComponents.FitSizeTypeName), Fields = FieldMap.Empty };
            var aligner = new ComponentData { LogicalId = "x/AlignTo#0", Type = new TypeRef(SpatialComponents.AlignToTypeName), Fields = FieldMap.Empty };

            var ordered = SpatialComponentSource.OrderForEmit(new[] { aligner, sizer });

            Assert.Equal(SpatialComponents.FitSizeTypeName, ordered[0].Type.FullName);
            Assert.Equal(SpatialComponents.AlignToTypeName, ordered[1].Type.FullName);

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

            var alignerResult = BuilderParser.Parse(AlignToDownBackSource);
            var crate = Assert.Single(alignerResult.Model.Roots, r => r.Name == "Crate");
            var alignerComponent = Assert.Single(crate.Components);
            Assert.Equal(
                "x.AlignTo(target: floor, y: AxisAlign.AbutMax, z: AxisAlign.AbutMax);",
                SpatialComponentSource.RenderStatement(
                    "x", alignerComponent.Type.FullName, alignerComponent.Fields,
                    new Dictionary<string, string> { [SpatialComponents.AlignToFields.Target] = "floor" }));
        }
    }
}
