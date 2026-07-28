using System.IO;
using System.Linq;
using NUnit.Framework;
using SceneBuilder.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;

// Gate for the multi-scene-builders b1-t1 router (spec checklist #1): SceneBuilderRouter.Discover()
// must enumerate one BuilderRoute per REAL compiled ISceneDefinition with an on-disk builder
// source, resolving each one's ScenePath from its OWN sidecar — never collapsed to a single
// "DemoScene" value. Shared harness (SetUp/TearDown) seeds two distinct builders (RouteAlpha,
// RouteBeta — unity-gate/Assets/Fixtures/RouteAlpha.cs / RouteBeta.cs) into two distinct scenes
// under the REAL <ProjectRoot>/SceneBuilders/ dir (not a temp dir — Discover() only resolves
// SceneBuilderPaths.Builder(name)); reused by the b2/b3 routing checklist tests.
public class MultiSceneRoutingTests
{
    private const string AlphaScenePath = "Assets/GateTests/__MultiSceneAlpha.unity";
    private const string BetaScenePath = "Assets/GateTests/__MultiSceneBeta.unity";

    private string _alphaBuilderPath;
    private string _alphaSidecarPath;
    private string _betaBuilderPath;
    private string _betaSidecarPath;

    private static string Source(string builderName, string body) => $@"
using SceneBuilder.Authoring;
public class {builderName} : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    [SetUp]
    public void SetUp()
    {
        SceneBuilderRouter.ResetForTests();

        SceneBuilderPaths.EnsureBuildersDirectory();
        _alphaBuilderPath = SceneBuilderPaths.Builder("RouteAlpha");
        _alphaSidecarPath = SceneBuilderPaths.Sidecar("RouteAlpha");
        _betaBuilderPath = SceneBuilderPaths.Builder("RouteBeta");
        _betaSidecarPath = SceneBuilderPaths.Sidecar("RouteBeta");

        File.WriteAllText(_alphaBuilderPath, Source("RouteAlpha", "        scene.Add(\"Alpha\");"));
        File.WriteAllText(_betaBuilderPath, Source("RouteBeta", "        scene.Add(\"Beta\");"));

        // Distinct scene per builder, seeded via the real Build seam (SceneBuilderBuild.Run writes
        // the sidecar's IdentityMap.Scene = the passed scenePath — the exact read Discover() relies
        // on for ScenePath). Alpha replaces the active scene, Beta opens additively, so both are
        // open+loaded for the b2/b3 open-scene routing checklist tests that reuse this harness.
        var alphaScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SceneBuilderBuild.Run(_alphaBuilderPath, AlphaScenePath, _alphaSidecarPath, alphaScene);

        var betaScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        SceneBuilderBuild.Run(_betaBuilderPath, BetaScenePath, _betaSidecarPath, betaScene);

        SceneBuilderRouter.ResetForTests();
    }

    [TearDown]
    public void TearDown()
    {
        SceneBuilderRouter.ResetForTests();

        if (File.Exists(_alphaBuilderPath)) File.Delete(_alphaBuilderPath);
        if (File.Exists(_alphaSidecarPath)) File.Delete(_alphaSidecarPath);
        if (File.Exists(_betaBuilderPath)) File.Delete(_betaBuilderPath);
        if (File.Exists(_betaSidecarPath)) File.Delete(_betaSidecarPath);

        if (File.Exists(AlphaScenePath)) AssetDatabase.DeleteAsset(AlphaScenePath);
        if (File.Exists(BetaScenePath)) AssetDatabase.DeleteAsset(BetaScenePath);
    }

    // Checklist #1: Discover() must enumerate BOTH seeded builders with correct builder/sidecar
    // paths, and each one's ScenePath must come from ITS OWN sidecar (proves per-sidecar read, not
    // a default/collapsed value). Asserts CONTAINS-both + fields, not exact count — other compiled
    // ISceneDefinition fixtures exist in the domain (e.g. SpatialAuthoringExamplesFixture) and are
    // correctly excluded only by the on-disk-.cs-exists filter, so an exact-count assert is brittle.
    [Test]
    public void MultiScene_Discover_EnumeratesAllBuilders()
    {
        var routes = SceneBuilderRouter.Discover();

        Assert.IsTrue(routes.Any(r => r.BuilderName == "RouteAlpha"),
            "Discover() must include a route for the RouteAlpha builder (its .cs exists under SceneBuilders/).");
        Assert.IsTrue(routes.Any(r => r.BuilderName == "RouteBeta"),
            "Discover() must include a route for the RouteBeta builder (its .cs exists under SceneBuilders/).");

        var alpha = routes.First(r => r.BuilderName == "RouteAlpha");
        var beta = routes.First(r => r.BuilderName == "RouteBeta");

        Assert.AreEqual(SceneBuilderPaths.Builder("RouteAlpha"), alpha.BuilderPath);
        Assert.AreEqual(SceneBuilderPaths.Sidecar("RouteAlpha"), alpha.SidecarPath);
        Assert.AreEqual(AlphaScenePath, alpha.ScenePath,
            "RouteAlpha's ScenePath must be read from RouteAlpha's own sidecar.");

        Assert.AreEqual(SceneBuilderPaths.Builder("RouteBeta"), beta.BuilderPath);
        Assert.AreEqual(SceneBuilderPaths.Sidecar("RouteBeta"), beta.SidecarPath);
        Assert.AreEqual(BetaScenePath, beta.ScenePath,
            "RouteBeta's ScenePath must be read from RouteBeta's own sidecar.");

        Assert.AreNotEqual(alpha.ScenePath, beta.ScenePath,
            "Each builder's ScenePath must be distinct — never collapsed to a single DemoScene value.");
    }
}
