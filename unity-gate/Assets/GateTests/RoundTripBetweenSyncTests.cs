using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using SceneBuilder.Authoring;
using SceneBuilder.Editor;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;

// M-Between (spec 45) scene->code gate tests: the live driven-read mask wiring, the byte-identical
// mask seam between parse and live-read, the emit-suppression it unlocks, fraction back-solve on a
// manual drag, m_Enabled exclusion, and player-build stripping. Drives the full loop (code->scene via
// SceneBuilderBuild.Run, scene->code via EmittedCodeCompiles.SyncAndAssertCompiles) against a live
// editor scene, mirroring RoundTripSpatialSyncTests.cs's harness. Synchronous EditMode does NOT tick
// [ExecuteAlways].Update() — every geometry- or back-solve-dependent assertion calls
// Between.Evaluate() explicitly.
public class RoundTripBetweenSyncTests
{
    private const string ScenePath = "Assets/GateTests/__RoundTripBetweenSyncTemp.unity";
    private const float Tol = 1e-3f;

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
using static SceneBuilder.Authoring.AssetRefs;
public class RoundTripBetweenSyncScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    // Two unit-cube anchors 10 apart on X, plus a unit-cube Crate placed at fraction 0 (flush against
    // "A") along X. Crate starts with no authored .Transform(pos:) — Between drives it entirely.
    private const string AnchorsBody =
        "        var a = scene.Add(\"A\")\n" +
        "            .Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"Cube\")))\n" +
        "            .Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Builtin(\"Default-Material\") }))\n" +
        "            .Transform(pos: (-5f, 0f, 0f));\n" +
        "        var b = scene.Add(\"B\")\n" +
        "            .Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"Cube\")))\n" +
        "            .Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Builtin(\"Default-Material\") }))\n" +
        "            .Transform(pos: (5f, 0f, 0f));\n";

    private const string CrateBetweenXBody =
        "        var crate = scene.Add(\"Crate\")\n" +
        "            .Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"Cube\")))\n" +
        "            .Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Builtin(\"Default-Material\") }))\n" +
        "            .Between(from: a, to: b, fraction: 0f, axis: Between.Axis.X);\n";

    private static GameObject FindRoot(Scene scene, string name)
    {
        return scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);
    }

    private void RunBuild(Scene scene)
    {
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
    }

    // Extracts the full `.Transform(pos:` call text (balanced parens), scoped by the caller to a
    // single statement's substring, so an assertion can inspect the driven-axis literal without
    // colliding with an unrelated numeric literal elsewhere in the file.
    private static string ExtractTransformPosCall(string source)
    {
        const string marker = ".Transform(pos:";
        int start = source.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;

        int i = start + marker.Length;
        int depth = 1;
        while (i < source.Length && depth > 0)
        {
            if (source[i] == '(') depth++;
            else if (source[i] == ')') depth--;
            i++;
        }

        return source.Substring(start, i - start);
    }

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_rtbs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "RoundTripBetweenSyncScene.cs");
        _sidecarPath = Path.Combine(_dir, "RoundTripBetweenSyncScene.sbmap.json");
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

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    // Primary back-solve contract: dragging the object partway along the owned axis (X) AND partway
    // along a free axis (Y) simultaneously — Evaluate() must back-solve `fraction` from the X
    // component alone, leave the dragged position exactly as dragged (never re-drive it), and Sync
    // must patch ONLY `.Between(fraction:)` plus a free-axis `.Transform(pos:)` for Y — the owned X
    // component must never reach a `.Transform(pos:)` call.
    [Test]
    public void SceneToCode_DragBetweenAlongAndOffAxis_BackSolvesFraction_OwnedAxisNeverEmitted()
    {
        File.WriteAllText(_builderPath, Source(AnchorsBody + CrateBetweenXBody));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        RunBuild(scene);

        var crate = FindRoot(EditorSceneManager.GetActiveScene(), "Crate");
        Assert.IsNotNull(crate, "Crate was not created by SceneBuilderBuild.Run");
        var between = crate.GetComponent<Between>();
        between.Evaluate(); // baseline: places self flush against "A" (fraction 0), sets the write baseline

        // Corridor along X spans ps0=-4.0 to ps1=4.0 (unit-cube anchors, unit-cube self); 40% of that
        // corridor is x=-0.8. Y is a free axis for an axis:X Between; drag it too.
        crate.transform.position = new Vector3(-0.8f, 2f, 0f);
        between.Evaluate();

        Assert.AreEqual(0.4f, between.fraction, Tol,
            "A manual drag to 40% of the corridor must back-solve fraction to 0.4, mirroring FitSize's value back-solve.");
        Assert.AreEqual(-0.8f, crate.transform.position.x, Tol,
            "The back-solve branch must preserve the dragged position, never re-drive it back to the old fraction.");

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "The back-solved fraction and free-Y drag are real scene changes; Sync must report Changed.");

        var rewritten = File.ReadAllText(_builderPath);
        int crateStart = rewritten.IndexOf("scene.Add(\"Crate\")", StringComparison.Ordinal);
        Assert.GreaterOrEqual(crateStart, 0, "Expected the Crate statement in the rewritten source.\n" + rewritten);
        var crateStatement = rewritten.Substring(crateStart);

        StringAssert.Contains("fraction: 0.4f", crateStatement,
            "Sync must patch the Between fraction argument to the back-solved value, preserving the from/to/axis arguments in place.\n" + rewritten);

        var posCall = ExtractTransformPosCall(crateStatement);
        Assert.IsNotNull(posCall, "The free-Y drag must sync as a .Transform(pos:) call.\n" + rewritten);
        StringAssert.DoesNotContain("-0.8f", posCall,
            "The owned X axis must never be written to source as a raw .Transform(pos:) argument — only fraction carries that intent.\n" + posCall);
        StringAssert.Contains("2f", posCall, "The free Y drag value must be present in the pos: argument.\n" + posCall);

        var second = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(second.Changed, "NOT CONVERGED: a Sync immediately after the patch, with no further edit, reported Changed=true.");
    }

    // Back-solve must be unclamped: dragging past either anchor must back-solve a fraction outside
    // [0,1], not clip to the endpoint.
    [Test]
    public void SceneToCode_DragBetweenPastAnchors_BackSolvesUnclampedFraction()
    {
        const string twoCratesBody =
            "        var crate1 = scene.Add(\"Crate1\")\n" +
            "            .Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"Cube\")))\n" +
            "            .Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Builtin(\"Default-Material\") }))\n" +
            "            .Between(from: a, to: b, fraction: 0f, axis: Between.Axis.X);\n" +
            "        var crate2 = scene.Add(\"Crate2\")\n" +
            "            .Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"Cube\")))\n" +
            "            .Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Builtin(\"Default-Material\") }))\n" +
            "            .Between(from: a, to: b, fraction: 0f, axis: Between.Axis.X);\n";

        File.WriteAllText(_builderPath, Source(AnchorsBody + twoCratesBody));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        RunBuild(scene);

        var crate1 = FindRoot(EditorSceneManager.GetActiveScene(), "Crate1");
        var crate2 = FindRoot(EditorSceneManager.GetActiveScene(), "Crate2");
        var between1 = crate1.GetComponent<Between>();
        var between2 = crate2.GetComponent<Between>();
        between1.Evaluate();
        between2.Evaluate();

        // Corridor along X spans ps0=-4.0 to ps1=4.0 (travel 8.0). Past "A": x=-8.0 -> fraction -0.5.
        // Past "B": x=8.0 -> fraction 1.5.
        crate1.transform.position = new Vector3(-8f, 0f, 0f);
        crate2.transform.position = new Vector3(8f, 0f, 0f);
        between1.Evaluate();
        between2.Evaluate();

        Assert.AreEqual(-0.5f, between1.fraction, Tol, "Dragging past the from-anchor must back-solve a negative fraction, not clip to 0.");
        Assert.AreEqual(1.5f, between2.fraction, Tol, "Dragging past the to-anchor must back-solve a fraction > 1, not clip to 1.");
    }

    // The byte-identical seam (spec's explicit divergence guard): the live driven-read mask
    // (LiveTransformRead.DrivenChannels) must equal the model's parsed DrivenChannels for the SAME
    // Between — both derive from the one BetweenDrivenMask owner. World case: exactly one position bit.
    [Test]
    public void Between_DrivenReadMask_EqualsParseModelMask_World()
    {
        var source = Source(AnchorsBody + CrateBetweenXBody);
        var parsed = BuilderParser.Parse(source);
        var modelMask = parsed.Model.Roots.Single(r => r.Name == "Crate").Transform.DrivenChannels;
        Assert.AreEqual(ChannelMask.PositionX, modelMask, "Sanity: parse must set exactly the X position bit for a world-axis Between.");

        File.WriteAllText(_builderPath, source);
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        RunBuild(scene);

        var crate = FindRoot(EditorSceneManager.GetActiveScene(), "Crate");
        var liveMask = LiveTransformRead.DrivenChannels(crate);

        Assert.AreEqual(modelMask, liveMask,
            "Live driven-read mask must equal the model's parsed DrivenChannels for the same Between (the byte-identical seam) — a bypass in either site breaks this equality.");
    }

    // Oriented case: alongOrientationOf drives the whole position, not just the named axis.
    [Test]
    public void Between_DrivenReadMask_EqualsParseModelMask_Oriented()
    {
        const string orientedCrateBody =
            "        var root = scene.Add(\"Root\");\n" +
            "        var crate = scene.Add(\"Crate\")\n" +
            "            .Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"Cube\")))\n" +
            "            .Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Builtin(\"Default-Material\") }))\n" +
            "            .Between(from: a, to: b, fraction: 0f, axis: Between.Axis.X, alongOrientationOf: root);\n";

        var source = Source(AnchorsBody + orientedCrateBody);
        var parsed = BuilderParser.Parse(source);
        var modelMask = parsed.Model.Roots.Single(r => r.Name == "Crate").Transform.DrivenChannels;
        Assert.AreEqual(ChannelMask.PositionX | ChannelMask.PositionY | ChannelMask.PositionZ, modelMask,
            "Sanity: parse must set the full position mask for an oriented Between.");

        File.WriteAllText(_builderPath, source);
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        RunBuild(scene);

        var crate = FindRoot(EditorSceneManager.GetActiveScene(), "Crate");
        var liveMask = LiveTransformRead.DrivenChannels(crate);

        Assert.AreEqual(modelMask, liveMask,
            "Live driven-read mask must equal the model's parsed DrivenChannels for an oriented Between (whole position, the byte-identical seam).");
    }

    // Anti-churn (mirrors RoundTripSpatialSyncTests item 11): an authored Between, synced with NO
    // edit, must not leak its owned X position into a redundant .Transform(pos:) — Reconciler's
    // generic driven-hold only suppresses it once the live-read mask carries the same bit parse set.
    [Test]
    public void SceneToCode_AuthoredBetweenNoEdit_EmitsNoOwnedPosTransform_SecondSyncNoOp()
    {
        File.WriteAllText(_builderPath, Source(AnchorsBody + CrateBetweenXBody));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        RunBuild(scene);

        var crate = FindRoot(EditorSceneManager.GetActiveScene(), "Crate");
        Assert.IsNotNull(crate, "Crate was not created by SceneBuilderBuild.Run");
        crate.GetComponent<Between>().Evaluate(); // baseline placement, no manual edit afterward

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(result.Changed, "An unedited Between scene must Sync as a no-op.");

        var rewritten = File.ReadAllText(_builderPath);
        int crateStart = rewritten.IndexOf("scene.Add(\"Crate\")", StringComparison.Ordinal);
        Assert.GreaterOrEqual(crateStart, 0, "Expected the Crate statement in the rewritten source.\n" + rewritten);
        var crateStatement = rewritten.Substring(crateStart);

        StringAssert.Contains(".Between(", crateStatement, "Rewritten source must still carry the .Between(...) call.\n" + rewritten);
        StringAssert.DoesNotContain(".Transform(pos:", crateStatement,
            "Between's owned X position must never be re-emitted as a redundant .Transform(pos:) call.\n" + rewritten);
    }

    // Player build: Between must be stripped after baking its final placement, mirroring
    // SpatialBuildStripper's existing FitSize/SurfaceSnap contract.
    [Test]
    public void BuildStrip_RemovesBetween_BakesPlacement()
    {
        var from = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var to = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            from.transform.position = new Vector3(-5f, 0f, 0f);
            to.transform.position = new Vector3(5f, 0f, 0f);
            go.transform.position = new Vector3(100f, 100f, 100f);

            var between = go.AddComponent<Between>();
            between.from = from.transform;
            between.to = to.transform;
            between.fraction = 0.5f;
            between.axis = Between.Axis.X;

            SpatialBuildStripper.StripScene(go.scene);

            Assert.IsNull(go.GetComponent<Between>(), "Build strip must remove Between.");
            Assert.AreEqual(0, GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go),
                "Build strip must leave no missing-script stub.");
            Assert.AreEqual(0f, go.transform.position.x, Tol,
                "Build strip must bake the Between-driven placement (fraction 0.5 centres self on X) before removing the component.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(from);
            UnityEngine.Object.DestroyImmediate(to);
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    // m_Enabled has no place in Between's closed (.Between(...)) authoring grammar — disabling it
    // releases its driven channel via the transform, exactly like FitSize/SurfaceSnap.
    [Test]
    public void Between_MEnabled_NotAuthorable()
    {
        Assert.IsTrue(SerializedFieldExclusions.IsExcluded(typeof(Between), "m_Enabled"),
            "Between.m_Enabled must be excluded from builder source, mirroring FitSize/SurfaceSnap's ClosedGrammarComponents entry.");
    }
}
