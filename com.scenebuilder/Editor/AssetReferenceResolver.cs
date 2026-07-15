#nullable enable
using System;
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
        /// <summary>
        /// Core lowering delegate: <c>displayPath → (guid, fileId, typeHint)</c>. A path that resolves
        /// to a real asset returns its authoritative identity; a NON-EMPTY authored path that maps to no
        /// asset throws a loud error rather than returning null (which Core would treat as an empty-GUID
        /// clear). Only called by Core for populated refs — <c>Asset(null)</c> never reaches here.
        /// </summary>
        public static (string guid, long fileId, string typeHint)? LoweringResolver(string displayPath)
        {
            if (string.IsNullOrEmpty(displayPath))
            {
                return null;
            }

            var guid = AssetDatabase.AssetPathToGUID(displayPath);
            if (string.IsNullOrEmpty(guid))
            {
                throw new InvalidOperationException(
                    $"[SceneBuilder] Asset not found at path '{displayPath}' (referenced via Asset(\"{displayPath}\")). " +
                    "The asset is missing or not imported — fix the path or restore the asset.");
            }

            var main = AssetDatabase.LoadMainAssetAtPath(displayPath);
            long fileId = 0;
            string typeHint = "";
            if (main != null)
            {
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(main, out _, out var localId))
                {
                    fileId = localId;
                }

                typeHint = main.GetType().Name;
            }

            return (guid, fileId, typeHint);
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
