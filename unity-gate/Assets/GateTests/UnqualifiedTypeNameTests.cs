using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;

// specs/20-unqualified-type-names.md — the 9-case EditMode round-trip suite proving the
// usings-aware resolution chokepoint (ComponentTypeResolver + ComponentTypeNormalizer, wired through
// DesiredModelLoader.Load and SceneBuilderSync.cs's re-parse via ComponentTypeNormalizer.ParseAndNormalize)
// works end-to-end against a live editor scene: short built-in/user-script names resolve and
// materialize, qualified names are unchanged (regression), short names never requalify on Sync
// (anti-churn), identity survives rebuild, and unresolvable/ambiguous short names throw a located
// error and leave the scene untouched. Follows RoundTripComponentTests.cs's temp-dir/SetUp/TearDown
// harness and RoundTripBuiltinRefErrorTests.cs's located-error assertion pattern.
public class UnqualifiedTypeNameTests
{
    private const string ScenePath = "Assets/GateTests/__UnqualifiedTypeNameTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    // `usings` carries the under-test import(s) (or "" for the missing-using case #7) ABOVE the
    // always-present `using SceneBuilder.Authoring;` so each test controls exactly which namespaces
    // are in scope for short-name resolution — the whole point of the milestone.
    private static string Source(string usings, string body) => $@"{usings}
using SceneBuilder.Authoring;
public class RoundTripScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    private static GameObject FindRoot(Scene scene, string name)
    {
        return scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);
    }

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_utn_" + System.Guid.NewGuid().ToString("N"));
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

    // 1. Short built-in name resolves via `using UnityEngine;` and materializes the real Rigidbody.
    [Test]
    public void Build_ShortBuiltinName_MaterializesRealRigidbody()
    {
        File.WriteAllText(_builderPath, Source(
            "using UnityEngine;",
            "        var cube = scene.Add(\"Cube\");\n" +
            "        cube.Component<Rigidbody>(c => c.Set(r => r.mass, 5f));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        Assert.DoesNotThrow(() => SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene),
            "Build threw resolving a short built-in name with a matching using.");

        var cube = FindRoot(EditorSceneManager.GetActiveScene(), "Cube");
        Assert.IsNotNull(cube, "Cube was not created by SceneBuilderBuild.Run");
        var rb = cube.GetComponent<Rigidbody>();
        Assert.IsNotNull(rb, "Short-named Component<Rigidbody> did not materialize a real Rigidbody");
        Assert.AreEqual(typeof(UnityEngine.Rigidbody), rb.GetType(),
            "Materialized component is not UnityEngine.Rigidbody");
        Assert.AreEqual(5f, rb.mass, "Authored mass=5 did not materialize via the short-named component");
    }

    // 2. Short user-script name resolves via a user `using` and attaches exactly once (not duplicated).
    [Test]
    public void Build_ShortUserScriptName_AttachesEnemyNotDuplicated()
    {
        File.WriteAllText(_builderPath, Source(
            "using MyGame.Enemies;",
            "        var cube = scene.Add(\"Cube\");\n" +
            "        cube.Component<Enemy>(c => c.Set(e => e.health, 100));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var cube = FindRoot(EditorSceneManager.GetActiveScene(), "Cube");
        Assert.IsNotNull(cube, "Cube was not created by SceneBuilderBuild.Run");
        var enemies = cube.GetComponents<MyGame.Enemies.Enemy>();
        Assert.AreEqual(1, enemies.Length,
            "Short-named user-script Component<Enemy> did not attach exactly once");
        Assert.AreEqual(100, enemies[0].health, "Authored health=100 did not materialize on Enemy");
    }

    // 3. Fully-qualified name (no matching using) resolves identically to #1 — regression: the
    //    qualified path is unaffected by the new usings-aware resolution.
    [Test]
    public void Build_FullyQualifiedName_IdenticalToShort()
    {
        File.WriteAllText(_builderPath, Source(
            "",
            "        var cube = scene.Add(\"Cube\");\n" +
            "        cube.Component<UnityEngine.Rigidbody>(c => c.Set(r => r.mass, 5f));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        Assert.DoesNotThrow(() => SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene),
            "Build threw resolving a fully-qualified component name.");

        var cube = FindRoot(EditorSceneManager.GetActiveScene(), "Cube");
        Assert.IsNotNull(cube, "Cube was not created by SceneBuilderBuild.Run");
        var rb = cube.GetComponent<Rigidbody>();
        Assert.IsNotNull(rb, "Fully-qualified Component<UnityEngine.Rigidbody> did not materialize");
        Assert.AreEqual(5f, rb.mass, "Authored mass=5 did not materialize via the qualified component");
    }

    // 4. Anti-churn: Sync never rewrites a short authored token to its qualified form, even across a
    //    no-op Sync (source + sidecar mtimes unchanged, Changed==false) and an unrelated-edit Sync.
    [Test]
    public void Sync_ShortName_DoesNotRequalify_AndNoEditIsNoOp()
    {
        File.WriteAllText(_builderPath, Source(
            "using UnityEngine;",
            "        var cube = scene.Add(\"Cube\");\n" +
            "        cube.Component<Rigidbody>(c => c.Set(r => r.mass, 5f));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var builderMtimeBefore = File.GetLastWriteTimeUtc(_builderPath);
        var sidecarMtimeBefore = File.GetLastWriteTimeUtc(_sidecarPath);

        // (a) no-edit Sync is a NO-OP.
        var noEdit = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(noEdit.Changed, "NOT CONVERGED: re-syncing an unchanged scene reported Changed=true.");
        Assert.AreEqual(builderMtimeBefore, File.GetLastWriteTimeUtc(_builderPath),
            "Builder source was rewritten by a no-edit Sync.");
        Assert.AreEqual(sidecarMtimeBefore, File.GetLastWriteTimeUtc(_sidecarPath),
            "Sidecar was rewritten by a no-edit Sync.");
        var afterNoEdit = File.ReadAllText(_builderPath);
        StringAssert.Contains("Component<Rigidbody>", afterNoEdit,
            "Short authored token was lost after a no-edit Sync.\n" + afterNoEdit);
        StringAssert.DoesNotContain("Component<UnityEngine.Rigidbody>", afterNoEdit,
            "A no-edit Sync requalified the short authored token.\n" + afterNoEdit);

        // (b) an UNRELATED scene edit still leaves the short token alone.
        new GameObject("Extra");
        var unrelated = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(unrelated.Changed, "Sync reported no change despite an unrelated new GameObject");
        var afterUnrelated = File.ReadAllText(_builderPath);
        StringAssert.Contains("scene.Add(\"Extra\")", afterUnrelated,
            "Builder source did not gain the unrelated Extra object.\n" + afterUnrelated);
        StringAssert.Contains("Component<Rigidbody>", afterUnrelated,
            "Short authored token was lost after an unrelated-edit Sync.\n" + afterUnrelated);
        StringAssert.DoesNotContain("Component<UnityEngine.Rigidbody>", afterUnrelated,
            "An unrelated-edit Sync requalified the short authored token.\n" + afterUnrelated);
    }

    // 5. Identity across rebuild: a short-named component's GlobalObjectId is stable across
    //    edit -> Sync -> Build again, proving the sidecar stored the qualified ComponentType and
    //    PlanExecutor pre-resolved it (no add/remove churn).
    [Test]
    public void Build_ShortName_IdentityStableAcrossRebuild()
    {
        File.WriteAllText(_builderPath, Source(
            "using UnityEngine;",
            "        var cube = scene.Add(\"Cube\");\n" +
            "        cube.Component<Rigidbody>(c => c.Set(r => r.mass, 5f));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var cube = FindRoot(EditorSceneManager.GetActiveScene(), "Cube");
        Assert.IsNotNull(cube, "Cube was not created by SceneBuilderBuild.Run");
        var rb = cube.GetComponent<Rigidbody>();
        Assert.IsNotNull(rb, "Short-named Component<Rigidbody> did not materialize");
        var idBefore = GlobalObjectId.GetGlobalObjectIdSlow(rb).ToString();

        rb.mass = 9f;
        EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var cubeAfter = FindRoot(EditorSceneManager.GetActiveScene(), "Cube");
        Assert.IsNotNull(cubeAfter, "Cube did not survive the rebuild");
        var rbsAfter = cubeAfter.GetComponents<Rigidbody>();
        Assert.AreEqual(1, rbsAfter.Length,
            "Rebuild added/removed the short-named Rigidbody instead of updating it in place");
        var idAfter = GlobalObjectId.GetGlobalObjectIdSlow(rbsAfter[0]).ToString();
        Assert.AreEqual(idBefore, idAfter,
            "GlobalObjectId of the short-named component changed across a Sync + rebuild cycle");
        Assert.AreEqual(9f, rbsAfter[0].mass, "Edited mass=9 did not survive the Sync + rebuild cycle");
    }

    // 6. A typo that matches no simple name anywhere throws a located error naming the object and the
    //    bad token, with NO "Did you mean" clause (no candidate exists) — and leaves the scene untouched.
    [Test]
    public void Build_TypoName_ThrowsLocatedError_SceneUntouched()
    {
        File.WriteAllText(_builderPath, Source(
            "using UnityEngine;",
            "        var cube = scene.Add(\"Cube\");\n" +
            "        cube.Component<Rigidbdy>(c => c.Set(r => r.mass, 5f));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();

        var ex = Assert.Throws<InvalidOperationException>(
            () => SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene),
            "Build did not throw on an unresolvable typo'd component name.");

        StringAssert.Contains("Cube", ex.Message, "Error does not name the object.\n" + ex.Message);
        StringAssert.Contains("'Rigidbdy'", ex.Message, "Error does not name the bad token.\n" + ex.Message);
        StringAssert.Contains("cannot resolve", ex.Message, "Error does not say 'cannot resolve'.\n" + ex.Message);
        StringAssert.Contains("Qualify it, or add a matching using", ex.Message,
            "Error does not tell the author how to fix it.\n" + ex.Message);
        // A typo with no simple-name match anywhere offers no suggestion — asserting "Did you mean"
        // here would be red for the wrong reason (ComponentTypeNormalizer.cs SuggestQualified returns empty).
        StringAssert.DoesNotContain("Did you mean", ex.Message,
            "Error offered a suggestion for a token with no simple-name match.\n" + ex.Message);

        Assert.IsNull(FindRoot(EditorSceneManager.GetActiveScene(), "Cube"),
            "Scene was touched despite Build throwing on an unresolvable component name.");
    }

    // 7. A short name with NO matching using throws a located "cannot resolve" error. Because both
    //    UnityEngine.Rigidbody and the MyGame.Physics.Rigidbody fixture share the simple name
    //    "Rigidbody", a suggestion IS offered — but which one is not asserted (SuggestQualified sorts
    //    Ordinal, so the exact candidate is an implementation detail, not part of the contract).
    [Test]
    public void Build_ShortNameNoUsing_ThrowsLocatedError()
    {
        File.WriteAllText(_builderPath, Source(
            "",
            "        var cube = scene.Add(\"Cube\");\n" +
            "        cube.Component<Rigidbody>(c => c.Set(r => r.mass, 5f));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();

        var ex = Assert.Throws<InvalidOperationException>(
            () => SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene),
            "Build did not throw on a short component name with no matching using.");

        StringAssert.Contains("Cube", ex.Message, "Error does not name the object.\n" + ex.Message);
        StringAssert.Contains("'Rigidbody'", ex.Message, "Error does not name the bad token.\n" + ex.Message);
        StringAssert.Contains("cannot resolve", ex.Message, "Error does not say 'cannot resolve'.\n" + ex.Message);
        StringAssert.Contains("Qualify it, or add a matching using", ex.Message,
            "Error does not tell the author how to fix it.\n" + ex.Message);
        StringAssert.Contains("Did you mean", ex.Message,
            "Error did not offer a suggestion despite a simple-name match existing.\n" + ex.Message);

        Assert.IsNull(FindRoot(EditorSceneManager.GetActiveScene(), "Cube"),
            "Scene was touched despite Build throwing on a short name with no matching using.");
    }

    // 8. Two in-scope namespaces both defining "Rigidbody" throws a located AMBIGUOUS error listing
    //    both fully-qualified candidates and telling the author to qualify — nothing is guessed, and
    //    the scene is untouched.
    [Test]
    public void Build_AmbiguousShortName_ThrowsLocatedError()
    {
        File.WriteAllText(_builderPath, Source(
            "using UnityEngine;\nusing MyGame.Physics;",
            "        var cube = scene.Add(\"Cube\");\n" +
            "        cube.Component<Rigidbody>();"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();

        var ex = Assert.Throws<InvalidOperationException>(
            () => SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene),
            "Build did not throw on an ambiguous short component name.");

        StringAssert.Contains("AMBIGUOUS", ex.Message, "Error does not say AMBIGUOUS.\n" + ex.Message);
        StringAssert.Contains("MyGame.Physics.Rigidbody", ex.Message,
            "Error does not list the MyGame.Physics.Rigidbody candidate.\n" + ex.Message);
        StringAssert.Contains("UnityEngine.Rigidbody", ex.Message,
            "Error does not list the UnityEngine.Rigidbody candidate.\n" + ex.Message);
        StringAssert.Contains("Qualify it", ex.Message, "Error does not tell the author to qualify.\n" + ex.Message);

        Assert.IsNull(FindRoot(EditorSceneManager.GetActiveScene(), "Cube"),
            "Scene was touched despite Build throwing on an ambiguous component name.");
    }

    // 9. A nested-namespace built-in short name (UnityEngine.UI.Image via `using UnityEngine.UI;`)
    //    attaches and a `.Set` on it applies to the live component.
    [Test]
    public void Build_NestedNamespaceImage_AttachesAndSetApplies()
    {
        File.WriteAllText(_builderPath, Source(
            "using UnityEngine.UI;",
            "        var cube = scene.Add(\"Cube\");\n" +
            "        cube.Component<Image>(c => c.Set(\"m_RaycastTarget\", false));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        Assert.DoesNotThrow(() => SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene),
            "Build threw resolving a nested-namespace short built-in name.");

        var cube = FindRoot(EditorSceneManager.GetActiveScene(), "Cube");
        Assert.IsNotNull(cube, "Cube was not created by SceneBuilderBuild.Run");
        var image = cube.GetComponent<UnityEngine.UI.Image>();
        Assert.IsNotNull(image, "Short-named Component<Image> did not materialize a real UnityEngine.UI.Image");
        Assert.IsFalse(image.raycastTarget,
            "Authored raycastTarget=false did not apply (default is true, so this proves .Set applied)");
    }
}
