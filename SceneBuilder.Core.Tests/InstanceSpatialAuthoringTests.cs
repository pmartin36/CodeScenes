using System.Linq;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // `.FitSize`/`.AlignTo`/`.Between` on a prefab-instance receiver (inline chain, captured
    // handle, and the typed `Instance<TRef>` facade) lower onto the instance's AddedComponents,
    // never onto its (always-empty) Components list.
    public class InstanceSpatialAuthoringTests
    {
        private const string PrefabPath = "Assets/Prefabs/Sofa.prefab";

        private static PrefabInstanceNode ParseSingleInstance(string statements) =>
            Assert.IsType<PrefabInstanceNode>(
                BuilderParser.Parse($@"
public class InstanceSpatialScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{statements}
    }}
}}
").Model.Roots.Single(r => r is PrefabInstanceNode));

        [Fact]
        public void InlineChain_FitSizeThenAlignTo_LowersToAddedComponentsNotComponents()
        {
            var instance = ParseSingleInstance(
                "        var floor = scene.Add(\"Floor\");\n" +
                $"        scene.Instance(\"{PrefabPath}\").FitSize(width: 2f).AlignTo(floor, y: AxisAlign.AbutMax);");

            Assert.Equal(2, instance.AddedComponents.Length);
            Assert.Equal(SpatialComponents.FitSizeTypeName, instance.AddedComponents[0].Component.Type.FullName);
            Assert.Equal(SpatialComponents.AlignToTypeName, instance.AddedComponents[1].Component.Type.FullName);
            Assert.Empty(instance.Components);
        }

        [Fact]
        public void CapturedChain_FitSizeThenAlignTo_LowersToAddedComponentsNotComponents()
        {
            var instance = ParseSingleInstance(
                "        var floor = scene.Add(\"Floor\");\n" +
                $"        var ball = scene.Instance(\"{PrefabPath}\");\n" +
                "        ball.FitSize(width: 2f);\n" +
                "        ball.AlignTo(floor, y: AxisAlign.AbutMax);");

            Assert.Equal(2, instance.AddedComponents.Length);
            Assert.Equal(SpatialComponents.FitSizeTypeName, instance.AddedComponents[0].Component.Type.FullName);
            Assert.Equal(SpatialComponents.AlignToTypeName, instance.AddedComponents[1].Component.Type.FullName);
            Assert.Empty(instance.Components);
        }

        [Fact]
        public void InlineChain_Between_LowersToAddedComponents()
        {
            var instance = ParseSingleInstance(
                "        var a = scene.Add(\"A\");\n" +
                "        var b = scene.Add(\"B\");\n" +
                $"        scene.Instance(\"{PrefabPath}\").Between(from: a, to: b, fraction: 0.25f, axis: Between.Axis.X);");

            var added = Assert.Single(instance.AddedComponents);
            Assert.Equal(SpatialComponents.BetweenTypeName, added.Component.Type.FullName);
            Assert.Empty(instance.Components);
        }

        [Fact]
        public void FitSize_OnInstance_DrivesScaleChannelOnInstanceTransform()
        {
            var instance = ParseSingleInstance(
                $"        scene.Instance(\"{PrefabPath}\").FitSize(width: 2f);");

            Assert.Equal(SpatialComponents.FitSizeMask, instance.Transform.DrivenChannels);
        }
    }
}
