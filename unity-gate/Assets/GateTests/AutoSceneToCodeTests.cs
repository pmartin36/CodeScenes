using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SceneBuilder.Editor;
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
    // Scene must save under Assets (EditorSceneManager.SaveScene is project-relative); the builder
    // + sidecar live in a system temp dir so Unity never tries to import/compile the builder .cs.
    private const string ScenePath = "Assets/GateTests/__AutoSceneToCodeTemp.unity";

    private string _dir;
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
        _dir = Path.Combine(Path.GetTempPath(), "sb_a2c_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "AutoSceneToCodeScene.cs");
        _sidecarPath = Path.Combine(_dir, "AutoSceneToCodeScene.sbmap.json");

        SceneBuilderAutoSync.ResetForTests();
        SuppressionScope.ResetForTests();
    }

    [TearDown]
    public void TearDown()
    {
        SceneBuilderAutoSync.ResetForTests();
        SuppressionScope.ResetForTests();

        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

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

    // Regression: the new 4-arg Run overload, given the scene's own cold-read snapshot, must be
    // equivalent in effect to the existing 3-arg Run — RoundTrip*/SyncFuzz tests calling the 3-arg
    // form must stay green untouched.
    [Test]
    public void Run_PreAssembledSnapshotOverload_EquivalentTo_ColdRead()
    {
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Alpha\");"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var alpha = FindRoot(EditorSceneManager.GetActiveScene(), "Alpha");
        Assert.IsNotNull(alpha, "Alpha was not created by SceneBuilderBuild.Run");
        alpha.transform.position = new Vector3(4f, 5f, 6f);

        var coldBuilderPath = _builderPath + ".cold.cs";
        var coldSidecarPath = _sidecarPath + ".cold.json";
        File.Copy(_builderPath, coldBuilderPath);
        File.Copy(_sidecarPath, coldSidecarPath);

        var liveScene = EditorSceneManager.GetActiveScene();
        var coldResult = SceneBuilderSync.Run(coldBuilderPath, coldSidecarPath, liveScene);

        var preAssembled = SceneSnapshotReader.Read(liveScene);
        var overloadResult = SceneBuilderSync.Run(_builderPath, _sidecarPath, liveScene, preAssembled);

        Assert.AreEqual(coldResult.EditsApplied, overloadResult.EditsApplied,
            "The pre-assembled-snapshot overload must apply the same number of edits as the cold 3-arg Run given an equivalent snapshot.");
        Assert.AreEqual(coldResult.Changed, overloadResult.Changed);
        Assert.AreEqual(File.ReadAllText(coldBuilderPath), File.ReadAllText(_builderPath),
            "The pre-assembled-snapshot overload must produce byte-identical source to the cold 3-arg Run given an equivalent snapshot.");
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
