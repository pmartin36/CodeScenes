using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using SceneBuilder.Editor;
using Debug = UnityEngine.Debug;

// Gate for BuilderCompileCheck — the in-process replacement for Unity's compiler.
//
// The builder moved to <ProjectRoot>/SceneBuilders/ so writes stop triggering domain reloads. That
// removed the backstop that caught four shipped "emitted C# does not compile" bugs. These tests
// prove the replacement actually catches that class against the REAL loaded assembly set (a check
// whose references don't resolve SceneBuilder.Authoring would report phantom errors, or — worse —
// a check wired to ignore errors would report none), and that it is cheap enough to run on EVERY
// sync, which is the constraint that made relocation necessary in the first place.
public class BuilderCompileCheckTests
{
    // Compiles: this is the shape sync emits.
    private const string CleanSource = @"
using SceneBuilder.Authoring;
public class CompileCheckScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Cube"").Transform(pos: (1.53f, 2f, 3f));
    }
}";

    // Shipped bug #1: emitting `1.53` instead of `1.53f`. A double tuple has no conversion to the
    // (float, float, float)? parameter — a real compile error that pure parsing never sees.
    private const string UnsuffixedFloatSource = @"
using SceneBuilder.Authoring;
public class CompileCheckScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Cube"").Transform(pos: (1.53, 2.0, 3.0));
    }
}";

    // Shipped bug #3: emitting `Asset("...")` without `using static SceneBuilder.Authoring.AssetRefs;`.
    private const string MissingUsingSource = @"
using SceneBuilder.Authoring;
public class CompileCheckScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Cube"").Component<UnityEngine.MeshRenderer>(m => m.Set(""m_Materials.Array.data[0]"", Asset(""Assets/M.mat"")));
    }
}";

    [Test]
    public void CleanSource_CompilesWithNoDiagnostics()
    {
        var errors = BuilderCompileCheck.Check(CleanSource);

        // A failure here usually means the reference set did NOT resolve the Authoring/Unity
        // assemblies — which would make every other check in this suite a false negative.
        Assert.IsEmpty(
            errors.Select(e => e.ToString()),
            "Valid builder source reported compile errors — the reference set is not resolving correctly.");
    }

    [Test]
    public void UnsuffixedFloatLiteral_IsReportedWithLineNumber()
    {
        var errors = BuilderCompileCheck.Check(UnsuffixedFloatSource);

        Assert.IsNotEmpty(errors, "`1.53` (no `f` suffix) must NOT compile — this bug shipped once already.");
        Assert.IsTrue(
            errors.Any(e => e.Line == 7),
            "Diagnostic must carry the 1-based line of the offending emission; got: "
                + string.Join(" | ", errors.Select(e => e.ToString())));
        Assert.IsTrue(
            errors.All(e => !string.IsNullOrEmpty(e.Id) && !string.IsNullOrEmpty(e.Message)),
            "Every diagnostic needs an id + message so the Console names the defect.");
    }

    [Test]
    public void BareAssetCall_WithoutItsUsing_IsReported()
    {
        var errors = BuilderCompileCheck.Check(MissingUsingSource);

        Assert.IsNotEmpty(errors, "`Asset(...)` without `using static ...AssetRefs;` must NOT compile.");
        Assert.IsTrue(
            errors.Any(e => e.Id == "CS0103"),
            "Expected CS0103 (name does not exist); got: " + string.Join(" | ", errors.Select(e => e.ToString())));
    }

    [Test]
    public void ReferenceSet_IsCachedAcrossCalls()
    {
        // Reference identity IS the caching contract: rebuilding MetadataReferences from
        // AppDomain.GetAssemblies() per sync is exactly the cost this must never pay again.
        var first = BuilderCompileCheck.References();
        var second = BuilderCompileCheck.References();

        Assert.AreSame(first, second, "Reference set must be built once per domain, not per check.");
        Assert.Greater(first.Length, 10, "Reference set looks implausibly small.");
    }

    [Test]
    public void WarmCheck_IsCheapEnoughToRunOnEverySync()
    {
        // Ensure the one-time work has happened, then measure the cost that actually recurs.
        BuilderCompileCheck.Check(CleanSource);

        // Warm passes: must reuse the cached references AND the template compilation's
        // ReferenceManager, so cost is proportional to the builder file, not the assembly count.
        const int iterations = 10;
        var warm = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            BuilderCompileCheck.Check(CleanSource);
        }

        warm.Stop();

        var perCheckMs = warm.Elapsed.TotalMilliseconds / iterations;

        // The one-time figures come from the product's own instrumentation, so they are the REAL
        // first-run costs regardless of which test happened to trigger them first.
        Debug.Log($"[SceneBuilder gate] BuilderCompileCheck cost: "
                  + $"reference-set build={BuilderCompileCheck.ReferenceBuildMilliseconds:F1}ms (once per domain), "
                  + $"first check={BuilderCompileCheck.FirstCheckMilliseconds:F1}ms (once per domain), "
                  + $"warm={perCheckMs:F2}ms/check over {iterations} checks, "
                  + $"references={BuilderCompileCheck.References().Length}.");

        // Generous bound: a regression that rebuilds references or re-binds assemblies per check
        // lands orders of magnitude above this, while a healthy warm check is single-digit ms.
        Assert.Less(
            perCheckMs,
            100d,
            $"Warm compile check cost {perCheckMs:F1}ms — too slow to run on every sync. "
                + "The reference set / template compilation is probably no longer being reused.");
    }

    // ---- The wiring: the check must run INSIDE SceneBuilderSync.Run's write path ----
    //
    // A check that merely EXISTS is worthless; the four bugs escaped because nothing ran. This drives
    // a real scene->code sync whose builder carries a pre-existing compile error (it still PARSES, so
    // sync happily patches and writes it) and proves the written source is vetted and reported.

    private const string ScenePath = "Assets/GateTests/__CompileCheckTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    // Parses cleanly (BuilderParser only walks the Build body, and this Build body is ordinary
    // authoring) but does NOT compile: the helper method references an undefined type. That is exactly
    // the gap relocation opened — source text sync is happy to write, that Unity would have rejected.
    private static string BrokenSourceAt(float x) => $@"
using SceneBuilder.Authoring;
public class CompileCheckWiringScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
        scene.Add(""WiringCube"").Transform(pos: ({x}f, 0f, 0f));
    }}

    private void Helper()
    {{
        ThisTypeDoesNotExist.Nope();
    }}
}}";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_cc_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "CompileCheckWiringScene.cs");
        _sidecarPath = Path.Combine(_dir, "CompileCheckWiringScene.sbmap.json");
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

    [Test]
    public void Sync_WritingNonCompilingSource_ReportsItToTheConsole()
    {
        File.WriteAllText(_builderPath, BrokenSourceAt(1f));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var go = GameObject.Find("WiringCube");
        Assert.IsNotNull(go, "Build did not create WiringCube — the broken builder must still PARSE.");

        // A real scene edit, so sync has a source edit to write.
        go.transform.position = new Vector3(9f, 0f, 0f);

        LogAssert.Expect(LogType.Error, new Regex("DOES NOT COMPILE"));

        var result = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        Assert.Greater(result.EditsApplied, 0, "Sync should have written the patched position.");
        Assert.IsNotEmpty(
            result.CompileErrors.Select(e => e.ToString()),
            "Sync wrote source that does not compile and did not surface it — the check is not wired in.");
        Assert.IsTrue(
            result.CompileErrors.Any(e => e.Id == "CS0103"),
            "Expected the undefined-type error to be reported; got: "
                + string.Join(" | ", result.CompileErrors.Select(e => e.ToString())));

        // The patch itself must still have landed: the check REPORTS, it never blocks the sync.
        StringAssert.Contains("9f", File.ReadAllText(_builderPath));
    }
}
