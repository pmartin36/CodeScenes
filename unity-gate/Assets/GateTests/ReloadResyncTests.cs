using System.IO;
using System.Linq;
using NUnit.Framework;
using SceneBuilder.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Gate for SceneBuilderResync surviving a domain reload: SceneBuilderAutoSync.ResetForTests()
// clears the reload-cleared static state (baselines, snapshot assemblers, last-built paths) while
// leaving the checkpoint/sidecar/source files on disk intact, standing in for a real domain reload
// the way AutoTriggerTests' Disarm/Arm test stands in for re-subscription. Follows
// ExternalEditResyncTests' seed/edit pattern.
public class ReloadResyncTests
{
    private const string BuilderName = "ReloadResyncScene";
    private const string ScenePath = "Assets/GateTests/__ReloadResyncTemp.unity";

    private string _builderPath;
    private string _sidecarPath;
    private string _statePath;

    private static string Source(float ax, float ay, float az) => $@"
using SceneBuilder.Authoring;
public class ReloadResyncScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
        scene.Add(""Alpha"").Transform(pos: ({ax}f, {ay}f, {az}f));
        scene.Add(""Beta"").Transform(pos: (0f, 0f, 0f));
    }}
}}";

    private static GameObject FindRoot(Scene scene, string name) =>
        scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);

    [SetUp]
    public void SetUp()
    {
        SceneBuilderPaths.EnsureBuildersDirectory();
        _builderPath = SceneBuilderPaths.Builder(BuilderName);
        _sidecarPath = SceneBuilderPaths.Sidecar(BuilderName);
        _statePath = SceneBuilderPaths.StateForSidecar(_sidecarPath);

        SceneBuilderRouter.ResetForTests();
        SceneBuilderAutoSync.ResetForTests();
        SuppressionScope.ResetForTests();
    }

    [TearDown]
    public void TearDown()
    {
        SceneBuilderRouter.ResetForTests();
        SceneBuilderAutoSync.ResetForTests();
        SuppressionScope.ResetForTests();

        if (File.Exists(_builderPath)) File.Delete(_builderPath);
        if (File.Exists(_sidecarPath)) File.Delete(_sidecarPath);
        if (File.Exists(_statePath)) File.Delete(_statePath);

        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }
    }

    private Scene Build()
    {
        File.WriteAllText(_builderPath, Source(1f, 2f, 3f));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        SceneBuilderRouter.ResetForTests();
        return EditorSceneManager.GetActiveScene();
    }

    [Test]
    public void SimulatedReload_ResyncFromDisk_RecoversUnobservedEdit()
    {
        var scene = Build();
        var alpha = FindRoot(scene, "Alpha");
        Assert.IsNotNull(alpha, "Alpha was not created by SceneBuilderBuild.Run");
        alpha.transform.position = new Vector3(7f, 8f, 9f);

        // Simulated domain reload: the pump's static state is cleared but the checkpoint, sidecar
        // and source files remain — the reload-survival contract is that resync reads its inputs
        // from disk, not from a static field a reload would have wiped.
        SceneBuilderAutoSync.ResetForTests();

        SceneBuilderResync.ResyncAllOpenScenes();

        StringAssert.Contains("(7f, 8f, 9f)", File.ReadAllText(_builderPath),
            "A resync after simulated reload-cleared state must recover an external scene edit made before the reload, proving its inputs came from disk.");
    }

    [Test]
    public void AfterReload_NextEdit_StillSyncs()
    {
        var scene = Build();
        var alpha = FindRoot(scene, "Alpha");
        alpha.transform.position = new Vector3(7f, 8f, 9f);

        SceneBuilderAutoSync.ResetForTests();
        SceneBuilderResync.ResyncAllOpenScenes();

        StringAssert.Contains("(7f, 8f, 9f)", File.ReadAllText(_builderPath),
            "Precondition: the pre-reload edit must be recovered before checking post-reload propagation.");

        SceneBuilderAutoSync.WireDefaultExecutors();
        var beta = FindRoot(EditorSceneManager.GetActiveScene(), "Beta");
        Assert.IsNotNull(beta, "Beta was not found after the reload resync.");
        beta.transform.position = new Vector3(4f, 5f, 6f);

        var now = 100.0;
        SceneBuilderAutoSync.Clock = () => now;
        SceneBuilderAutoSync.NotifySceneChanged(new[] { beta.GetEntityId() });
        now += SceneBuilderAutoSync.SettleSeconds + 0.01;
        SceneBuilderAutoSync.PumpOnce(now);

        StringAssert.Contains("(4f, 5f, 6f)", File.ReadAllText(_builderPath),
            "After a simulated reload, a subsequent normal pump cycle must still sync an edit — no lost subscription.");
    }
}
