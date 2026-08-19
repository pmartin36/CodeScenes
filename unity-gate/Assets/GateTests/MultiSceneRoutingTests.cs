using System.IO;
using System.Linq;
using NUnit.Framework;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Serialization;
using SceneBuilder.Editor;
using SceneBuilder.Editor.Licensing;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// Gate for the multi-scene-builders b1-t1 router (spec checklist #1): SceneBuilderRouter.Discover()
// must enumerate one BuilderRoute per *.cs directly under SceneBuilderPaths.BuildersDirectory
// (excluding Generated/), resolving each one's ScenePath from its OWN sidecar — never collapsed to
// a single "DemoScene" value. No compiled ISceneDefinition counterpart is required (builders live
// outside Assets/ and are never compiled). Shared harness (SetUp/TearDown) seeds two distinct
// builders (RouteAlpha, RouteBeta) into two distinct scenes under the REAL
// <ProjectRoot>/SceneBuilders/ dir (not a temp dir — Discover() only resolves
// SceneBuilderPaths.Builder(name)); reused by the b2/b3 routing checklist tests.
public class MultiSceneRoutingTests
{
    private const string AlphaScenePath = "Assets/GateTests/__MultiSceneAlpha.unity";
    private const string BetaScenePath = "Assets/GateTests/__MultiSceneBeta.unity";

    private string _alphaBuilderPath;
    private string _alphaSidecarPath;
    private string _betaBuilderPath;
    private string _betaSidecarPath;
    private Scene _alphaScene;
    private Scene _betaScene;

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
        LicenseGate.ResetToDefault();
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
        // on for ScenePath). Both are open+loaded for the b2/b3 open-scene routing checklist tests
        // that reuse this harness.
        _alphaScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SceneBuilderBuild.Run(_alphaBuilderPath, AlphaScenePath, _alphaSidecarPath, _alphaScene);

        // NewScene(..., Additive) also makes the newly created scene the ACTIVE one — so without an
        // explicit SetActiveScene below, Beta (created second) would be active, not Alpha. Restoring
        // Alpha as active here makes the harness's documented invariant ("Alpha is the active scene")
        // actually true, which the b2 routing tests below depend on to prove routing does NOT fall
        // back to GetActiveScene().
        _betaScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        SceneBuilderBuild.Run(_betaBuilderPath, BetaScenePath, _betaSidecarPath, _betaScene);
        EditorSceneManager.SetActiveScene(_alphaScene);

        SceneBuilderRouter.ResetForTests();
    }

    [TearDown]
    public void TearDown()
    {
        LicenseEnforcement.Register();
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
    // a default/collapsed value). Asserts CONTAINS-both + fields, not exact count — the scan is
    // *.cs under SceneBuilders/ excluding Generated/, and other tests may leave/seed other builder
    // .cs files in that same real directory, so an exact-count assert is brittle.
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

    // Regression gate (multiscene-discover-filescan-fix b1-t1): builders live OUTSIDE Assets/ and are
    // NEVER compiled, so a TypeCache-based Discover() sees zero routes for them in the real editor —
    // all live auto-sync is dead. "OrphanRouteZeta" deliberately has NO compiled ISceneDefinition
    // counterpart anywhere in the domain (unlike RouteAlpha/RouteBeta, which still have compiled
    // fixtures), so this MUST fail against TypeCache discovery and MUST pass once Discover() scans
    // SceneBuilderPaths.BuildersDirectory's *.cs files directly.
    [Test]
    public void MultiScene_Discover_EnumeratesOutOfAssetsBuilders()
    {
        const string name = "OrphanRouteZeta";
        SceneBuilderPaths.EnsureBuildersDirectory();
        var builderPath = SceneBuilderPaths.Builder(name);
        var sidecarPath = SceneBuilderPaths.Sidecar(name);

        File.WriteAllText(builderPath, Source(name, "        scene.Add(\"Zeta\");"));
        SceneBuilderRouter.ResetForTests();

        try
        {
            var routes = SceneBuilderRouter.Discover();

            Assert.IsTrue(routes.Any(r => r.BuilderName == name),
                "Discover() must include a route for a builder .cs with NO compiled ISceneDefinition " +
                "counterpart anywhere in the domain — discovery must be a filesystem scan of " +
                "SceneBuilders/*.cs, not TypeCache.");

            var route = routes.First(r => r.BuilderName == name);

            Assert.AreEqual(SceneBuilderPaths.Builder(name), route.BuilderPath);
            Assert.AreEqual(SceneBuilderPaths.Sidecar(name), route.SidecarPath);
            Assert.AreEqual("Assets/SceneBuilder/" + name + ".unity", route.ScenePath,
                "With no sidecar written, ScenePath must fall back to the deterministic default.");
        }
        finally
        {
            SceneBuilderRouter.ResetForTests();
            if (File.Exists(builderPath)) File.Delete(builderPath);
            if (File.Exists(sidecarPath)) File.Delete(sidecarPath);
        }
    }

    // Checklist #2 (b2-t1): a code->scene cycle for a changed builder must route by that BUILDER'S
    // OWN scene — never GetActiveScene(). Alpha stays the active scene throughout (per SetUp); the
    // change is to Beta's source. Only Beta's live+saved scene may change; Alpha (live GameObjects
    // and its saved .unity bytes) and Alpha's own source must be untouched.
    [Test]
    public void MultiScene_CodeToScene_RoutesToOwningScene_LeavesOtherUntouched()
    {
        var alphaSceneBytesBefore = File.ReadAllBytes(AlphaScenePath);
        var alphaSourceBefore = File.ReadAllText(_alphaBuilderPath);

        File.WriteAllText(_betaBuilderPath, Source("RouteBeta", "        scene.Add(\"Beta\");\n        scene.Add(\"Gamma\");"));
        SceneBuilderRouter.ResetForTests();

        Assert.AreEqual(AlphaScenePath, EditorSceneManager.GetActiveScene().path,
            "Precondition: Alpha must be the active scene while Beta's builder changes.");

        SceneBuilderAutoSync.ExecuteCodeToScene(new[] { _betaBuilderPath });

        Assert.IsTrue(_betaScene.GetRootGameObjects().Any(go => go.name == "Gamma"),
            "ExecuteCodeToScene must build the routed builder's change into ITS OWN live scene, not the active scene.");
        StringAssert.Contains("Gamma", File.ReadAllText(BetaScenePath),
            "The routed builder's own scene ASSET must be saved with the change.");

        CollectionAssert.AreEqual(alphaSceneBytesBefore, File.ReadAllBytes(AlphaScenePath),
            "Alpha's scene asset must be byte-identical — a code change to Beta must never touch a non-owning scene.");
        Assert.AreEqual(alphaSourceBefore, File.ReadAllText(_alphaBuilderPath),
            "Alpha's builder source must be untouched by a Beta code change.");
    }

    // Checklist #5 (b2-t1): when the routed builder's OWN scene is not open, the code->scene cycle
    // must be a safe, located skip — no exception, no write to any scene (never fall back to
    // building into whatever scene happens to be active).
    [Test]
    public void MultiScene_CodeToScene_SceneNotOpen_SkipsSafely()
    {
        Assert.IsTrue(_betaScene.IsValid(), "Precondition: Beta's scene must be open before closing it.");
        EditorSceneManager.CloseScene(_betaScene, true);
        Assert.AreEqual(AlphaScenePath, EditorSceneManager.GetActiveScene().path,
            "Precondition: Alpha must be the (only remaining, unrelated) active scene.");

        var alphaSceneBytesBefore = File.ReadAllBytes(AlphaScenePath);
        var betaSceneBytesBefore = File.ReadAllBytes(BetaScenePath);

        File.WriteAllText(_betaBuilderPath, Source("RouteBeta", "        scene.Add(\"Beta\");\n        scene.Add(\"Gamma\");"));
        SceneBuilderRouter.ResetForTests();

        LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex("RouteBeta.*not open"));

        Assert.DoesNotThrow(() => SceneBuilderAutoSync.ExecuteCodeToScene(new[] { _betaBuilderPath }),
            "A routed builder whose own scene is not open must be a safe skip, never an exception.");

        CollectionAssert.AreEqual(alphaSceneBytesBefore, File.ReadAllBytes(AlphaScenePath),
            "The active scene must never be written to for a builder whose own scene is closed.");
        CollectionAssert.AreEqual(betaSceneBytesBefore, File.ReadAllBytes(BetaScenePath),
            "Beta's (closed) scene asset on disk must be untouched.");
    }

    // Checklist #3 (b2-t2): a scene->code cycle must route by the ACTIVE scene's OWN governing
    // builder — never the last-built builder. Alpha is the active scene throughout (per SetUp); a
    // live edit in Alpha must patch ONLY RouteAlpha.cs, leaving RouteBeta.cs (the non-active
    // builder, but the LAST one Build.Run touched in SetUp) byte-identical.
    [Test]
    public void MultiScene_SceneToCode_RoutesToOwningBuilder_LeavesOtherUntouched()
    {
        Assert.AreEqual(AlphaScenePath, EditorSceneManager.GetActiveScene().path,
            "Precondition: Alpha must be the active scene.");

        var betaSourceBefore = File.ReadAllText(_betaBuilderPath);

        var alphaRoot = _alphaScene.GetRootGameObjects().First(go => go.name == "Alpha");
        alphaRoot.transform.position = new UnityEngine.Vector3(1f, 2f, 3f);

        SceneBuilderAutoSync.ExecuteSceneToCode(new[] { alphaRoot.GetEntityId() });

        StringAssert.Contains("(1f, 2f, 3f)", File.ReadAllText(_alphaBuilderPath),
            "ExecuteSceneToCode must patch the ACTIVE scene's own governing builder (RouteAlpha) with the moved transform.");
        Assert.AreEqual(betaSourceBefore, File.ReadAllText(_betaBuilderPath),
            "A scene edit in Alpha (the active scene) must never patch RouteBeta's source — routing is by the active scene's OWN builder, not the last-built one.");
    }

    // Checklist #6 (b2-t2): when the active scene matches no discovered route, a scene->code cycle
    // must be a clean no-op — no builder written, no exception.
    [Test]
    public void MultiScene_SceneToCode_NoGoverningBuilder_NoOp()
    {
        var alphaSourceBefore = File.ReadAllText(_alphaBuilderPath);
        var betaSourceBefore = File.ReadAllText(_betaBuilderPath);

        // Replace the active scene with a brand-new, never-saved one — its path is "", which no
        // discovered route can match.
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var plainScene = EditorSceneManager.GetActiveScene();
        Assert.IsFalse(SceneBuilderRouter.TryRouteScene(plainScene, out _),
            "Precondition: a brand-new unsaved scene must not match any discovered route.");

        var orphan = new UnityEngine.GameObject("Orphan");
        try
        {
            Assert.DoesNotThrow(() => SceneBuilderAutoSync.ExecuteSceneToCode(new[] { orphan.GetEntityId() }),
                "A scene edit on an active scene with no governing builder must be a safe no-op, never an exception.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(orphan);
        }

        Assert.AreEqual(alphaSourceBefore, File.ReadAllText(_alphaBuilderPath),
            "No governing builder for the active scene means RouteAlpha's source must be untouched.");
        Assert.AreEqual(betaSourceBefore, File.ReadAllText(_betaBuilderPath),
            "No governing builder for the active scene means RouteBeta's source must be untouched.");
    }

    // Checklist #4 (b2-t3): per-builder session state (conflict-aware baseline, sidecar identity map)
    // must never bleed between builders. SetUp builds Alpha THEN Beta — so a routing bug that keys off
    // "whichever builder was built/routed most recently" (a single global baseline, or the retired
    // Builder(BuilderName="DemoScene") fallback) would silently attribute Alpha's captured baseline to
    // Beta's source. CaptureBaseline(scene) must resolve EACH scene's OWN governing builder via the
    // router, never a shared global.
    [Test]
    public void MultiScene_Sidecars_DoNotCrossContaminate()
    {
        var alphaSourceBefore = File.ReadAllText(_alphaBuilderPath);
        var betaSourceBefore = File.ReadAllText(_betaBuilderPath);

        // Capture Alpha's baseline first, then Beta's — SetUp built Beta LAST, so
        // SceneBuilderBuild.LastBuilderPath still points at Beta's builder when Alpha's baseline is
        // captured. A per-builder-correct CaptureBaseline must still attribute Alpha's OWN source to
        // Alpha's baseline (not Beta's, not whichever was last built).
        SceneBuilderAutoSync.CaptureBaseline(_alphaScene);
        SceneBuilderAutoSync.CaptureBaseline(_betaScene);

        var alphaBaseline = SceneBuilderAutoSync.BaselineSourceFor("RouteAlpha");
        var betaBaseline = SceneBuilderAutoSync.BaselineSourceFor("RouteBeta");

        Assert.AreEqual(alphaSourceBefore, alphaBaseline,
            "RouteAlpha's captured baseline must come from RouteAlpha's OWN builder source, never " +
            "another builder's (or whichever builder happened to be built/routed last).");
        Assert.AreEqual(betaSourceBefore, betaBaseline,
            "RouteBeta's captured baseline must come from RouteBeta's OWN builder source.");
        Assert.AreNotEqual(alphaBaseline, betaBaseline,
            "Two distinct builders must never share the same captured baseline content.");

        Assert.IsNotNull(SceneBuilderAutoSync.BaselineSnapshotFor("RouteAlpha"),
            "RouteAlpha must have its own non-null baseline snapshot after its own CaptureBaseline call.");
        Assert.IsNotNull(SceneBuilderAutoSync.BaselineSnapshotFor("RouteBeta"),
            "RouteBeta must have its own non-null baseline snapshot after its own CaptureBaseline call.");

        // Sidecar isolation: each builder's own on-disk identity map must describe ONLY its own scene
        // and its own objects — never leak the other builder's Scene path or entries.
        var alphaMap = IdentityMapJson.Deserialize(File.ReadAllText(_alphaSidecarPath));
        var betaMap = IdentityMapJson.Deserialize(File.ReadAllText(_betaSidecarPath));

        Assert.AreEqual(AlphaScenePath, alphaMap.Scene, "RouteAlpha's sidecar must record its own Scene path.");
        Assert.AreEqual(BetaScenePath, betaMap.Scene, "RouteBeta's sidecar must record its own Scene path.");
        Assert.IsTrue(alphaMap.Entries.Any(e => e.Name == "Alpha"), "RouteAlpha's sidecar must contain its own object.");
        Assert.IsFalse(alphaMap.Entries.Any(e => e.Name == "Beta"), "RouteAlpha's sidecar must not leak RouteBeta's object entries.");
        Assert.IsTrue(betaMap.Entries.Any(e => e.Name == "Beta"), "RouteBeta's sidecar must contain its own object.");
        Assert.IsFalse(betaMap.Entries.Any(e => e.Name == "Alpha"), "RouteBeta's sidecar must not leak RouteAlpha's object entries.");
    }

    // Checklist #7 (b3-t1): CodeScenes/Build Active Scene must route by the ACTIVE scene's OWN
    // governing builder (like ExecuteCodeToScene's routing, but from the manual menu entry point) —
    // never the last-built builder's paths. Beta is made active; only Beta's live+saved scene may
    // change, Alpha (live scene bytes and its own source) must be untouched.
    [Test]
    public void MultiScene_Menu_BuildActiveScene_TargetsActiveBuildersScene()
    {
        EditorSceneManager.SetActiveScene(_betaScene);
        Assert.AreEqual(BetaScenePath, EditorSceneManager.GetActiveScene().path,
            "Precondition: Beta must be the active scene.");

        var alphaSceneBytesBefore = File.ReadAllBytes(AlphaScenePath);
        var alphaSourceBefore = File.ReadAllText(_alphaBuilderPath);

        File.WriteAllText(_betaBuilderPath, Source("RouteBeta", "        scene.Add(\"Beta\");\n        scene.Add(\"Gamma\");"));
        SceneBuilderRouter.ResetForTests();

        SceneBuilderBuild.BuildActiveScene();

        Assert.IsTrue(_betaScene.GetRootGameObjects().Any(go => go.name == "Gamma"),
            "Build Active Scene must build the active scene's OWN governing builder's change into that scene.");
        StringAssert.Contains("Gamma", File.ReadAllText(BetaScenePath),
            "The routed builder's own scene ASSET must be saved with the change.");

        CollectionAssert.AreEqual(alphaSceneBytesBefore, File.ReadAllBytes(AlphaScenePath),
            "Alpha's scene asset must be byte-identical — Build Active Scene must never touch a non-active scene.");
        Assert.AreEqual(alphaSourceBefore, File.ReadAllText(_alphaBuilderPath),
            "Alpha's builder source must be untouched by building the (Beta) active scene.");
    }

    // Checklist #7 (b3-t1): when the active scene has no governing builder, Build Active Scene must
    // log ONE located Error containing "no governing builder" and write to no scene/builder — never
    // throw, never silently build into the wrong scene.
    [Test]
    public void MultiScene_Menu_BuildActiveScene_NoGoverningBuilder_LogsLocatedError_WritesNothing()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var plainScene = EditorSceneManager.GetActiveScene();
        Assert.IsFalse(SceneBuilderRouter.TryRouteScene(plainScene, out _),
            "Precondition: a brand-new unsaved scene must not match any discovered route.");

        var alphaSourceBefore = File.ReadAllText(_alphaBuilderPath);
        var betaSourceBefore = File.ReadAllText(_betaBuilderPath);

        LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex("no governing builder"));

        Assert.DoesNotThrow(() => SceneBuilderBuild.BuildActiveScene(),
            "An active scene with no governing builder must be a safe located error, never an exception.");

        Assert.AreEqual(alphaSourceBefore, File.ReadAllText(_alphaBuilderPath),
            "No governing builder for the active scene means RouteAlpha's source must be untouched.");
        Assert.AreEqual(betaSourceBefore, File.ReadAllText(_betaBuilderPath),
            "No governing builder for the active scene means RouteBeta's source must be untouched.");
    }
}
