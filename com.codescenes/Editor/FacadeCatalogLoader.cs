#nullable enable
using System.IO;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Reads <see cref="SceneBuilderPaths.FacadeManifestPath"/> — THE shared load site for Build and
    /// Sync so neither direction open-codes its own read+cache (b5-t5). Fail-safe: a missing manifest
    /// (no façades regenerated yet, or none authored) returns null, and every catalog-aware Core
    /// overload already falls back to plain string forms on a null catalog.
    /// </summary>
    public static class FacadeCatalogLoader
    {
        private static string? s_cachedPath;
        private static System.DateTime s_cachedWriteTimeUtc;
        private static long s_cachedLength;
        private static FacadeCatalog? s_cached;

        /// <summary>
        /// Returns the deserialized manifest, or null when it does not exist. Cached by
        /// (path, mtime, length): a repeat call within the session skips the re-read unless the file
        /// actually changed (e.g. a b5-t2 regen bumped its mtime). Static fields are wiped on domain
        /// reload — harmless, just re-reads once.
        /// </summary>
        public static FacadeCatalog? Load()
        {
            var path = SceneBuilderPaths.FacadeManifestPath;
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

            var catalog = FacadeCatalog.Deserialize(File.ReadAllText(path));
            s_cached = catalog;
            s_cachedPath = path;
            s_cachedWriteTimeUtc = writeTimeUtc;
            s_cachedLength = length;
            return catalog;
        }
    }
}
