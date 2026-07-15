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
    /// <c>fileId</c>); a null/empty GUID clears the field; a GUID that maps to nothing is a located
    /// error (§7), never a silent null.</item>
    /// <item><see cref="ReadObjectReference"/> — an object-reference field pointing at an asset →
    /// <c>ValueNode.AssetRef</c> with the re-derived <c>DisplayPath</c>; a null asset field → the None
    /// inhabitant <c>AssetRef(null)</c>; a scene-object reference stays Unsupported (M5).</item>
    /// </list>
    /// FileId is taken from <see cref="AssetDatabase.TryGetGUIDAndLocalFileIdentifier(UnityEngine.Object, out string, out long)"/>
    /// on BOTH the write (via the main asset) and read sides, so the two directions agree on identity
    /// and round-trip cleanly.
    /// </summary>
    public static class AssetReferenceResolver
    {
        // Unity's two BUILT-IN resource containers. Their objects (primitive meshes Cube/Sphere/
        // Capsule/Cylinder/Plane/Quad, Default-Material, Sprites-Default, the UI shaders) are NOT
        // project assets: they carry real, well-known GUIDs but live inside the editor installation,
        // so AssetDatabase.LoadMainAssetAtPath returns null for them. That null is what made the
        // resolver conclude "the asset was deleted" and throw — breaking Build for any scene holding
        // a primitive. A built-in is NOT a deleted asset and must never be reported as one.
        private const string BuiltinResourcesPath = "Library/unity default resources";
        private const string BuiltinExtraPath = "Resources/unity_builtin_extra";
        private const string BuiltinResourcesGuid = "0000000000000000e000000000000000";
        private const string BuiltinExtraGuid = "0000000000000000f000000000000000";

        /// <summary>
        /// True for a GUID belonging to one of Unity's built-in resource containers. GUID is the
        /// identity authority on both the read and write sides, so both directions agree.
        /// </summary>
        public static bool IsBuiltinGuid(string? guid) =>
            guid == BuiltinResourcesGuid || guid == BuiltinExtraGuid;

        /// <summary>True for an authored path naming a built-in resource container.</summary>
        public static bool IsBuiltinPath(string? path) =>
            path == BuiltinResourcesPath || path == BuiltinExtraPath;

        // The well-known GUID of the built-in container at the given path. Only called for a path
        // IsBuiltinPath already accepted.
        private static string BuiltinGuidFor(string path) =>
            path == BuiltinExtraPath ? BuiltinExtraGuid : BuiltinResourcesGuid;

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

            /// <summary>The Core lowering delegate. See <see cref="LoweringResolver"/>.</summary>
            public (string guid, long fileId, string typeHint)? Resolve(string displayPath)
            {
                if (string.IsNullOrEmpty(displayPath))
                {
                    return null;
                }

                // A BUILT-IN container is checked FIRST, by path: it ships with the editor, so it is
                // neither "missing at path" nor "deleted" — it must reach neither throw below. It also
                // never LOADS via LoadMainAssetAtPath, which is exactly what made the deletion check
                // misfire and break Build for any scene holding a primitive. Hand the well-known GUID
                // back so the write side recognises it and LEAVES THE LIVE VALUE ALONE; never harvest
                // it into Assets[] (a built-in has no project path to track).
                if (IsBuiltinPath(displayPath))
                {
                    Debug.LogWarning(
                        $"[SceneBuilder] Asset(\"{displayPath}\") names a Unity BUILT-IN resource. Built-in " +
                        "references are not supported (the path is shared by every built-in object and so " +
                        "cannot identify one); the field is left untouched in the scene.");
                    return (BuiltinGuidFor(displayPath), 0, "Object");
                }

                // Prefer the live path→GUID mapping; when the authored path is stale (moved/renamed),
                // recover the GUID from the sidecar cache. A path unknown to BOTH is genuinely missing.
                var guid = AssetDatabase.AssetPathToGUID(displayPath);
                if (string.IsNullOrEmpty(guid))
                {
                    guid = RecoverGuidFromCache(displayPath);
                    if (string.IsNullOrEmpty(guid))
                    {
                        throw new InvalidOperationException(
                            $"[SceneBuilder] Asset not found at path '{displayPath}' (referenced via Asset(\"{displayPath}\")). " +
                            "The asset is missing or not imported — fix the path or restore the asset.");
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
                    throw new InvalidOperationException(
                        $"[SceneBuilder] Asset {guid} (was '{displayPath}') not found — the asset was deleted. " +
                        "Restore it or remove the reference.");
                }

                long fileId = 0;
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(main, out _, out var localId))
                {
                    fileId = localId;
                }

                var typeHint = main.GetType().Name;

                _harvested.Add(new AssetEntry { Guid = guid, LastKnownPath = currentPath, TypeHint = typeHint });
                return (guid, fileId, typeHint);
            }

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
        /// a null field → <c>AssetRef(null)</c> (None); an asset reference → a populated
        /// <c>ValueNode.AssetRef</c> (GUID/FileId/TypeHint + re-derived DisplayPath); a scene-object
        /// reference → <c>Unsupported</c> (M5, still skipped by the bridge).
        /// </summary>
        public static ValueNode ReadObjectReference(SerializedProperty p)
        {
            var obj = p.objectReferenceValue;
            if (obj == null)
            {
                // None / cleared — the null inhabitant of ValueNode.AssetRef, NOT an error, NOT skipped.
                return new ValueNode.AssetRef(null);
            }

            // A reference to a scene GameObject/Component (not a project asset) is M5 — leave it
            // Unsupported so the bridge skips it exactly as it did pre-M4.
            if (!AssetDatabase.Contains(obj))
            {
                return new ValueNode.Unsupported("ObjectReference");
            }

            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out var guid, out var fileId)
                && !string.IsNullOrEmpty(guid))
            {
                // A BUILT-IN resource (primitive mesh, Default-Material, ...) is not representable as
                // an Asset("path") ref: every built-in mesh SHARES the container path
                // 'Library/unity default resources' and is distinguished only by fileId, so the
                // authored path form is ambiguous and cannot round-trip back to a specific object.
                // Emitting one produced a ref that Build could not resolve. Treat it as UNSUPPORTED —
                // the bridge skips the field whole and the Materializer records it in Plan.Skipped, so
                // it is FLAGGED, never silently dropped, and the scene's own value is left untouched.
                // Authoring a built-in from code needs a distinct form (e.g. Builtin("Cube")) — unbuilt.
                if (IsBuiltinGuid(guid))
                {
                    return new ValueNode.Unsupported("BuiltinResource");
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

            // A BUILT-IN resource cannot be resolved from (guid, fileId=0) — the container holds many
            // objects and the authored path names none of them specifically. It is NOT deleted, so it
            // must not throw; and it must NOT be cleared to null either. Leave the live value exactly
            // as the scene has it (a primitive keeps its mesh/material) and flag it in the console.
            if (IsBuiltinGuid(guid))
            {
                Debug.LogWarning(
                    $"[SceneBuilder] {(owner != null && owner.gameObject != null ? owner.gameObject.name : "<unknown>")} > " +
                    $"{(owner != null ? owner.GetType().Name : "<unknown>")}.{FieldNameOf(path)}: built-in resource " +
                    "reference is not supported and was left untouched.");
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
        private static SerializedProperty? FindOrCreateProperty(SerializedObject so, string path)
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
    }
}
