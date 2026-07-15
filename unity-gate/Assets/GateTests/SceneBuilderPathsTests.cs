using System.IO;
using NUnit.Framework;
using UnityEngine;
using SceneBuilder.Editor;

// Gate for SceneBuilderPaths — proves the builder/sidecar land OUTSIDE Unity's asset pipeline.
//
// This is the whole point of the relocation: Unity's refresh only scans Assets/ and Packages/, so a
// builder .cs under Assets/ costs a ~2s domain reload per write, which is fatal for a sync that
// fires on every scene change. These assertions are against the REAL Application.dataPath of a live
// editor, not a fixture — the path math is exactly what ships.
public class SceneBuilderPathsTests
{
    [Test]
    public void ProjectRoot_IsTheFolderContainingAssets()
    {
        var root = SceneBuilderPaths.ProjectRoot;

        Assert.AreEqual(
            Path.GetFullPath(Application.dataPath),
            Path.GetFullPath(Path.Combine(root, "Assets")),
            "ProjectRoot must be the folder CONTAINING Assets/ (dataPath's parent).");
        Assert.IsTrue(Directory.Exists(root), "ProjectRoot must exist.");
    }

    [Test]
    public void BuildersDirectory_IsOutsideAssetsAndPackages()
    {
        var builders = Path.GetFullPath(SceneBuilderPaths.BuildersDirectory);
        var root = Path.GetFullPath(SceneBuilderPaths.ProjectRoot);

        Assert.AreEqual(
            Path.GetFullPath(Path.Combine(root, "SceneBuilders")),
            builders,
            "Builders must live at <ProjectRoot>/SceneBuilders/.");

        // The load-bearing claim: neither of the two roots Unity scans contains the builders folder.
        var assets = Path.GetFullPath(Path.Combine(root, "Assets")) + Path.DirectorySeparatorChar;
        var packages = Path.GetFullPath(Path.Combine(root, "Packages")) + Path.DirectorySeparatorChar;

        Assert.IsFalse(
            builders.StartsWith(assets, System.StringComparison.Ordinal),
            "Builders folder must NOT be under Assets/ — writes there trigger a domain reload.");
        Assert.IsFalse(
            builders.StartsWith(packages, System.StringComparison.Ordinal),
            "Builders folder must NOT be under Packages/ — writes there trigger a domain reload.");
    }

    [Test]
    public void BuilderAndSidecar_ResolveIntoTheBuildersDirectory()
    {
        var builders = Path.GetFullPath(SceneBuilderPaths.BuildersDirectory);

        Assert.AreEqual(
            Path.GetFullPath(Path.Combine(builders, "DemoScene.cs")),
            Path.GetFullPath(SceneBuilderPaths.Builder("DemoScene")));
        Assert.AreEqual(
            Path.GetFullPath(Path.Combine(builders, "DemoScene.sbmap.json")),
            Path.GetFullPath(SceneBuilderPaths.Sidecar("DemoScene")));
    }

    [Test]
    public void EnsureBuildersDirectory_CreatesItAndIsIdempotent()
    {
        var existedBefore = Directory.Exists(SceneBuilderPaths.BuildersDirectory);
        try
        {
            var created = SceneBuilderPaths.EnsureBuildersDirectory();
            Assert.IsTrue(Directory.Exists(created), "Builders directory must exist after Ensure.");

            // Second call must not throw — Ensure runs before every Build/Sync.
            Assert.AreEqual(created, SceneBuilderPaths.EnsureBuildersDirectory());
        }
        finally
        {
            // Leave a clean project behind if the folder was ours to create.
            if (!existedBefore && Directory.Exists(SceneBuilderPaths.BuildersDirectory))
            {
                Directory.Delete(SceneBuilderPaths.BuildersDirectory, true);
            }
        }
    }
}
