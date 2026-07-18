#nullable enable
using System;
using System.Linq;
using UnityEditor;
using SceneBuilder.Core.Model;
using CoreAssetRef = SceneBuilder.Core.Model.AssetRef;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// THE single decision site for whether a built-in resource reference (authored via
    /// <c>Builtin(name[, typeHint])</c>, or an authored container path that should have been) can be
    /// resolved, and the one place that builds its error message. Three callers share it:
    /// <list type="bullet">
    /// <item><see cref="ResolveOrThrow"/> — called UNLOCATED (no object/component/field) from
    /// <see cref="AssetReferenceResolver.LoweringResolver.ResolveBuiltin"/>, the always-on backstop every
    /// <c>AssetRefLowering.Lower</c> caller inherits regardless of whether it went through
    /// <see cref="DesiredModelLoader"/>.</item>
    /// <item><see cref="Validate"/> — a LOCATED pre-lowering pass over the desired-but-unlowered model,
    /// called from <see cref="DesiredModelLoader.Load"/>, which walks the tree (it, not
    /// <c>AssetRefLowering</c>, knows the object/component/field) and calls <see cref="ResolveOrThrow"/>
    /// / <see cref="ThrowContainerPath"/> with a real <c>location</c> so the thrown message is
    /// immediately actionable.</item>
    /// <item><see cref="ThrowUnknownBuiltinId"/> — called LOCATED from
    /// <see cref="AssetReferenceResolver.ReadBuiltinRef"/>, the scene→code read seam, when a live
    /// built-in <c>(guid, fileId)</c> derives no catalog object.</item>
    /// </list>
    /// Never returns null on a miss/ambiguity/container-path — always throws
    /// <see cref="InvalidOperationException"/>, so a silent skip (which Core's lowering degrades an
    /// unresolved built-in to) can never happen once this pass runs.
    /// </summary>
    internal static class BuiltinRefValidator
    {
        /// <summary>
        /// The single resolvability decision + message for a built-in NAME lookup. A catalog hit returns
        /// its real <c>(guid, fileId, typeHint)</c>; a miss or an unqualifiable ambiguity THROWS with a
        /// message that names the bad name and (per case) near-miss suggestions or both candidate types.
        /// <paramref name="location"/> == null produces the unlocated backstop text (no leading
        /// "object &gt; Component.field: " clause); non-null produces the located text.
        /// </summary>
        internal static (string guid, long fileId, string typeHint) ResolveOrThrow(
            string name, string? typeHint, string? location)
        {
            var obj = BuiltinCatalog.Resolve(name, typeHint, out var ambiguous);
            if (obj != null)
            {
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out var guid, out var fileId)
                    || string.IsNullOrEmpty(guid))
                {
                    throw new InvalidOperationException(
                        Prefix(location) + $"built-in resource '{name}' could not be identified.");
                }

                return (guid, fileId, obj.GetType().Name);
            }

            if (ambiguous)
            {
                var types = BuiltinCatalog.CandidateTypeNames(name);
                var example = types.Count > 0 ? types[0] : "";
                throw new InvalidOperationException(
                    Prefix(location) +
                    $"built-in name '{name}' is AMBIGUOUS — it matches {string.Join(", ", types)}. " +
                    $"Qualify it, e.g. Builtin(\"{name}\", \"{example}\").");
            }

            var suggestions = BuiltinCatalog.Suggest(name, typeHint).ToList();
            var suggestClause = suggestions.Count > 0
                ? $" Did you mean: {string.Join(", ", suggestions)}?"
                : "";
            throw new InvalidOperationException(
                Prefix(location) +
                $"no built-in resource named '{name}'.{suggestClause} — authored via Builtin(\"{name}\").");
        }

        /// <summary>
        /// Throws the located/unlocated error for an authored path naming a built-in resource CONTAINER
        /// (e.g. <c>Asset("Library/unity default resources")</c>) instead of a specific object, pointing
        /// the author at <c>Builtin(...)</c> as the fix.
        /// </summary>
        internal static void ThrowContainerPath(string path, string? location)
        {
            throw new InvalidOperationException(
                Prefix(location) +
                $"Asset(\"{path}\") names a built-in resource CONTAINER, not an asset — the path is shared " +
                "by every built-in object. Use the built-in's own name instead, e.g. Builtin(\"Cube\").");
        }

        /// <summary>
        /// Throws the located error for a built-in <c>(guid, fileId)</c> pair that <see
        /// cref="BuiltinCatalog.TryDeriveName"/> could not derive a catalog object for — e.g. the
        /// object was renamed/removed in this editor version. Called from
        /// <see cref="AssetReferenceResolver.ReadBuiltinRef"/>, the scene→code read seam.
        /// </summary>
        internal static void ThrowUnknownBuiltinId(string guid, long fileId, string? location)
        {
            throw new InvalidOperationException(
                Prefix(location) +
                $"built-in resource (guid={guid}, fileId={fileId}) is not known to this editor version — " +
                "it may have been renamed or removed. Re-author the field to point at its current built-in name.");
        }

        /// <summary>
        /// A LOCATED pre-pass over the DESIRED-but-unlowered model (serialized field paths already
        /// resolved by <see cref="AuthoredPathResolver"/>): throws on the first unresolvable
        /// <c>Builtin(...)</c> or authored container path, naming the object, the component type and the
        /// field. Mirrors <c>AssetRefLowering.LowerAssetRef</c>'s guard order exactly (skip <c>Ref ==
        /// null</c>, skip an already-resolved <c>Guid</c>, skip an empty <c>DisplayPath</c>) so the two
        /// agree on what "unresolved built-in" means.
        /// </summary>
        internal static void Validate(SceneModel model)
        {
            foreach (var root in model.Roots)
            {
                ValidateGameObject(root);
            }
        }

        private static void ValidateGameObject(GameObjectNode go)
        {
            foreach (var component in go.Components)
            {
                var componentTypeName = SimpleTypeName(component.Type.FullName);
                foreach (var kv in component.Fields)
                {
                    ValidateField(go.Name, componentTypeName, kv.Key, kv.Value);
                }
            }

            foreach (var child in go.Children)
            {
                ValidateGameObject(child);
            }
        }

        private static void ValidateField(string objectName, string componentTypeName, string path, ValueNode node)
        {
            switch (node)
            {
                case ValueNode.AssetRef assetRef:
                    ValidateAssetRef(objectName, componentTypeName, path, assetRef.Ref);
                    break;
                case ValueNode.List list:
                    for (var i = 0; i < list.Items.Count; i++)
                    {
                        ValidateField(objectName, componentTypeName, $"{path}[{i}]", list.Items[i]);
                    }

                    break;
                case ValueNode.Nested nested:
                    foreach (var kv in nested.Fields)
                    {
                        ValidateField(objectName, componentTypeName, $"{path}.{kv.Key}", kv.Value);
                    }

                    break;
            }
        }

        private static void ValidateAssetRef(
            string objectName, string componentTypeName, string path, CoreAssetRef? reference)
        {
            if (reference is null
                || !string.IsNullOrEmpty(reference.Guid)
                || string.IsNullOrEmpty(reference.DisplayPath))
            {
                return;
            }

            var location = $"{objectName} > {componentTypeName}.{path}";

            if (reference.IsBuiltin)
            {
                var typeHint = string.IsNullOrEmpty(reference.TypeHint) ? null : reference.TypeHint;
                ResolveOrThrow(reference.DisplayPath, typeHint, location);
                return;
            }

            if (BuiltinCatalog.IsContainerPath(reference.DisplayPath))
            {
                ThrowContainerPath(reference.DisplayPath, location);
            }

            // b3-t4: located pre-pass backstop for direct-Load callers (Sync healing) that skip
            // PlanningValidator's collect-all walk. Guarded on the MAIN asset still being live at
            // the authored path: a stale/moved path is left to Resolve's own move-recovery backstop
            // (which throws unlocated on a genuine miss) instead of falsely reporting "no sub-asset"
            // for an asset that simply moved.
            if (!string.IsNullOrEmpty(reference.SubAsset)
                && AssetDatabase.LoadMainAssetAtPath(reference.DisplayPath) != null)
            {
                AssetReferenceResolver.LoweringResolver.ResolveSubObjectOrThrow(
                    reference.DisplayPath, reference.SubAsset, location);
            }
        }

        private static string SimpleTypeName(string fullName)
        {
            var dot = fullName.LastIndexOf('.');
            return dot >= 0 ? fullName.Substring(dot + 1) : fullName;
        }

        private static string Prefix(string? location) =>
            location != null ? $"[SceneBuilder] {location}: " : "[SceneBuilder] ";
    }
}
