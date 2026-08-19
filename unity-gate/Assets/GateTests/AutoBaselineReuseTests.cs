using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using SceneBuilder.Editor;
using SceneBuilder.Editor.Licensing;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Gate for the auto-sync baseline-capture tail (spec checklist #6 continued): a converged
// scene->code cycle's CaptureBaseline tail must reuse the cycle's incremental snapshot instead of
// an unconditional cold re-assemble, so the FULL cycle (head incremental read AND baseline
// capture) stays O(change-set), not O(scene). Real scene, real GameObjects, real GlobalObjectId,
// driven through the routed pump (SceneBuilderBuild.Run seed + ExecuteSceneToCode), following
// AutoSceneToCodeTests' harness pattern.
public class AutoBaselineReuseTests
{
    private readonly List<string> _createdBuilderPaths = new();
    private readonly List<string> _createdSidecarPaths = new();
    private readonly List<string> _createdScenePaths = new();

    [SetUp]
    public void SetUp()
    {
        SceneBuilderPaths.EnsureBuildersDirectory();
        LicenseGate.ResetToDefault();
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

        foreach (var path in _createdBuilderPaths)
        {
            if (File.Exists(path)) File.Delete(path);
        }
        foreach (var path in _createdSidecarPaths)
        {
            if (File.Exists(path)) File.Delete(path);
        }
        foreach (var path in _createdScenePaths)
        {
            if (File.Exists(path)) AssetDatabase.DeleteAsset(path);
        }
        _createdBuilderPaths.Clear();
        _createdSidecarPaths.Clear();
        _createdScenePaths.Clear();
    }

    private static GameObject FindRoot(Scene scene, string name) =>
        scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);

    // A single shared "Door" plus <paramref name="referenceHolderCount"/> "Opener{i}" objects, each
    // holding a DoorOpener.target reference to Door — mirrors the shape the counted GlobalObjectIdCache
    // seam must stay proportional to the change set under, regardless of how many such objects exist.
    private static string BuilderSource(string builderName, int referenceHolderCount)
    {
        var body = new StringBuilder();
        body.AppendLine("        var door = scene.Add(\"Door\");");
        for (var i = 0; i < referenceHolderCount; i++)
        {
            body.AppendLine($"        var opener{i} = scene.Add(\"Opener{i}\");");
            body.AppendLine($"        opener{i}.Component<DoorOpener>(c => c.Set(\"target\", door));");
        }

        return $@"
using SceneBuilder.Authoring;
public class {builderName} : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";
    }

    /// <summary>
    /// Seeds a fresh routed builder with <paramref name="referenceHolderCount"/> reference-holding
    /// objects, warms the assembler with one converged cold cycle, then drives ONE converged
    /// single-field-edit cycle (head incremental read + baseline-capture tail) on an already-mapped
    /// object and returns the per-builder assembler's <c>Ids.ResolutionCount</c> for that cycle alone.
    /// </summary>
    private int RunSingleFieldEditCycle_ReturnTailCycleResolutionCount(int referenceHolderCount)
    {
        var builderName = "AutoBaselineReuse_N" + referenceHolderCount;
        var builderPath = SceneBuilderPaths.Builder(builderName);
        var sidecarPath = SceneBuilderPaths.Sidecar(builderName);
        var scenePath = $"Assets/GateTests/__AutoBaselineReuseTemp_N{referenceHolderCount}.unity";
        _createdBuilderPaths.Add(builderPath);
        _createdSidecarPaths.Add(sidecarPath);
        _createdScenePaths.Add(scenePath);

        File.WriteAllText(builderPath, BuilderSource(builderName, referenceHolderCount));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var seedScene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(builderPath, scenePath, sidecarPath, seedScene);
        SceneBuilderRouter.ResetForTests();

        // Warm: a converged cold cycle establishes the per-builder assembler's node cache + baseline.
        SceneBuilderAutoSync.ExecuteSceneToCode(Array.Empty<EntityId>());

        var liveScene = EditorSceneManager.GetActiveScene();
        var opener0 = FindRoot(liveScene, "Opener0");
        Assert.IsNotNull(opener0, "Opener0 was not created by SceneBuilderBuild.Run");

        var assembler = SceneBuilderAutoSync.AssemblerFor(builderName);
        Assert.IsNotNull(assembler,
            "AssemblerFor must return the per-builder assembler warmed by the prior ExecuteSceneToCode cycle.");
        assembler.Ids.ResetCount();

        opener0.tag = "Player";
        SceneBuilderAutoSync.ExecuteSceneToCode(new[] { opener0.GetEntityId() });

        return assembler.Ids.ResolutionCount;
    }

    // THE guard: a converged single-field-edit cycle (head read AND baseline-capture tail) must
    // resolve GlobalObjectId proportional to the CHANGE SET, not scene size. A cold baseline-capture
    // tail re-resolves every reference-holding object, so the count at N=40 would exceed the count at
    // N=5; an incremental tail keeps both counts equal (and small).
    [Test]
    public void FullCycle_SingleFieldEdit_ResolutionCountIndependentOfN()
    {
        var smallCount = RunSingleFieldEditCycle_ReturnTailCycleResolutionCount(referenceHolderCount: 5);
        var largeCount = RunSingleFieldEditCycle_ReturnTailCycleResolutionCount(referenceHolderCount: 40);

        Assert.AreEqual(smallCount, largeCount,
            "A converged single-field-edit cycle's resolution count must be independent of scene size " +
            "(N reference-holding objects) — a cold baseline-capture tail re-resolves all N objects and " +
            "the count diverges between N=5 and N=40, proving the tail is not incremental.");
    }

    // Consecutive value-only edits must each stay incremental — no cycle after the first may cold-
    // reset and re-resolve the whole scene. Bound is well under the scene's 20 reference-holding
    // objects (each contributing at least 2 resolves — the GameObject and its DoorOpener component —
    // to a cold tail), so a cold tail fails this bound on every cycle while a genuinely incremental
    // tail (a handful of resolves per edit) stays comfortably under it.
    [Test]
    public void ConsecutiveValueEdits_ResolutionCountStaysBounded_AcrossMultipleCycles()
    {
        const int referenceHolderCount = 20;
        const int bound = referenceHolderCount;

        var builderName = "AutoBaselineReuse_Consecutive";
        var builderPath = SceneBuilderPaths.Builder(builderName);
        var sidecarPath = SceneBuilderPaths.Sidecar(builderName);
        var scenePath = "Assets/GateTests/__AutoBaselineReuseTemp_Consecutive.unity";
        _createdBuilderPaths.Add(builderPath);
        _createdSidecarPaths.Add(sidecarPath);
        _createdScenePaths.Add(scenePath);

        File.WriteAllText(builderPath, BuilderSource(builderName, referenceHolderCount));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var seedScene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(builderPath, scenePath, sidecarPath, seedScene);
        SceneBuilderRouter.ResetForTests();

        SceneBuilderAutoSync.ExecuteSceneToCode(Array.Empty<EntityId>());

        var liveScene = EditorSceneManager.GetActiveScene();
        var assembler = SceneBuilderAutoSync.AssemblerFor(builderName);
        Assert.IsNotNull(assembler,
            "AssemblerFor must return the per-builder assembler warmed by the prior ExecuteSceneToCode cycle.");

        for (var cycle = 0; cycle < 3; cycle++)
        {
            var opener = FindRoot(liveScene, $"Opener{cycle}");
            Assert.IsNotNull(opener, $"Opener{cycle} was not created by SceneBuilderBuild.Run");

            assembler.Ids.ResetCount();
            opener.tag = "Player";
            SceneBuilderAutoSync.ExecuteSceneToCode(new[] { opener.GetEntityId() });

            Assert.Less(assembler.Ids.ResolutionCount, bound,
                $"Cycle {cycle}: a value-only edit's resolution count ({assembler.Ids.ResolutionCount}) must " +
                $"stay well under the {referenceHolderCount}-object scene size — a cold baseline-capture tail " +
                "re-resolves the whole scene every cycle and fails this bound.");
        }
    }
}
