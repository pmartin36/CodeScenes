#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Echo-suppression primitive for the auto-sync loop. Two independent concerns:
    /// scene-write suppression (ref-counted, exception-safe, time-bounded) around the code-&gt;scene
    /// write chokepoint, and a source-write registry so the code-&gt;scene watcher can recognize
    /// (and drop) its own writes. Both are properties of the write path — callers/triggers never
    /// open a scope themselves.
    /// </summary>
    public static class SuppressionScope
    {
        /// <summary>Default time-bound backstop for a scene-suppression scope, in seconds.</summary>
        public const double DefaultSceneBoundSeconds = 5.0;

        private static int _sceneDepth;
        private static double _sceneDeadline;

        private static readonly Dictionary<string, string> _writes = new();

        /// <summary>
        /// True iff at least one un-disposed scene-suppression scope is outstanding AND its
        /// time-bound has not elapsed.
        /// </summary>
        public static bool SceneWriteSuppressed =>
            _sceneDepth > 0 && EditorApplication.timeSinceStartup <= _sceneDeadline;

        /// <summary>
        /// Opens a ref-counted scene-suppression scope. Disposing drains the ref-count, including
        /// on an unhandled exception thrown by the guarded body. <paramref name="boundSeconds"/> is
        /// a time-bound backstop: even a leaked (never-disposed) scope stops suppressing once it
        /// elapses.
        /// </summary>
        public static IDisposable SuppressScene(double boundSeconds = DefaultSceneBoundSeconds)
        {
            _sceneDepth++;
            _sceneDeadline = EditorApplication.timeSinceStartup + boundSeconds;
            return new Handle();
        }

        /// <summary>Stable hash of string content (SHA-256, lowercase hex).</summary>
        public static string ComputeContentHash(string content)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            byte[] hash;
            using (var sha256 = SHA256.Create())
            {
                hash = sha256.ComputeHash(bytes);
            }
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString();
        }

        /// <summary>Records that <paramref name="path"/> was just written with <paramref name="contentHash"/>.</summary>
        public static void RecordWrite(string path, string contentHash)
        {
            _writes[Path.GetFullPath(path)] = contentHash;
        }

        /// <summary>True iff the last recorded write to <paramref name="path"/> matches <paramref name="contentHash"/>.</summary>
        public static bool IsOwnWrite(string path, string contentHash)
        {
            return _writes.TryGetValue(Path.GetFullPath(path), out var recorded)
                && string.Equals(recorded, contentHash, StringComparison.Ordinal);
        }

        /// <summary>Test hygiene: resets scene-suppression depth/deadline and the write registry.</summary>
        public static void ResetForTests()
        {
            _sceneDepth = 0;
            _sceneDeadline = 0;
            _writes.Clear();
        }

        private sealed class Handle : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                _sceneDepth = Math.Max(0, _sceneDepth - 1);
            }
        }
    }
}
