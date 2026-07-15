using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;

// M1 bidirectional round-trip gate tests: drive REAL transform changes through the new
// programmatic Build/Sync APIs (SceneBuilderBuild.Run / SceneBuilderSync.Run) and assert
// propagation in BOTH directions against a live EditMode scene. Uses a temp builder + temp
// sidecar (NOT the real Assets/SceneBuilder/DemoScene.cs), cleaned up in [TearDown]. This is
// the template every future milestone's round-trips follow: drive a change on one side, assert
// the other.
public class RoundTripTransformTests
{
    // Scene must save under Assets (EditorSceneManager.SaveScene is project-relative); the builder
    // + sidecar live in a system temp dir so Unity never tries to import/compile the builder .cs.
    private const string ScenePath = "Assets/GateTests/__RoundTripTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string SourceAt(float x, float y, float z) => $@"
using SceneBuilder.Authoring;
public class RoundTripScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
        scene.Add(""RoundTripCube"").Transform(pos: ({x}f, {y}f, {z}f));
    }}
}}";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_rt_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "RoundTripScene.cs");
        _sidecarPath = Path.Combine(_dir, "RoundTripScene.sbmap.json");
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
    public void CodeToScene_AddsGameObjectAtAuthoredPosition()
    {
        File.WriteAllText(_builderPath, SourceAt(1f, 2f, 3f));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();

        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var go = GameObject.Find("RoundTripCube");
        Assert.IsNotNull(go, "RoundTripCube was not created by SceneBuilderBuild.Run");
        Assert.AreEqual(new Vector3(1f, 2f, 3f), go.transform.position,
            "GameObject was not materialized at the authored transform position");
    }

    [Test]
    public void SceneToCode_MovedTransformRewritesBuilderSource()
    {
        // Build an object authored at the origin.
        File.WriteAllText(_builderPath, SourceAt(0f, 0f, 0f));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        // Move the real GameObject in the scene.
        var go = GameObject.Find("RoundTripCube");
        Assert.IsNotNull(go, "RoundTripCube was not created by SceneBuilderBuild.Run");
        go.transform.position = new Vector3(0f, 5f, 0f);

        // Sync the moved scene back into the builder source.
        var result = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite a moved transform");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("(0f, 5f, 0f)", rewritten,
            "Builder source was not rewritten to reflect the moved transform position.\n" + rewritten);
    }
}
