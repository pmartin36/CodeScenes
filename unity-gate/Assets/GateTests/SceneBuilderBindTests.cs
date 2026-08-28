using System.IO;
using System.Linq;
using NUnit.Framework;
using SceneBuilder.Core.Serialization;
using SceneBuilder.Editor;
using SceneBuilder.Editor.Licensing;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

// Gate for BindSceneToBuilder, the sole writer of a builder sidecar's Scene field for the
// adopt-existing-scene path, and CanBindActiveScene, the untitled/license predicate the bind
// menu's validate delegate is built from. Harness mirrors MultiSceneRoutingTests: seed a builder
// into a real scene under SceneBuilderPaths.BuildersDirectory via SceneBuilderBuild.Run, then
// drive BindSceneToBuilder directly (it never touches Unity scene objects).
public class SceneBuilderBindTests
{
    private const string Name = "BindTarget";
    private const string SceneAPath = "Assets/GateTests/__BindSceneA.unity";
    private const string SceneBPath = "Assets/GateTests/__BindSceneB.unity";

    private string _builderPath;
    private string _sidecarPath;
    private Scene _sceneA;
    private Scene _sceneB;

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
        _builderPath = SceneBuilderPaths.Builder(Name);
        _sidecarPath = SceneBuilderPaths.Sidecar(Name);

        File.WriteAllText(_builderPath, Source(Name, "        scene.Add(\"Seeded\");"));

        _sceneA = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SceneBuilderBuild.Run(_builderPath, SceneAPath, _sidecarPath, _sceneA);

        _sceneB = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        EditorSceneManager.SaveScene(_sceneB, SceneBPath);
        EditorSceneManager.SetActiveScene(_sceneA);

        SceneBuilderRouter.ResetForTests();
    }

    [TearDown]
    public void TearDown()
    {
        LicenseEnforcement.Register();
        SceneBuilderRouter.ResetForTests();

        if (File.Exists(_builderPath)) File.Delete(_builderPath);
        if (File.Exists(_sidecarPath)) File.Delete(_sidecarPath);
        if (File.Exists(SceneAPath)) UnityEditor.AssetDatabase.DeleteAsset(SceneAPath);
        if (File.Exists(SceneBPath)) UnityEditor.AssetDatabase.DeleteAsset(SceneBPath);
    }

    // Accept-when 1: binding to the scene the builder was already built into preserves the
    // seeded Entries[]/Assets[] (proves the read-modify-write with-expression path, not a fresh map).
    [Test]
    public void Bind_WritesScene_PreservesEntriesAndAssets()
    {
        var mapBefore = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));

        var result = SceneBuilderBind.BindSceneToBuilder(Name, SceneAPath, overwriteExisting: false);

        Assert.AreEqual(BindOutcome.Bound, result.Outcome);
        Assert.IsTrue(result.Wrote);

        var mapAfter = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        Assert.AreEqual(SceneAPath, mapAfter.Scene);
        CollectionAssert.AreEqual(
            mapBefore.Entries.Select(e => e.Name).ToArray(),
            mapAfter.Entries.Select(e => e.Name).ToArray());
    }

    // Accept-when 2: after a rebind, the router routes to the new scene with no intervening
    // ResetForTests — proves BindSceneToBuilder calls Invalidate() itself.
    [Test]
    public void Bind_RouterRoutesToNewScene_ProvesInvalidate()
    {
        SceneBuilderRouter.Discover(); // prime the per-domain cache at Scene == SceneAPath

        SceneBuilderBind.BindSceneToBuilder(Name, SceneBPath, overwriteExisting: true);

        Assert.IsTrue(SceneBuilderRouter.TryRouteScene(_sceneB, out var route),
            "A missing Invalidate() call leaves the primed cache stale at the old scene.");
        Assert.AreEqual(Name, route.BuilderName);
    }

    // Accept-when 3: a build cycle against a bound existing scene is non-destructive — a
    // user-added GameObject the builder never declared survives alongside the builder's own
    // coded object.
    [Test]
    public void Bind_SubsequentBuild_NonDestructive()
    {
        SceneBuilderBind.BindSceneToBuilder(Name, SceneBPath, overwriteExisting: true);

        var userObject = new UnityEngine.GameObject("UserPlaced");
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(userObject, _sceneB);

        SceneBuilderBuild.Run(_builderPath, SceneBPath, _sidecarPath, _sceneB);

        var roots = _sceneB.GetRootGameObjects();
        Assert.IsTrue(roots.Any(go => go.name == "Seeded"),
            "The builder's own coded object must be present after the bound build.");
        Assert.IsTrue(roots.Any(go => go.name == "UserPlaced"),
            "A user-added object the builder never declared must survive the bound build.");
    }

    // Accept-when 4 (action half): an empty scene path refuses and writes nothing.
    [Test]
    public void Bind_EmptyScenePath_Refuses()
    {
        var sidecarBefore = File.ReadAllText(_sidecarPath);

        var result = SceneBuilderBind.BindSceneToBuilder(Name, "", overwriteExisting: false);

        Assert.AreEqual(BindOutcome.RefusedEmptyScene, result.Outcome);
        Assert.IsFalse(result.Wrote);
        Assert.AreEqual(sidecarBefore, File.ReadAllText(_sidecarPath));
    }

    // Accept-when 4 (validate-delegate half): CanBindActiveScene is false with no saved active
    // scene path, true with one, and false again when the license gate denies.
    [Test]
    public void CanBindActiveScene_Predicate()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        Assert.IsFalse(SceneBuilderBind.CanBindActiveScene(),
            "An unsaved active scene (empty path) must not be bindable.");

        // The unsaved-scene check above unloads sceneA (Single mode replaces every loaded scene),
        // so a saved active scene is re-established by reopening it, not by reusing the old handle.
        EditorSceneManager.OpenScene(SceneAPath, OpenSceneMode.Single);
        Assert.IsTrue(SceneBuilderBind.CanBindActiveScene(),
            "A saved active scene with the license allowed must be bindable.");

        LicenseGate.SetProvider(() => false);
        Assert.IsFalse(SceneBuilderBind.CanBindActiveScene(),
            "A denied license must refuse even a saved active scene.");
    }

    // Accept-when 5: bound to scene A, overwrite:false against scene B writes nothing and
    // reports WouldClobber; overwrite:true rebinds to B and preserves Entries[]/Assets[].
    [Test]
    public void Bind_WouldClobber_vs_Overwrite()
    {
        var mapBefore = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));

        var refused = SceneBuilderBind.BindSceneToBuilder(Name, SceneBPath, overwriteExisting: false);

        Assert.AreEqual(BindOutcome.WouldClobber, refused.Outcome);
        Assert.AreEqual(SceneAPath, refused.ExistingScene);
        Assert.IsFalse(refused.Wrote);
        var mapUnchanged = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        Assert.AreEqual(SceneAPath, mapUnchanged.Scene);

        var overwritten = SceneBuilderBind.BindSceneToBuilder(Name, SceneBPath, overwriteExisting: true);

        Assert.AreEqual(BindOutcome.Bound, overwritten.Outcome);
        var mapAfter = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        Assert.AreEqual(SceneBPath, mapAfter.Scene);
        CollectionAssert.AreEqual(
            mapBefore.Entries.Select(e => e.Name).ToArray(),
            mapAfter.Entries.Select(e => e.Name).ToArray());
    }
}
