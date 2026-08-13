using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Reconcile
{
    // Renders a ValueNode.ManagedReference as the `new T { ... }` object initializer a
    // [SerializeReference] field's whole-value patch/introduce span carries. A member's value sits
    // in a real, statically-typed C# field slot here (unlike a `.Set(key, value)` call argument,
    // which accepts a loosely-typed overload) — so a DIRECT ObjectRef/AssetRef member needs an
    // explicit `.As<T>()` cast to the field's own declared type, or the initializer fails to
    // compile (CS0029: NodeHandle/AssetReference is not implicitly convertible to the field type).
    // A nested ManagedReference member needs no cast: its rendered `new NestedType { ... }`
    // expression already has the field's own concrete type, assignable to the declaring field's
    // interface/base type exactly as authored. Every other member kind renders through the
    // existing, pure SourceExpr.ValueNodeLiteral formatter unchanged.
    internal static class ManagedReferenceEmission
    {
        internal static string Render(
            ValueNode.ManagedReference node,
            Func<string?, (string? Handle, bool Introduce)> resolveOwnerHandle,
            List<SourceEdit> edits,
            string ownerNodeLogicalId,
            AssetCatalog? assetCatalog)
        {
            if (node.ConcreteType is null)
            {
                return "null";
            }

            var members = node.Fields.Select(kv =>
                kv.Key + " = " + RenderMember(
                    node.ConcreteType, kv.Key, kv.Value, resolveOwnerHandle, edits, ownerNodeLogicalId, assetCatalog));

            return "new " + node.ConcreteType.FullName + " { " + string.Join(", ", members) + " }";
        }

        private static string RenderMember(
            TypeRef concreteType,
            string fieldKey,
            ValueNode memberValue,
            Func<string?, (string? Handle, bool Introduce)> resolveOwnerHandle,
            List<SourceEdit> edits,
            string ownerNodeLogicalId,
            AssetCatalog? assetCatalog)
        {
            switch (memberValue)
            {
                case ValueNode.ManagedReference nested:
                    return Render(nested, resolveOwnerHandle, edits, ownerNodeLogicalId, assetCatalog);

                case ValueNode.ObjectRef objectRef:
                {
                    var token = ComponentReconciler.RenderObjectRefToken(
                        objectRef.TargetLogicalId, resolveOwnerHandle, edits, ownerNodeLogicalId);
                    return WithCast(token, concreteType, fieldKey);
                }

                case ValueNode.AssetRef(var assetRef):
                {
                    var rendered = SourceExpr.RenderAssetRef(assetRef, assetCatalog);
                    return WithCast(rendered, concreteType, fieldKey);
                }

                default:
                    return SourceExpr.ValueNodeLiteral(memberValue, assetCatalog);
            }
        }

        // Fully-qualified (never the short form) so the cast resolves with no dependency on the
        // builder file's own `using` directives — the member's declared type is a fact about the
        // loaded concrete type, not something the emitted file is guaranteed to have imported.
        private static string WithCast(string rendered, TypeRef concreteType, string fieldKey)
        {
            var fieldTypeFullName = ManagedFieldTypeResolver.Resolve(concreteType, fieldKey);
            return fieldTypeFullName is null ? rendered : rendered + ".As<" + fieldTypeFullName + ">()";
        }
    }

    // Resolves a [SerializeReference] concrete type's own declared field type by reflecting the
    // LIVE loaded type — legitimate at runtime (this assembly runs in-process inside the Unity
    // Editor, where the fixture/user types ARE loaded) without adding any compile-time reference to
    // Unity or user assemblies. A type not found in any loaded assembly (e.g. a headless test with
    // no such type loaded) resolves to null and the caller falls back to the uncast token — no
    // worse than before this resolver existed.
    internal static class ManagedFieldTypeResolver
    {
        private static readonly Dictionary<string, Type?> TypesByFullName = new(StringComparer.Ordinal);

        internal static string? Resolve(TypeRef concreteType, string fieldKey)
        {
            var type = ResolveType(concreteType.FullName);
            var field = type?.GetField(fieldKey, BindingFlags.Public | BindingFlags.Instance);
            return field?.FieldType.FullName;
        }

        private static Type? ResolveType(string fullName)
        {
            if (TypesByFullName.TryGetValue(fullName, out var cached))
            {
                return cached;
            }

            Type? found = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? candidate;
                try
                {
                    candidate = assembly.GetType(fullName, throwOnError: false);
                }
                catch
                {
                    candidate = null;
                }

                if (candidate != null)
                {
                    found = candidate;
                    break;
                }
            }

            TypesByFullName[fullName] = found;
            return found;
        }
    }
}
