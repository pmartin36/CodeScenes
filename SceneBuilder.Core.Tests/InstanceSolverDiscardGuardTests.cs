using System.Linq;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // A solver verb on a prefab-instance receiver never lands in the discarded `Components`
    // list, and a builder call on an instance receiver with no real destination is a located
    // parse error rather than a silently-accepted no-op.
    public class InstanceSolverDiscardGuardTests
    {
        private const string PrefabPath = "Assets/Prefabs/Sofa.prefab";

        private static PrefabInstanceNode ParseSingleInstance(string statements) =>
            Assert.IsType<PrefabInstanceNode>(
                BuilderParser.Parse($@"
public class InstanceSolverDiscardGuardScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{statements}
    }}
}}
").Model.Roots.Single(r => r is PrefabInstanceNode));

        [Fact]
        public void SolverVerbOnInstance_NeverLandsInComponents_AlwaysLandsInAddedComponents()
        {
            var instance = ParseSingleInstance(
                $"        scene.Instance(\"{PrefabPath}\").FitSize(width: 2f);");

            Assert.Empty(instance.Components);
            Assert.NotEmpty(instance.AddedComponents);
        }

        [Fact]
        public void UnroutedCallOnInstanceReceiver_IsLocatedParseError_NotACleanNoOp()
        {
            var ex = Assert.Throws<ParseException>(() => BuilderParser.Parse($@"
public class InstanceUnroutedCallScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
        scene.Instance(""{PrefabPath}"").Bogus();
    }}
}}
"));

            Assert.True(ex.Line > 0);
            Assert.True(ex.Column > 0);
        }
    }
}
