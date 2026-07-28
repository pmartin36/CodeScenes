#nullable enable
using System.IO;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Reads <see cref="SceneBuilderPaths.AssetManifestPath"/> — THE shared load site for Build and
    /// Sync so neither direction open-codes its own read+cache. Mirrors <see cref="FacadeCatalogLoader"/>
    /// with one deliberate deviation: an UNPARSEABLE manifest (not just an absent one) also returns
    /// null rather than throwing, matching every catalog-aware Core overload's null-safe fallback.
    /// </summary>
    public static class AssetCatalogLoader
    {
        private static string? s_cachedPath;
        private static System.DateTime s_cachedWriteTimeUtc;
        private static long s_cachedLength;
        private static AssetCatalog? s_cached;

        /// <summary>
        /// Returns the deserialized manifest, or null when it does not exist or fails to parse.
        /// Cached by (path, mtime, length): a repeat call within the session skips the re-read unless
        /// the file actually changed.
        /// </summary>
        public static AssetCatalog? Load()
        {
            var path = SceneBuilderPaths.AssetManifestPath;
            if (!File.Exists(path))
            {
                s_cached = null;
                s_cachedPath = null;
                return null;
            }

            var info = new FileInfo(path);
            var writeTimeUtc = info.LastWriteTimeUtc;
            var length = info.Length;

            if (s_cached != null
                && string.Equals(s_cachedPath, path, System.StringComparison.Ordinal)
                && s_cachedWriteTimeUtc == writeTimeUtc
                && s_cachedLength == length)
            {
                return s_cached;
            }

            AssetCatalog catalog;
            try
            {
                catalog = AssetCatalog.Deserialize(File.ReadAllText(path));
            }
            catch
            {
                s_cached = null;
                s_cachedPath = null;
                return null;
            }

            s_cached = catalog;
            s_cachedPath = path;
            s_cachedWriteTimeUtc = writeTimeUtc;
            s_cachedLength = length;
            return catalog;
        }
    }
}
