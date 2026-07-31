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

        /// <summary>
        /// Resolves member keys in the model AND remaps the parse's field-argument spans (keyed by the
        /// same member key) in lockstep, so Reconcile's span-local field patching keeps matching.
        /// Also descends every <see cref="PrefabInstanceNode"/>'s <c>Overrides</c>/<c>AddedComponents</c>/
        /// <c>RemovedComponents</c>, resolving each <see cref="OverrideTarget.ComponentType"/> short token
        /// to its usings-resolved FullName and each <c>member:&lt;field&gt;</c>
        /// <see cref="PropertyOverride.PropertyPath"/> to its serialized path — the b1-t2 fix for the
        /// silent typed-instance-override drop (M10 short type + nested typed selector).
        /// </summary>
        /// <remarks>
        /// Call this through <see cref="DesiredModelLoader.Load"/>, which is the only product caller —
        /// resolution is one stage of source→desired and a model that has been resolved but NOT
        /// asset-ref-lowered is not safe to diff. The bare <c>Resolve(SceneModel)</c> overload that used
        /// to exist here was exactly that trap and is deliberately gone: Build used it and, by pairing
        /// it with a lowering call Sync forgot, produced the two directions' silent divergence.
        /// </remarks>
        public static (SceneModel Model, IReadOnlyDictionary<string, IReadOnlyDictionary<string, SourceSpan>> Spans) Resolve(
            SceneModel model,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, SourceSpan>> fieldArgumentSpans,
            IReadOnlyList<string> usings)
        {
            var resolved = ResolveModel(model, usings);
            var spans = RemapSpans(fieldArgumentSpans, resolved.KeyRewrites);
            return (resolved.Model, spans);
        }

        private static (SceneModel Model, Dictionary<string, Dictionary<string, string>> KeyRewrites) ResolveModel(
            SceneModel model, IReadOnlyList<string> usings)
        {
            var probes = new List<GameObject>();
            var soByType = new Dictionary<Type, SerializedObject>();
            var keyRewrites = new Dictionary<string, Dictionary<string, string>>();

            try
            {
                var roots = model.Roots.Select(n => ResolveNode(n, soByType, probes, keyRewrites, usings)).ToArray();
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
            Dictionary<string, Dictionary<string, string>> keyRewrites,
            IReadOnlyList<string> usings)
        {
            var components = node.Components.Select(c => ResolveComponent(c, soByType, probes, keyRewrites)).ToArray();
            var children = node.Children.Select(c => ResolveNode(c, soByType, probes, keyRewrites, usings)).ToArray();
            var rebuilt = node with { Components = components, Children = children };

            return rebuilt is PrefabInstanceNode instance
                ? NormalizeInstanceOverrides(instance, soByType, probes, usings)
                : rebuilt;
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

            var so = GetProbe(component.Type, soByType, probes);
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

        private static SerializedObject GetProbe(TypeRef typeRef, Dictionary<Type, SerializedObject> soByType, List<GameObject> probes)
        {
            var type = ComponentTypeResolver.Resolve(typeRef)
                ?? throw new InvalidOperationException($"[SceneBuilder] Cannot resolve component type '{typeRef.FullName}' to resolve authored member paths.");
            return GetProbe(type, soByType, probes);
        }

        private static SerializedObject GetProbe(Type type, Dictionary<Type, SerializedObject> soByType, List<GameObject> probes)
        {
            if (soByType.TryGetValue(type, out var existing))
            {
                return existing;
            }

            var probe = new GameObject("__SceneBuilderProbe") { hideFlags = HideFlags.HideAndDontSave };
            probes.Add(probe);

            // GameObject.GetComponent(Type) can return a Unity FAKE-NULL (a live C# reference wrapping a
            // null native pointer) when the component is absent — the C# `??` operator does NOT treat that
            // as null, so it must be checked with Unity's overloaded `== null` or ComponentDefaultTemplate.Create
            // is skipped and `new SerializedObject(fake-null)` throws "Object at index 0 is null".
            var component = probe.GetComponent(type);
            if (component == null)
            {
                component = ComponentDefaultTemplate.Create(probe, type);
            }

            if (component == null)
            {
                throw new InvalidOperationException(
                    $"[SceneBuilder] Could not instantiate a probe of '{type.FullName}' to resolve authored member paths.");
            }

            var so = new SerializedObject(component);
            soByType[type] = so;
            return so;
        }

        // b1-t2: descends a PrefabInstanceNode's override collections, resolving each OverrideTarget's
        // short ComponentType -> FullName (usings-aware, matching ComponentTypeNormalizer.NormalizeComponent)
        // and each PropertyPath's transient "member:<field>" sigil -> serialized path (reusing the same
        // probe/ResolvePath logic as component fields above). Mirrors the is-PrefabInstanceNode
        // descend-and-rebuild pattern in NestedOverrideBootstrap.ResolveGameObject.
        private static PrefabInstanceNode NormalizeInstanceOverrides(
            PrefabInstanceNode pin,
            Dictionary<Type, SerializedObject> soByType,
            List<GameObject> probes,
            IReadOnlyList<string> usings)
        {
            var overrides = pin.Overrides
                .Select(o =>
                {
                    var (target, type) = NormalizeTargetType(o.Target, usings);
                    var path = type != null
                        ? NormalizeOverridePath(o.PropertyPath, type, soByType, probes)
                        : o.PropertyPath;
                    return o with { Target = target, PropertyPath = path };
                })
                .ToArray();

            var addedComponents = pin.AddedComponents
                .Select(a =>
                {
                    // AddedComponent.Target locates the OWNER sub-object (its ComponentType is always
                    // "" — PrefabInstanceProbe.ReadAddedComponents never populates it), so there is
                    // nothing to normalize on Target itself here. The component's own short type token
                    // lives on Component.Type and is the thing that needs usings-aware resolution
                    // (e.g. root-target `.AddComponent<Light>()` -> "UnityEngine.Light") so the diff's
                    // (Target, Type.FullName) key matches the snapshot's live-read FullName key.
                    var componentType = ResolveComponentTypeToken(a.Component.Type.FullName, usings, out var resolvedType);
                    var component = a.Component;
                    if (resolvedType != null && componentType != a.Component.Type.FullName)
                    {
                        component = ResolveComponent(
                            component with { Type = component.Type with { FullName = componentType } },
                            soByType, probes, new Dictionary<string, Dictionary<string, string>>());
                    }
                    return a with { Component = component };
                })
                .ToArray();

            var removedComponents = pin.RemovedComponents
                .Select(t => NormalizeTargetType(t, usings).Target)
                .ToArray();

            return pin with
            {
                Overrides = overrides,
                AddedComponents = addedComponents,
                RemovedComponents = removedComponents,
            };
        }

        // Resolves an OverrideTarget's short ComponentType to its usings-resolved FullName. A target
        // with no ComponentType (e.g. a RemovedGameObject's Parent) is returned unchanged with a null
        // Type. Unresolved/ambiguous -> located throw, mirroring ComponentTypeNormalizer.NormalizeComponent.
        private static (OverrideTarget Target, Type? Type) NormalizeTargetType(OverrideTarget target, IReadOnlyList<string> usings)
        {
            if (string.IsNullOrEmpty(target.ComponentType))
            {
                return (target, null);
            }

            var fn = ResolveComponentTypeToken(target.ComponentType, usings, out var type);
            return (fn == target.ComponentType ? target : target with { ComponentType = fn }, type);
        }

        // Shared short-token -> usings-resolved-FullName resolution for both an OverrideTarget's
        // ComponentType (property/removed-component targets) and an AddedComponent's own
        // ComponentData.Type (b4-t1 fix: the latter was never normalized — AddedComponent.Target.
        // ComponentType is always "", so gating this resolution on it was a no-op that let a
        // root-target `.AddComponent<Light>()` keep the short "Light" token, mismatching the
        // snapshot's live-read FullName key in InstanceOverrideDiff.EmitAddedComponents). An empty
        // token passes through unresolved (null Type). Unresolved/ambiguous -> located throw,
        // mirroring ComponentTypeNormalizer.NormalizeComponent.
        private static string ResolveComponentTypeToken(string token, IReadOnlyList<string> usings, out Type? type)
        {
            if (string.IsNullOrEmpty(token))
            {
                type = null;
                return token;
            }

            type = ComponentTypeResolver.Resolve(new TypeRef(token), usings, out var ambiguous);

            if (type != null)
            {
                return type.FullName!;
            }

            if (ambiguous.Count >= 2)
            {
                var candidates = string.Join(", ", ambiguous.Select(t => t.FullName).OrderBy(n => n, StringComparer.Ordinal));
                var example = ambiguous.Select(t => t.FullName).OrderBy(n => n, StringComparer.Ordinal).First();
                throw new InvalidOperationException(
                    $"[SceneBuilder] instance override component type '{token}' is AMBIGUOUS — it matches "
                    + $"{candidates}. Qualify it, e.g. Component<{example}>.");
            }

            var suggestions = ComponentTypeNormalizer.SuggestQualified(token);
            var suggestClause = suggestions.Count > 0 ? $" Did you mean '{suggestions[0]}'?" : "";
            throw new InvalidOperationException(
                $"[SceneBuilder] cannot resolve instance override component type '{token}'.{suggestClause} "
                + "Qualify it, or add a matching using.");
        }

        // Rewrites a PropertyOverride.PropertyPath carrying the transient "member:<field>" sigil to its
        // real serialized path, reusing GetProbe/ResolvePath exactly as ResolveComponent does for
        // ordinary component fields. Non-member paths (already serialized, e.g. from a captured scene
        // edit) pass through unchanged.
        private static string NormalizeOverridePath(
            string path, Type resolvedType, Dictionary<Type, SerializedObject> soByType, List<GameObject> probes)
        {
            if (!path.StartsWith(MemberSigil, StringComparison.Ordinal))
            {
                return path;
            }

            var so = GetProbe(resolvedType, soByType, probes);
            var member = path.Substring(MemberSigil.Length);
            return ResolvePath(so, member, resolvedType.FullName);
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
