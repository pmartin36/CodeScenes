using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Grammar;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // AlignMin/AlignMax/AlignCenter need a target's extent to resolve against (unlike AbutMin/
    // AbutMax, which fall back to a world raycast/scan) -- an Align* axis with no target is a
    // located violation, and the recognizer/parser must agree on it exactly like every other
    // structural AlignTo shape.
    public class AlignModeAgreementTests
    {
        private static string Scene(string className, string statement) => $@"
public class {className}Scene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
        {statement}
    }}
}}
";

        private static (System.Collections.Generic.IReadOnlyList<ShapeViolation> Violations, ParseException? Exception, SyntaxTree Tree) Analyze(string source)
        {
            var tree = CSharpSyntaxTree.ParseText(source);
            var root = tree.GetRoot();
            var buildMethod = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.Text == "Build");
            var sceneParamName = buildMethod.ParameterList.Parameters[0].Identifier.Text;
            var body = buildMethod.Body!;

            var violations = FlatShapeRecognizer.Analyze(body, sceneParamName);

            ParseException? parserException = null;
            try
            {
                BuilderParser.Parse(source);
            }
            catch (ParseException ex)
            {
                parserException = ex;
            }

            return (violations, parserException, tree);
        }

        [Theory]
        [InlineData("AlignMin", "x")]
        [InlineData("AlignMax", "y")]
        [InlineData("AlignCenter", "z")]
        public void AlignPresetWithoutTarget_RecognizerAndParserAgreeOnOneLocatedViolation(string preset, string axis)
        {
            var source = Scene(
                $"NoTarget{preset}",
                $@"scene.Add(""A"").AlignTo({axis}: AxisAlign.{preset});");

            var (violations, exception, tree) = Analyze(source);

            var violation = Assert.Single(violations);
            Assert.Contains(preset, violation.Message);
            Assert.Contains("target", violation.Message, System.StringComparison.OrdinalIgnoreCase);

            Assert.NotNull(exception);
            var location = Location.Create(tree, new TextSpan(violation.Span.Start, violation.Span.Length));
            var position = location.GetLineSpan().StartLinePosition;
            var expectedLine = position.Line + 1;
            var expectedColumn = position.Character + 1;
            var expectedMessage = $"{violation.Message} at line {expectedLine}, column {expectedColumn}.";

            Assert.Equal(expectedMessage, exception!.Message);
            Assert.Equal(expectedLine, exception.Line);
            Assert.Equal(expectedColumn, exception.Column);
        }

        // Abut modes have no target requirement (raycast/fallback-scan resolves the surface): the
        // no-target rule must not fire for them.
        [Theory]
        [InlineData("AbutMin")]
        [InlineData("AbutMax")]
        public void AbutPresetWithoutTarget_ReportsZeroViolations(string preset)
        {
            var source = Scene(
                $"NoTarget{preset}",
                $@"scene.Add(""A"").AlignTo(y: AxisAlign.{preset});");

            var (violations, exception, _) = Analyze(source);

            Assert.Empty(violations);
            Assert.Null(exception);
        }

        // A well-formed call with an explicit target present must not trip the no-target rule for
        // any Align* preset, mixed with an Abut axis (the minigolf deliverable shape).
        [Fact]
        public void AlignPresetsWithTargetPresent_ReportZeroViolations()
        {
            var source = Scene(
                "MixedWithTarget",
                @"var floor = scene.Add(""Floor"");
        scene.Add(""Crate"").AlignTo(floor, x: AxisAlign.AlignCenter, y: AxisAlign.AlignMax, z: AxisAlign.AbutMax);");

            var (violations, exception, _) = Analyze(source);

            Assert.Empty(violations);
            Assert.Null(exception);
        }
    }
}
