using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using SceneBuilder.Authoring;
using SceneBuilder.Editor;

// M-Spatial (FitSize/AlignTo) full bidirectional SYNC round-trip gate tests — the spec's confirmation
// checklist items 11, 12, 13, 14, 18, and the sync half of 19 (specs/19-spatial-authoring-components.md).
// Drives the FULL loop (code->scene via SceneBuilderBuild.Run, scene->code via
// EmittedCodeCompiles.SyncAndAssertCompiles) against a live editor scene. Mirrors
// RoundTripObjectRefTests.cs's harness verbatim. Production for M-Spatial is already landed; these
// tests are expected to PASS on first run — this file IS the mandatory
// full-loop gate coverage (CLAUDE.md hard requirement), not a RED test driving new production. A
// failure here is a genuine Unity-boundary escape.
//
// Synchronous EditMode does NOT tick [ExecuteAlways].Update() — every geometry- or back-solve-dependent
// assertion explicitly calls component.Evaluate() (RoundTripSpatialTests.cs's established pattern).
public class RoundTripSpatialSyncTests
{
    private const string ScenePath = "Assets/GateTests/__RoundTripSpatialSyncTemp.unity";
    private const float Tol = 1e-3f;

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
using static SceneBuilder.Authoring.AssetRefs;
public class RoundTripSpatialSyncScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    // A static floor: a Cube scaled (10,1,10) so its top face sits at world Y = 0.5 (unambiguous,
    // distinguishable from any driven-axis value used below). No FitSize/AlignTo — a plain surface.
    private const string FloorBody =
        "        var floor = scene.Add(\"Floor\")\n" +
        "            .Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"Cube\")))\n" +
        "            .Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Builtin(\"Default-Material\") }))\n" +
        "            .Transform(scale: (10f, 1f, 10f));\n";

    // A Crate: unit Cube, aspect-locked FitSize height 2, aligned down onto the floor (explicit target),
    // starting above the floor.
    private const string CrateBody =
        "        var crate = scene.Add(\"Crate\")\n" +
        "            .Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"Cube\")))\n" +
        "            .Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Builtin(\"Default-Material\") }))\n" +
        "            .Transform(pos: (0f, 5f, 0f))\n" +
        "            .FitSize(height: 2f)\n" +
        "            .AlignTo(floor, y: AxisAlign.AbutMax);\n";

    private static GameObject FindRoot(Scene scene, string name)
    {
        return scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);
    }

    private void RunBuild(Scene scene)
    {
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
    }

    // Extracts the full `.Transform(pos: (...))` call text (balanced parens) so an assertion can
    // inspect the driven-axis literal WITHOUT colliding with unrelated numeric literals elsewhere in
    // the file (e.g. the floor's scale: (10f, 1f, 10f)).
    private static string ExtractTransformPosCall(string source)
    {
        const string marker = ".Transform(pos:";
        int start = source.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;

        int i = start + marker.Length;
        int depth = 1; // already inside the outer '(' of "Transform("
        while (i < source.Length && depth > 0)
        {
            if (source[i] == '(') depth++;
            else if (source[i] == ')') depth--;
            i++;
        }

        return source.Substring(start, i - start);
    }

    // ComponentDefaultTemplate.Register (formerly SerializedFieldBridge.GetDefaultFieldMap)
    // lazily instantiates a throwaway MonoBehaviour instance
    // (bare GameObject, no MeshFilter/Renderer) the FIRST time a component type is snapshot-read, to
    // compute a per-type default-field map — cached for the rest of the domain's lifetime. For
    // FitSize/AlignTo (both [ExecuteAlways] with an OnValidate that logs a located error against that
    // bare probe object) this fires exactly ONCE per type, non-deterministically on whichever [Test]
    // happens to sync a FitSize/AlignTo first. Force the warm-up HERE, isolated from any real test's
    // LogAssert window, so no individual [Test] bears this one-time, unrelated Console-noise cost.
    [OneTimeSetUp]
    public void WarmSpatialDefaultFieldCache()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sb_rtss_warm_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var builderPath = Path.Combine(dir, "WarmScene.cs");
        var sidecarPath = Path.Combine(dir, "WarmScene.sbmap.json");
        try
        {
            File.WriteAllText(builderPath, Source(
                "        scene.Add(\"Warm\")\n" +
                "            .Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"Cube\")))\n" +
                "            .Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Builtin(\"Default-Material\") }))\n" +
                "            .FitSize(height: 1f)\n" +
                "            .AlignTo(NodeHandle.None, y: AxisAlign.AbutMax);\n"));

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var scene = EditorSceneManager.GetActiveScene();

            var prevIgnore = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
            try
            {
                SceneBuilderBuild.Run(builderPath, ScenePath, sidecarPath, scene);
                EmittedCodeCompiles.SyncAndAssertCompiles(builderPath, sidecarPath, EditorSceneManager.GetActiveScene());
            }
            finally
            {
                LogAssert.ignoreFailingMessages = prevIgnore;
            }
        }
        finally
        {
            Directory.Delete(dir, true);
            if (File.Exists(ScenePath)) AssetDatabase.DeleteAsset(ScenePath);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
    }

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_rtss_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "RoundTripSpatialSyncScene.cs");
        _sidecarPath = Path.Combine(_dir, "RoundTripSpatialSyncScene.sbmap.json");
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

        // Reset the ambient active scene so this suite's Floor/Crate Renderers never leak into a
        // LATER suite's AlignTo fallback-scan (a global "every Renderer in the scene" search) —
        // RoundTripSpatialTests.cs's tests build directly against the ambient scene without ever
        // resetting it themselves.
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    // 11. Anti-churn (source pin): author FitSize+AlignTo, Build, Sync with NO edit. The rewritten
    //     source still carries .FitSize/.AlignTo and carries NO .Transform(scale:/pos:) — the driven
    //     channels never leak into source — and the sync itself is a no-op.
    [Test]
    public void RoundTrip_FitSizeAlignToNoEdit_SyncIsNoOpAndEmitsNoTransform()
    {
        File.WriteAllText(_builderPath, Source(FloorBody + CrateBody));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        RunBuild(scene);

        var crate = FindRoot(EditorSceneManager.GetActiveScene(), "Crate");
        Assert.IsNotNull(crate, "Crate was not created by SceneBuilderBuild.Run");
        crate.GetComponent<FitSize>().Evaluate();
        crate.GetComponent<AlignTo>().Evaluate();

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(result.Changed, "An unedited FitSize/AlignTo scene must Sync as a no-op.");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains(".FitSize(", rewritten, "Rewritten source must still carry the .FitSize(...) call.\n" + rewritten);
        StringAssert.Contains(".AlignTo(", rewritten, "Rewritten source must still carry the .AlignTo(...) call.\n" + rewritten);

        // Scoped to the Crate statement only — the Floor legitimately authors its OWN unrelated
        // (non-driven) .Transform(scale: (10f, 1f, 10f)) call, which would otherwise false-positive
        // a whole-file substring check. The Crate's own authored `.Transform(pos: (0f, 5f, 0f))` is
        // expected to remain verbatim (X/Z are free, unchanged, and un-synced); only a driven
        // .Transform(scale:) — never authored to begin with — must never appear.
        int crateStart = rewritten.IndexOf("scene.Add(\"Crate\")", StringComparison.Ordinal);
        Assert.GreaterOrEqual(crateStart, 0, "Expected the Crate statement in the rewritten source.\n" + rewritten);
        var crateStatement = rewritten.Substring(crateStart);
        StringAssert.Contains(".Transform(pos: (0f, 5f, 0f))", crateStatement,
            "The Crate's original authored pos: argument must remain unchanged (no edit occurred).\n" + rewritten);
        StringAssert.DoesNotContain(".Transform(scale:", crateStatement,
            "Driven scale must never leak into the Crate's source as a .Transform(scale:) write.\n" + rewritten);
    }

    // 11b. Scope-FINAL regression #1 (silent source corruption): rebuilding a PERSISTED FitSize object
    //      must NOT corrupt its authored intent. Run is "reconcile-into-existing, never wipe", so the
    //      SAME FitSize instance survives a rebuild with its in-memory _lastWritten (the last scale it
    //      drove, 2,2,2). Since spec 23 removed scene-write suppression, materialize re-writes the FULL
    //      authored scale (the source's default 1,1,1 — CrateBody has no .Transform(scale:)) onto that
    //      live instance every sync. Without PlanExecutor resetting FitSize's baseline (the twin of the
    //      m_LocalPosition/AlignTo reset), that plugin-own write diverges from _lastWritten and is
    //      misread as a manual rescale, back-solving a WRONG value (2 -> ~1) that scene->code would emit
    //      into source. The fix resets the baseline at the central m_LocalScale write site, so the next
    //      Evaluate re-derives from the authored value (2) and both value and world height stay 2.
    [Test]
    public void Rebuild_PersistedFitSize_DoesNotBackSolveWrongValue()
    {
        File.WriteAllText(_builderPath, Source(FloorBody + CrateBody));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        RunBuild(scene);

        var crate = FindRoot(EditorSceneManager.GetActiveScene(), "Crate");
        Assert.IsNotNull(crate, "Crate was not created by SceneBuilderBuild.Run");
        var fitSize = crate.GetComponent<FitSize>();
        fitSize.Evaluate(); // baseline: drives localScale to solve height 2, sets _lastWritten
        Assert.AreEqual(2f, fitSize.value, Tol, "Baseline FitSize.value must equal the authored height 2.");
        Assert.AreEqual(2f, crate.GetComponent<Renderer>().bounds.size.y, Tol, "Baseline world height must be 2.");

        // Rebuild in place: reconciles onto the SAME persisted FitSize instance and re-writes the full
        // authored (default 1,1,1) scale via PlanExecutor's m_LocalScale case — the corruption seam.
        RunBuild(EditorSceneManager.GetActiveScene());

        var rebuiltCrate = FindRoot(EditorSceneManager.GetActiveScene(), "Crate");
        Assert.IsNotNull(rebuiltCrate, "Crate disappeared after the in-place rebuild.");
        var rebuiltFitSize = rebuiltCrate.GetComponent<FitSize>();
        rebuiltFitSize.Evaluate();

        Assert.AreEqual(2f, rebuiltFitSize.value, Tol,
            "REGRESSION: the rebuild's own m_LocalScale write must not be misread as a manual rescale — " +
            "FitSize.value must stay the authored 2, not back-solve to ~1 (silent source corruption).");
        Assert.AreEqual(2f, rebuiltCrate.GetComponent<Renderer>().bounds.size.y, Tol,
            "World height must remain 2 after the in-place rebuild (the crate must not shrink).");
    }

    // 12. Snapped axis re-snaps, free axis persists: dragging the object off the floor on Y (driven,
    //     WITHIN AlignTo's default captureThreshold of 2.5 units — the baseline flush Y is 1.5, so
    //     a 2-unit displacement to 3.5 is a within-threshold drag, not a sticky-detach) and to a new X
    //     (free) must re-snap Y geometrically and keep the free X. After Sync, the driven Y literal
    //     must not leak into the rewritten .Transform(pos:) call, and a rebuild from the rewritten
    //     source must still resolve to the dragged free-X and still re-snap Y correctly.
    [Test]
    public void SceneToCode_DraggedSnappedAxis_ReSnapsY_FreeXPersists()
    {
        File.WriteAllText(_builderPath, Source(FloorBody + CrateBody));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        RunBuild(scene);

        var crate = FindRoot(EditorSceneManager.GetActiveScene(), "Crate");
        var sizer = crate.GetComponent<FitSize>();
        var aligner = crate.GetComponent<AlignTo>();
        sizer.Evaluate();
        aligner.Evaluate(); // baseline align: bottom face flush on the floor (world Y top == 0.5)

        const float draggedX = 3f;
        const float draggedY = 3.5f; // baseline Y is 1.5; a 2-unit drag stays within captureThreshold (2.5)
        crate.transform.position = new Vector3(draggedX, draggedY, 0f); // drag: Y off-floor (within threshold), X to a new free value
        aligner.Evaluate(); // re-snap

        var bounds = crate.GetComponent<Renderer>().bounds;
        Assert.AreEqual(0.5f, bounds.min.y, Tol, "Re-Evaluate must re-snap Y flush on the floor after the drag.");
        Assert.AreEqual(draggedX, crate.transform.position.x, Tol, "Re-Evaluate must leave the free X axis at its dragged value.");

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "The free-X drag is a real scene change; Sync must report Changed.");

        var rewritten = File.ReadAllText(_builderPath);
        var posCall = ExtractTransformPosCall(rewritten);
        Assert.IsNotNull(posCall, "Expected a .Transform(pos: ...) call in the rewritten source for the free-X drag.\n" + rewritten);
        StringAssert.DoesNotContain("3.5f", posCall, "The raw un-snapped drag value (Y=3.5) must never leak into the pos: argument.\n" + posCall);
        StringAssert.DoesNotContain("1.5f", posCall, "The resolved driven-Y snap value (1.5) must never leak into the pos: argument.\n" + posCall);
        StringAssert.Contains("3f", posCall, "The free X drag value must be present in the pos: argument.\n" + posCall);

        // Robust round-trip: rebuild from the rewritten source into a fresh scene — free X must
        // survive, and AlignTo must still correctly re-snap Y regardless of the source's driven-Y
        // placeholder.
        RunBuild(EditorSceneManager.GetActiveScene());
        var rebuiltCrate = FindRoot(EditorSceneManager.GetActiveScene(), "Crate");
        Assert.IsNotNull(rebuiltCrate, "Crate was not recreated by the rebuild.");
        Assert.AreEqual(draggedX, rebuiltCrate.transform.position.x, Tol, "Rebuild from the rewritten source must preserve the free X value.");

        rebuiltCrate.GetComponent<FitSize>().Evaluate();
        var rebuiltAlignTo = rebuiltCrate.GetComponent<AlignTo>();
        rebuiltAlignTo.Evaluate();
        var rebuiltBounds = rebuiltCrate.GetComponent<Renderer>().bounds;
        Assert.AreEqual(0.5f, rebuiltBounds.min.y, Tol, "The rebuilt AlignTo must still re-snap Y flush on the floor.");
    }

    // A Crate carrying a target-less AlignTo (NodeHandle.None) — used only by field-edit tests below
    // that never exercise real snap geometry, so a real Floor anchor is unnecessary.
    private const string CrateBodyNoTarget =
        "        var crate = scene.Add(\"Crate\")\n" +
        "            .Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"Cube\")))\n" +
        "            .Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Builtin(\"Default-Material\") }))\n" +
        "            .Transform(pos: (0f, 5f, 0f))\n" +
        "            .FitSize(height: 2f)\n" +
        "            .AlignTo(NodeHandle.None, y: AxisAlign.AbutMax);\n";

    // 13. Field edit round-trips: editing the FitSize height and introducing an AlignTo axis directly on
    //     the live components must patch ONLY the corresponding .FitSize/.AlignTo argument — nothing
    //     structural (no .Transform) — and a second Sync (no further edit) is a no-op.
    [Test]
    public void SceneToCode_EditedFitSizeHeightAndAlignToAxis_PatchesArgumentOnly()
    {
        File.WriteAllText(_builderPath, Source(CrateBodyNoTarget));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        RunBuild(scene);

        var crate = FindRoot(EditorSceneManager.GetActiveScene(), "Crate");
        Assert.IsNotNull(crate, "Crate was not created by SceneBuilderBuild.Run");
        crate.GetComponent<FitSize>().value = 3f; // mode is already Height, materialized from source
        crate.GetComponent<AlignTo>().xMode = AlignTo.Mode.AbutMax;

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Editing FitSize.height and introducing AlignTo.xMode is a real scene change; Sync must report Changed.");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains(".FitSize(height: 3f)", rewritten, "FitSize.height edit must patch the .FitSize(height:) argument.\n" + rewritten);
        StringAssert.DoesNotContain("height: 2f", rewritten, "The old FitSize height argument must not remain.\n" + rewritten);
        StringAssert.Contains("x: AxisAlign.AbutMax", rewritten, "AlignTo.xMode introduce must patch the .AlignTo(...) call to include x: AxisAlign.AbutMax.\n" + rewritten);

        // The Crate's own pre-existing, un-driven `.Transform(pos: (0f, 5f, 0f))` (from CrateBodyNoTarget)
        // is NOT something this test edits — it must remain verbatim. A pure field edit must not
        // introduce a NEW driven-channel write (a .Transform(scale:) leak, or a DIFFERENT
        // .Transform(pos:) than the one already authored) — mirrors item 11's scoped check.
        StringAssert.Contains(".Transform(pos: (0f, 5f, 0f))", rewritten,
            "The Crate's pre-existing, unrelated pos: argument must remain unchanged by a pure FitSize/AlignTo field edit.\n" + rewritten);
        StringAssert.DoesNotContain(".Transform(scale:", rewritten,
            "A pure field edit must not introduce a .Transform(scale:) call.\n" + rewritten);

        var second = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(second.Changed, "NOT CONVERGED: a Sync immediately after the patch, with no further edit, reported Changed=true.");
    }

    // spec 52 (AlignTo offset) regression: introducing/editing a per-axis AlignTo OFFSET on a live
    // component must fold into the axis keyword (`z: AxisAlign.AbutMax.Offset(0.75f)`), never emit a
    // bare `zOffset:` argument (which does NOT compile: 'AlignTo' has no `zOffset` parameter, so
    // BuilderCompileCheck logs "emitted builder source DOES NOT COMPILE"). SyncAndAssertCompiles
    // fails on any such non-compiling emission; a second Sync must converge byte-stable.
    [Test]
    public void SceneToCode_IntroducedAlignToAxisOffset_FoldsIntoAxisKeyword_Compiles_SecondSyncNoOp()
    {
        const string body =
            "        var crate = scene.Add(\"Crate\")\n" +
            "            .Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"Cube\")))\n" +
            "            .Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Builtin(\"Default-Material\") }))\n" +
            "            .AlignTo(NodeHandle.None, z: AxisAlign.AbutMax);\n";
        File.WriteAllText(_builderPath, Source(body));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        RunBuild(scene);

        var crate = FindRoot(EditorSceneManager.GetActiveScene(), "Crate");
        Assert.IsNotNull(crate, "Crate was not created by SceneBuilderBuild.Run");

        // Introduce a per-axis offset on the ALREADY-pinned z axis (mode stays AbutMax).
        crate.GetComponent<AlignTo>().zOffset = 0.75f;

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Introducing an AlignTo z-offset is a real scene change; Sync must report Changed.");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("z: AxisAlign.AbutMax.Offset(0.75f)", rewritten,
            "The introduced z-offset must fold into the z: axis keyword.\n" + rewritten);
        StringAssert.DoesNotContain("zOffset", rewritten,
            "The offset must never emit as a bare zOffset: argument (does not compile).\n" + rewritten);

        var second = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(second.Changed, "NOT CONVERGED: a Sync immediately after the offset fold, with no further edit, reported Changed=true.");
    }

    // 14. Created-in-editor object with an AlignTo: a GameObject added directly in the scene (not via
    //     the builder) with an AlignTo attached (target set to the pre-existing Floor) must append a new
    //     .Add(...) statement carrying .AlignTo(target: floor, y: AxisAlign.AbutMax) on scene->code
    //     sync, and the emitted source must compile. A second Sync (no further change) is a no-op.
    [Test]
    public void SceneToCode_CreatedObjectWithAlignTo_AppendsAlignToCall_SecondSyncNoOp()
    {
        File.WriteAllText(_builderPath, Source(FloorBody));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        RunBuild(scene);

        var floor = FindRoot(EditorSceneManager.GetActiveScene(), "Floor");
        var created = new GameObject("Crate");
        created.AddComponent<MeshFilter>();
        created.AddComponent<MeshRenderer>();
        var aligner = created.AddComponent<AlignTo>();
        aligner.target = floor.transform;
        aligner.yMode = AlignTo.Mode.AbutMax;

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "A newly created object must be reported as a scene change.");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains(".Add(\"Crate\")", rewritten, "A new .Add(...) statement must appear for the created object.\n" + rewritten);
        StringAssert.Contains(".AlignTo(target: floor, y: AxisAlign.AbutMax)", rewritten, "The created object's AlignTo must emit the dedicated .AlignTo(...) call.\n" + rewritten);
        StringAssert.DoesNotContain("Component<SceneBuilder.Authoring.AlignTo>", rewritten,
            "AlignTo must never be emitted via the generic .Component<>() form.\n" + rewritten);

        var second = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(second.Changed, "NOT CONVERGED: a Sync immediately after the append, with no further change, reported Changed=true.");
    }

    // 18. FitSize back-solves intent from a manual scale: two Evaluate() calls are mandatory — the
    //     manual-override branch only fires when the component has already written once and localScale
    //     has since diverged. After the manual override, Sync must patch the FitSize's height argument to
    //     the back-solved world height and never write a raw .Transform(scale:).
    [Test]
    public void SceneToCode_ManualScaleOnFitSize_BackSolvesHeightIntent_NoTransformWrite()
    {
        const string body =
            "        var crate = scene.Add(\"Crate\")\n" +
            "            .Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"Cube\")))\n" +
            "            .Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Builtin(\"Default-Material\") }))\n" +
            "            .FitSize(height: 2f);\n";
        File.WriteAllText(_builderPath, Source(body));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        RunBuild(scene);

        var crate = FindRoot(EditorSceneManager.GetActiveScene(), "Crate");
        var sizer = crate.GetComponent<FitSize>();
        sizer.Evaluate(); // #1: drives localScale to (2,2,2), sets the _lastWritten baseline

        crate.transform.localScale = new Vector3(4f, 4f, 4f); // manual override (unit cube -> world height 4)
        sizer.Evaluate(); // #2: manual-override branch back-solves height from the new world size

        var bounds = crate.GetComponent<Renderer>().bounds;
        Assert.AreEqual(4f, bounds.size.y, Tol, "The manually-set scale must stand (back-solve reads intent, never overwrites the manual scale).");
        Assert.AreEqual(FitSize.Mode.Height, sizer.mode, "Back-solve must preserve the authored Height mode, not switch the discriminator.");
        Assert.AreEqual(bounds.size.y, sizer.value, Tol, "The back-solved FitSize.value intent must equal the new observed world height.");

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "The back-solved height intent is a real source change; Sync must report Changed.");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains(".FitSize(height: 4f)", rewritten, "Sync must patch the FitSize height argument to the back-solved world height.\n" + rewritten);
        StringAssert.DoesNotContain("height: 2f", rewritten, "The old FitSize height argument must not remain.\n" + rewritten);
        StringAssert.DoesNotContain(".Transform(scale:", rewritten, "The manual scale must never be written as a raw .Transform(scale:).\n" + rewritten);

        var second = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(second.Changed, "NOT CONVERGED: a Sync immediately after the back-solve patch, with no further edit, reported Changed=true.");
    }

    // 19 (sync half). Disabling a FitSize releases its driven channel: a manual scale set while disabled
    //    is no longer suppressed and DOES sync as a .Transform(scale:) write.
    [Test]
    public void SceneToCode_DisabledFitSize_ReleasesChannel_ManualScaleSyncs()
    {
        const string body =
            "        var crate = scene.Add(\"Crate\")\n" +
            "            .Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"Cube\")))\n" +
            "            .Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Builtin(\"Default-Material\") }))\n" +
            "            .FitSize(height: 2f);\n";
        File.WriteAllText(_builderPath, Source(body));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        RunBuild(scene);

        var crate = FindRoot(EditorSceneManager.GetActiveScene(), "Crate");
        var sizer = crate.GetComponent<FitSize>();
        sizer.enabled = false;

        var manualScale = new Vector3(3f, 3f, 3f);
        crate.transform.localScale = manualScale;

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "A disabled FitSize's released channel must let the manual scale sync as a real change.");

        var rewritten = File.ReadAllText(_builderPath);
        // Whitespace-tolerant: an INTRODUCED (previously-absent) .Transform(...) argument is rendered
        // via Roslyn's default NameColon (no forced space after the colon), unlike the dedicated
        // .FitSize/.AlignTo renderer's hand-formatted "name: value" — assert on structure, not on the
        // exact whitespace convention of this unrelated (pre-existing, non-spatial) code path.
        StringAssert.IsMatch(@"\.Transform\(\s*scale:\s*\(3f,\s*3f,\s*3f\)\s*\)", rewritten,
            "A disabled FitSize must release the scale channel so the manual scale is written as a .Transform(scale:).\n" + rewritten);
    }
}
