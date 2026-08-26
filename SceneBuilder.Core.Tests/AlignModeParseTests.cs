using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // The AlignMin/AlignMax/AlignCenter parse+emit surface layered on top of the AlignTo core: each
    // preset must round-trip through the shared Mode-enum field exactly like AbutMin/AbutMax, not
    // fall through to Unsupported.
    public class AlignModeParseTests
    {
        private static string Scene(string className, string statement) => $@"
public class {className}Scene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
        var target = scene.Add(""Target"");
        {statement}
    }}
}}
";

        private static ComponentData SingleAlignTo(ParseResult result)
        {
            var root = result.Model.Roots.Single(r => r.Name == "Crate");
            return Assert.Single(root.Components);
        }

        [Theory]
        [InlineData("AlignMin", "x", "XMode")]
        [InlineData("AlignMax", "y", "YMode")]
        [InlineData("AlignCenter", "z", "ZMode")]
        public void Parse_AlignPresetAxis_StoresModeEnumFieldNotUnsupported(string preset, string axis, string modeFieldProperty)
        {
            var source = Scene(
                $"AlignPreset{preset}",
                $@"scene.Add(""Crate"").AlignTo(target, {axis}: AxisAlign.{preset});");

            var result = BuilderParser.Parse(source);
            var component = SingleAlignTo(result);

            var modeField = modeFieldProperty switch
            {
                "XMode" => SpatialComponents.AlignToFields.XMode,
                "YMode" => SpatialComponents.AlignToFields.YMode,
                "ZMode" => SpatialComponents.AlignToFields.ZMode,
                _ => throw new System.InvalidOperationException(),
            };

            var expected = new ValueNode.Enum(SpatialComponents.AlignToEnums.ModeTypeName, new[] { preset }, false);
            Assert.Equal(expected, component.Fields[modeField]);
        }

        [Fact]
        public void Parse_AlignMaxWithOffset_StoresPairedOffsetField()
        {
            var source = Scene(
                "AlignMaxOffset",
                @"scene.Add(""Crate"").AlignTo(target, y: AxisAlign.AlignMax.Offset(0.5f));");

            var result = BuilderParser.Parse(source);
            var component = SingleAlignTo(result);

            var expectedMode = new ValueNode.Enum(SpatialComponents.AlignToEnums.ModeTypeName, new[] { SpatialComponents.AlignToEnums.AlignMax }, false);
            Assert.Equal(expectedMode, component.Fields[SpatialComponents.AlignToFields.YMode]);
            Assert.Equal(ValueNode.Primitive.Float(0.5f), component.Fields[SpatialComponents.AlignToFields.YOffset]);
        }

        // The minigolf deliverable shape: X centers, Y align-max (tops co-planar), Z abut-max.
        [Fact]
        public void Parse_MixedAlignCenterAlignMaxAbutMax_AllThreeAxesStoredAndDriven()
        {
            var source = Scene(
                "MixedAlignAbut",
                @"scene.Add(""Crate"").AlignTo(target, x: AxisAlign.AlignCenter, y: AxisAlign.AlignMax, z: AxisAlign.AbutMax);");

            var result = BuilderParser.Parse(source);
            var root = result.Model.Roots.Single(r => r.Name == "Crate");
            var component = Assert.Single(root.Components);

            Assert.Equal(
                new ValueNode.Enum(SpatialComponents.AlignToEnums.ModeTypeName, new[] { SpatialComponents.AlignToEnums.AlignCenter }, false),
                component.Fields[SpatialComponents.AlignToFields.XMode]);
            Assert.Equal(
                new ValueNode.Enum(SpatialComponents.AlignToEnums.ModeTypeName, new[] { SpatialComponents.AlignToEnums.AlignMax }, false),
                component.Fields[SpatialComponents.AlignToFields.YMode]);
            Assert.Equal(
                new ValueNode.Enum(SpatialComponents.AlignToEnums.ModeTypeName, new[] { SpatialComponents.AlignToEnums.AbutMax }, false),
                component.Fields[SpatialComponents.AlignToFields.ZMode]);

            Assert.Equal(
                ChannelMask.PositionX | ChannelMask.PositionY | ChannelMask.PositionZ,
                root.Transform.DrivenChannels);
        }

        [Fact]
        public void Emit_AlignCenterMode_RendersAxisAlignMemberName()
        {
            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(SpatialComponents.AlignToFields.Target, new ValueNode.ObjectRef("floor")),
                new KeyValuePair<string, ValueNode>(
                    SpatialComponents.AlignToFields.XMode,
                    new ValueNode.Enum(SpatialComponents.AlignToEnums.ModeTypeName, new[] { SpatialComponents.AlignToEnums.AlignCenter }, false)),
            });
            var fieldExpressions = new Dictionary<string, string> { [SpatialComponents.AlignToFields.Target] = "floor" };

            var text = SpatialComponentSource.RenderStatement("crate", SpatialComponents.AlignToTypeName, fields, fieldExpressions);

            Assert.Equal("crate.AlignTo(target: floor, x: AxisAlign.AlignCenter);", text);
        }

        // Core-layer fixed point: parsing the mixed Align/Abut call then re-rendering it must
        // reproduce the identical call text (no residual Unsupported, no dropped axis).
        [Fact]
        public void Parse_Emit_MixedAlignAbutCall_TextRoundTripsIdentically()
        {
            var source = Scene(
                "RoundTripMixedAlign",
                @"scene.Add(""Crate"").AlignTo(target, x: AxisAlign.AlignCenter, y: AxisAlign.AlignMax, z: AxisAlign.AbutMax);");

            var result = BuilderParser.Parse(source);
            var crate = result.Model.Roots.Single(r => r.Name == "Crate");
            var component = Assert.Single(crate.Components);
            var fieldExpressions = new Dictionary<string, string> { [SpatialComponents.AlignToFields.Target] = "target" };

            var text = SpatialComponentSource.RenderStatement("x", component.Type.FullName, component.Fields, fieldExpressions);

            Assert.Equal(
                "x.AlignTo(target: target, x: AxisAlign.AlignCenter, y: AxisAlign.AlignMax, z: AxisAlign.AbutMax);",
                text);
        }

        [Theory]
        [InlineData(SpatialComponents.AlignToEnums.AlignMin)]
        [InlineData(SpatialComponents.AlignToEnums.AlignMax)]
        [InlineData(SpatialComponents.AlignToEnums.AlignCenter)]
        public void IsAlignPreset_RecognizesEachNewMode(string member)
        {
            Assert.True(SpatialComponents.IsAlignPreset(member));
        }
    }
}
