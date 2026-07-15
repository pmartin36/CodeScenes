using System;
using System.Collections.Generic;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Lowering
{
    public sealed record AssetRefResolution(string? DisplayPath, AssetRefError? Error)
    {
        public bool IsResolved => Error is null;
    }

    // Read-side inverse of AssetRefLowering: re-derives a GUID's DisplayPath cache-first
    // (IdentityMap.Assets), falling back to an injected guid resolver. IO-free. See
    // research.md Blueprint/DATA_FLOW.
    public static class AssetRefResolver
    {
        // Cache-first re-derivation: scan assets for a matching guid with a non-empty
        // LastKnownPath; on cache miss, fall back to the injected guid resolver.
        public static string? ReDerive(
            string guid,
            IReadOnlyList<AssetEntry> assets,
            Func<string, string?>? guidResolver = null)
        {
            if (string.IsNullOrEmpty(guid))
            {
                return null;
            }

            foreach (var asset in assets)
            {
                if (string.Equals(asset.Guid, guid, StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(asset.LastKnownPath))
                {
                    return asset.LastKnownPath;
                }
            }

            return guidResolver?.Invoke(guid);
        }

        // Located resolution: empty/null Guid is None (never an error). Otherwise
        // re-derive; a guid that maps to nothing (cache AND resolver miss) produces a
        // located AssetRefError instead of being null-coerced or dropped.
        public static AssetRefResolution Resolve(
            string objectName, string componentType, string fieldName,
            AssetRef assetRef,
            IReadOnlyList<AssetEntry> assets,
            Func<string, string?>? guidResolver = null)
        {
            if (string.IsNullOrEmpty(assetRef.Guid))
            {
                return new AssetRefResolution(null, null);
            }

            var path = ReDerive(assetRef.Guid, assets, guidResolver);
            if (path is null)
            {
                return new AssetRefResolution(
                    null,
                    new AssetRefError(objectName, componentType, fieldName, assetRef.Guid, assetRef.DisplayPath));
            }

            return new AssetRefResolution(path, null);
        }
    }
}
