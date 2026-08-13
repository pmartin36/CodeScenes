using System;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Validation;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // PrefabSingleRootValidator.Validate is the prefab-path rule: a prefab defines exactly one
    // top-level root. It reports one located Error per EXTRA root, not one flag for the whole
    // file, so a builder with three top-level roots surfaces both offenders.
    public class PrefabSingleRootValidatorTests
    {
        // Verbatim copy of SceneBuilderBuild.LineOf/ColumnOf so the test computes expectations
        // with the same offset->line/col algorithm the validator must use.
        private static int LineOf(string source, int offset)
        {
            var line = 1;
            for (var i = 0; i < offset && i < source.Length; i++)
            {
                if (source[i] == '\n')
                {
                    line++;
                }
            }

            return line;
        }

        private static int ColumnOf(string source, int offset)
        {
            var lineStart = source.LastIndexOf('\n', Math.Min(offset, source.Length - 1));
            return offset - lineStart;
        }

        private const string OneRootPrefab = @"
public class CarPrefab : IPrefabDefinition
{
    public void Build(PrefabRoot root)
    {
        var car = root.Add(""Car"");
        car.Add(""Body"");
    }
}
";

        private const string TwoRootPrefab = @"
public class TwoRootPrefab : IPrefabDefinition
{
    public void Build(PrefabRoot root)
    {
        var car = root.Add(""Car"");
        var truck = root.Add(""Truck"");
    }
}
";

        private const string ThreeRootPrefab = @"
public class ThreeRootPrefab : IPrefabDefinition
{
    public void Build(PrefabRoot root)
    {
        var car = root.Add(""Car"");
        var truck = root.Add(""Truck"");
        var bike = root.Add(""Bike"");
    }
}
";

        [Fact]
        public void Validate_SingleRoot_ReturnsOkWithNoDiagnostics()
        {
            var parse = BuilderParser.Parse(OneRootPrefab);

            var result = PrefabSingleRootValidator.Validate(parse, OneRootPrefab);

            Assert.True(result.Ok);
            Assert.Empty(result.Diagnostics);
        }

        [Fact]
        public void Validate_TwoRoots_ReturnsOneLocatedErrorForSecondRoot()
        {
            var parse = BuilderParser.Parse(TwoRootPrefab);
            Assert.Equal(2, parse.Model.Roots.Length);
            var secondRoot = parse.Model.Roots[1];

            var result = PrefabSingleRootValidator.Validate(parse, TwoRootPrefab);

            Assert.False(result.Ok);
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal(DiagnosticCodes.PrefabMultipleRoots, diagnostic.Code);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);

            var anchor = parse.Anchors[secondRoot.LogicalId];
            Assert.Equal(LineOf(TwoRootPrefab, anchor.Start), diagnostic.Line);
            Assert.Equal(ColumnOf(TwoRootPrefab, anchor.Start), diagnostic.Col);
        }

        [Fact]
        public void Validate_ThreeRoots_ReturnsOneDiagnosticPerExtraRoot()
        {
            var parse = BuilderParser.Parse(ThreeRootPrefab);
            Assert.Equal(3, parse.Model.Roots.Length);

            var result = PrefabSingleRootValidator.Validate(parse, ThreeRootPrefab);

            Assert.False(result.Ok);
            Assert.Equal(2, result.Diagnostics.Count);
            Assert.All(result.Diagnostics, d => Assert.Equal(DiagnosticCodes.PrefabMultipleRoots, d.Code));
        }
    }
}
