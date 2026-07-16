using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SceneBuilder.Core.Validation
{
    public sealed class ProjectLayout
    {
        public string ProjectRoot { get; }
        public string AssetsRoot { get; }
        public string ScriptAssembliesDir { get; }
        public string? EditorVersion { get; }
        public string? ManagedDllDir { get; }
        public bool ManagedDllsAvailable => ManagedDllDir is not null;

        public IReadOnlyList<string> ReferenceAssemblies { get; }

        private ProjectLayout(
            string projectRoot,
            string assetsRoot,
            string scriptAssembliesDir,
            string? editorVersion,
            string? managedDllDir,
            IReadOnlyList<string> referenceAssemblies)
        {
            ProjectRoot = projectRoot;
            AssetsRoot = assetsRoot;
            ScriptAssembliesDir = scriptAssembliesDir;
            EditorVersion = editorVersion;
            ManagedDllDir = managedDllDir;
            ReferenceAssemblies = referenceAssemblies;
        }

        public static ProjectLayout Infer(
            string builderFilePath, string? projectOverride = null, string? managedOverride = null)
        {
            var projectRoot = projectOverride ?? FindProjectRoot(builderFilePath);

            var assetsRoot = Path.Combine(projectRoot, "Assets");
            var scriptAssembliesDir = Path.Combine(projectRoot, "Library", "ScriptAssemblies");

            var editorVersion = ReadEditorVersion(Path.Combine(projectRoot, "ProjectSettings", "ProjectVersion.txt"));

            string? managedDllDir;
            if (managedOverride != null)
            {
                managedDllDir = Directory.Exists(managedOverride) ? managedOverride : null;
            }
            else
            {
                managedDllDir = ResolveManagedDir(
                    editorVersion,
                    Environment.GetEnvironmentVariable("UNITY_EDITOR"),
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            }

            var referenceAssemblies = managedDllDir is not null
                ? BuildReferenceAssemblies(managedDllDir, scriptAssembliesDir)
                : Array.Empty<string>();

            return new ProjectLayout(
                projectRoot, assetsRoot, scriptAssembliesDir, editorVersion, managedDllDir, referenceAssemblies);
        }

        internal static string? ResolveManagedDir(
            string? editorVersion, string? unityEditorPath, string homeDir)
        {
            string? candidate = null;
            if (!string.IsNullOrEmpty(unityEditorPath))
            {
                var editorDir = Path.GetDirectoryName(unityEditorPath);
                if (editorDir is not null)
                {
                    candidate = Path.Combine(editorDir, "Data", "Managed");
                }
            }
            else if (!string.IsNullOrEmpty(editorVersion))
            {
                candidate = Path.Combine(homeDir, "Unity", "Hub", "Editor", editorVersion, "Editor", "Data", "Managed");
            }

            return candidate is not null && Directory.Exists(candidate) ? candidate : null;
        }

        private static string FindProjectRoot(string builderFilePath)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(builderFilePath));
            while (dir is not null)
            {
                if (Directory.Exists(Path.Combine(dir, "ProjectSettings")) &&
                    Directory.Exists(Path.Combine(dir, "Assets")))
                {
                    return dir;
                }

                dir = Directory.GetParent(dir)?.FullName;
            }

            throw new InvalidOperationException(
                $"Could not locate a Unity project root above '{builderFilePath}'. Pass --project.");
        }

        private static string? ReadEditorVersion(string projectVersionPath)
        {
            if (!File.Exists(projectVersionPath))
            {
                return null;
            }

            foreach (var line in File.ReadAllLines(projectVersionPath))
            {
                const string prefix = "m_EditorVersion:";
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return line.Substring(prefix.Length).Trim();
                }
            }

            return null;
        }

        private static IReadOnlyList<string> BuildReferenceAssemblies(string managedDllDir, string scriptAssembliesDir)
        {
            var result = new List<string>();
            result.AddRange(Directory.GetFiles(managedDllDir, "*.dll"));

            var unityEngineDir = Path.Combine(managedDllDir, "UnityEngine");
            if (Directory.Exists(unityEngineDir))
            {
                result.AddRange(Directory.GetFiles(unityEngineDir, "*.dll"));
            }

            if (Directory.Exists(scriptAssembliesDir))
            {
                result.AddRange(Directory.GetFiles(scriptAssembliesDir, "*.dll"));
            }

            return result;
        }
    }
}
