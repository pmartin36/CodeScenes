using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;
using SceneBuilder.Core.Parsing;

// DUPLICATE SIBLING NAMES — the Unity boundary.
//
// Unity EXPLICITLY permits two children of one parent to share a name (duplicating a GameObject in
// the Hierarchy is the everyday way to produce it), so the tool must cope with it rather than fight
// the engine's data model. Two same-named statements with no `.Id(...)` are distinguishable only by
// their POSITION in the file, which means any edit that moves a statement silently re-points
// identity at a different real object: a pure reorder destroys a live component and recreates it on
// the wrong GameObject.
//
// The headless Core tests prove the id arithmetic on POCO fixtures. They are structurally blind to
// what this file checks: that against a REAL editor scene, with real GlobalObjectIds and real
// GameObject instances, the round trip does not destroy the user's objects.
public class DuplicateSiblingNameTests
{
    private const string ScenePath = "Assets/GateTests/__DuplicateSiblingTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class DupScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_dup_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "DupScene.cs");
        _sidecarPath = Path.Combine(_dir, "DupScene.sbmap.json");
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

    private static GameObject[] RootsNamed(string name) =>
        EditorSceneManager.GetActiveScene().GetRootGameObjects().Where(go => go.name == name).ToArray();

    // BUILD REFUSES, and does not touch the scene.
    //
    // A pair a human or an LLM hand-authored has no correct answer available — the information that
    // tells the two objects apart was never written down — so Build must surface a located error
    // (§7), not guess. Guessing wrong destroys a real object and repurposes another, and the end
    // state LOOKS right, so nothing would ever surface.
    [Test]
    public void Build_HandAuthoredDuplicateSiblings_RefusesAndLeavesSceneUntouched()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Enemy\");\n" +
            "        scene.Add(\"Enemy\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var error = Assert.Throws<ParseException>(() =>
            SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene()));

        StringAssert.Contains("REFUSED", error.Message);
        StringAssert.Contains(".Id(", error.Message);

        // Refused BEFORE Materialize/Execute: nothing was created.
        Assert.AreEqual(0, EditorSceneManager.GetActiveScene().GetRootGameObjects().Length,
            "Build refused but still mutated the scene.");
    }

    // A disambiguated pair builds fine — the refusal is about AMBIGUITY, not about duplicate names,
    // which Unity permits and the tool must support.
    [Test]
    public void Build_DuplicateSiblingsWithExplicitIds_BuildsBothObjects()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Enemy\").Id(\"Enemy-1\");\n" +
            "        scene.Add(\"Enemy\").Id(\"Enemy-2\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        Assert.AreEqual(2, RootsNamed("Enemy").Length, "Both same-named siblings should exist in the scene.");
    }

    // THE WRITE PATH CANNOT CREATE THE HAZARD.
    //
    // Duplicating a GameObject in the Hierarchy is how a user produces same-named siblings. Sync is
    // the only path that writes builder source, so it heads the duplicate with its own `var` handle
    // at the moment it would otherwise emit a second positional `Add("Enemy")` — the file never
    // CONTAINS an ambiguous pair, not even for one sync.
    [Test]
    public void Sync_DuplicateNamedSiblingCreatedInScene_InjectsDeterministicSemanticId()
    {
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Enemy\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        // The user duplicates it: a second root GameObject with the SAME name.
        new GameObject("Enemy");
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

        SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var emitted = File.ReadAllText(_builderPath);

        // Deterministic and SEMANTIC — derived from the object's own name, never a random GUID (the
        // file gets rewritten by an LLM; an opaque GUID does not survive that).
        StringAssert.Contains("var enemy", emitted);

        // And the file is no longer ambiguous, which is the whole deliverable.
        Assert.IsEmpty(BuilderParser.Parse(emitted).Ambiguities,
            "Sync emitted a file that is still ambiguous:\n" + emitted);
    }

    // THE HEADLINE. A pure reorder of two same-named siblings must not destroy anything.
    //
    // This is the test the whole milestone exists for, and the one every other invariant is blind to:
    // before the fix the emitted source still parsed, still compiled and still converged — it just
    // described the WRONG objects, so the Rigidbody on the first Enemy was destroyed and a new one
    // created on the second. Identity (EntityId), not shape, is what catches that.
    [Test]
    public void Reorder_DuplicateNamedSiblings_PreservesObjectAndComponentIdentity()
    {
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Enemy\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        // First Enemy owns a Rigidbody; the user duplicates the object to get a second, bare Enemy.
        var first = RootsNamed("Enemy").Single();
        var rigidbody = first.AddComponent<Rigidbody>();
        var second = new GameObject("Enemy");
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

        // Sync writes the pair down — the duplicate heads its own `var` handle, so they are no
        // longer position-only.
        SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var firstId = first.GetEntityId();
        var secondId = second.GetEntityId();
        var rigidbodyId = rigidbody.GetEntityId();

        // THE PURE REORDER: swap the two in the Hierarchy. Creates nothing, destroys nothing.
        second.transform.SetSiblingIndex(0);
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

        SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var emitted = File.ReadAllText(_builderPath);
        Assert.IsEmpty(BuilderParser.Parse(emitted).Ambiguities,
            "Reordering left an ambiguous file:\n" + emitted);

        // Close the loop: build the code back into the scene. THIS is where a wrong-but-consistent
        // model destroys real objects.
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var enemies = RootsNamed("Enemy");
        Assert.AreEqual(2, enemies.Length, "A pure reorder changed the object count:\n" + emitted);

        var liveIds = enemies.Select(go => go.GetEntityId()).ToArray();
        CollectionAssert.Contains(liveIds, firstId,
            "The Rigidbody-owning Enemy was DESTROYED and recreated by a pure reorder:\n" + emitted);
        CollectionAssert.Contains(liveIds, secondId,
            "The second Enemy was DESTROYED and recreated by a pure reorder:\n" + emitted);

        // The component is the load-bearing part: identity swaps show up as a destroyed Rigidbody
        // re-created on the OTHER object, which silently loses every value the user set on it.
        Assert.IsTrue(rigidbody != null,
            "The live Rigidbody was DESTROYED by a pure reorder:\n" + emitted);
        Assert.AreEqual(rigidbodyId, rigidbody.GetEntityId());
        Assert.AreEqual(firstId, rigidbody.gameObject.GetEntityId(),
            "The Rigidbody ended up on the WRONG Enemy — identity was swapped:\n" + emitted);
    }
}
