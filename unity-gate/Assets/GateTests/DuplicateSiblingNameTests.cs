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

    // BUILD REFUSES on a colliding LogicalId too, and the header is KIND-NEUTRAL — it must not say
    // "duplicate sibling name" for a hazard that is a hand-authored `.Id(...)` collision, not a
    // positional one. The per-kind instruction lives in the conflict's own Reason.
    [Test]
    public void Build_HandAuthoredCollidingLogicalIds_RefusesAndLeavesSceneUntouched()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Enemy\").Id(\"Enemy-2\");\n" +
            "        scene.Add(\"Enemy\").Id(\"Enemy-2\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var error = Assert.Throws<ParseException>(() =>
            SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene()));

        StringAssert.Contains("REFUSED", error.Message);
        StringAssert.Contains("Enemy-2", error.Message);
        StringAssert.DoesNotContain("duplicate sibling name", error.Message,
            "Header must be kind-neutral: a colliding LogicalId is not a duplicate SIBLING NAME hazard.");

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

    // THE UNITY-BOUNDARY PROOF for b2's write path: not just "contains a `var` handle" (already
    // checked above) but that the emitted source is a real, live, self-consistent artifact — it
    // parses unambiguously, COMPILES, and is a fixed point (a second Sync with no scene change is a
    // no-op). A `var` handle that fails any of those would be a regression this file cannot see
    // otherwise.
    [Test]
    public void Sync_HierarchyDuplicatedSibling_EmitsCompilingConvergentHandle()
    {
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Enemy\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        // The user duplicates it in the Hierarchy: a second root GameObject with the SAME name.
        new GameObject("Enemy");
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

        SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var emitted = File.ReadAllText(_builderPath);

        StringAssert.Contains("var enemy", emitted);

        Assert.IsEmpty(BuilderParser.Parse(emitted).Ambiguities,
            "Sync emitted a file that is still ambiguous:\n" + emitted);

        var errors = BuilderCompileCheck.Check(emitted);
        Assert.IsEmpty(errors, BuilderCompileCheck.Format(errors, "b4-t1 emission", emitted));

        // Convergence: no scene change, so a second Sync must be a no-op.
        var second = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.AreEqual(0, second.PatchEdits,
            "A second Sync with no scene change was not a no-op:\n" + File.ReadAllText(_builderPath));
    }

    // SYNC HEALS A COLLIDING LogicalId BEFORE RECONCILING.
    //
    // A colliding `.Id("Enemy-2")` pair can never reach Build (it refuses on ANY ambiguity), so the
    // only way this hazard reaches a live editor is a code paste: the incumbent already has a
    // sidecar-tracked object, then a second, colliding statement is introduced by hand/LLM. Sync
    // must heal the SOURCE (re-mint the later occurrence into its own `var` handle) BEFORE it
    // reconciles — healing after would already have lost a node to FlattenModel's last-write-wins
    // (see IdCollisionDataLossTests). The incumbent's own statement — and its sidecar-tracked
    // identity — must survive untouched.
    [Test]
    public void Sync_HealsCollidingLogicalId_RemintsSecondOccurrence_IncumbentSurvives()
    {
        // 1. Establish the incumbent: a single, unambiguous Id("Enemy-2").
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Enemy\").Id(\"Enemy-2\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var incumbent = RootsNamed("Enemy").Single();
        var incumbentId = incumbent.GetEntityId();

        // 2. Introduce the collision (simulates a code paste): a second, identical Id("Enemy-2").
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Enemy\").Id(\"Enemy-2\");\n" +
            "        scene.Add(\"Enemy\").Id(\"Enemy-2\");"));

        // 3. Matching scene edit: a second root "Enemy" so the healed second node has an object.
        new GameObject("Enemy");
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

        // 4. Sync must heal before reconciling.
        SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var emitted = File.ReadAllText(_builderPath);

        // Second occurrence re-minted into its own handle...
        StringAssert.Contains("var enemy", emitted);
        // ...and the incumbent's own statement is byte-identical — untouched, not re-minted.
        StringAssert.Contains("scene.Add(\"Enemy\").Id(\"Enemy-2\");", emitted);
        // The file is no longer ambiguous.
        Assert.IsEmpty(BuilderParser.Parse(emitted).Ambiguities,
            "Sync did not heal the colliding LogicalId:\n" + emitted);

        var errors = BuilderCompileCheck.Check(emitted);
        Assert.IsEmpty(errors, BuilderCompileCheck.Format(errors, "b4-t3 heal", emitted));

        // The incumbent GameObject was never touched by Sync (Sync never mutates the scene).
        Assert.IsTrue(incumbent != null, "The incumbent Enemy was destroyed by Sync.");
        Assert.AreEqual(incumbentId, incumbent.GetEntityId());

        // 5. THE CRUX: a subsequent Build no longer refuses, AND the sidecar-tracked incumbent
        // survives it — the re-mint must not destroy and recreate the object the sidecar already
        // tracked.
        Assert.DoesNotThrow(() =>
            SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene()));

        var liveIds = RootsNamed("Enemy").Select(go => go.GetEntityId()).ToArray();
        CollectionAssert.Contains(liveIds, incumbentId,
            "The re-mint destroyed and recreated the sidecar-tracked incumbent:\n" + emitted);
    }

    // THE ROUND TRIP FOR THE HANDLE-HEADED TIER, NOT THE POSITIONAL ONE.
    //
    // `Reorder_DuplicateNamedSiblings_PreservesObjectAndComponentIdentity` above puts the Rigidbody
    // on the SIDECAR-MAPPED incumbent (positional `scene.Add("Enemy")`), which was already the safe
    // case pre-fix. This test puts the Rigidbody on the HANDLE-HEADED duplicate (`var enemy`) instead
    // — the object whose identity is carried purely by its own statement, not by sidecar position —
    // and proves a pure reorder still doesn't destroy-and-recreate it or move its component onto the
    // other Enemy. This is the tier the milestone actually introduced.
    [Test]
    public void ReorderHandleHeadedDuplicate_WithComponentOnTheHandle_PreservesIdentityThroughRoundTrip()
    {
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Enemy\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var first = RootsNamed("Enemy").Single();

        // The user duplicates it in the Hierarchy: a second, same-named root sibling.
        var second = new GameObject("Enemy");
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

        // Sync #1: the duplicate is headed by its own `var` handle; `first` stays positional.
        SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        // Give the HANDLE-HEADED duplicate (not the positional incumbent) a Rigidbody.
        var rigidbody = second.AddComponent<Rigidbody>();
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

        // Sync #2: the component is written onto the `var enemy` statement.
        SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var firstId = first.GetEntityId();
        var secondId = second.GetEntityId();
        var rigidbodyId = rigidbody.GetEntityId();

        // THE PURE REORDER: swap the two in the Hierarchy. Creates nothing, destroys nothing.
        second.transform.SetSiblingIndex(0);
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

        // Sync #3: the reorder must carry the `var enemy` statement (and its Component) with it.
        SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var emitted = File.ReadAllText(_builderPath);
        Assert.IsEmpty(BuilderParser.Parse(emitted).Ambiguities,
            "Reordering the handle-headed duplicate left an ambiguous file:\n" + emitted);

        // Close the loop: build the code back into the scene.
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var enemies = RootsNamed("Enemy");
        Assert.AreEqual(2, enemies.Length, "A pure reorder changed the object count:\n" + emitted);

        var liveIds2 = enemies.Select(go => go.GetEntityId()).ToArray();
        CollectionAssert.Contains(liveIds2, firstId,
            "The incumbent Enemy was DESTROYED and recreated by a pure reorder:\n" + emitted);
        CollectionAssert.Contains(liveIds2, secondId,
            "The handle-headed Enemy was DESTROYED and recreated by a pure reorder:\n" + emitted);

        // The component is the load-bearing part: identity swaps show up as a destroyed Rigidbody
        // re-created on the OTHER object, which silently loses every value the user set on it.
        Assert.IsTrue(rigidbody != null,
            "The live Rigidbody was DESTROYED by a pure reorder:\n" + emitted);
        Assert.AreEqual(rigidbodyId, rigidbody.GetEntityId());
        Assert.AreEqual(secondId, rigidbody.gameObject.GetEntityId(),
            "The Rigidbody ended up on the WRONG Enemy — the var handle failed to carry component " +
            "identity through the reorder:\n" + emitted);
    }
}
