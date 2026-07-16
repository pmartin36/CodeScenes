using System;
using System.IO;
using System.Linq;
using SceneBuilder.Core.Validation;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class ProjectLayoutTests : IDisposable
    {
        private readonly string _tempRoot;

        public ProjectLayoutTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "ProjectLayoutTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort cleanup */ }
        }

        private string MakeUnityProject(string name)
        {
            var root = Path.Combine(_tempRoot, name);
            Directory.CreateDirectory(Path.Combine(root, "Assets"));
            Directory.CreateDirectory(Path.Combine(root, "Library", "ScriptAssemblies"));
            Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
            return root;
        }

        [Fact]
        public void Infer_NestedBuilderPath_FindsProjectRoot()
        {
            var root = MakeUnityProject("proj1");
            var builderDir = Path.Combine(root, "SceneBuilders", "Deep");
            Directory.CreateDirectory(builderDir);
            var builderPath = Path.Combine(builderDir, "Foo.cs");
            File.WriteAllText(builderPath, "// builder");

            var layout = ProjectLayout.Infer(builderPath);

            Assert.Equal(Path.GetFullPath(root), Path.GetFullPath(layout.ProjectRoot));
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "Assets")), Path.GetFullPath(layout.AssetsRoot));
            Assert.Equal(
                Path.GetFullPath(Path.Combine(root, "Library", "ScriptAssemblies")),
                Path.GetFullPath(layout.ScriptAssembliesDir));
        }

        [Fact]
        public void Infer_ParsesEditorVersion_FromProjectVersionTxt()
        {
            var root = MakeUnityProject("proj2");
            File.WriteAllText(
                Path.Combine(root, "ProjectSettings", "ProjectVersion.txt"),
                "m_EditorVersion: 6000.5.3f1\nm_EditorVersionWithRevision: 6000.5.3f1 (c2eb47b3a2a9)\n");
            var builderPath = Path.Combine(root, "Foo.cs");
            File.WriteAllText(builderPath, "// builder");

            var layout = ProjectLayout.Infer(builderPath);

            Assert.Equal("6000.5.3f1", layout.EditorVersion);
        }

        [Fact]
        public void Infer_ProjectOverride_UsesGivenRoot()
        {
            var root = MakeUnityProject("proj3");
            // Builder file lives entirely outside any Unity project tree.
            var builderPath = Path.Combine(_tempRoot, "Foo.cs");
            File.WriteAllText(builderPath, "// builder");

            var layout = ProjectLayout.Infer(builderPath, projectOverride: root);

            Assert.Equal(Path.GetFullPath(root), Path.GetFullPath(layout.ProjectRoot));
        }

        [Fact]
        public void Infer_ManagedOverride_ExistingDir_IsAvailable()
        {
            var root = MakeUnityProject("proj4");
            var managedDir = Path.Combine(_tempRoot, "FakeManaged");
            Directory.CreateDirectory(managedDir);
            var builderPath = Path.Combine(root, "Foo.cs");
            File.WriteAllText(builderPath, "// builder");

            var layout = ProjectLayout.Infer(builderPath, managedOverride: managedDir);

            Assert.Equal(Path.GetFullPath(managedDir), Path.GetFullPath(layout.ManagedDllDir!));
            Assert.True(layout.ManagedDllsAvailable);
        }

        [Fact]
        public void Infer_NoProjectRoot_Throws()
        {
            var bareDir = Path.Combine(_tempRoot, "NoProjectHere");
            Directory.CreateDirectory(bareDir);
            var builderPath = Path.Combine(bareDir, "Foo.cs");
            File.WriteAllText(builderPath, "// builder");

            Assert.Throws<InvalidOperationException>(() => ProjectLayout.Infer(builderPath));
        }

        [Fact]
        public void ResolveManagedDir_ManagedUnlocatable_ReturnsNull()
        {
            var result = ProjectLayout.ResolveManagedDir(
                editorVersion: "0000.0.0f0",
                unityEditorPath: null,
                homeDir: _tempRoot);

            Assert.Null(result);
        }

        [Fact]
        public void ResolveManagedDir_UnityEditorExe_WinsOverHub()
        {
            var editorDir = Path.Combine(_tempRoot, "Editor");
            var managedDir = Path.Combine(editorDir, "Data", "Managed");
            Directory.CreateDirectory(managedDir);
            var unityExePath = Path.Combine(editorDir, "Unity");

            // Hub-default candidate under a bogus version must NOT be the one returned.
            var result = ProjectLayout.ResolveManagedDir(
                editorVersion: "0000.0.0f0",
                unityEditorPath: unityExePath,
                homeDir: _tempRoot);

            Assert.Equal(Path.GetFullPath(managedDir), Path.GetFullPath(result!));
        }

        [Fact]
        public void ReferenceAssemblies_IncludesManagedAndScriptAssemblies()
        {
            var root = MakeUnityProject("proj5");
            var managedDir = Path.Combine(_tempRoot, "FakeManaged2");
            Directory.CreateDirectory(managedDir);
            File.WriteAllText(Path.Combine(managedDir, "UnityEngine.dll"), "stub");
            File.WriteAllText(Path.Combine(root, "Library", "ScriptAssemblies", "GateFixtures.dll"), "stub");
            var builderPath = Path.Combine(root, "Foo.cs");
            File.WriteAllText(builderPath, "// builder");

            var layout = ProjectLayout.Infer(builderPath, managedOverride: managedDir);

            Assert.Contains(layout.ReferenceAssemblies, p => Path.GetFileName(p) == "UnityEngine.dll");
            Assert.Contains(layout.ReferenceAssemblies, p => Path.GetFileName(p) == "GateFixtures.dll");
        }

        [Fact]
        public void Infer_ManagedUnlocatable_ReferenceAssembliesEmpty_SkipNotPass()
        {
            var root = MakeUnityProject("proj6");
            File.WriteAllText(
                Path.Combine(root, "ProjectSettings", "ProjectVersion.txt"),
                "m_EditorVersion: 0000.0.0f0\n");
            var builderPath = Path.Combine(root, "Foo.cs");
            File.WriteAllText(builderPath, "// builder");

            var layout = ProjectLayout.Infer(builderPath, managedOverride: Path.Combine(_tempRoot, "DoesNotExist"));

            Assert.False(layout.ManagedDllsAvailable);
            Assert.Null(layout.ManagedDllDir);
            Assert.Empty(layout.ReferenceAssemblies);
        }
    }
}
