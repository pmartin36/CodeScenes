using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using SceneBuilder.Editor;
using SceneBuilder.Core.Serialization;
using SceneBuilder.Core.Sync;

// Gate for the adapter's sync checkpoint: after a code->scene Materialize, a
// `<name>.sbstate.json` must exist next to the identity sidecar at
// SceneBuilderPaths.StateForSidecar(sidecarPath), carrying hashes of the artifacts the build
// just committed, and a repeat build must rewrite it byte-identically. Real GameObject/scene
// through the same SceneBuilderBuild.Run entry point RoundTripTransformTests uses.
public class SyncCheckpointWriteTests
{
    private const string ScenePath = "Assets/GateTests/__SyncCheckpointWriteTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string SourceAt(float x, float y, float z) => $@"
using SceneBuilder.Authoring;
public class CheckpointScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
        scene.Add(""CheckpointCube"").Transform(pos: ({x}f, {y}f, {z}f));
        scene.Add(""CheckpointOther"").Transform(pos: (0f, 0f, 0f));
    }}
}}";

    private string StatePath => SceneBuilderPaths.StateForSidecar(_sidecarPath);

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_sccw_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "CheckpointScene.cs");
        _sidecarPath = Path.Combine(_dir, "CheckpointScene.sbmap.json");
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

    private UnityEngine.SceneManagement.Scene Build()
    {
        File.WriteAllText(_builderPath, SourceAt(1f, 2f, 3f));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        return EditorSceneManager.GetActiveScene();
    }

    [Test]
    public void Materialize_WritesSbstateNextToSidecar()
    {
        Build();

        Assert.IsTrue(File.Exists(StatePath),
            "A build must write a checkpoint file next to the sidecar at SceneBuilderPaths.StateForSidecar(sidecarPath).");
    }

    [Test]
    public void Checkpoint_HasExpectedSceneAndSchemaVersion()
    {
        Build();
        Assert.IsTrue(File.Exists(StatePath), "Checkpoint file was not written.");

        var checkpoint = SyncCheckpointJson.Deserialize(File.ReadAllText(StatePath));

        Assert.AreEqual(SyncCheckpoint.CurrentSchemaVersion, checkpoint.SchemaVersion,
            "Checkpoint must carry the current SyncCheckpoint schema version.");
        Assert.AreEqual(ScenePath, checkpoint.Scene,
            "Checkpoint must record the scene path it was written for.");
    }

    [Test]
    public void Checkpoint_SidecarHashMatchesCommittedMap()
    {
        File.WriteAllText(_builderPath, SourceAt(1f, 2f, 3f));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        var result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsTrue(File.Exists(StatePath), "Checkpoint file was not written.");

        var checkpoint = SyncCheckpointJson.Deserialize(File.ReadAllText(StatePath));

        Assert.AreEqual(CanonicalHash.Of(result.Map), checkpoint.LastSidecarHash,
            "LastSidecarHash must be CanonicalHash.Of the IdentityMap actually committed to the sidecar.");
    }

    [Test]
    public void Checkpoint_SnapshotHashMatchesLiveScene()
    {
        File.WriteAllText(_builderPath, SourceAt(1f, 2f, 3f));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        var result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsTrue(File.Exists(StatePath), "Checkpoint file was not written.");

        var checkpoint = SyncCheckpointJson.Deserialize(File.ReadAllText(StatePath));
        var sceneRef = ObjectReferenceResolver.BuildSceneRefResolver(result.Map);
        var liveSnapshot = SceneSnapshotReader.Read(EditorSceneManager.GetActiveScene(), sceneRef);

        Assert.AreEqual(CanonicalHash.Of(liveSnapshot), checkpoint.LastSnapshotHash,
            "LastSnapshotHash must be CanonicalHash.Of the post-commit scene snapshot.");
    }

    [Test]
    public void SecondIdenticalBuild_RewritesSbstateByteIdentical()
    {
        var scene = Build();
        Assert.IsTrue(File.Exists(StatePath), "Checkpoint file was not written by the first build.");
        var first = File.ReadAllText(StatePath);

        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(File.Exists(StatePath), "Checkpoint file was not written by the second build.");
        var second = File.ReadAllText(StatePath);

        Assert.AreEqual(first, second,
            "Rebuilding the same scene from the same source must rewrite the checkpoint byte-identically.");
    }

    [Test]
    public void Build_LeavesNoTmpFileNextToCheckpoint()
    {
        Build();
        Assert.IsTrue(File.Exists(StatePath), "Checkpoint file was not written.");

        Assert.IsFalse(File.Exists(StatePath + ".tmp"),
            "An atomic checkpoint write must not leave a .tmp file behind.");
    }
}
