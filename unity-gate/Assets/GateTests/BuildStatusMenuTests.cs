using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using SceneBuilder.Editor;
using SceneBuilder.Editor.Licensing;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

// Gate for SceneBuilderBuildStatusMenu: the glanceable CodeScenes/ status surface that reads
// SceneBuilderBuildStatus. Reports "No build errors" (disabled, no ping target) while every
// builder is clean and "Build error: <file>:<line>" (enabled, ping target set to the offending
// file/line/col) while any builder carries a standing refusal. The surface is recomputed live
// from the recorder on every read and the visible menu item is rebuilt to match.
public class BuildStatusMenuTests
{
    private const string BuilderName = "BuildStatusMenuScene";
    private const string ScenePath = "Assets/GateTests/__BuildStatusMenuTemp.unity";

    private string _builderPath;
    private string _sidecarPath;
    private string _statePath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class BuildStatusMenuScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    [SetUp]
    public void SetUp()
    {
        LicenseGate.ResetToDefault();
        SceneBuilderPaths.EnsureBuildersDirectory();
        _builderPath = SceneBuilderPaths.Builder(BuilderName);
        _sidecarPath = SceneBuilderPaths.Sidecar(BuilderName);
        _statePath = SceneBuilderPaths.StateForSidecar(_sidecarPath);

        SceneBuilderRouter.ResetForTests();
        SceneBuilderBuildStatus.ResetForTests();
        SceneBuilderBuildStatusMenu.ResetForTests();
        ConflictSurfacing.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        LicenseEnforcement.Register();
        SceneBuilderRouter.ResetForTests();
        SceneBuilderBuildStatus.ResetForTests();
        SceneBuilderBuildStatusMenu.ResetForTests();
        ConflictSurfacing.Clear();

        if (File.Exists(_builderPath)) File.Delete(_builderPath);
        if (File.Exists(_sidecarPath)) File.Delete(_sidecarPath);
        if (File.Exists(_statePath)) File.Delete(_statePath);

        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }
    }

    private void BuildClean()
    {
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Alpha\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        SceneBuilderRouter.ResetForTests();
    }

    private void BuildRefused()
    {
        File.WriteAllText(_builderPath, Source(
            "        var cube = scene.Add(\"Cube\");\n        cube.Component<Rigidbdy>();"));

        LogAssert.Expect(LogType.Error, new Regex(Regex.Escape(_builderPath) + @"\(\d+,\d+\)"));

        SceneBuilderBuild.BuildActiveScene();
    }

    [Test]
    public void Clean_NoRefusal_ReportsNoBuildErrors()
    {
        Assert.IsFalse(SceneBuilderBuildStatusMenu.HasBuildError);
        Assert.AreEqual("No build errors", SceneBuilderBuildStatusMenu.StatusLabel);
        Assert.IsNull(SceneBuilderBuildStatusMenu.PingTarget);
    }

    [Test]
    public void Refusal_ReportsBuildErrorWithFileAndLine()
    {
        BuildClean();
        SceneBuilderRouter.ResetForTests();
        BuildRefused();

        var refusal = SceneBuilderBuildStatus.GetRefusal(BuilderName);
        Assert.IsNotNull(refusal, "Precondition: a refused build must set the standing status.");

        Assert.IsTrue(SceneBuilderBuildStatusMenu.HasBuildError);
        Assert.AreEqual($"Build error: {Path.GetFileName(refusal.File)}:{refusal.Line}",
            SceneBuilderBuildStatusMenu.StatusLabel);

        Assert.IsTrue(SceneBuilderBuildStatusMenu.PingTarget.HasValue);
        var ping = SceneBuilderBuildStatusMenu.PingTarget!.Value;
        Assert.AreEqual(refusal.File, ping.file);
        Assert.AreEqual(refusal.Line, ping.line);
        Assert.AreEqual(refusal.Col, ping.col);
    }

    [Test]
    public void RevertsToClean_AfterSubsequentValidRebuild()
    {
        BuildClean();
        SceneBuilderRouter.ResetForTests();
        BuildRefused();
        Assert.IsTrue(SceneBuilderBuildStatusMenu.HasBuildError,
            "Precondition: a refusal must stand before the clean rebuild.");

        File.WriteAllText(_builderPath, Source("        scene.Add(\"Alpha\");\n        scene.Add(\"Beta\");"));
        SceneBuilderBuild.BuildActiveScene();

        Assert.IsFalse(SceneBuilderBuildStatusMenu.HasBuildError);
        Assert.AreEqual("No build errors", SceneBuilderBuildStatusMenu.StatusLabel);
        Assert.IsNull(SceneBuilderBuildStatusMenu.PingTarget);
    }

    [Test]
    public void SurfaceIsReadLiveFromRecorder_NotFromAMenuStaticMemo()
    {
        BuildClean();
        SceneBuilderRouter.ResetForTests();
        BuildRefused();

        var labelBeforeReset = SceneBuilderBuildStatusMenu.StatusLabel;

        // A domain reload clears every static field a class holds. ResetForTests clears the same
        // statics the menu keeps for its own display bookkeeping, so the label below matching
        // proves it is recomputed from the recorder on every read rather than cached.
        SceneBuilderBuildStatusMenu.ResetForTests();

        Assert.AreEqual(labelBeforeReset, SceneBuilderBuildStatusMenu.StatusLabel);
    }

    [Test]
    public void Refresh_RegistersDynamicMenuItem_AndRemovesItOnceClean()
    {
        BuildClean();
        SceneBuilderRouter.ResetForTests();
        BuildRefused();
        SceneBuilderBuildStatusMenu.Refresh();

        var refusal = SceneBuilderBuildStatus.GetRefusal(BuilderName);
        Assert.IsNotNull(refusal, "Precondition: a refused build must set the standing status.");
        var errorPath = SceneBuilderBuildStatusMenu.MenuRoot
            + $"Build error: {Path.GetFileName(refusal.File)}:{refusal.Line}";

        Assert.IsTrue(SceneBuilderBuildStatusMenu.IsRegistered(errorPath),
            "Refresh must register the dynamic 'Build error: ...' item while a refusal stands.");

        File.WriteAllText(_builderPath, Source("        scene.Add(\"Alpha\");\n        scene.Add(\"Beta\");"));
        SceneBuilderBuild.BuildActiveScene();
        SceneBuilderBuildStatusMenu.Refresh();

        Assert.IsFalse(SceneBuilderBuildStatusMenu.IsRegistered(errorPath),
            "Refresh must remove the stale 'Build error: ...' item once the builder builds clean.");
        Assert.IsTrue(SceneBuilderBuildStatusMenu.IsRegistered(SceneBuilderBuildStatusMenu.MenuRoot + "No build errors"),
            "Refresh must register the 'No build errors' item once every builder is clean.");
    }
}
