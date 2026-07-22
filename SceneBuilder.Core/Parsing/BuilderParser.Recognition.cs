using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SceneBuilder.Core.Parsing
{
    // Up-front recognition gate: derives ParseCore's accept/reject decision from
    // SceneBuilder.Grammar.FlatShapeRecognizer (the single source of truth for the M1 builder-file
    // shape) instead of letting it fall out implicitly from the per-site Apply* throws below. See
    // .agent_handoffs/codescenes-analyzers/b1-t2/research.md.
    public static partial class BuilderParser
    {
        private static void RecognizeOrThrow(SyntaxTree tree, BlockSyntax body, string sceneParamName)
        {
            var violations = SceneBuilder.Grammar.FlatShapeRecognizer.Analyze(body, sceneParamName);
            if (violations.Count > 0)
            {
                throw FailFromViolation(tree, violations[0]);
            }
        }

        private static ParseException FailFromViolation(SyntaxTree tree, SceneBuilder.Grammar.ShapeViolation v)
        {
            var location = Location.Create(tree, new TextSpan(v.Span.Start, v.Span.Length));
            var position = location.GetLineSpan().StartLinePosition;
            var line = position.Line + 1;
            var column = position.Character + 1;
            return new ParseException($"{v.Message} at line {line}, column {column}.", line, column);
        }
    }
}
