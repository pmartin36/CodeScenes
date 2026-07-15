#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// M3 <c>ResolveAuthoredPaths</c>: rewrites every transient <c>member:&lt;name&gt;</c> field key
    /// (produced by Core's parse for a typed setter <c>.Set(r =&gt; r.mass, v)</c>) to its real
    /// serialized <c>propertyPath</c> using a probe <see cref="SerializedObject"/> of the component's
    /// type. Runs on the desired model BEFORE any Diff, in both directions. Unresolvable member ->
    /// located error (§7). Post-resolution every <c>Fields</c> key is a serialized path.
    /// </summary>
    public static class AuthoredPathResolver
    {
        private const string MemberSigil = "member:";

        public static SceneModel Resolve(SceneModel model) => ResolveModel(model).Model;

        /// <summary>
        /// Resolves member keys in the model AND remaps the parse's field-argument spans (keyed by the
        /// same member key) in lockstep, so Reconcile's span-local field patching keeps matching.
        /// </summary>
        public static (SceneModel Model, IReadOnlyDictionary<string, IReadOnlyDictionary<string, SourceSpan>> Spans) Resolve(
            SceneModel model,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, SourceSpan>> fieldArgumentSpans)
        {
            var resolved = ResolveModel(model);
            var spans = RemapSpans(fieldArgumentSpans, resolved.KeyRewrites);
            return (resolved.Model, spans);
        }

        private static (SceneModel Model, Dictionary<string, Dictionary<string, string>> KeyRewrites) ResolveModel(SceneModel model)
        {
            var probes = new List<GameObject>();
            var soByType = new Dictionary<Type, SerializedObject>();
            var keyRewrites = new Dictionary<string, Dictionary<string, string>>();

            try
            {
                var roots = model.Roots.Select(n => ResolveNode(n, soByType, probes, keyRewrites)).ToArray();
                return (model with { Roots = roots }, keyRewrites);
            }
            finally
            {
                foreach (var probe in probes)
                {
                    UnityEngine.Object.DestroyImmediate(probe);
                }
            }
        }

        private static GameObjectNode ResolveNode(
            GameObjectNode node,
            Dictionary<Type, SerializedObject> soByType,
            List<GameObject> probes,
            Dictionary<string, Dictionary<string, string>> keyRewrites)
        {
            var components = node.Components.Select(c => ResolveComponent(c, soByType, probes, keyRewrites)).ToArray();
            var children = node.Children.Select(c => ResolveNode(c, soByType, probes, keyRewrites)).ToArray();
            return node with { Components = components, Children = children };
        }

        private static ComponentData ResolveComponent(
            ComponentData component,
            Dictionary<Type, SerializedObject> soByType,
            List<GameObject> probes,
            Dictionary<string, Dictionary<string, string>> keyRewrites)
        {
            var hasMember = component.Fields.Any(f => f.Key.StartsWith(MemberSigil, StringComparison.Ordinal));
            if (!hasMember)
            {
                return component;
            }

            var so = GetProbe(component.Type.FullName, soByType, probes);
            var rewritten = new List<KeyValuePair<string, ValueNode>>(component.Fields.Count);
            var perComponent = new Dictionary<string, string>();

            foreach (var (key, value) in component.Fields)
            {
                if (!key.StartsWith(MemberSigil, StringComparison.Ordinal))
                {
                    rewritten.Add(new KeyValuePair<string, ValueNode>(key, value));
                    continue;
                }

                var member = key.Substring(MemberSigil.Length);
                var path = ResolvePath(so, member, component.Type.FullName);
                perComponent[key] = path;
                rewritten.Add(new KeyValuePair<string, ValueNode>(path, value));
            }

            if (perComponent.Count > 0)
            {
                keyRewrites[component.LogicalId] = perComponent;
            }

            return component with { Fields = new FieldMap(rewritten) };
        }

        private static SerializedObject GetProbe(string typeFullName, Dictionary<Type, SerializedObject> soByType, List<GameObject> probes)
        {
            var type = ComponentTypeResolver.Resolve(typeFullName)
                ?? throw new InvalidOperationException($"[SceneBuilder] Cannot resolve component type '{typeFullName}' to resolve authored member paths.");

            if (soByType.TryGetValue(type, out var existing))
            {
                return existing;
            }

            var probe = new GameObject("__SceneBuilderProbe") { hideFlags = HideFlags.HideAndDontSave };
            probes.Add(probe);

            // GameObject.GetComponent(Type) can return a Unity FAKE-NULL (a live C# reference wrapping a
            // null native pointer) when the component is absent — the C# `??` operator does NOT treat that
            // as null, so it must be checked with Unity's overloaded `== null` or AddComponent is skipped
            // and `new SerializedObject(fake-null)` throws "Object at index 0 is null".
            var component = probe.GetComponent(type);
            if (component == null)
            {
                component = probe.AddComponent(type);
            }

            if (component == null)
            {
                throw new InvalidOperationException(
                    $"[SceneBuilder] Could not instantiate a probe of '{typeFullName}' to resolve authored member paths.");
            }

            var so = new SerializedObject(component);
            soByType[type] = so;
            return so;
        }

        // User MonoBehaviour serialized field: path == member name. Built-in: Unity's m_-mangled path
        // (mass -> m_Mass). Fail loud + located if neither resolves.
        private static string ResolvePath(SerializedObject so, string member, string typeFullName)
        {
            if (so.FindProperty(member) != null)
            {
                return member;
            }

            var mangled = "m_" + char.ToUpperInvariant(member[0]) + member.Substring(1);
            if (so.FindProperty(mangled) != null)
            {
                return mangled;
            }

            throw new InvalidOperationException(
                $"[SceneBuilder] Cannot resolve authored member '{member}' to a serialized path on '{typeFullName}'. " +
                "Use the raw .Set(\"m_Path\", value) form.");
        }

        private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, SourceSpan>> RemapSpans(
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, SourceSpan>> spans,
            Dictionary<string, Dictionary<string, string>> keyRewrites)
        {
            if (keyRewrites.Count == 0)
            {
                return spans;
            }

            var result = new Dictionary<string, IReadOnlyDictionary<string, SourceSpan>>();
            foreach (var (componentId, fieldSpans) in spans)
            {
                if (!keyRewrites.TryGetValue(componentId, out var rewrite))
                {
                    result[componentId] = fieldSpans;
                    continue;
                }

                var remapped = new Dictionary<string, SourceSpan>();
                foreach (var (fieldKey, span) in fieldSpans)
                {
                    remapped[rewrite.TryGetValue(fieldKey, out var newKey) ? newKey : fieldKey] = span;
                }

                result[componentId] = remapped;
            }

            return result;
        }
    }
}
