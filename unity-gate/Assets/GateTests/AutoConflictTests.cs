using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SceneBuilder.Editor;
using SceneBuilder.Editor.Licensing;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// Gate for field-level conflict resolution (spec checklist #9, #10): a both-sides-changed
// cycle auto-applies every NON-overlapping field in its own direction, and resolves a TRUE same-field
// overlap scene-wins with the prior code value preserved in an inline `// CONFLICT:` marker, a located
// Console error, and no modal. Follows RoundTripComponentTests' two-phase build pattern (build the bare
// object first so it earns a mapped GlobalObjectId, then author the component so field edits key onto
// an already-durable object) and AutoSceneToCodeTests'/AutoTriggerTests' direct-executor-drive pattern.
public class AutoConflictTests
{
    private const string BuilderName = "AutoConflictScene";
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
        LicenseGate.ResetToDefault();
        // Unrelated helper dir kept only for Conflict_DualTrigger_RunsOneCombinedCycle_NotTwoSingles'
        // External.cs (never a governing builder).
        _dir = Path.Combine(Path.GetTempPath(), "sb_conflict_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

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

        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        if (File.Exists(_builderPath)) File.Delete(_builderPath);
        if (File.Exists(_sidecarPath)) File.Delete(_sidecarPath);

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
        SceneBuilderRouter.ResetForTests();

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
    // baseline).
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

    // The wired production single-direction executors
    // (ExecuteSceneToCode/ExecuteCodeToScene) never call CaptureBaseline, so a live session's baseline
    // stays null forever and every dual-trigger cycle degrades to the clobbering fallback — the
    // conflict-aware merge is unreachable outside a test that seeds the baseline directly. This drives
    // ONLY the production executor (never CaptureBaseline directly) to
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

        Assert.IsNotNull(SceneBuilderAutoSync.BaselineSourceFor(BuilderName),
            "A converged production scene->code cycle (ExecuteSceneToCode) must establish THIS builder's " +
            "own conflict-aware baseline — it must not stay null until a test calls CaptureBaseline directly.");
        Assert.IsNotNull(SceneBuilderAutoSync.BaselineSnapshotFor(BuilderName),
            "A converged production scene->code cycle (ExecuteSceneToCode) must establish THIS builder's own baseline snapshot.");

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

    // A scene-side reset to
    // the type default (a RemoveComponentField, not a patch) that conflicts with a SAME-field code
    // edit must still: (a) remove the `.Set(...)` from source (scene wins), (b) leave a `// CONFLICT:`
    // marker preserving the prior CODE value, (c) render the plugin's fixed removal placeholder as
    // pure ASCII (today: a U+2014 em dash), and (d) not silently drop the marker altogether because
    // the conflicting key is attributed to a COMPONENT anchor, which `Parse.Anchors` alone (the
    // GameObject-anchor map `InsertConflictMarkers` was fed) cannot resolve.
    [Test]
    public void Conflict_SceneResetToTypeDefault_VsCodeEdit_RemovesSetter_PreservesCodeInMarker()
    {
        // Two-phase build (per BuildTwoMassObjects): Box earns a mapped GlobalObjectId bare first,
        // then Rigidbody.m_Mass is authored at a NON-default value (5f; the type default is 1f) so a
        // later live reset to 1f is a genuine default-reset, not a fixed point.
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Box\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        File.WriteAllText(_builderPath, Source(
            "        var box = scene.Add(\"Box\");\n" +
            "        box.Component<UnityEngine.Rigidbody>(c => c.Set(\"m_Mass\", 5f));"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        SceneBuilderRouter.ResetForTests();

        scene = EditorSceneManager.GetActiveScene();
        var box = FindRoot(scene, "Box");
        Assert.IsNotNull(box, "Box was not created.");

        SceneBuilderAutoSync.CaptureBaseline(scene);

        // Scene-side change: mass returns to the Rigidbody TYPE DEFAULT (1f) — a removal, not a patch.
        box.GetComponent<Rigidbody>().mass = 1f;

        // Code-side change to the SAME field of the SAME object: an external edit to a different,
        // non-default value.
        File.WriteAllText(_builderPath, Source(
            "        var box = scene.Add(\"Box\");\n" +
            "        box.Component<UnityEngine.Rigidbody>(c => c.Set(\"m_Mass\", 3f));"));

        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Box.*m_Mass|m_Mass.*Box"));

        SceneBuilderAutoSync.ExecuteBothChanged(
            new[] { box.GetEntityId() },
            new[] { _builderPath });

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.DoesNotContain("\"m_Mass\"", rewritten,
            "The scene reset to the type default must REMOVE the m_Mass setter, not patch it to a literal.\n" + rewritten);
        StringAssert.Contains("// CONFLICT:", rewritten,
            "A removal that conflicts with a same-field code edit must still leave a `// CONFLICT:` marker " +
            "(the component-anchored key must resolve against the MERGED anchor map, not just GameObject anchors).\n" + rewritten);
        StringAssert.Contains("3f", rewritten,
            "The prior CODE value must be preserved (recoverable) in the marker, never silently discarded.\n" + rewritten);
        StringAssert.Contains(ConflictSurfacing.RemovedFieldMarkerValue, rewritten,
            "The marker's scene-side text must be the plugin's one fixed removal placeholder.\n" + rewritten);

        var conflictLine = rewritten.Split('\n').FirstOrDefault(l => l.Contains("// CONFLICT:"));
        Assert.IsNotNull(conflictLine, "Expected a `// CONFLICT:` marker line in the rewritten source.\n" + rewritten);
        foreach (var ch in conflictLine)
        {
            Assert.LessOrEqual((int)ch, 0x7F,
                $"The plugin's own fixed marker copy must be ASCII-only; found U+{(int)ch:X4} in: {conflictLine}");
        }

        EmittedCodeCompiles.AssertCompiles(rewritten,
            $"After {nameof(Conflict_SceneResetToTypeDefault_VsCodeEdit_RemovesSetter_PreservesCodeInMarker)}");
    }

    [Test]
    public void Conflict_CodeToSceneProductionCycle_EstablishesBaseline()
    {
        var scene = BuildTwoMassObjects();

        // A real prior converged cycle via the production code->scene executor only.
        SceneBuilderAutoSync.ExecuteCodeToScene(new[] { _builderPath });

        Assert.IsNotNull(SceneBuilderAutoSync.BaselineSourceFor(BuilderName),
            "A converged production code->scene cycle (ExecuteCodeToScene) must establish THIS builder's " +
            "own conflict-aware baseline — it must not stay null until a test calls CaptureBaseline directly.");
        Assert.IsNotNull(SceneBuilderAutoSync.BaselineSnapshotFor(BuilderName),
            "A converged production code->scene cycle (ExecuteCodeToScene) must establish THIS builder's own baseline snapshot.");
    }
}
