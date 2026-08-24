using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using SceneBuilder.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// Spec 48 (specs/48-requirecomponent-preserved-not-churned.md): a live component Unity auto-adds
// because a surviving component declares it required via [RequireComponent] -- canonically
// CanvasRenderer, required by Graphic and therefore by UnityEngine.UI.Image -- must never be planned
// for removal on a code->scene build just because the builder omits it. Drives the real Build path
// (real [RequireComponent] reflection, real DestroyImmediate refusal) against a live editor scene.
// Builds directly via SceneBuilderBuild.Run (not the general fixed-point proof harness): the harness's
// convergence check asserts a GLOBAL zero-op rebuild, which is a stronger and unrelated claim -- this
// gate only owns the CanvasRenderer, not every field on the object.
public class RequireComponentPreservedTests
{
    private const string BuilderName = "RequireComponentPreservedGateScene";
    private const string ScenePath = "Assets/GateTests/__RequireComponentPreservedTemp.unity";

    private string _builderPath;
    private string _sidecarPath;

    private static string Source() => @"
using SceneBuilder.Authoring;
public class RequireComponentPreservedGateScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Panel"").Component<UnityEngine.UI.Image>(_ => { });
    }
}";

    private static GameObject FindRoot(Scene scene, string name) =>
        scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);

    [SetUp]
    public void SetUp()
    {
        SceneBuilderPaths.EnsureBuildersDirectory();
        _builderPath = SceneBuilderPaths.Builder(BuilderName);
        _sidecarPath = SceneBuilderPaths.Sidecar(BuilderName);
        SceneBuilderRouter.ResetForTests();
    }

    [TearDown]
    public void TearDown()
    {
        SceneBuilderRouter.ResetForTests();

        if (File.Exists(_builderPath)) File.Delete(_builderPath);
        if (File.Exists(_sidecarPath)) File.Delete(_sidecarPath);

        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }
    }

    // A builder that never names CanvasRenderer, built twice from unchanged source: the second build
    // must not attempt to remove the RequireComponent-added CanvasRenderer. Unity's own DestroyImmediate
    // refuses a dependency-held component and logs "Can't remove ... because ... depends on it" --
    // asserting zero such (or any other unexpected) console output across both builds is therefore
    // proof no removal was ever attempted, not just that a refused removal left it standing.
    [Test]
    public void ImageBuiltTwice_NeverPlansRemoveOfRequiredCanvasRenderer()
    {
        File.WriteAllText(_builderPath, Source());
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();

        LogAssert.Expect(LogType.Log, new Regex(@"^\[SceneBuilder\] Built in place: .*", RegexOptions.Singleline));
        var first = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(first.Diagnostics, "First build was refused: "
            + string.Join("; ", first.Diagnostics.Select(d => d.Message)));

        var panelAfterFirst = FindRoot(EditorSceneManager.GetActiveScene(), "Panel");
        Assert.IsNotNull(panelAfterFirst, "Panel was not created by the first build.");
        Assert.IsNotNull(panelAfterFirst.GetComponent<CanvasRenderer>(),
            "An Image host must carry a live CanvasRenderer after the first build (RequireComponent honored).");

        LogAssert.Expect(LogType.Log, new Regex(@"^\[SceneBuilder\] Built in place: .*", RegexOptions.Singleline));
        var second = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(second.Diagnostics, "Second build (unchanged source) was refused: "
            + string.Join("; ", second.Diagnostics.Select(d => d.Message)));

        var panelAfterSecond = FindRoot(EditorSceneManager.GetActiveScene(), "Panel");
        Assert.IsNotNull(panelAfterSecond, "Panel is missing after the second build.");
        Assert.IsNotNull(panelAfterSecond.GetComponent<CanvasRenderer>(),
            "The required CanvasRenderer must still be live after a second build from source that "
                + "never named it.");

        // No unmatched log line survived either build -- in particular no CanvasRenderer removal
        // refusal, which native DestroyImmediate would log the instant a RemoveComponent plan op
        // targeted a still-required component.
        LogAssert.NoUnexpectedReceived();
    }
}
