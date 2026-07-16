using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using SceneBuilder.Editor;

// M-Builtin emitted-code gate test (#13): the FIRST `Builtin(...)` synced into a builder file that
// never contained an `Asset(...)` call must inject `using static SceneBuilder.Authoring.AssetRefs;`
// and the resulting source must actually COMPILE — EmittedCodeCompiles.SyncAndAssertCompiles asserts
// that for every call site in the gate. Pins the Core-side fix (b1-t4's `EnsureAssetRefsUsing` OR'ing
// the `Builtin` identifier beside `Asset`, SceneBuilder.Core/Reconcile/EmittedUsings.cs) against the
// REAL Sync path (SceneBuilderSync.Run -> SourcePatchApplier.Apply), not just the headless Core suite.
public class RoundTripBuiltinRefEmittedCodeTests : BuiltinRefGateHarness
{
    // #13: the harness's Source(...) omits the using for a body with no `Asset(` substring, so a
    // builder that starts as `scene.Add("Anchor");` alone begins with neither the directive nor any
    // asset-ref factory call. Explicitly asserted below as the test's PRECONDITION so the case cannot
    // silently go vacuous if the shared harness ever changes. A live scene primitive (built-in
    // mesh + material) is then synced into that file for the first time.
    [Test]
    public void SceneToCode_FirstBuiltinRefSyncedIntoFileWithNoAssetCall_InjectsAssetRefsUsingAndCompiles()
    {
        File.WriteAllText(BuilderPath, Source("        scene.Add(\"Anchor\");"));

        var starting = File.ReadAllText(BuilderPath);
        StringAssert.DoesNotContain("using static", starting,
            "Test premise invalid: the starting builder source already carries a using static directive.\n" + starting);
        StringAssert.DoesNotContain("Asset(", starting,
            "Test premise invalid: the starting builder source already calls Asset(...).\n" + starting);

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(BuilderPath, ScenePath, SidecarPath, scene);

        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = "Cube";

        // This is the #13 assertion: SyncAndAssertCompiles fails the test outright if the emitted
        // source does not compile (e.g. CS0103 from a missing using). The explicit checks below
        // additionally pin WHY it compiles — the injected directive — not merely that it happens to.
        var result = EmittedCodeCompiles.SyncAndAssertCompiles(BuilderPath, SidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite a new built-in-mesh primitive in the scene");

        var rewritten = File.ReadAllText(BuilderPath);
        StringAssert.Contains("Builtin(\"Cube\")", rewritten,
            "Non-vacuity premise failed: source did not gain Builtin(\"Cube\").\n" + rewritten);

        const string usingDirective = "using static SceneBuilder.Authoring.AssetRefs;";
        StringAssert.Contains(usingDirective, rewritten,
            "The first Builtin(...) synced into a file with no prior Asset(...) call did not inject the " +
            "AssetRefs using directive.\n" + rewritten);

        var occurrences = rewritten.Split(new[] { usingDirective }, StringSplitOptions.None).Length - 1;
        Assert.AreEqual(1, occurrences,
            "Expected exactly one `" + usingDirective + "`; found " + occurrences + ".\n" + rewritten);

        Assert.Less(
            rewritten.IndexOf(usingDirective, StringComparison.Ordinal),
            rewritten.IndexOf("public class RoundTripBuiltinScene", StringComparison.Ordinal),
            "The using directive must precede the type declaration.\n" + rewritten);
    }
}
