using System;
using System.Collections.Generic;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;

namespace SceneBuilder.Core.Validation
{
    // The prefab-path rule: a prefab defines exactly one top-level root. Reports one located
    // Error diagnostic per EXTRA root (Model.Roots[1..]), located at that root's Add(...) anchor
    // via the same offset->line/col conversion PlanningValidator uses.
    public static class PrefabSingleRootValidator
    {
        public static ValidationResult Validate(ParseResult parse, string source, string file = "")
        {
            var bag = new DiagnosticBag();
            var roots = parse.Model.Roots;

            for (var i = 1; i < roots.Length; i++)
            {
                var root = roots[i];
                var (line, col) = LocationOfAnchor(source, parse.Anchors, root.LogicalId);

                bag.Add(new Diagnostic
                {
                    File = file,
                    Line = line,
                    Col = col,
                    Code = DiagnosticCodes.PrefabMultipleRoots,
                    Severity = DiagnosticSeverity.Error,
                    Message = $"A prefab defines exactly one root object, but this builder produces " +
                        $"{roots.Length}. The extra root '{root.Name}' is not allowed.",
                    Suggestion = "Nest it under the single root, or move it to its own prefab under Prefabs/.",
                });
            }

            return bag.ToResult();
        }

        private static (int Line, int Col) LocationOfAnchor(
            string source, IReadOnlyDictionary<string, SourceSpan> anchors, string logicalId) =>
            anchors.TryGetValue(logicalId, out var span) ? (LineOf(source, span.Start), ColumnOf(source, span.Start)) : (0, 0);

        // Verbatim copy of PlanningValidator.LineOf/ColumnOf so both validators convert offset ->
        // line/col the same way.
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
    }
}
