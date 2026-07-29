using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b1-t3: BuilderParser must parse `.RectTransform(...)` into TransformData: Kind + five Vec2
    // fields, table-driven off RectTransformFields (b1-t1). A node without the call stays
    // Kind=="Transform" with all five fields null (b1-t1's invariant). See
    // .agent_handoffs/m-ui-recttransform/b1-t3/research.md.
    public class RectTransformParseTests
    {
        private const float Tolerance = 1e-5f;

        [Fact]
        public void Parse_RectTransformCall_SetsKindAndFiveVec2Fields()
        {
            var source = @"
public class PanelScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Panel"").RectTransform(anchoredPos: (-10, -10), sizeDelta: (200, 120), anchorMin: (1, 1), anchorMax: (1, 1), pivot: (0.25f, 1));
    }
}
";
            var result = BuilderParser.Parse(source);

            var node = Assert.Single(result.Model.Roots);
            Assert.Equal("RectTransform", node.Transform.Kind);
            AssertVec2(-10, -10, node.Transform.AnchoredPosition);
            AssertVec2(200, 120, node.Transform.SizeDelta);
            AssertVec2(1, 1, node.Transform.AnchorMin);
            AssertVec2(1, 1, node.Transform.AnchorMax);
            AssertVec2(0.25f, 1, node.Transform.Pivot);
        }

        [Fact]
        public void Parse_BareRectTransform_KindRectTransform_DefaultsForOmittedArgs()
        {
            var source = @"
public class PanelScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Panel"").RectTransform();
    }
}
";
            var result = BuilderParser.Parse(source);

            var node = Assert.Single(result.Model.Roots);
            Assert.Equal("RectTransform", node.Transform.Kind);
            AssertVec2(RectTransformFields.DefaultAnchoredPosition, node.Transform.AnchoredPosition);
            AssertVec2(RectTransformFields.DefaultSizeDelta, node.Transform.SizeDelta);
            AssertVec2(RectTransformFields.DefaultAnchorMin, node.Transform.AnchorMin);
            AssertVec2(RectTransformFields.DefaultAnchorMax, node.Transform.AnchorMax);
            AssertVec2(RectTransformFields.DefaultPivot, node.Transform.Pivot);
        }

        [Fact]
        public void Parse_PartialRectTransform_AuthoredArgKept_OthersDefaulted()
        {
            var source = @"
public class PanelScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Panel"").RectTransform(sizeDelta: (200, 120));
    }
}
";
            var result = BuilderParser.Parse(source);

            var node = Assert.Single(result.Model.Roots);
            Assert.Equal("RectTransform", node.Transform.Kind);
            AssertVec2(200, 120, node.Transform.SizeDelta);
            AssertVec2(RectTransformFields.DefaultAnchoredPosition, node.Transform.AnchoredPosition);
            AssertVec2(RectTransformFields.DefaultAnchorMin, node.Transform.AnchorMin);
            AssertVec2(RectTransformFields.DefaultAnchorMax, node.Transform.AnchorMax);
            AssertVec2(RectTransformFields.DefaultPivot, node.Transform.Pivot);
        }

        [Fact]
        public void Parse_NoRectTransformCall_KindStaysTransform_NoUiFields()
        {
            var source = @"
public class PlainScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Plain"").Transform(pos: (1, 2, 3));
    }
}
";
            var result = BuilderParser.Parse(source);

            var node = Assert.Single(result.Model.Roots);
            Assert.Equal("Transform", node.Transform.Kind);
            Assert.Null(node.Transform.AnchoredPosition);
            Assert.Null(node.Transform.SizeDelta);
            Assert.Null(node.Transform.AnchorMin);
            Assert.Null(node.Transform.AnchorMax);
            Assert.Null(node.Transform.Pivot);
        }

        [Fact]
        public void Parse_TransformAndRectTransformOnSameNode_BothProjected()
        {
            var source = @"
public class PanelScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Panel"").Transform(rot: (0, 90, 0), scale: (2, 2, 2)).RectTransform(anchoredPos: (5, 6));
    }
}
";
            var result = BuilderParser.Parse(source);

            var node = Assert.Single(result.Model.Roots);
            var expectedRotation = Rotation.EulerToQuat(new Vec3(0, 90, 0));

            Assert.Equal("RectTransform", node.Transform.Kind);
            Assert.Equal(expectedRotation.X, node.Transform.Rotation.X, Tolerance);
            Assert.Equal(expectedRotation.Y, node.Transform.Rotation.Y, Tolerance);
            Assert.Equal(expectedRotation.Z, node.Transform.Rotation.Z, Tolerance);
            Assert.Equal(expectedRotation.W, node.Transform.Rotation.W, Tolerance);
            Assert.Equal(2f, node.Transform.Scale.X, Tolerance);
            Assert.Equal(2f, node.Transform.Scale.Y, Tolerance);
            Assert.Equal(2f, node.Transform.Scale.Z, Tolerance);
            AssertVec2(5, 6, node.Transform.AnchoredPosition);
            AssertVec2(RectTransformFields.DefaultSizeDelta, node.Transform.SizeDelta);
        }

        [Fact]
        public void Parse_InstanceChainRectTransform_ProjectsOntoInstanceNode()
        {
            var source = @"
public class ArenaScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(""Assets/Prefabs/Panel.prefab"").RectTransform(sizeDelta: (5, 6));
    }
}
";
            var result = BuilderParser.Parse(source);

            var node = Assert.Single(result.Model.Roots);
            Assert.IsType<PrefabInstanceNode>(node);
            Assert.Equal("RectTransform", node.Transform.Kind);
            AssertVec2(5, 6, node.Transform.SizeDelta);
            AssertVec2(RectTransformFields.DefaultAnchoredPosition, node.Transform.AnchoredPosition);
        }

        private static void AssertVec2(float expectedX, float expectedY, Vec2? actual)
        {
            Assert.NotNull(actual);
            Assert.Equal(expectedX, actual!.Value.X, Tolerance);
            Assert.Equal(expectedY, actual.Value.Y, Tolerance);
        }

        private static void AssertVec2(Vec2 expected, Vec2? actual) => AssertVec2(expected.X, expected.Y, actual);
    }
}
