using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;

// Pins that SceneBuilderBuild.Run / SceneBuilderSync.Run keep their observable behavior once their
// bodies route through the target-parameterized RunCore/SyncCore seam, and that the seam itself —
// constructed directly as SceneBuildSyncTarget + RunCore/SyncCore — produces the same result as the
// public wrappers. This is the seam a second IBuildSyncTarget implementation plugs into without
// forking any of the shared pipeline stages.
public class BuildSyncTargetSeamTests
{
    private const string ScenePath = "Assets/GateTests/__BuildSyncTargetSeamTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string SourceAt(float x, float y, float z) => $@"
using SceneBuilder.Authoring;
public class SeamScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
        scene.Add(""SeamCube"").Transform(pos: ({x}f, {y}f, {z}f));
    }}
}}";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_seam_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "SeamScene.cs");
        _sidecarPath = Path.Combine(_dir, "SeamScene.sbmap.json");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }
    }

    [Test]
    public void PublicRun_BuildsAuthoredHierarchyAndWritesSidecar()
    {
        File.WriteAllText(_builderPath, SourceAt(1f, 2f, 3f));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();

        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var go = GameObject.Find("SeamCube");
        Assert.IsNotNull(go, "SeamCube was not created by SceneBuilderBuild.Run");
        Assert.AreEqual(new Vector3(1f, 2f, 3f), go.transform.position,
            "GameObject was not materialized at the authored transform position");
        Assert.IsTrue(File.Exists(_sidecarPath), "Sidecar was not written by SceneBuilderBuild.Run");
    }

    [Test]
    public void PublicRun_SyncsMovedTransformBackIntoSource()
    {
        File.WriteAllText(_builderPath, SourceAt(0f, 0f, 0f));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var go = GameObject.Find("SeamCube");
        Assert.IsNotNull(go, "SeamCube was not created by SceneBuilderBuild.Run");
        go.transform.position = new Vector3(4f, 5f, 6f);

        SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("(4f, 5f, 6f)", rewritten,
            "Builder source was not rewritten to reflect the moved transform position.\n" + rewritten);
    }

    // Drives the target-parameterized core directly: SceneBuildSyncTarget + RunCore/SyncCore, the
    // exact seam a second IBuildSyncTarget implementation plugs into. Proves the core itself
    // performs the build+sync, not just the SceneBuilderBuild.Run/SceneBuilderSync.Run wrappers
    // that construct a target and call it on the caller's behalf.
    [Test]
    public void DirectTargetCore_BuildsAndSyncsThroughSameSeamAsPublicRun()
    {
        File.WriteAllText(_builderPath, SourceAt(7f, 8f, 9f));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();

        var buildTarget = new SceneBuildSyncTarget(scene, ScenePath);
        SceneBuilderBuild.RunCore(buildTarget, _builderPath, _sidecarPath);

        var go = GameObject.Find("SeamCube");
        Assert.IsNotNull(go,
            "SeamCube was not created by SceneBuilderBuild.RunCore via SceneBuildSyncTarget");
        Assert.AreEqual(new Vector3(7f, 8f, 9f), go.transform.position,
            "GameObject was not materialized at the authored transform position via RunCore");
        Assert.IsTrue(File.Exists(_sidecarPath), "Sidecar was not written by SceneBuilderBuild.RunCore");

        go.transform.position = new Vector3(10f, 11f, 12f);
        var syncTarget = new SceneBuildSyncTarget(EditorSceneManager.GetActiveScene(), ScenePath);
        SceneBuilderSync.SyncCore(syncTarget, _builderPath, _sidecarPath, preAssembled: null);

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("(10f, 11f, 12f)", rewritten,
            "Builder source was not rewritten to reflect the moved transform position via SyncCore.\n"
            + rewritten);
    }
}
