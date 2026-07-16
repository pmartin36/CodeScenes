using System;
using System.Collections.Generic;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;

namespace SceneBuilder.Core.Validation
{
    // The ONE shared planning walk both the editor Build (b3) and the headless validator (b4)
    // drive over a parse + a non-throwing resolution provider. Collect-all: never throws, never
    // stops at the first diagnostic. Order per spec §270: (1) structural ambiguities (resolver
    // NOT consulted), (2) component types, (3) asset refs.
    public static class PlanningValidator
    {
        public static ValidationResult Validate(
            ParseResult parse, string source, IResolutionProvider resolver, string file = "")
        {
            var bag = new DiagnosticBag();

            AddAmbiguityDiagnostics(bag, parse, source, file);
            AddTypeDiagnostics(bag, parse, source, resolver, file);
            AddAssetDiagnostics(bag, parse, source, resolver, file);

            return bag.ToResult();
        }

        private static void AddAmbiguityDiagnostics(DiagnosticBag bag, ParseResult parse, string source, string file)
        {
            foreach (var conflict in parse.Ambiguities)
            {
                var code = conflict.Kind == ConflictKind.DuplicateLogicalId
                    ? DiagnosticCodes.DuplicateLogicalId
                    : DiagnosticCodes.AmbiguousDuplicateSibling;

                var (line, col) = LocationOf(source, conflict.Location);

                bag.Add(new Diagnostic
                {
                    File = file,
                    Line = line,
                    Col = col,
                    Code = code,
                    Severity = DiagnosticSeverity.Error,
                    Message = conflict.Reason,
                    Suggestion = null,
                });
            }
        }

        private static void AddTypeDiagnostics(
            DiagnosticBag bag, ParseResult parse, string source, IResolutionProvider resolver, string file)
        {
            foreach (var root in parse.Model.Roots)
            {
                WalkTypes(bag, root, parse, source, resolver, file);
            }
        }

        private static void WalkTypes(
            DiagnosticBag bag, GameObjectNode node, ParseResult parse, string source, IResolutionProvider resolver, string file)
        {
            foreach (var component in node.Components)
            {
                var resolution = resolver.ResolveComponentType(component.Type, parse.Usings);
                var (line, col) = LocationOfAnchor(source, parse.ComponentAnchors, component.LogicalId);

                switch (resolution)
                {
                    case TypeResolution.Unresolved unresolved:
                        var suggestion = unresolved.Suggestions.Count > 0
                            ? $"Did you mean {QuotedJoin(unresolved.Suggestions)}? Qualify it or add a matching using."
                            : "Qualify the type or add a matching using.";
                        bag.Add(new Diagnostic
                        {
                            File = file,
                            Line = line,
                            Col = col,
                            Code = DiagnosticCodes.UnresolvedType,
                            Severity = DiagnosticSeverity.Error,
                            Message = $"Cannot resolve component type '{component.Type.FullName}'.",
                            Suggestion = suggestion,
                        });
                        break;

                    case TypeResolution.Ambiguous ambiguous:
                        bag.Add(new Diagnostic
                        {
                            File = file,
                            Line = line,
                            Col = col,
                            Code = DiagnosticCodes.AmbiguousType,
                            Severity = DiagnosticSeverity.Error,
                            Message =
                                $"Component type '{component.Type.FullName}' is ambiguous between " +
                                $"{QuotedJoin(ambiguous.Candidates)}. Qualify it.",
                            Suggestion = "Qualify it with the full namespace.",
                        });
                        break;

                    default:
                        // Resolved / Deferred -> not an error.
                        break;
                }
            }

            foreach (var child in node.Children)
            {
                WalkTypes(bag, child, parse, source, resolver, file);
            }
        }

        private static void AddAssetDiagnostics(
            DiagnosticBag bag, ParseResult parse, string source, IResolutionProvider resolver, string file)
        {
            foreach (var root in parse.Model.Roots)
            {
                WalkAssets(bag, root, parse, source, resolver, file);
            }
        }

        private static void WalkAssets(
            DiagnosticBag bag, GameObjectNode node, ParseResult parse, string source, IResolutionProvider resolver, string file)
        {
            foreach (var component in node.Components)
            {
                foreach (var field in component.Fields)
                {
                    WalkAssetValue(bag, component.LogicalId, field.Key, field.Value, parse, source, resolver, file);
                }
            }

            foreach (var child in node.Children)
            {
                WalkAssets(bag, child, parse, source, resolver, file);
            }
        }

        private static void WalkAssetValue(
            DiagnosticBag bag,
            string componentLogicalId,
            string topLevelKey,
            ValueNode value,
            ParseResult parse,
            string source,
            IResolutionProvider resolver,
            string file)
        {
            switch (value)
            {
                case ValueNode.AssetRef assetRef:
                    var reference = assetRef.Ref;
                    if (reference == null)
                    {
                        break;
                    }

                    var resolution = reference.IsBuiltin
                        ? resolver.ResolveBuiltin(reference.DisplayPath, reference.TypeHint)
                        : resolver.ResolveAssetPath(reference.DisplayPath, null);

                    EmitAssetDiagnostic(bag, componentLogicalId, topLevelKey, reference.DisplayPath, resolution, parse, source, file);
                    break;

                case ValueNode.Nested nested:
                    foreach (var nestedField in nested.Fields)
                    {
                        WalkAssetValue(bag, componentLogicalId, topLevelKey, nestedField.Value, parse, source, resolver, file);
                    }
                    break;

                case ValueNode.List list:
                    foreach (var item in list.Items)
                    {
                        WalkAssetValue(bag, componentLogicalId, topLevelKey, item, parse, source, resolver, file);
                    }
                    break;

                default:
                    break;
            }
        }

        private static void EmitAssetDiagnostic(
            DiagnosticBag bag,
            string componentLogicalId,
            string topLevelKey,
            string displayPath,
            AssetResolution resolution,
            ParseResult parse,
            string source,
            string file)
        {
            string? suggestion;
            string message;

            switch (resolution)
            {
                case AssetResolution.Unresolved unresolved:
                    message = $"Asset path '{displayPath}' not found (no .meta on disk).";
                    suggestion = unresolved.Suggestions.Count > 0
                        ? $"Nearest: '{unresolved.Suggestions[0]}'."
                        : null;
                    break;

                case AssetResolution.Ambiguous ambiguous:
                    message = $"Asset path '{displayPath}' is ambiguous between {QuotedJoin(ambiguous.Candidates)}.";
                    suggestion = null;
                    break;

                default:
                    // Resolved / Deferred -> not an error.
                    return;
            }

            var (line, col) = LocationOfFieldSpan(source, parse, componentLogicalId, topLevelKey);

            bag.Add(new Diagnostic
            {
                File = file,
                Line = line,
                Col = col,
                Code = DiagnosticCodes.AssetPathNotFound,
                Severity = DiagnosticSeverity.Error,
                Message = message,
                Suggestion = suggestion,
            });
        }

        private static string QuotedJoin(IReadOnlyList<string> values)
        {
            var quoted = new string[values.Count];
            for (var i = 0; i < values.Count; i++)
            {
                quoted[i] = $"'{values[i]}'";
            }

            return string.Join(", ", quoted);
        }

        private static (int Line, int Col) LocationOf(string source, SourceSpan? location) =>
            location == null ? (0, 0) : (LineOf(source, location.Value.Start), ColumnOf(source, location.Value.Start));

        private static (int Line, int Col) LocationOfAnchor(
            string source, IReadOnlyDictionary<string, SourceSpan> anchors, string logicalId) =>
            anchors.TryGetValue(logicalId, out var span) ? (LineOf(source, span.Start), ColumnOf(source, span.Start)) : (0, 0);

        private static (int Line, int Col) LocationOfFieldSpan(
            string source, ParseResult parse, string componentLogicalId, string topLevelKey)
        {
            if (parse.FieldArgumentSpans.TryGetValue(componentLogicalId, out var fields)
                && fields.TryGetValue(topLevelKey, out var span))
            {
                return (LineOf(source, span.Start), ColumnOf(source, span.Start));
            }

            return LocationOfAnchor(source, parse.ComponentAnchors, componentLogicalId);
        }

        // Verbatim copy of SceneBuilderBuild.LineOf/ColumnOf (SceneBuilderBuild.cs:176-194) so
        // both the editor and headless paths convert offset -> line/col the SAME way.
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
