using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SceneBuilder.Grammar;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Forward guard for the structural single-source refactor: BuilderParser no longer carries its
    // own per-site grammar throws — it derives ALL flat-shape recognition from FlatShapeRecognizer
    // (the up-front RecognizeOrThrow gate). This test pins that FlatShapeRecognizer keeps flagging
    // EVERY construct the removed per-site throws used to flag, so an accidental future removal of a
    // recognizer case is caught here rather than silently re-accepting a shape Build cannot parse.
    //
    // The corpus is shared with RecognizerAgreementTests (which proves recognizer/parser AGREE on
    // acceptance + message + location); this file asserts the recognizer, on its own, still REJECTS
    // each construct with a located violation.
    public class RecognizerCompletenessTests
    {
        [Theory]
        [MemberData(nameof(RecognizerAgreementTests.CompletenessCases), MemberType = typeof(RecognizerAgreementTests))]
        public void Recognizer_FlagsEveryFormerlyThrownConstruct(string caseName, string source)
        {
            var tree = CSharpSyntaxTree.ParseText(source);
            var root = tree.GetRoot();
            var buildMethod = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.Text == "Build");
            var sceneParamName = buildMethod.ParameterList.Parameters[0].Identifier.Text;
            var body = buildMethod.Body!;

            var violations = FlatShapeRecognizer.Analyze(body, sceneParamName);

            Assert.NotEmpty(violations);

            var first = violations[0];
            Assert.Contains(first.Code, new[] { "SB1001", "SB1002", "SB1003" });
            Assert.True(first.Span.Start >= 0 && first.Span.Length > 0,
                $"[{caseName}] violation must be located (Start={first.Span.Start}, Length={first.Span.Length})");
            Assert.True(first.Span.Start + first.Span.Length <= source.Length,
                $"[{caseName}] violation span must fall within the source");
        }
    }
}
