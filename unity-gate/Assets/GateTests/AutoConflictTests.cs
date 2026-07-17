using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SceneBuilder.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// Gate for the b6-t1 field-level conflict resolution (spec checklist #9, #10): a both-sides-changed
// cycle auto-applies every NON-overlapping field in its own direction, and resolves a TRUE same-field
// overlap scene-wins with the prior code value preserved in an inline `// CONFLICT:` marker, a located
// Console error, and no modal. Follows RoundTripComponentTests' two-phase build pattern (build the bare
// object first so it earns a mapped GlobalObjectId, then author the component so field edits key onto
// an already-durable object) and AutoSceneToCodeTests'/AutoTriggerTests' direct-executor-drive pattern.
public class AutoConflictTests
{
    private const string ScenePath = "Assets/GateTests/__AutoConflictTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class AutoConflictScene : ISceneDefinition
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
        _dir = Path.Combine(Path.GetTempPath(), "sb_conflict_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "AutoConflictScene.cs");
        _sidecarPath = Path.Combine(_dir, "AutoConflictScene.sbmap.json");

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

    /// <summary>
    /// Two-phase build (per RoundTripComponentTests): Box + Beta are created bare first so they each
    /// earn a mapped GlobalObjectId, then a Rigidbody with an explicit m_Mass=1f is authored onto both
    /// and rebuilt — so a later live/code edit to m_Mass targets an already-durable, mapped component.
    /// </summary>
    private Scene BuildTwoMassObjects()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Box\");\n" +
            "        scene.Add(\"Beta\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        File.WriteAllText(_builderPath, Source(
            "        var box = scene.Add(\"Box\");\n" +
            "        box.Component<UnityEngine.Rigidbody>(c => c.Set(\"m_Mass\", 1f));\n" +
            "        var beta = scene.Add(\"Beta\");\n" +
            "        beta.Component<UnityEngine.Rigidbody>(c => c.Set(\"m_Mass\", 1f));"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        return EditorSceneManager.GetActiveScene();
    }

    // (#9) A scene edit to field A of object X and a code edit to field B of object Y must BOTH apply
    // in their own direction with NO conflict raised: Box's live mass change lands in source, Beta's
    // external source mass change lands in the live scene, no `// CONFLICT:` marker anywhere.
    [Test]
    public void Conflict_NonOverlappingFields_BothApply_NoConflictRaised()
    {
        var scene = BuildTwoMassObjects();
        var box = FindRoot(scene, "Box");
        var beta = FindRoot(scene, "Beta");
        Assert.IsNotNull(box, "Box was not created.");
        Assert.IsNotNull(beta, "Beta was not created.");

        SceneBuilderAutoSync.CaptureBaseline(scene);

        // Scene-side change: Box's mass, live.
        box.GetComponent<Rigidbody>().mass = 9f;

        // Code-side change: Beta's mass, external edit (bypassing WriteIfChanged so it reads as a
        // real external write) — Box's statement is untouched.
        File.WriteAllText(_builderPath, Source(
            "        var box = scene.Add(\"Box\");\n" +
            "        box.Component<UnityEngine.Rigidbody>(c => c.Set(\"m_Mass\", 1f));\n" +
            "        var beta = scene.Add(\"Beta\");\n" +
            "        beta.Component<UnityEngine.Rigidbody>(c => c.Set(\"m_Mass\", 7f));"));

        SceneBuilderAutoSync.ExecuteBothChanged(
            new[] { box.GetEntityId() },
            new[] { _builderPath });

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.DoesNotContain("// CONFLICT:", rewritten,
            "Non-overlapping edits on different objects/fields must never raise a conflict marker.\n" + rewritten);
        StringAssert.Contains("9f", rewritten,
            "Box's scene-changed mass must be patched into the source (its own, non-conflicting direction).\n" + rewritten);
        StringAssert.Contains("7f", rewritten,
            "Beta's code-changed mass must be preserved in the source (it is the authority for that field).\n" + rewritten);

        var betaAfter = FindRoot(EditorSceneManager.GetActiveScene(), "Beta");
        Assert.AreEqual(7f, betaAfter.GetComponent<Rigidbody>().mass,
            "Beta's code-changed mass must be materialized into the live scene (its own, non-conflicting direction).");
    }

    // (#10) A scene edit and a code edit to the SAME field of the SAME object must resolve scene-wins:
    // the source statement carries the scene value, the prior code value is preserved in an inline
    // `// CONFLICT:` marker (never silently discarded), and a located Console error is logged. No modal.
    [Test]
    public void Conflict_SameFieldSameObject_SceneWins_PreservesCodeInMarker_LocatesError()
    {
        var scene = BuildTwoMassObjects();
        var box = FindRoot(scene, "Box");
        Assert.IsNotNull(box, "Box was not created.");

        SceneBuilderAutoSync.CaptureBaseline(scene);

        // Scene-side change: Box's mass, live.
        box.GetComponent<Rigidbody>().mass = 9f;

        // Code-side change to the SAME field of the SAME object: Box's mass, external edit.
        File.WriteAllText(_builderPath, Source(
            "        var box = scene.Add(\"Box\");\n" +
            "        box.Component<UnityEngine.Rigidbody>(c => c.Set(\"m_Mass\", 3f));\n" +
            "        var beta = scene.Add(\"Beta\");\n" +
            "        beta.Component<UnityEngine.Rigidbody>(c => c.Set(\"m_Mass\", 1f));"));

        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Box.*m_Mass|m_Mass.*Box"));

        SceneBuilderAutoSync.ExecuteBothChanged(
            new[] { box.GetEntityId() },
            new[] { _builderPath });

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("// CONFLICT:", rewritten,
            "A true same-field-same-object overlap must leave a `// CONFLICT:` marker in the source.\n" + rewritten);
        StringAssert.Contains("3f", rewritten,
            "The prior CODE value must be preserved (recoverable) in the marker, never silently discarded.\n" + rewritten);
        StringAssert.Contains("9f", rewritten,
            "The conflicting statement's live value must be the SCENE value (scene-wins tie-break).\n" + rewritten);

        var boxAfter = FindRoot(EditorSceneManager.GetActiveScene(), "Box");
        Assert.AreEqual(9f, boxAfter.GetComponent<Rigidbody>().mass,
            "Scene-wins: the live scene must keep the scene value on the conflicting field, not revert to the code value.");
    }

    // The b4 pump must route a tick where BOTH a scene deadline and a source deadline are due in the
    // SAME window through ONE combined conflict-aware cycle — never the two single-direction executors
    // independently (which would let one side silently clobber the other via reconcile-against-stale-
    // baseline, per research.md Refinement 2).
    [Test]
    public void Conflict_DualTrigger_RunsOneCombinedCycle_NotTwoSingles()
    {
        var go = new GameObject("Target");
        var path = Path.Combine(_dir, "External.cs");
        File.WriteAllText(path, "// external, never recorded via WriteIfChanged");
        try
        {
            var sceneCycles = 0;
            var sourceCycles = 0;
            var conflictCycles = 0;
            SceneBuilderAutoSync.SceneToCodeExecutor = _ => sceneCycles++;
            SceneBuilderAutoSync.CodeToSceneExecutor = _ => sourceCycles++;
            SceneBuilderAutoSync.ConflictExecutor = (_, __) => conflictCycles++;

            var now = 100.0;
            SceneBuilderAutoSync.Clock = () => now;
            SceneBuilderAutoSync.NotifySceneChanged(new[] { go.GetEntityId() });
            SceneBuilderAutoSync.NotifySourceChanged(path);

            now += SceneBuilderAutoSync.SettleSeconds + 0.01;
            SceneBuilderAutoSync.PumpOnce(now);

            Assert.AreEqual(1, SceneBuilderAutoSync.ConflictCycleCount,
                "When both a scene change and a real external source change settle in the SAME window, " +
                "the pump must run the combined conflict-aware cycle exactly once.");
            Assert.AreEqual(1, conflictCycles, "ConflictExecutor must be invoked exactly once for the dual-trigger tick.");
            Assert.AreEqual(0, sceneCycles,
                "The single-direction scene->code executor must NOT also run on a dual-trigger tick — " +
                "the combined cycle replaces it, it does not run alongside it.");
            Assert.AreEqual(0, sourceCycles,
                "The single-direction code->scene executor must NOT also run on a dual-trigger tick — " +
                "the combined cycle replaces it, it does not run alongside it.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    // Scope-validator finding (bucket-b6.md, HIGH): the wired production single-direction executors
    // (ExecuteSceneToCode/ExecuteCodeToScene) never call CaptureBaseline, so a live session's baseline
    // stays null forever and every dual-trigger cycle degrades to the clobbering fallback — the
    // conflict-aware merge (this task's whole deliverable) is unreachable outside a test that seeds the
    // baseline directly. This drives ONLY the production executor (never CaptureBaseline directly) to
    // prove the real session path establishes it, then proves the practical consequence: a code-only
    // edit made after that converged cycle must survive a later dual-trigger conflict cycle.
    [Test]
    public void Conflict_SceneToCodeProductionCycle_EstablishesBaseline_SoLaterConflictCycleDoesNotClobberCode()
    {
        var scene = BuildTwoMassObjects();
        var box = FindRoot(scene, "Box");
        var beta = FindRoot(scene, "Beta");
        Assert.IsNotNull(box, "Box was not created.");
        Assert.IsNotNull(beta, "Beta was not created.");

        // A real prior converged cycle, via the production executor only — no direct CaptureBaseline call.
        SceneBuilderAutoSync.ExecuteSceneToCode(Array.Empty<EntityId>());

        Assert.IsNotNull(SceneBuilderAutoSync.BaselineSource,
            "A converged production scene->code cycle (ExecuteSceneToCode) must establish the " +
            "conflict-aware baseline itself — it must not stay null until a test calls CaptureBaseline directly.");
        Assert.IsNotNull(SceneBuilderAutoSync.BaselineSnapshot,
            "A converged production scene->code cycle (ExecuteSceneToCode) must establish the baseline snapshot.");

        // Both-sides change: Box live in the scene, Beta externally in code (non-overlapping fields).
        box.GetComponent<Rigidbody>().mass = 9f;
        File.WriteAllText(_builderPath, Source(
            "        var box = scene.Add(\"Box\");\n" +
            "        box.Component<UnityEngine.Rigidbody>(c => c.Set(\"m_Mass\", 1f));\n" +
            "        var beta = scene.Add(\"Beta\");\n" +
            "        beta.Component<UnityEngine.Rigidbody>(c => c.Set(\"m_Mass\", 7f));"));

        SceneBuilderAutoSync.ExecuteBothChanged(
            new[] { box.GetEntityId() },
            new[] { _builderPath });

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("7f", rewritten,
            "Beta's code-changed mass must survive the conflict-aware cycle when the baseline was " +
            "established through the real production single-direction path, not a direct CaptureBaseline call.\n" + rewritten);
    }

    [Test]
    public void Conflict_CodeToSceneProductionCycle_EstablishesBaseline()
    {
        var scene = BuildTwoMassObjects();

        // A real prior converged cycle via the production code->scene executor only.
        SceneBuilderAutoSync.ExecuteCodeToScene(new[] { _builderPath });

        Assert.IsNotNull(SceneBuilderAutoSync.BaselineSource,
            "A converged production code->scene cycle (ExecuteCodeToScene) must establish the " +
            "conflict-aware baseline itself — it must not stay null until a test calls CaptureBaseline directly.");
        Assert.IsNotNull(SceneBuilderAutoSync.BaselineSnapshot,
            "A converged production code->scene cycle (ExecuteCodeToScene) must establish the baseline snapshot.");
    }
}
