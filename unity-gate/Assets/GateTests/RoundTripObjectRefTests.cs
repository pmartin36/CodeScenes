using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;
using SceneBuilder.Core.Reconcile;

// M5 (cross-object references) bidirectional round-trip gate tests — the spec's confirmation
// checklist (specs/06-m5-cross-object-references.md), items 1-6. Drives the FULL loop (code->scene
// via SceneBuilderBuild.Run, scene->code via EmittedCodeCompiles.SyncAndAssertCompiles) against a
// live editor scene with real GameObject/Component/SerializedProperty/GlobalObjectId/IdentityMap —
// the boundary the POCO Core tests and the b5 focused-unit tests (PlanExecutorObjectRefTests,
// AssetReferenceResolverObjectRefReadTests) are structurally blind to. Mirrors the
// RoundTripComponentTests / RoundTripAssetRefTests harness verbatim (temp builder .cs + sidecar in a
// system temp dir, EmptyScene per test, [SetUp]/[TearDown] cleanup).
//
// b1-b5 production for M5 is already landed (confirmed by research.md); these tests are expected to
// PASS on first run — this file IS the mandatory full-loop gate coverage (CLAUDE.md hard requirement),
// not a RED test driving new production. A failure here is a genuine Unity-boundary escape.
public class RoundTripObjectRefTests
{
    private const string ScenePath = "Assets/GateTests/__RoundTripObjectRefTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class RoundTripObjectRefScene : ISceneDefinition
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
        _dir = Path.Combine(Path.GetTempPath(), "sb_rtor_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "RoundTripObjectRefScene.cs");
        _sidecarPath = Path.Combine(_dir, "RoundTripObjectRefScene.sbmap.json");
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

    // 1. Mutual A<->B reference (code->scene): Materialize resolves BOTH target slots, no ordering
    //    error, regardless of authoring order.
    [Test]
    public void CodeToScene_MutualReference_BothTargetsResolve()
    {
        File.WriteAllText(_builderPath, Source(
            "        var a = scene.Add(\"A\");\n" +
            "        var b = scene.Add(\"B\");\n" +
            "        a.Component<DoorOpener>(c => c.Set(x => x.target, b));\n" +
            "        b.Component<DoorOpener>(c => c.Set(x => x.target, a));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var a = FindRoot(EditorSceneManager.GetActiveScene(), "A");
        var b = FindRoot(EditorSceneManager.GetActiveScene(), "B");
        Assert.IsNotNull(a, "A was not created by SceneBuilderBuild.Run");
        Assert.IsNotNull(b, "B was not created by SceneBuilderBuild.Run");
        Assert.AreEqual(b, a.GetComponent<DoorOpener>().target, "A's target did not resolve to B");
        Assert.AreEqual(a, b.GetComponent<DoorOpener>().target, "B's target did not resolve to A");
    }

    // 2. Rewire a target in the scene (scene->code): dragging a different mapped GameObject into the
    //    target field swaps the source handle argument to the new target's handle name.
    [Test]
    public void SceneToCode_RewiredTarget_SwapsHandleArgument()
    {
        File.WriteAllText(_builderPath, Source(
            "        var door = scene.Add(\"Door\");\n" +
            "        var other = scene.Add(\"Other\");\n" +
            "        var opener = scene.Add(\"Opener\");\n" +
            "        opener.Component<DoorOpener>(c => c.Set(x => x.target, door));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var opener = FindRoot(EditorSceneManager.GetActiveScene(), "Opener");
        var other = FindRoot(EditorSceneManager.GetActiveScene(), "Other");
        Assert.IsNotNull(opener, "Opener was not created by SceneBuilderBuild.Run");
        Assert.IsNotNull(other, "Other was not created by SceneBuilderBuild.Run");
        opener.GetComponent<DoorOpener>().target = other;

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite a rewired reference field");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("x.target, other", rewritten,
            "Builder source did not swap the handle argument to 'other'.\n" + rewritten);
        StringAssert.DoesNotContain("x.target, door", rewritten,
            "Builder source still carries the old 'door' handle argument.\n" + rewritten);
    }

    // 3. Clear a target to None in the scene (scene->code): the source argument becomes
    //    NodeHandle.None (the target object itself stays alive — this is a legit clear, not dangling).
    [Test]
    public void SceneToCode_ClearedTargetToNone_WritesNodeHandleNone()
    {
        File.WriteAllText(_builderPath, Source(
            "        var door = scene.Add(\"Door\");\n" +
            "        var opener = scene.Add(\"Opener\");\n" +
            "        opener.Component<DoorOpener>(c => c.Set(x => x.target, door));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var opener = FindRoot(EditorSceneManager.GetActiveScene(), "Opener");
        Assert.IsNotNull(opener, "Opener was not created by SceneBuilderBuild.Run");
        opener.GetComponent<DoorOpener>().target = null;

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite a cleared reference field");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("NodeHandle.None", rewritten,
            "Builder source did not write NodeHandle.None for a cleared reference field.\n" + rewritten);
    }

    // 4. Delete the referenced GameObject (scene->code): a loud, located ConflictKind.DanglingReference
    //    naming the source object + field, and the source is NOT rewritten (never a silent clear).
    [Test]
    public void SceneToCode_DeletedTarget_ReportsDanglingReferenceAndDoesNotClearSource()
    {
        File.WriteAllText(_builderPath, Source(
            "        var door = scene.Add(\"Door\");\n" +
            "        var opener = scene.Add(\"Opener\");\n" +
            "        opener.Component<DoorOpener>(c => c.Set(x => x.target, door));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var door = FindRoot(EditorSceneManager.GetActiveScene(), "Door");
        Assert.IsNotNull(door, "Door was not created by SceneBuilderBuild.Run");
        Object.DestroyImmediate(door);

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        Assert.IsTrue(result.Conflicts.Any(c => c.Kind == ConflictKind.DanglingReference),
            "Sync did not report a DanglingReference conflict for the deleted target.");
        var conflict = result.Conflicts.First(c => c.Kind == ConflictKind.DanglingReference);
        StringAssert.Contains("Opener", conflict.Reason, "Conflict reason does not name the source object.\n" + conflict.Reason);
        StringAssert.Contains("target", conflict.Reason, "Conflict reason does not name the field.\n" + conflict.Reason);

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("x.target, door", rewritten,
            "Builder source no longer carries the dangling 'door' handle argument — the field was silently rewritten.\n" + rewritten);
        StringAssert.DoesNotContain("NodeHandle.None", rewritten,
            "Builder source was silently cleared to NodeHandle.None instead of reporting a dangling reference.\n" + rewritten);
    }

    // 5. Idempotence: after a scene rewire updates the source, a follow-up Build materializes the same
    //    target, and a second Sync (with no further scene change) is a no-op.
    [Test]
    public void RoundTrip_RewireThenMaterialize_IsIdempotent()
    {
        File.WriteAllText(_builderPath, Source(
            "        var door = scene.Add(\"Door\");\n" +
            "        var other = scene.Add(\"Other\");\n" +
            "        var opener = scene.Add(\"Opener\");\n" +
            "        opener.Component<DoorOpener>(c => c.Set(x => x.target, door));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var opener = FindRoot(EditorSceneManager.GetActiveScene(), "Opener");
        var other = FindRoot(EditorSceneManager.GetActiveScene(), "Other");
        opener.GetComponent<DoorOpener>().target = other;

        EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        // Rebuild from the rewritten source into a fresh scene — the target must still resolve to Other.
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        var rebuiltOpener = FindRoot(EditorSceneManager.GetActiveScene(), "Opener");
        var rebuiltOther = FindRoot(EditorSceneManager.GetActiveScene(), "Other");
        Assert.AreEqual(rebuiltOther, rebuiltOpener.GetComponent<DoorOpener>().target,
            "Rebuilding from the rewired source did not materialize the new target.");

        var second = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(second.Changed,
            "NOT CONVERGED: a Sync immediately after a rebuild, with no further scene change, reported Changed=true.");
        Assert.AreEqual(0, second.PatchEdits,
            "NOT CONVERGED: the no-op re-sync's reconcile produced " + second.PatchEdits + " patch edit(s).");
    }

    // 6. Component-typed target (code->scene): a field referencing another object's Rigidbody resolves
    //    to the COMPONENT, not merely its GameObject.
    [Test]
    public void CodeToScene_ComponentTypedTarget_ResolvesToComponent()
    {
        File.WriteAllText(_builderPath, Source(
            "        var body = scene.Add(\"Body\");\n" +
            "        body.Component<UnityEngine.Rigidbody>();\n" +
            "        var joint = scene.Add(\"Joint\");\n" +
            "        joint.Component<UnityEngine.HingeJoint>(c => c.Set(x => x.connectedBody, body));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var joint = FindRoot(EditorSceneManager.GetActiveScene(), "Joint");
        var body = FindRoot(EditorSceneManager.GetActiveScene(), "Body");
        Assert.IsNotNull(joint, "Joint was not created by SceneBuilderBuild.Run");
        Assert.IsNotNull(body, "Body was not created by SceneBuilderBuild.Run");
        var hingeJoint = joint.GetComponent<HingeJoint>();
        var rb = body.GetComponent<Rigidbody>();
        Assert.IsNotNull(hingeJoint, "HingeJoint was not materialized on Joint");
        Assert.IsNotNull(rb, "Rigidbody was not materialized on Body");
        Assert.AreEqual(rb, hingeJoint.connectedBody,
            "Component-typed target resolved to the GameObject instead of its Rigidbody component");
    }
}
