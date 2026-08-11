#nullable enable
using System.IO;
using UnityEditor.PackageManager;
using UnityEngine;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Resolves where builder sources and their identity sidecars live: <c>&lt;ProjectRoot&gt;/SceneBuilders/</c>,
    /// deliberately OUTSIDE Unity's asset pipeline.
    /// </summary>
    /// <remarks>
    /// Unity's asset refresh only scans <c>Assets/</c> and <c>Packages/</c>. A builder <c>.cs</c> under
    /// <c>Assets/</c> is compiled source, so every write triggers a ~2s domain reload — fatal for a sync
    /// that fires on every scene change. <c>&lt;ProjectRoot&gt;/SceneBuilders/</c> is provably outside the
    /// scanned roots, so the builder is read/written with plain <see cref="File"/> IO and Unity never
    /// imports, compiles, or reloads for it.
    /// </remarks>
    public static class SceneBuilderPaths
    {
        /// <summary>Folder, directly under the project root, holding builder sources + sidecars.</summary>
        public const string BuildersFolderName = "SceneBuilders";

        /// <summary>
        /// The folder CONTAINING <c>Assets/</c>. <see cref="Application.dataPath"/> is
        /// <c>&lt;ProjectRoot&gt;/Assets</c>, so its parent is the project root — the same resolution
        /// Unity's own <c>com.unity.ide.rider</c> package uses to place generated .csproj/.sln files.
        /// </summary>
        public static string ProjectRoot => Directory.GetParent(Application.dataPath)!.FullName;

        /// <summary>Absolute path of the builders folder. May not exist yet — see <see cref="EnsureBuildersDirectory"/>.</summary>
        public static string BuildersDirectory => Path.Combine(ProjectRoot, BuildersFolderName);

        /// <summary>File suffix of an identity sidecar.</summary>
        public const string SidecarSuffix = ".sbmap.json";

        /// <summary>File suffix of a sync checkpoint.</summary>
        public const string StateSuffix = ".sbstate.json";

        /// <summary>Absolute path of the builder source for <paramref name="builderName"/>.</summary>
        public static string Builder(string builderName) => Path.Combine(BuildersDirectory, builderName + ".cs");

        /// <summary>Absolute path of the identity sidecar for <paramref name="builderName"/>.</summary>
        public static string Sidecar(string builderName) => Path.Combine(BuildersDirectory, builderName + SidecarSuffix);

        /// <summary>Absolute path of the sync checkpoint for <paramref name="builderName"/>.</summary>
        public static string State(string builderName) => Path.Combine(BuildersDirectory, builderName + StateSuffix);

        /// <summary>
        /// Absolute path of the sync checkpoint co-located with the sidecar at <paramref name="sidecarPath"/>
        /// (i.e. <c>&lt;name&gt;.sbstate.json</c> next to <c>&lt;name&gt;.sbmap.json</c>), derived from the
        /// sidecar path itself rather than <see cref="BuildersDirectory"/> so a caller using a non-default
        /// sidecar location still gets the checkpoint alongside it.
        /// </summary>
        public static string StateForSidecar(string sidecarPath)
        {
            var dir = Path.GetDirectoryName(sidecarPath) ?? "";
            var fileName = Path.GetFileName(sidecarPath);
            var baseName = fileName.EndsWith(SidecarSuffix, System.StringComparison.Ordinal)
                ? fileName[..^SidecarSuffix.Length]
                : Path.GetFileNameWithoutExtension(fileName);
            return Path.Combine(dir, baseName + StateSuffix);
        }

        /// <summary>Folder, directly under <see cref="BuildersDirectory"/>, holding tool-generated (never hand-authored) files.</summary>
        public const string GeneratedFolderName = "Generated";

        /// <summary>File name of the generated project catalog manifest (tags/layers) under <see cref="GeneratedDirectory"/>.</summary>
        public const string ProjectCatalogFileName = "ProjectCatalog.sbcatalog.json";

        /// <summary>Absolute path of the generated-files folder. May not exist yet — see <see cref="EnsureGeneratedDirectory"/>.</summary>
        public static string GeneratedDirectory => Path.Combine(BuildersDirectory, GeneratedFolderName);

        /// <summary>Absolute path of the generated project catalog manifest.</summary>
        public static string ProjectCatalogManifestPath => Path.Combine(GeneratedDirectory, ProjectCatalogFileName);

        /// <summary>Absolute path of the generated prefab-façade manifest. File name is the Core-owned
        /// single source of truth (<see cref="SceneBuilder.Core.Model.FacadeManifest.FileName"/>).</summary>
        public static string FacadeManifestPath =>
            Path.Combine(GeneratedDirectory, SceneBuilder.Core.Model.FacadeManifest.FileName);

        /// <summary>Absolute path of the generated asset catalog manifest. File name is the Core-owned
        /// single source of truth (<see cref="SceneBuilder.Core.Model.AssetManifest.FileName"/>).</summary>
        public static string AssetManifestPath =>
            Path.Combine(GeneratedDirectory, SceneBuilder.Core.Model.AssetManifest.FileName);

        /// <summary>Folder, inside the package, holding the shipped analyzer toolkit dlls.</summary>
        public const string AnalyzersFolderName = "Analyzers~";

        /// <summary>File name of the shipped diagnostics analyzer assembly.</summary>
        public const string AnalyzerAssemblyFileName = "CodeScenes.Analyzers.dll";

        /// <summary>File name of the shipped grammar assembly the analyzer assembly depends on.</summary>
        public const string GrammarAssemblyFileName = "SceneBuilder.Grammar.dll";

        /// <summary>
        /// Absolute, on-disk root of this package (embedded, <c>Packages/</c>, or
        /// <c>Library/PackageCache</c>), or null when the package cannot be resolved.
        /// </summary>
        public static string? PackageRootPath => PackageInfo.FindForAssembly(typeof(SceneBuilderPaths).Assembly)?.resolvedPath;

        /// <summary>Absolute path of <see cref="AnalyzersFolderName"/> under the package root, or null when the package cannot be resolved.</summary>
        public static string? AnalyzersDirectory =>
            PackageRootPath == null ? null : Path.Combine(PackageRootPath, AnalyzersFolderName);

        /// <summary>
        /// Creates the builders folder if missing and returns it. Idempotent, and safe to call before
        /// every read/write so a fresh project never fails for want of the directory.
        /// </summary>
        public static string EnsureBuildersDirectory()
        {
            Directory.CreateDirectory(BuildersDirectory);
            return BuildersDirectory;
        }

        /// <summary>
        /// Creates the generated-files folder if missing and returns it. Idempotent, and safe to call
        /// before every generated-file write so a fresh project never fails for want of the directory.
        /// </summary>
        public static string EnsureGeneratedDirectory()
        {
            Directory.CreateDirectory(GeneratedDirectory);
            return GeneratedDirectory;
        }

        /// <summary>
        /// Pure compare-before-write: writes <paramref name="contents"/> to <paramref name="path"/>
        /// ONLY when it differs from what is already on disk. Returns true when a write actually
        /// happened. Unlike <see cref="WriteIfChanged"/>, this does NOT record the write with
        /// <see cref="SuppressionScope"/> — it is the primitive for callers whose output is not a
        /// builder source/sidecar the code-&gt;scene file watcher needs to drop as a self-echo (e.g.
        /// tool-generated manifests it never watches).
        /// </summary>
        public static bool WriteTextIfChanged(string path, string contents)
        {
            if (File.Exists(path) && string.Equals(File.ReadAllText(path), contents, System.StringComparison.Ordinal))
            {
                return false;
            }

            File.WriteAllText(path, contents);
            return true;
        }

        /// <summary>
        /// Compare-before-write like <see cref="WriteTextIfChanged"/>, but the write itself is atomic:
        /// content lands in a sibling <c>.tmp</c> file first, then is renamed over <paramref name="path"/>
        /// (<see cref="File.Replace(string,string,string)"/> when <paramref name="path"/> already exists,
        /// otherwise <see cref="File.Move(string,string)"/>), so a crash or interrupt mid-write never
        /// leaves a partial/corrupt file at <paramref name="path"/>. Never routes through
        /// <see cref="SuppressionScope"/> — a sync checkpoint is a tool-generated state file, not a
        /// builder source/sidecar the code-&gt;scene file watcher needs to drop as a self-echo. Returns
        /// true when a write actually happened.
        /// </summary>
        public static bool WriteTextAtomicIfChanged(string path, string contents)
        {
            if (File.Exists(path) && string.Equals(File.ReadAllText(path), contents, System.StringComparison.Ordinal))
            {
                return false;
            }

            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, contents);

            if (File.Exists(path))
            {
                File.Replace(tempPath, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, path);
            }

            return true;
        }

        /// <summary>
        /// THE write path for builder sources and sidecars: writes <paramref name="contents"/> to
        /// <paramref name="path"/> ONLY when it differs from what is already on disk. Returns true when
        /// a write actually happened — an honest "did anything change?" for callers to report.
        /// </summary>
        /// <remarks>
        /// Every writer routes through here rather than calling <see cref="File.WriteAllText(string,string)"/>
        /// directly, so idempotence is inherited by default and cannot be forgotten by a future one.
        /// It matters more than it looks: code-&gt;scene is driven by the plugin's OWN file watcher, so a
        /// write with identical content is not free — it bumps the mtime, fires the watcher, and kicks
        /// off a build for nothing. A sync that always writes is a watcher that always fires.
        /// </remarks>
        public static bool WriteIfChanged(string path, string contents)
        {
            if (!WriteTextIfChanged(path, contents))
            {
                return false;
            }

            SuppressionScope.RecordWrite(path, SuppressionScope.ComputeContentHash(contents));
            return true;
        }
    }
}
