using System.IO;
using NUnit.Framework;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;

// The COMPILABILITY gate for emitted builder source.
//
// The round-trip tests hand builder source to Build/Sync as strings, and Roslyn PARSES text — it
// never COMPILES it. So the suite proved sync semantics and never proved the one property the
// user's real builder demands: that the emitted C# actually compiles. Four shipped bugs escaped
// through that hole (`1.53` vs `1.53f`; bare ObjectReference/LayerMask tokens; a bare `Asset(...)`
// call with no `using static SceneBuilder.Authoring.AssetRefs;`; a CS0841 statement-ordering bug) —
// all of the same class: "the generated C# does not compile."
//
// The check itself now lives in the PRODUCT (SceneBuilder.Editor.BuilderCompileCheck) and runs on
// every sync that writes source, because the builder moved out of Assets/ and Unity's compiler no
// longer vets it. This file is the gate's thin adapter over that same implementation: it turns the
// product's diagnostics into an NUnit failure. One implementation, two reporting surfaces.
//
// SyncAndAssertCompiles is the seam that closes the class: scene->code tests call it INSTEAD of
// SceneBuilderSync.Run, so the compile assertion is inherited by default and a future test cannot
// silently skip it by forgetting to opt in.
public static class EmittedCodeCompiles
{
    /// <summary>
    /// Asserts <paramref name="source"/> COMPILES with zero errors. Reports every error diagnostic
    /// plus the offending source, so a failure names the exact defect in the emission.
    /// </summary>
    public static void AssertCompiles(string source, string context)
    {
        var errors = BuilderCompileCheck.Check(source);
        if (errors.Count == 0)
        {
            return;
        }

        Assert.Fail(BuilderCompileCheck.Format(errors, context, source));
    }

    /// <summary>
    /// Runs the real scene-&gt;code sync and asserts the builder source it wrote COMPILES. Use this
    /// in place of <see cref="SceneBuilderSync.Run"/> everywhere in the gate.
    /// </summary>
    public static SceneBuilderSync.SyncResult SyncAndAssertCompiles(string builderPath, string sidecarPath, Scene scene)
    {
        var result = SceneBuilderSync.Run(builderPath, sidecarPath, scene);
        AssertCompiles(
            File.ReadAllText(builderPath),
            $"After SceneBuilderSync.Run on {Path.GetFileName(builderPath)}");
        return result;
    }
}
