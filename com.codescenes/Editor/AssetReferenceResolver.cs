#nullable enable
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using CoreAssetRef = SceneBuilder.Core.Model.AssetRef;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// The M4 asset-reference boundary between Core and Unity's <see cref="AssetDatabase"/>. Owns every
    /// path↔GUID↔object translation so Core stays IO-free:
    /// <list type="bullet">
    /// <item><see cref="LoweringResolver"/> — Build-time <c>displayPath → (guid, fileId, typeHint)</c>
    /// handed to Core's <c>AssetRefLowering</c>; a non-empty authored path that resolves to no asset is
    /// a loud failure (never a silent empty-GUID / clear).</item>
    /// <item><see cref="WriteAssetRef"/> — executes a <c>SetAssetRef</c> op: resolves
    /// <c>(guid, fileId) → UnityEngine.Object</c> and assigns <c>objectReferenceValue</c> (sub-object via
    /// <c>fileId</c>) — built-ins and project assets both resolve through the same
    /// <c>ResolveAssetObject</c> call; a null/empty GUID clears the field; a GUID that maps to nothing
    /// is a located error (§7), never a silent null.</item>
    /// <item><see cref="ReadObjectReference"/> — an object-reference field pointing at a project asset
    /// → <c>ValueNode.AssetRef</c> with the re-derived <c>DisplayPath</c>; a built-in resource →
    /// <see cref="ReadBuiltinRef"/>; a null asset field → the None inhabitant <c>AssetRef(null)</c>; a
    /// scene-object reference → <c>ValueNode.ObjectRef</c> (M5, via an injected identity resolver; a
    /// null GameObject/Component-typed field → <c>ObjectRef(null)</c>).</item>
    /// </list>
    /// FileId is taken from <see cref="AssetDatabase.TryGetGUIDAndLocalFileIdentifier(UnityEngine.Object, out string, out long)"/>
    /// on BOTH the write (via the main asset) and read sides, so the two directions agree on identity
    /// and round-trip cleanly.
    /// </summary>
    public static class AssetReferenceResolver
    {
        /// <summary>
        /// True for a GUID belonging to one of Unity's built-in resource containers. GUID is the
        /// identity authority on both the read and write sides, so both directions agree.
        /// </summary>
        public static bool IsBuiltinGuid(string? guid) =>
            guid == BuiltinCatalog.BuiltinResourcesGuid || guid == BuiltinCatalog.BuiltinExtraGuid;

        /// <summary>
        /// A Build-time lowering resolver bound to the sidecar <c>Assets[]</c> cache — the
        /// GUID-authoritative boundary that makes an authored <c>Asset("path")</c> survive the asset
        /// being moved/renamed (§"Move/rename stability"). It maps <c>displayPath → (guid, fileId,
        /// typeHint)</c> and:
        /// <list type="bullet">
        /// <item>resolves the authored path to a GUID directly when it still points at a live asset;</item>
        /// <item>when the authored path no longer resolves (asset moved/renamed), RECOVERS the GUID from
        /// the <c>Assets[]</c> cache entry whose <c>LastKnownPath</c> equals the authored path, then
        /// re-derives the asset's CURRENT path from that GUID — the reference survives;</item>
        /// <item>keeps MOVED (GUID alive at a new path) DISTINCT from MISSING: only when the resolved
        /// GUID maps to NOTHING (asset truly deleted) is it a loud, located error;</item>
        /// <item>records every resolved GUID at its CURRENT path into <see cref="Harvested"/> so Build can
        /// refresh <c>Assets[]</c> (spec: "ensure every referenced GUID has an Assets[] entry with its
        /// current path").</item>
        /// </list>
        /// Only called by Core for populated refs — <c>Asset(null)</c> never reaches here.
        /// </summary>
        public sealed class LoweringResolver
        {
            private readonly IReadOnlyList<AssetEntry> _cache;
            private readonly List<AssetEntry> _harvested = new();

            public LoweringResolver(IEnumerable<AssetEntry>? cachedAssets)
            {
                _cache = cachedAssets != null ? new List<AssetEntry>(cachedAssets) : new List<AssetEntry>();
            }

            /// <summary>
            /// Every GUID resolved during lowering, paired with its CURRENT path — Build merges these
            /// into the sidecar <c>Assets[]</c> so the cache stays a valid move-recovery source.
            /// </summary>
            public IReadOnlyList<AssetEntry> Harvested => _harvested;

            /// <summary>
            /// The non-throwing path→GUID decision shared by <see cref="Resolve"/> (the throwing Build
            /// backstop) and <see cref="UnityResolutionProvider"/> (the collect-all headless-validation
            /// seam) — ONE decision, two surfaces; never hand-sync a second copy.
            /// </summary>
            internal enum PathProbeKind
            {
                Empty,
                ContainerPath,
                MissingPath,
                DeletedGuid,
                Resolved,
            }

            internal readonly struct PathProbe
            {
                internal readonly PathProbeKind Kind;
                internal readonly string Guid;
                internal readonly long FileId;
                internal readonly string TypeHint;
                internal readonly string CurrentPath;

                internal PathProbe(PathProbeKind kind, string guid, long fileId, string typeHint, string currentPath)
                {
                    Kind = kind;
                    Guid = guid;
                    FileId = fileId;
                    TypeHint = typeHint;
                    CurrentPath = currentPath;
                }

                internal static PathProbe Empty() => new(PathProbeKind.Empty, "", 0, "", "");

                internal static PathProbe ContainerPath() => new(PathProbeKind.ContainerPath, "", 0, "", "");

                internal static PathProbe MissingPath() => new(PathProbeKind.MissingPath, "", 0, "", "");

                internal static PathProbe DeletedGuid(string guid) => new(PathProbeKind.DeletedGuid, guid, 0, "", "");

                internal static PathProbe Resolved(string guid, long fileId, string typeHint, string currentPath) =>
                    new(PathProbeKind.Resolved, guid, fileId, typeHint, currentPath);
            }

            /// <summary>
            /// The pure path→GUID decision, with NO throw and NO harvest side effect — see
            /// <see cref="PathProbe"/>. <see cref="Resolve"/> is the throwing/harvesting wrapper over this.
            /// </summary>
            internal PathProbe TryResolve(string displayPath)
            {
                if (string.IsNullOrEmpty(displayPath))
                {
                    return PathProbe.Empty();
                }

                // An authored path naming a BUILT-IN container (e.g. 'Library/unity default resources')
                // never identifies a specific object.
                if (BuiltinCatalog.IsContainerPath(displayPath))
                {
                    return PathProbe.ContainerPath();
                }

                // Prefer the live path→GUID mapping; when the authored path is stale (moved/renamed),
                // recover the GUID from the sidecar cache. A path unknown to BOTH is genuinely missing.
                var guid = AssetDatabase.AssetPathToGUID(displayPath);
                if (string.IsNullOrEmpty(guid))
                {
                    guid = RecoverGuidFromCache(displayPath);
                    if (string.IsNullOrEmpty(guid))
                    {
                        return PathProbe.MissingPath();
                    }
                }

                // GUID is the authority: re-derive the CURRENT path from it, then LOAD the asset. Load
                // (not GUIDToAssetPath emptiness) is the deletion authority — Unity retains recently
                // deleted GUID→path entries within a session, so a stale path can still come back; only a
                // GUID whose asset cannot be loaded is truly MISSING (distinct from a mere move/rename).
                var currentPath = AssetDatabase.GUIDToAssetPath(guid);
                var main = string.IsNullOrEmpty(currentPath) ? null : AssetDatabase.LoadMainAssetAtPath(currentPath);
                if (main == null)
                {
                    return PathProbe.DeletedGuid(guid);
                }

                long fileId = 0;
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(main, out _, out var localId))
                {
                    fileId = localId;
                }

                var typeHint = main.GetType().Name;
                return PathProbe.Resolved(guid, fileId, typeHint, currentPath);
            }

            /// <summary>The Core lowering delegate. See <see cref="LoweringResolver"/>.</summary>
            public (string guid, long fileId, string typeHint)? Resolve(string displayPath)
            {
                var probe = TryResolve(displayPath);
                switch (probe.Kind)
                {
                    case PathProbeKind.Empty:
                        return null;

                    case PathProbeKind.ContainerPath:
                        // An authored path naming a BUILT-IN container (e.g. 'Library/unity default
                        // resources') never identifies a specific object — it is a located, loud refusal
                        // pointing at Builtin(...) as the fix, not a warn-and-continue. This is the
                        // backstop any direct caller of Resolve inherits, even one that skipped
                        // DesiredModelLoader's located pre-pass.
                        BuiltinRefValidator.ThrowContainerPath(displayPath, location: null);
                        return null; // unreachable — ThrowContainerPath always throws.

                    case PathProbeKind.MissingPath:
                        throw new InvalidOperationException(
                            $"[SceneBuilder] Asset not found at path '{displayPath}' (referenced via Asset(\"{displayPath}\")). " +
                            "The asset is missing or not imported — fix the path or restore the asset.");

                    case PathProbeKind.DeletedGuid:
                        throw new InvalidOperationException(
                            $"[SceneBuilder] Asset {probe.Guid} (was '{displayPath}') not found — the asset was deleted. " +
                            "Restore it or remove the reference.");

                    default:
                        _harvested.Add(new AssetEntry { Guid = probe.Guid, LastKnownPath = probe.CurrentPath, TypeHint = probe.TypeHint });
                        return (probe.Guid, probe.FileId, probe.TypeHint);
                }
            }

            /// <summary>
            /// The Core lowering delegate for <c>Builtin(name[, typeHint])</c> refs — Core's
            /// <c>AssetRefLowering.Lower</c> 3rd argument. Resolves via <see cref="BuiltinCatalog"/> and
            /// NEVER harvests (a built-in has no project path to track in <see cref="Harvested"/> / the
            /// sidecar <c>Assets[]</c>). Always throws (never returns null) on a miss or an unqualifiable
            /// ambiguity — the always-on, UNLOCATED backstop; <see cref="BuiltinRefValidator.Validate"/>
            /// (run earlier, over the desired-but-unlowered model) is what gives the author a LOCATED
            /// error before lowering ever reaches this method.
            /// </summary>
            public (string guid, long fileId, string typeHint)? ResolveBuiltin(string name, string? typeHint)
                => BuiltinRefValidator.ResolveOrThrow(name, typeHint, location: null);

            private string RecoverGuidFromCache(string authoredPath)
            {
                foreach (var entry in _cache)
                {
                    if (string.Equals(entry.LastKnownPath, authoredPath, StringComparison.Ordinal)
                        && !string.IsNullOrEmpty(entry.Guid))
                    {
                        return entry.Guid;
                    }
                }

                return "";
            }
        }

        /// <summary>
        /// Reads an object-reference <see cref="SerializedProperty"/> into a Core <see cref="ValueNode"/>:
        /// a null field → <c>AssetRef(null)</c> (None) for an asset-typed field, or
        /// <c>ObjectRef(null)</c> for a GameObject/Component-typed field (M5); an asset reference → a
        /// populated <c>ValueNode.AssetRef</c> (GUID/FileId/TypeHint + re-derived DisplayPath); a
        /// scene-object reference → <c>ValueNode.ObjectRef</c> resolved via
        /// <paramref name="resolveSceneRef"/> (M5) when supplied, else <c>Unsupported</c> (build path,
        /// M4-preserved — build never reads scene refs, only writes them via <c>SetReference</c>).
        /// </summary>
        public static ValueNode ReadObjectReference(SerializedProperty p, Func<UnityEngine.Object, string?>? resolveSceneRef = null)
        {
            var obj = p.objectReferenceValue;
            if (obj == null)
            {
                // None / cleared. A GameObject/Component-typed field's None is ObjectRef(null) (M5); an
                // asset-typed field's None is the null inhabitant of ValueNode.AssetRef (M4, unchanged).
                return ObjectReferenceResolver.IsSceneObjectField(p)
                    ? new ValueNode.ObjectRef(null)
                    : new ValueNode.AssetRef(null);
            }

            // A reference to a scene GameObject/Component (not a project asset) resolves via the
            // injected identity resolver (M5) when one was supplied; otherwise (the build read path,
            // which never consumes scene refs) it stays Unsupported exactly as pre-M5.
            if (!AssetDatabase.Contains(obj))
            {
                if (resolveSceneRef == null)
                {
                    return new ValueNode.Unsupported("ObjectReference");
                }

                var id = resolveSceneRef(obj);
                return id != null ? new ValueNode.ObjectRef(id) : new ValueNode.Unsupported("ObjectReference");
            }

            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out var guid, out var fileId)
                && !string.IsNullOrEmpty(guid))
            {
                // A BUILT-IN resource (primitive mesh, Default-Material, ...) reads back as a populated
                // Builtin(...) ref via ReadBuiltinRef — its own catalog-name lookup, not the ordinary
                // path-based branch below.
                if (IsBuiltinGuid(guid))
                {
                    return ReadBuiltinRef(guid, fileId, p);
                }

                return new ValueNode.AssetRef(new CoreAssetRef
                {
                    Guid = guid,
                    FileId = fileId,
                    TypeHint = obj.GetType().Name,
                    DisplayPath = AssetDatabase.GetAssetPath(obj),
                });
            }

            return new ValueNode.Unsupported("ObjectReference");
        }

        /// <summary>
        /// Directly-callable built-in read seam: resolves a built-in <c>(guid, fileId)</c> to its
        /// catalog name via <see cref="BuiltinCatalog.TryDeriveName"/> and returns the populated
        /// <c>ValueNode.AssetRef</c> (qualifier stamped into <c>TypeHint</c> only when the bare name is
        /// ambiguous — anti-churn). A pair the catalog cannot derive (e.g. removed in this editor
        /// version) throws the located <see cref="BuiltinRefValidator.ThrowUnknownBuiltinId"/> error
        /// naming the object/component/field from <paramref name="p"/> — never returns
        /// <c>Unsupported</c>, never returns a node at all.
        /// </summary>
        internal static ValueNode ReadBuiltinRef(string guid, long fileId, SerializedProperty? p)
        {
            if (!BuiltinCatalog.TryDeriveName(guid, fileId, out var name, out var typeName, out var nameIsAmbiguous))
            {
                BuiltinRefValidator.ThrowUnknownBuiltinId(guid, fileId, LocationOf(p));
            }

            return new ValueNode.AssetRef(new CoreAssetRef
            {
                Guid = guid,
                FileId = fileId,
                IsBuiltin = true,
                DisplayPath = name,
                TypeHint = nameIsAmbiguous ? typeName : "",   // anti-churn: qualifier ONLY when required
            });
        }

        /// <summary>
        /// Executes a <c>SetAssetRef</c> op against <paramref name="so"/> at <paramref name="path"/>
        /// (which may be an array element, e.g. <c>m_Materials[0]</c>). A null/empty
        /// <paramref name="guid"/> clears the field (<c>objectReferenceValue = null</c>); otherwise the
        /// asset is resolved by <c>(guid, fileId)</c> and assigned. A GUID that resolves to nothing is a
        /// loud, located error — never a silent null. Caller commits via <c>ApplyModifiedProperties</c>.
        /// </summary>
        public static void WriteAssetRef(
            SerializedObject so, string path, string? guid, long fileId, Component owner, IdentityMap map)
        {
            var prop = FindOrCreateProperty(so, path);
            if (prop == null)
            {
                Debug.LogWarning($"[SceneBuilder] Asset-ref property '{path}' not found on '{so.targetObject}'.");
                return;
            }

            if (string.IsNullOrEmpty(guid))
            {
                // None / clear form.
                prop.objectReferenceValue = null;
                return;
            }

            var asset = ResolveAssetObject(guid!, fileId);
            if (asset == null)
            {
                var lastKnown = LastKnownPathFor(guid!, map);
                var ownerName = owner != null && owner.gameObject != null ? owner.gameObject.name : "<unknown>";
                var componentType = owner != null ? owner.GetType().Name : "<unknown>";
                throw new InvalidOperationException(
                    $"[SceneBuilder] {ownerName} > {componentType}.{FieldNameOf(path)}: " +
                    $"asset {guid} (was '{lastKnown}') not found");
            }

            prop.objectReferenceValue = asset;
        }

        /// <summary>
        /// Resolves <c>(guid, fileId)</c> to the owning <see cref="UnityEngine.Object"/> (main asset or
        /// sub-object), or null when the GUID maps to no asset. Uses
        /// <see cref="AssetDatabase.TryGetGUIDAndLocalFileIdentifier(UnityEngine.Object, out string, out long)"/>
        /// as the identity authority — the same function the read side uses — so a sub-object FileId is
        /// matched exactly and never collapsed to the main asset.
        /// </summary>
        public static UnityEngine.Object? ResolveAssetObject(string guid, long fileId)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            var main = AssetDatabase.LoadMainAssetAtPath(path);
            if (main != null
                && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(main, out var mainGuid, out var mainId)
                && mainGuid == guid && mainId == fileId)
            {
                return main;
            }

            foreach (var candidate in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (candidate == null)
                {
                    continue;
                }

                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(candidate, out var g, out var f)
                    && g == guid && f == fileId)
                {
                    return candidate;
                }
            }

            // FileId 0 is the main-asset convention; fall back to it when no exact sub-object matched.
            return fileId == 0 ? main : null;
        }

        // Resolves an ordinary property path OR an array element path (`name[index]`), sizing the array
        // up to include the index so a materialize into a shorter (or empty) live array still lands.
        // internal: also reused by ObjectReferenceResolver (M5) for the same array-aware lookup.
        internal static SerializedProperty? FindOrCreateProperty(SerializedObject so, string path)
        {
            var bracket = path.LastIndexOf('[');
            if (bracket < 0 || !path.EndsWith("]", StringComparison.Ordinal))
            {
                return so.FindProperty(path);
            }

            var arrayPath = path.Substring(0, bracket);
            var indexText = path.Substring(bracket + 1, path.Length - bracket - 2);
            if (!int.TryParse(indexText, out var index) || index < 0)
            {
                return so.FindProperty(path);
            }

            var array = so.FindProperty(arrayPath);
            if (array == null || !array.isArray)
            {
                return null;
            }

            if (array.arraySize <= index)
            {
                array.arraySize = index + 1;
            }

            return array.GetArrayElementAtIndex(index);
        }

        private static string LastKnownPathFor(string guid, IdentityMap map)
        {
            if (map?.Assets != null)
            {
                foreach (var asset in map.Assets)
                {
                    if (string.Equals(asset.Guid, guid, StringComparison.Ordinal)
                        && !string.IsNullOrEmpty(asset.LastKnownPath))
                    {
                        return asset.LastKnownPath;
                    }
                }
            }

            return "";
        }

        private static string FieldNameOf(string path)
        {
            var bracket = path.LastIndexOf('[');
            return bracket < 0 ? path : path.Substring(0, bracket);
        }

        // Same "<object> > <ComponentType>.<field>" shape BuiltinRefValidator.ValidateAssetRef builds
        // for the located pre-lowering pass — derived here from a LIVE SerializedProperty instead of the
        // desired model. Unity-null-safe: a destroyed/absent target falls back to "<unknown>".
        private static string LocationOf(SerializedProperty? p)
        {
            var target = p?.serializedObject?.targetObject;
            if (target == null)
            {
                return "<unknown>";
            }

            var owner = target as Component;
            var objectName = owner != null && owner.gameObject != null ? owner.gameObject.name : target.name;
            return $"{objectName} > {target.GetType().Name}.{p!.propertyPath}";
        }
    }
}
