using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SceneBuilder.Core.Serialization;
using SceneBuilder.Editor;
using SceneBuilder.Editor.Licensing;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Gate for the b5-t1 scene->code executor (spec checklist #7, #8): save-on-create earning a
// durable GlobalObjectId, no forced save for edits on already-durable objects, and the new
// pre-assembled-snapshot Run overload. Drives SceneBuilderAutoSync.ExecuteSceneToCode /
// SceneBuilderSync.Run directly against a real EditMode scene (real GameObject/GlobalObjectId),
// following RoundTripStructuralTests' setup pattern: SceneBuilderBuild.Run seeds an initial
// builder + sidecar + saved scene, then the test drives a live edit and asserts propagation.
public class AutoSceneToCodeTests
{
    // Scene must save under Assets (EditorSceneManager.SaveScene is project-relative). The builder
    // + sidecar live under the real <ProjectRoot>/SceneBuilders/ dir (SceneBuilderPaths.Builder/Sidecar)
    // so SceneBuilderRouter.Discover() — a plain Directory.GetFiles("*.cs") scan of that dir — can
    // route this builder; a temp-dir seed lives outside BuildersDirectory and would silently no-op
    // ExecuteSceneToCode post-rewire.
    private const string ScenePath = "Assets/GateTests/__AutoSceneToCodeTemp.unity";
    private const string BuilderName = "AutoSceneToCodeScene";

    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class AutoSceneToCodeScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    private static GameObject FindRoot(Scene scene, string name) =>
        scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);

    [SetUp]
    public void SetUp()
    {
        LicenseGate.ResetToDefault();
        SceneBuilderPaths.EnsureBuildersDirectory();
        _builderPath = SceneBuilderPaths.Builder(BuilderName);
        _sidecarPath = SceneBuilderPaths.Sidecar(BuilderName);

        SceneBuilderRouter.ResetForTests();
        SceneBuilderAutoSync.ResetForTests();
        SuppressionScope.ResetForTests();
    }

    [TearDown]
    public void TearDown()
    {
        LicenseEnforcement.Register();
        SceneBuilderRouter.ResetForTests();
        SceneBuilderAutoSync.ResetForTests();
        SuppressionScope.ResetForTests();

        if (File.Exists(_builderPath)) File.Delete(_builderPath);
        if (File.Exists(_sidecarPath)) File.Delete(_sidecarPath);

        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }
    }

    // (a) checklist #8 / blocker 4: creating a GameObject live with no manual save must be saved
    // BY the executor so the object earns a durable GlobalObjectId, the builder source appends it,
    // and a second cycle with no further change writes nothing (no re-create).
    //
    // GROUND TRUTH NOTE (falsifies a blueprint assumption): research.md's proposed "create" signal
    // was `GlobalObjectId.GetGlobalObjectIdSlow(obj).targetObjectId == 0` pre-save. A direct probe
    // against this Unity install (6000.5.3f1) disproves that: once the ACTIVE SCENE already has a
    // saved path (identifierType becomes SceneObject), a brand-new, never-saved GameObject already
    // gets a nonzero, deterministically-hashed targetObjectId — id assignment is NOT gated on an
    // actual save on this version. `NeedsSaveForDurableId` per the blueprint's literal check would
    // therefore never fire in the realistic scenario (the active scene always already has a path),
    // silently defeating blocker-4. This test asserts the REAL observable contract instead — whether
    // the scene ASSET ON DISK actually gained the object — which is what "durable" means for the
    // reconcile to key on next session; it does not assume any particular in-memory id signal. The
    // code-writer must find a working save-trigger signal (e.g. diffing the persisted scene text /
    // sidecar-entry presence, not raw targetObjectId) — flag for validator if the blueprint's
    // NeedsSaveForDurableId cannot be made to satisfy this test as specified.
    [Test]
    public void SceneToCode_CreateLiveObject_SavesScene_EarnsDurableId_AppendsSource_SecondSyncNoOp()
    {
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Existing\");"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        SceneBuilderRouter.ResetForTests();

        var sceneTextBeforeCreate = File.ReadAllText(ScenePath);
        StringAssert.DoesNotContain("Weapon", sceneTextBeforeCreate,
            "Precondition: the saved scene asset must not yet mention the not-yet-created object.");

        var created = new GameObject("Weapon");
        var entityId = created.GetEntityId();

        Assert.AreEqual(sceneTextBeforeCreate, File.ReadAllText(ScenePath),
            "Precondition: creating a GameObject live must not itself touch the saved scene asset — it only becomes durable once the executor saves it.");

        SceneBuilderAutoSync.ExecuteSceneToCode(new[] { entityId });

        StringAssert.Contains("Weapon", File.ReadAllText(ScenePath),
            "ExecuteSceneToCode must SAVE the scene before snapshotting so the live-created object becomes durable on disk (checklist #8, blocker 4).");

        var afterFirst = File.ReadAllText(_builderPath);
        StringAssert.Contains("Add(\"Weapon\")", afterFirst,
            "Builder source did not gain an Add(\"Weapon\") statement for the live-created object.\n" + afterFirst);

        var sidecarAfterFirst = File.ReadAllText(_sidecarPath);

        // A second cycle with no further scene change must be a true no-op: no re-create, no re-save.
        SceneBuilderAutoSync.ExecuteSceneToCode(Array.Empty<EntityId>());

        Assert.AreEqual(afterFirst, File.ReadAllText(_builderPath),
            "A second executor cycle with no further scene change must not rewrite the builder source.");
        Assert.AreEqual(sidecarAfterFirst, File.ReadAllText(_sidecarPath),
            "A second executor cycle with no further scene change must not rewrite the sidecar.");
    }

    // Transform/component edits on an object that is ALREADY durable must NOT force a save — only
    // a create does. Asserted via the real on-disk scene asset (see ground-truth note above), not
    // via targetObjectId or the isDirty flag.
    [Test]
    public void SceneToCode_TransformEditOnSavedObject_DoesNotForceSave_Syncs()
    {
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Alpha\");"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        SceneBuilderRouter.ResetForTests();

        var alpha = FindRoot(EditorSceneManager.GetActiveScene(), "Alpha");
        Assert.IsNotNull(alpha, "Alpha was not created by SceneBuilderBuild.Run");

        var sceneTextBeforeEdit = File.ReadAllText(ScenePath);

        alpha.transform.position = new Vector3(1f, 2f, 3f);

        SceneBuilderAutoSync.ExecuteSceneToCode(new[] { alpha.GetEntityId() });

        Assert.AreEqual(sceneTextBeforeEdit, File.ReadAllText(ScenePath),
            "A transform edit on an already-durable object must NOT be force-saved by the executor (only a create earns a save) — the scene asset on disk must be untouched.");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("(1f, 2f, 3f)", rewritten,
            "Builder source was not synced with the moved transform.\n" + rewritten);
    }

    // Regression: the new 4-arg Run overload, given the scene's own cold-read snapshot BUILT WITH THE
    // SAME scene-identity resolver production uses (ObjectReferenceResolver.BuildSceneRefResolver over
    // the sidecar's IdentityMap — SceneBuilderAutoSync.cs always sets this before assembling), must be
    // equivalent in effect to the existing 3-arg Run — RoundTrip*/SyncFuzz tests calling the 3-arg
    // form must stay green untouched. The fixture carries an object-reference field re-pointed live in
    // the scene so the equivalence check actually exercises resolver-dependent sync, not just a
    // transform move.
    [Test]
    public void Run_PreAssembledSnapshotOverload_EquivalentTo_ColdRead()
    {
        File.WriteAllText(_builderPath, Source(
            "        var alpha = scene.Add(\"Alpha\");\n" +
            "        var beta = scene.Add(\"Beta\");\n" +
            "        var opener = scene.Add(\"Opener\");\n" +
            "        opener.Component<DoorOpener>(c => c.Set(\"target\", alpha));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        SceneBuilderRouter.ResetForTests();

        var alpha = FindRoot(EditorSceneManager.GetActiveScene(), "Alpha");
        var beta = FindRoot(EditorSceneManager.GetActiveScene(), "Beta");
        var opener = FindRoot(EditorSceneManager.GetActiveScene(), "Opener");
        Assert.IsNotNull(alpha, "Alpha was not created by SceneBuilderBuild.Run");
        Assert.IsNotNull(beta, "Beta was not created by SceneBuilderBuild.Run");
        Assert.IsNotNull(opener, "Opener was not created by SceneBuilderBuild.Run");
        alpha.transform.position = new Vector3(4f, 5f, 6f);
        // Re-point the live reference away from what the source authored — only a resolver-aware
        // read can detect this, an omitted/null resolver reads it as Unsupported and prunes it.
        opener.GetComponent<DoorOpener>().target = beta;

        var coldBuilderPath = _builderPath + ".cold.cs";
        var coldSidecarPath = _sidecarPath + ".cold.json";
        File.Copy(_builderPath, coldBuilderPath);
        File.Copy(_sidecarPath, coldSidecarPath);

        var liveScene = EditorSceneManager.GetActiveScene();
        var coldResult = SceneBuilderSync.Run(coldBuilderPath, coldSidecarPath, liveScene);

        Assert.Greater(coldResult.EditsApplied, 0,
            "Precondition: the cold read must detect at least one edit (the moved transform and/or the " +
            "re-pointed reference), or the equivalence check below is vacuous.");

        var map = IdentityMapJson.Deserialize(File.ReadAllText(_sidecarPath));
        var preAssembled = SceneSnapshotReader.Read(liveScene, ObjectReferenceResolver.BuildSceneRefResolver(map));
        var overloadResult = SceneBuilderSync.Run(_builderPath, _sidecarPath, liveScene, preAssembled);

        Assert.AreEqual(coldResult.EditsApplied, overloadResult.EditsApplied,
            "The pre-assembled-snapshot overload must apply the same number of edits as the cold 3-arg Run given an equivalent snapshot.");
        Assert.AreEqual(coldResult.Changed, overloadResult.Changed);
        Assert.AreEqual(File.ReadAllText(coldBuilderPath), File.ReadAllText(_builderPath),
            "The pre-assembled-snapshot overload must produce byte-identical source to the cold 3-arg Run given an equivalent snapshot.");
        StringAssert.Contains(", beta)", File.ReadAllText(_builderPath),
            "The re-pointed reference (Opener.target -> Beta) must reach the builder source through the " +
            "pre-assembled-snapshot overload, not only through the cold 3-arg Run.");

        // These live under the real SceneBuilders/ dir now (not a temp dir wiped wholesale by
        // TearDown) — clean up the scratch copies explicitly so they don't linger between runs.
        if (File.Exists(coldBuilderPath)) File.Delete(coldBuilderPath);
        if (File.Exists(coldSidecarPath)) File.Delete(coldSidecarPath);
    }

    // The pump's production default must be wired to the real executor, not left null — the whole
    // point of the auto-sync loop is that the happy path needs no manual wiring.
    [Test]
    public void AutoSync_WireDefaultExecutors_ArmsPumpWithRealSceneToCodeExecutor()
    {
        SceneBuilderAutoSync.WireDefaultExecutors();

        Assert.IsNotNull(SceneBuilderAutoSync.SceneToCodeExecutor,
            "WireDefaultExecutors must assign a real scene->code executor so the auto-sync pump is wired to production logic by default.");

        Action<System.Collections.Generic.IReadOnlyCollection<EntityId>> expected = SceneBuilderAutoSync.ExecuteSceneToCode;
        Assert.AreEqual(expected, SceneBuilderAutoSync.SceneToCodeExecutor,
            "The default-wired scene->code executor must be ExecuteSceneToCode.");
    }
}
