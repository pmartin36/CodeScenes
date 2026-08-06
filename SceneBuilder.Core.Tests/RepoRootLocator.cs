using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace SceneBuilder.Core.Tests
{
    // The one owner of the "walk up from this file to the directory containing SceneBuilder.sln"
    // resolution. Callers pass no path of their own: the anchor is captured here so the resolved
    // root never varies with which test in the assembly calls it.
    internal static class RepoRootLocator
    {
        public static string Find()
        {
            var dir = Path.GetDirectoryName(ThisFile());
            while (dir != null && !File.Exists(Path.Combine(dir, "SceneBuilder.sln")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            if (dir == null)
            {
                throw new InvalidOperationException(
                    $"RepoRootLocator.Find: no SceneBuilder.sln found walking up from '{ThisFile()}'");
            }

            return dir;
        }

        private static string ThisFile([CallerFilePath] string p = "") => p;
    }
}
