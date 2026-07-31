#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// A <see cref="DesiredModelLoader.Load"/> stage: normalizes every native/managed enum value and
    /// nested-struct type name on the SOURCE side to the SAME canonical shape
    /// <see cref="SerializedFieldBridge"/>'s read produces, via the SAME <see cref="SerializedMemberMap"/>
    /// resolver. The walk recurses through the whole value grammar (<see cref="ValueNode.Nested"/>,
    /// <see cref="ValueNode.List"/>), not just a component's top-level fields — a nested member's enum
    /// spelling and a nested struct's type name are read through the same
    /// <c>(componentType, component-rooted serialized path)</c> key a top-level field is, so they must
    /// be normalized against the same key or the two sides disagree below the top level. Wired once as
    /// a stage both sync directions inherit, so a raw <c>c.Set("m_RenderMode", 0)</c>, a short typed
    /// <c>c.Set(x =&gt; x.renderMode, RenderMode.ScreenSpaceCamera)</c>, and a short nested
    /// <c>new FontData { fontStyle = FontStyle.Bold }</c> all stay fixed points and none is rewritten
    /// to another's spelling.
    /// </summary>
    /// <remarks>
    /// Fail-soft everywhere: an unresolved component type, an unresolved or GENERIC nested struct
    /// type, an unresolved enum type, or a member that does not parse in the resolved type leaves the
    /// node exactly as authored. Never throws, never logs — an authoring mistake is caught downstream
    /// (compile or a refused write), not silently swallowed here, but it is also not this stage's job
    /// to diagnose it.
    /// </remarks>
    internal static class SerializedEnumNormalizer
    {
        public static SceneModel Normalize(SceneModel model) =>
            model with { Roots = model.Roots.Select(NormalizeNode).ToArray() };

        private static GameObjectNode NormalizeNode(GameObjectNode node)
        {
            var rebuilt = node with
            {
                Components = node.Components.Select(NormalizeComponent).ToArray(),
                Children = node.Children.Select(NormalizeNode).ToArray(),
            };

            // PrefabInstanceNode.Overrides[].Value carries Unity's own PropertyModification.value
            // STRING encoding (compared as Primitive.String in InstanceOverrideDiff) — rewriting it
            // to an Enum node would break the override diff, so it is deliberately left untouched.
            // AddedComponents[].Component is a plain ComponentData and is normalized explicitly below
            // (it is a PrefabInstanceNode-only member, so the base Components/Children rebuild above
            // never reaches it).
            if (rebuilt is PrefabInstanceNode prefabInstance)
            {
                return prefabInstance with
                {
                    AddedComponents = prefabInstance.AddedComponents
                        .Select(a => a with { Component = NormalizeComponent(a.Component) })
                        .ToArray(),
                };
            }

            return rebuilt;
        }

        private static ComponentData NormalizeComponent(ComponentData component)
        {
            var type = ComponentTypeResolver.Resolve(component.Type);
            if (type == null)
            {
                return component;
            }

            var rewritten = new List<KeyValuePair<string, ValueNode>>(component.Fields.Count);
            var changed = false;

            foreach (var (path, value) in component.Fields)
            {
                var normalized = NormalizeValue(type, path, value);
                rewritten.Add(new KeyValuePair<string, ValueNode>(path, normalized));
                changed |= !Equals(normalized, value);
            }

            return changed ? component with { Fields = new FieldMap(rewritten) } : component;
        }

        // Keyed at every depth by the SAME (componentType, component-rooted serialized path) pair
        // SerializedFieldBridge's read used for that node — that pairing is what makes source and
        // read unable to disagree. `path` is always rooted at the COMPONENT, never a struct-local
        // path (see ChildPathOf). The DESCENT itself is ValueWalk.Map, the one recursion over the
        // value grammar, so this stage cannot process a value while skipping a container kind.
        private static ValueNode NormalizeValue(Type componentType, string path, ValueNode value) =>
            ValueWalk.Map(
                value,
                (string?)path,
                (node, nodePath) => nodePath is null ? node : NormalizeNode(componentType, nodePath, node),
                (parent, parentPath, memberKey) => ChildPathOf(componentType, parent, parentPath, memberKey),
                dropEmptiedNested: false)!;

        // A null child path means "leave this subtree exactly as authored": the struct type does not
        // resolve to a concrete, non-generic managed type, so there is nothing on the read side to
        // agree with, and rewriting a generic's name would emit a backtick-arity spelling. Mirrors
        // ReadNested's own rejection.
        private static string? ChildPathOf(Type componentType, ValueNode parent, string? parentPath, string? memberKey)
        {
            if (parentPath == null)
            {
                return null;
            }

            if (memberKey == null)
            {
                return SerializedMemberMap.ElementPath(parentPath);
            }

            var structType = SerializedMemberMap.ResolveManagedFieldType(componentType, parentPath);
            return structType == null || structType.IsGenericType
                ? null
                : SerializedMemberMap.ChildPath(structType, parentPath, memberKey);
        }

        // The per-node decision, applied by the walk above at every depth.
        private static ValueNode NormalizeNode(Type componentType, string path, ValueNode value)
        {
            switch (value)
            {
                // A raw int authored where the resolver now knows an enum backs the path — e.g. a
                // hand-written `.Set("m_RenderMode", 0)`. Convert it to the SAME canonical node the
                // read side would produce for that value, so the diff compares like with like.
                case ValueNode.Primitive(PrimitiveKind.Int, int raw):
                    return SerializedMemberMap.TryEnumNode(componentType, path, raw, out var fromInt)
                        ? fromInt
                        : value;

                // An already-typed Enum node authored via a short/partial spelling — re-derive the
                // FULLY-QUALIFIED, ordinal-sorted canonical form through the SAME resolver, so it
                // stays byte-identical to what the read side would emit for the same value. Every
                // member must independently parse in the resolved type, or the node is left as
                // authored (an unresolvable spelling is a caller error surfaced elsewhere, not
                // silently rewritten here).
                case ValueNode.Enum e:
                    var enumType = SerializedMemberMap.ResolveEnumType(componentType, path);
                    if (enumType == null || e.Members.Count == 0)
                    {
                        return value;
                    }

                    return e.Members.All(m => Enum.TryParse(enumType, m, out _))
                        ? ValueNode.Enum.Canonical(enumType.FullName ?? enumType.Name, e.Members)
                        : value;

                // Record equality on ValueNode.Nested includes TypeName, so a hand-authored short
                // spelling (e.g. `FontData` under `using UnityEngine.UI;`) is as unequal as a short
                // enum member. Rewrite ONLY to the type the field actually resolves to, and only
                // when the authored spelling is a dot-boundary suffix of it — never a rename of a
                // genuinely different type. The members are the walk's business, not this arm's.
                case ValueNode.Nested nested:
                    var structType = SerializedMemberMap.ResolveManagedFieldType(componentType, path);
                    if (structType == null || structType.IsGenericType)
                    {
                        return nested;
                    }

                    var canonical = structType.FullName!.Replace('+', '.');
                    return !string.Equals(nested.TypeName, canonical, StringComparison.Ordinal)
                        && canonical.EndsWith("." + nested.TypeName, StringComparison.Ordinal)
                            ? nested with { TypeName = canonical }
                            : nested;

                // Vec2/3/4, Quat, Color, List, Unsupported, AssetRef, ObjectRef carry no enum or
                // type-name spelling of their own and are returned untouched; a container's children
                // are reached by the walk, never by an arm here.
                default:
                    return value;
            }
        }
    }
}
