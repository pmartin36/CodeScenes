using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SceneBuilder.Editor;

// Regression: a converged (0-op) build must NOT re-save the .unity asset. SceneBuildSyncTarget.Persist()
// unconditionally MarkSceneDirty + SaveScene, and RunCore called it with no plan.Ops guard, so a build
// that did nothing still rewrote the scene file on disk (mtime bump + VCS-modified flag + editor I/O on
// every keystroke under auto-sync). The FIRST build must still save (it stamps identity into the file).
public class ConvergedBuildNoResaveTests
{
    private string _dir;
    private string _builderPath;
    private string _sidecarPath;
    private const string ScenePath = "Assets/GateTests/__NoResaveTemp.unity";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_noresave_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "NoResaveScene.cs");
        _sidecarPath = Path.Combine(_dir, "NoResaveScene.sbmap.json");

        File.WriteAllText(_builderPath, @"
using SceneBuilder.Authoring;
public class NoResaveScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""NoResaveCube"");
    }
}");
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }

        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Test]
    public void ConvergedBuild_DoesNotReSaveScene_ButFirstBuildDoes()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // Build 1: creates the object (ops > 0) and MUST write the scene file to disk.
        var r1 = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(r1.Diagnostics, "build 1 refused: " + string.Join(";", r1.Diagnostics.Select(d => d.Message)));

        var fullPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, ScenePath);
        Assert.IsTrue(File.Exists(fullPath), "first build did not write the scene file — id-stamping save path broken");
        Assert.IsNotNull(GameObject.Find("NoResaveCube"), "first build did not create the authored object");

        var hashBefore = Md5(fullPath);
        var mtimeBefore = File.GetLastWriteTimeUtc(fullPath).Ticks;

        System.Threading.Thread.Sleep(1100); // exceed any filesystem mtime resolution

        // Build 2: nothing changed — a converged, 0-op build.
        var r2 = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(r2.Diagnostics, "build 2 refused: " + string.Join(";", r2.Diagnostics.Select(d => d.Message)));
        Assert.AreEqual(0, r2.PlanOpCount, "build 2 was not a 0-op converged build");

        var mtimeAfter = File.GetLastWriteTimeUtc(fullPath).Ticks;
        var hashAfter = Md5(fullPath);

        // The converged build must NOT rewrite the scene file.
        Assert.AreEqual(mtimeBefore, mtimeAfter,
            "converged 0-op build re-wrote the scene file (mtime advanced) — the pointless re-save is back");
        Assert.AreEqual(hashBefore, hashAfter, "converged 0-op build rewrote the scene bytes");
    }

    private static string Md5(string path)
    {
        using var md5 = MD5.Create();
        return System.BitConverter.ToString(md5.ComputeHash(File.ReadAllBytes(path)));
    }
}
