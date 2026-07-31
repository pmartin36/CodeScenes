using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // FitSize enum-shape/idempotence pins, split into a sibling partial to keep
    // SpatialComponentTests.cs under the file-size budget.
    public partial class SpatialComponentTests
    {
        private const string FitSizeWidthSource = @"
public class FitSizeWidthScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Crate"").FitSize(width: 3f);
    }
}
";

        // Pins the EXACT ValueNode.Enum shape SerializedFieldBridge.ReadEnum yields for a Unity enum
        // field (TypeFullName + single-member list + IsFlags=false) — a Primitive.Int would never
        // value-equal this and would churn/break idempotence.
        [Fact]
        public void Parse_FitSizeHeight_ProducesReaderShapedEnumModeValue()
        {
            var result = BuilderParser.Parse(FitSizeHeightSource);
            var component = Assert.Single(Assert.Single(result.Model.Roots).Components);

            Assert.Equal(
                new ValueNode.Enum(SpatialComponents.FitSizeEnums.ModeTypeName, new[] { SpatialComponents.FitSizeEnums.Height }, false),
                component.Fields[SpatialComponents.FitSizeFields.Mode]);
        }

        // A width-authored FitSize's `mode` field must be present and equal Enum(["Width"]) — this is
        // what lets APPEND (via emit-side ComponentDefaultOmission) recover the
        // width/height/depth keyword: with Mode.None at index 0, every authored mode is != default
        // and always survives omission.
        [Fact]
        public void Parse_FitSizeWidth_YieldsModeWidthField()
        {
            var result = BuilderParser.Parse(FitSizeWidthSource);
            var component = Assert.Single(Assert.Single(result.Model.Roots).Components);

            Assert.Equal(
                new ValueNode.Enum(SpatialComponents.FitSizeEnums.ModeTypeName, new[] { SpatialComponents.FitSizeEnums.Width }, false),
                component.Fields[SpatialComponents.FitSizeFields.Mode]);
            Assert.Equal(ValueNode.Primitive.Float(3f), component.Fields[SpatialComponents.FitSizeFields.Value]);
        }
    }
}
