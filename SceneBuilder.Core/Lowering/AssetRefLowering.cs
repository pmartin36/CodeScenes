using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Lowering
{
    // Fills Guid/FileId(/TypeHint) on unresolved ValueNode.AssetRef nodes by walking the
    // SceneModel tree and rebuilding it immutably. Non-builtin refs route through the path
    // resolver and have TypeHint stamped; built-in refs (IsBuiltin == true) route through the
    // builtinResolver and set ONLY Guid/FileId, preserving the authored TypeHint/DisplayPath
    // verbatim. See spec §Core deliverables (specs/17-builtin-resources.md).
    public static class AssetRefLowering
    {
        // fieldTypedResolver is consulted ONLY for a direct (non-nested) AssetRef value assigned to
        // a top-level component field: when supplied, it carries the field's AssetFieldContext down
        // to the resolver so a sub-asset name scan can narrow by expected type
        // (AssetReferenceResolver.LoweringResolver.TryResolveSubObject) instead of declaring
        // AMBIGUOUS. Null (every caller but DesiredModelLoader) preserves the plain untyped
        // resolution exactly. A nested/container/override AssetRef is never field-typed, matching the
        // planning-validator side of this same contract.
        public static SceneModel Lower(
            SceneModel model,
            Func<string, string?, (string guid, long fileId, string typeHint)?> resolver,
            Func<string, string?, (string guid, long fileId, string typeHint)?>? builtinResolver = null,
            Func<string, string?, AssetFieldContext, (string guid, long fileId, string typeHint)?>? fieldTypedResolver = null) =>
            Map(model, assetRef => LowerAssetRef(assetRef, resolver, builtinResolver), builtinResolver, fieldTypedResolver);

        // A typed `Assets.<Group>...<Leaf>` catalog chain (ValueNodeParser.TryParseAssetChain)
        // stamps a Guid straight from the catalog entry at PARSE time, before any lowering runs,
        // so a caller inspecting the parsed model directly sees it immediately. That stamp's
        // FileId is the catalog's own main-asset LOOKUP placeholder
        // (AssetCatalog.TryGetMember/SourceExpr.CatalogLookupFileId), never the live
        // AssetDatabase-native identity Lower's resolver computes -- and Lower's own "already
        // resolved" short-circuit (needed so a SECOND pass over an already-lowered model leaves
        // its genuinely-resolved refs untouched) cannot tell the two apart by value alone. Clears
        // every catalog-derived stamp back to unresolved so the caller's SUBSEQUENT Lower call
        // re-verifies against the live asset database instead of trusting a lookup key that was
        // never independently resolved. DesiredModelLoader.Load calls this immediately after
        // parsing, before Lower — the one seam every direction's freshly-parsed model passes
        // through, so no caller has to remember to invoke it.
        public static SceneModel ResetCatalogStampedAssetRefs(SceneModel model) =>
            Map(model, assetRef =>
                assetRef.Ref is { IsBuiltin: false } reference && reference.Guid.Length > 0
                    ? new ValueNode.AssetRef(reference with { Guid = "", FileId = 0 })
                    : assetRef);

        private static SceneModel Map(
            SceneModel model,
            Func<ValueNode.AssetRef, ValueNode.AssetRef> transform,
            Func<string, string?, (string guid, long fileId, string typeHint)?>? builtinResolver = null,
            Func<string, string?, AssetFieldContext, (string guid, long fileId, string typeHint)?>? fieldTypedResolver = null) =>
            model with { Roots = model.Roots.Select(root => MapGameObject(root, transform, builtinResolver, fieldTypedResolver)).ToArray() };

        private static GameObjectNode MapGameObject(
            GameObjectNode go,
            Func<ValueNode.AssetRef, ValueNode.AssetRef> transform,
            Func<string, string?, (string guid, long fileId, string typeHint)?>? builtinResolver,
            Func<string, string?, AssetFieldContext, (string guid, long fileId, string typeHint)?>? fieldTypedResolver)
        {
            var mapped = go with
            {
                Components = go.Components.Select(c => MapComponent(c, transform, builtinResolver, fieldTypedResolver)).ToArray(),
                Children = go.Children.Select(c => MapGameObject(c, transform, builtinResolver, fieldTypedResolver)).ToArray(),
            };

            if (mapped is PrefabInstanceNode instance)
            {
                return instance with
                {
                    Overrides = instance.Overrides.Select(o => MapPropertyOverride(o, transform)).ToArray(),
                    AddedComponents = instance.AddedComponents
                        .Select(a => a with { Component = MapComponent(a.Component, transform, builtinResolver, fieldTypedResolver) })
                        .ToArray(),
                };
            }

            return mapped;
        }

        private static PropertyOverride MapPropertyOverride(PropertyOverride @override, Func<ValueNode.AssetRef, ValueNode.AssetRef> transform)
        {
            return @override with
            {
                Value = MapNode(@override.Value, transform),
                ObjectReference = @override.ObjectReference is null
                    ? null
                    : MapNode(@override.ObjectReference, transform),
            };
        }

        private static ComponentData MapComponent(
            ComponentData component,
            Func<ValueNode.AssetRef, ValueNode.AssetRef> transform,
            Func<string, string?, (string guid, long fileId, string typeHint)?>? builtinResolver,
            Func<string, string?, AssetFieldContext, (string guid, long fileId, string typeHint)?>? fieldTypedResolver)
        {
            return component with
            {
                Fields = MapFieldMap(component.Type, component.Fields, transform, builtinResolver, fieldTypedResolver),
            };
        }

        private static FieldMap MapFieldMap(
            TypeRef componentType,
            FieldMap fields,
            Func<ValueNode.AssetRef, ValueNode.AssetRef> transform,
            Func<string, string?, (string guid, long fileId, string typeHint)?>? builtinResolver,
            Func<string, string?, AssetFieldContext, (string guid, long fileId, string typeHint)?>? fieldTypedResolver)
        {
            return new FieldMap(fields.Select(kv =>
            {
                // A DIRECT (top-level, non-nested) AssetRef field value is the only shape a
                // fieldTypedResolver ever sees — a nested/container value keeps the untyped path
                // below, matching PlanningValidator.WalkAssetValue's identical restriction.
                if (fieldTypedResolver != null && kv.Value is ValueNode.AssetRef directAssetRef)
                {
                    var fieldContext = new AssetFieldContext(componentType, kv.Key);
                    var lowered = LowerAssetRef(
                        directAssetRef,
                        (path, sub) => fieldTypedResolver(path, sub, fieldContext),
                        builtinResolver);
                    return new KeyValuePair<string, ValueNode>(kv.Key, lowered);
                }

                return new KeyValuePair<string, ValueNode>(kv.Key, MapNode(kv.Value, transform));
            }));
        }

        // The DESCENT is ValueWalk.Map, the one recursion over a value's container structure; this
        // pass supplies only its per-node decision. dropEmptiedNested: false -- both Map callers
        // replace one leaf kind and drop nothing, so an emptied nested value survives exactly as
        // it was found.
        private static ValueNode MapNode(ValueNode node, Func<ValueNode.AssetRef, ValueNode.AssetRef> transform) =>
            ValueWalk.Map(
                node,
                0,
                visit: (n, _) => n is ValueNode.AssetRef assetRef ? transform(assetRef) : n,
                descend: (_, context, _) => context,
                dropEmptiedNested: false)!;

        private static ValueNode.AssetRef LowerAssetRef(
            ValueNode.AssetRef assetRef,
            Func<string, string?, (string guid, long fileId, string typeHint)?> resolver,
            Func<string, string?, (string guid, long fileId, string typeHint)?>? builtinResolver)
        {
            var reference = assetRef.Ref;
            if (reference is null)
            {
                return assetRef;
            }

            if (!string.IsNullOrEmpty(reference.Guid))
            {
                return assetRef;
            }

            if (string.IsNullOrEmpty(reference.DisplayPath))
            {
                return assetRef;
            }

            if (reference.IsBuiltin)
            {
                // The authored TypeHint is the qualifier; "" means "the bare name is
                // unambiguous" -> null.
                var typeHint = string.IsNullOrEmpty(reference.TypeHint) ? null : reference.TypeHint;
                var hit = builtinResolver?.Invoke(reference.DisplayPath, typeHint);
                if (hit is null)
                {
                    return assetRef; // no builtinResolver, or a miss -> unresolved, no throw
                }

                // Guid + FileId ONLY. The resolved typeHint is DELIBERATELY DISCARDED: stamping
                // it would flip SourceExpr.cs's emit arm and churn Builtin("Cube") ->
                // Builtin("Cube","Mesh") on every sync. DisplayPath (the built-in NAME) is
                // likewise preserved verbatim.
                return new ValueNode.AssetRef(reference with { Guid = hit.Value.guid, FileId = hit.Value.fileId });
            }

            var subName = string.IsNullOrEmpty(reference.SubAsset) ? null : reference.SubAsset;
            var resolved = resolver(reference.DisplayPath, subName);
            if (resolved is null)
            {
                return assetRef;
            }

            var (guid, fileId, typeHint2) = resolved.Value;
            return new ValueNode.AssetRef(reference with { Guid = guid, FileId = fileId, TypeHint = typeHint2 });
        }
    }
}
