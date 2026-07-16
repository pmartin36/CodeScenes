#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// THE single chokepoint that turns a raw parsed <see cref="SceneModel"/> (component
    /// <c>TypeRef.FullName</c> holding whatever token the author wrote — short, qualified, or a
    /// GUID-anchored user script) into a type-QUALIFIED model, resolving each short name against the
    /// builder file's captured <c>using</c> directives the way C# itself would. Every downstream stage
    /// — <see cref="AuthoredPathResolver"/>, Diff, Materialize, Reconcile, the sidecar's
    /// <c>IdentityRemapper</c>, and <c>PlanExecutor</c>'s pre-resolve — assumes a qualified
    /// <c>FullName</c>; this is the one place that guarantee is produced.
    /// </summary>
    public static class ComponentTypeNormalizer
    {
        /// <summary>
        /// Parses <paramref name="source"/> and normalizes the resulting <see cref="ParseResult"/> so
        /// BOTH its <c>Model</c> AND its <c>IdentityMap</c> component entries carry the resolved
        /// qualified component type — the sidecar's <c>ComponentType</c> is written from
        /// <c>IdentityMap.Entries</c>, not from <c>Model</c>, so normalizing the model alone would leave
        /// the persisted identity out of sync with the type <c>IdentityRemapper</c>/<c>ComponentReconciler</c>
        /// key matching on. The join is exact: component <c>LogicalId</c> is UNCHANGED by <see cref="Normalize"/>.
        /// </summary>
        public static ParseResult ParseAndNormalize(string source, IdentityMap? existingMap)
        {
            var parse = BuilderParser.Parse(source, existingMap);
            var model = Normalize(parse.Model, parse.Usings, parse.ComponentAnchors);

            var qualifiedByLogicalId = new Dictionary<string, string>();
            CollectComponentTypes(model.Roots, qualifiedByLogicalId);
            var entries = parse.IdentityMap.Entries
                .Select(e => e.Kind == "Component"
                    && qualifiedByLogicalId.TryGetValue(e.LogicalId, out var fn)
                        ? e with { ComponentType = fn }
                        : e)
                .ToArray();
            var identityMap = parse.IdentityMap with { Entries = entries };

            // ParseResult is a plain sealed class (NOT a record) — no `with`; construct explicitly,
            // carrying every field. If a field is added to ParseResult, add it here too.
            return new ParseResult
            {
                Model = model,
                IdentityMap = identityMap,
                Anchors = parse.Anchors,
                ComponentAnchors = parse.ComponentAnchors,
                FlagPresence = parse.FlagPresence,
                FieldArgumentSpans = parse.FieldArgumentSpans,
                Handles = parse.Handles,
                NodeAnchors = parse.NodeAnchors,
                Ambiguities = parse.Ambiguities,
                Usings = parse.Usings,
            };
        }

        private static void CollectComponentTypes(GameObjectNode[] nodes, Dictionary<string, string> map)
        {
            foreach (var node in nodes)
            {
                foreach (var c in node.Components)
                {
                    map[c.LogicalId] = c.Type.FullName;
                }

                CollectComponentTypes(node.Children, map);
            }
        }

        public static SceneModel Normalize(
            SceneModel model,
            IReadOnlyList<string> usings,
            IReadOnlyDictionary<string, SourceSpan> componentAnchors)
        {
            var roots = model.Roots.Select(n => NormalizeNode(n, usings, componentAnchors)).ToArray();
            return model with { Roots = roots };
        }

        private static GameObjectNode NormalizeNode(
            GameObjectNode node,
            IReadOnlyList<string> usings,
            IReadOnlyDictionary<string, SourceSpan> componentAnchors)
        {
            var components = node.Components
                .Select(c => NormalizeComponent(node, c, usings, componentAnchors))
                .ToArray();
            var children = node.Children
                .Select(c => NormalizeNode(c, usings, componentAnchors))
                .ToArray();
            return node with { Components = components, Children = children };
        }

        private static ComponentData NormalizeComponent(
            GameObjectNode owner,
            ComponentData component,
            IReadOnlyList<string> usings,
            IReadOnlyDictionary<string, SourceSpan> componentAnchors)
        {
            var token = component.Type.FullName;
            var type = ComponentTypeResolver.Resolve(component.Type, usings, out var ambiguous);

            if (type != null)
            {
                var fn = type.FullName!;
                return fn == token ? component : component with { Type = component.Type with { FullName = fn } };
            }

            if (ambiguous.Count >= 2)
            {
                var candidates = string.Join(", ", ambiguous.Select(t => t.FullName).OrderBy(n => n, StringComparer.Ordinal));
                var example = ambiguous.Select(t => t.FullName).OrderBy(n => n, StringComparer.Ordinal).First();
                throw new InvalidOperationException(
                    Location(owner.Name, token, component.LogicalId, componentAnchors) +
                    $"component type '{token}' is AMBIGUOUS — it matches {candidates}. " +
                    $"Qualify it, e.g. Component<{example}>.");
            }

            var suggestions = SuggestQualified(token);
            var suggestClause = suggestions.Count > 0
                ? $" Did you mean '{suggestions[0]}'?"
                : "";
            throw new InvalidOperationException(
                Location(owner.Name, token, component.LogicalId, componentAnchors) +
                $"cannot resolve component type '{token}'.{suggestClause} Qualify it, or add a matching using.");
        }

        internal static IReadOnlyList<string> SuggestQualified(string token)
        {
            var results = new List<string>();
            foreach (var t in TypeCache.GetTypesDerivedFrom<UnityEngine.Component>())
            {
                if (t.Name == token && t.FullName != null && !results.Contains(t.FullName))
                {
                    results.Add(t.FullName);
                }
            }

            results.Sort(StringComparer.Ordinal);
            return results;
        }

        private static string Location(
            string objectName,
            string token,
            string logicalId,
            IReadOnlyDictionary<string, SourceSpan> componentAnchors)
        {
            var span = componentAnchors.TryGetValue(logicalId, out var s)
                ? $" (source {s.Start}..{s.Start + s.Length})"
                : "";
            return $"[SceneBuilder] {objectName} > '{token}'{span}: ";
        }
    }
}
