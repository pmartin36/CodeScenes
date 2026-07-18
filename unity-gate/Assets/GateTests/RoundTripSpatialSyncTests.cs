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

// M-Spatial (FitSize/SurfaceSnap) full bidirectional SYNC round-trip gate tests — the spec's confirmation
// checklist items 11, 12, 13, 14, 18, and the sync half of 19 (specs/19-spatial-authoring-components.md).
// Drives the FULL loop (code->scene via SceneBuilderBuild.Run, scene->code via
// EmittedCodeCompiles.SyncAndAssertCompiles) against a live editor scene. Mirrors
// RoundTripObjectRefTests.cs's harness verbatim. b1-b6 production for M-Spatial is already landed
// (research.md confirms); these tests are expected to PASS on first run — this file IS the mandatory
// full-loop gate coverage (CLAUDE.md hard requirement), not a RED test driving new production. A
// failure here is a genuine Unity-boundary escape and routes to the owning b2-b6 production task.
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
    // distinguishable from any driven-axis value used below). No FitSize/SurfaceSnap — a plain surface.
    private const string FloorBody =
        "        var floor = scene.Add(\"Floor\")\n" +
        "            .Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"Cube\")))\n" +
        "            .Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Builtin(\"Default-Material\") }))\n" +
        "            .Transform(scale: (10f, 1f, 10f));\n";

    // A Crate: unit Cube, aspect-locked FitSize height 2, down-SurfaceSnap, starting above the floor.
    private const string CrateBody =
        "        var crate = scene.Add(\"Crate\")\n" +
        "            .Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"Cube\")))\n" +
        "            .Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Builtin(\"Default-Material\") }))\n" +
        "            .Transform(pos: (0f, 5f, 0f))\n" +
        "            .FitSize(height: 2f)\n" +
        "            .SurfaceSnap(down: true);\n";

    private static GameObject FindRoot(Scene scene, string name)
    {
        return scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);
    }

    // PlanExecutor adds the FitSize/SurfaceSnap MonoBehaviour (triggering its [ExecuteAlways] OnValidate ->
    // Evaluate synchronously) BEFORE the SetField pass populates the sibling MeshFilter's mesh, so a
    // transient "no MeshFilter/mesh to size" error is expected Console noise on every Build that
    // creates a FitSize — irrelevant here (the reporting surface is the assertions below, not the
    // Console), matching the RoundTripSpatialTests.cs (b6-t1) precedent. Scoped to ONLY the Build call
    // so a genuine error from a later explicit Evaluate() still fails the test.
    private void RunBuild(Scene scene)
    {
        var prevIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        try
        {
            SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        }
        finally
        {
            LogAssert.ignoreFailingMessages = prevIgnore;
        }
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

    // SerializedFieldBridge.GetDefaultFieldMap lazily instantiates a throwaway MonoBehaviour instance
    // (bare GameObject, no MeshFilter/Renderer) the FIRST time a component type is snapshot-read, to
    // compute a per-type default-field map — cached for the rest of the domain's lifetime. For
    // FitSize/SurfaceSnap (both [ExecuteAlways] with an OnValidate that logs a located error against that
    // bare probe object) this fires exactly ONCE per type, non-deterministically on whichever [Test]
    // happens to sync a FitSize/SurfaceSnap first. Force the warm-up HERE, isolated from any real test's
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
                "            .SurfaceSnap(down: true);\n"));

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
        // LATER suite's SurfaceSnap fallback-scan (a global "every Renderer in the scene" search) —
        // RoundTripSpatialTests.cs's tests build directly against the ambient scene without ever
        // resetting it themselves.
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    // 11. Anti-churn (source pin): author FitSize+SurfaceSnap, Build, Sync with NO edit. The rewritten
    //     source still carries .FitSize/.SurfaceSnap and carries NO .Transform(scale:/pos:) — the driven
    //     channels never leak into source — and the sync itself is a no-op.
    [Test]
    public void RoundTrip_FitSizeSurfaceSnapNoEdit_SyncIsNoOpAndEmitsNoTransform()
    {
        File.WriteAllText(_builderPath, Source(FloorBody + CrateBody));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        RunBuild(scene);

        var crate = FindRoot(EditorSceneManager.GetActiveScene(), "Crate");
        Assert.IsNotNull(crate, "Crate was not created by SceneBuilderBuild.Run");
        crate.GetComponent<FitSize>().Evaluate();
        crate.GetComponent<SurfaceSnap>().Evaluate();

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(result.Changed, "An unedited FitSize/SurfaceSnap scene must Sync as a no-op.");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains(".FitSize(", rewritten, "Rewritten source must still carry the .FitSize(...) call.\n" + rewritten);
        StringAssert.Contains(".SurfaceSnap(", rewritten, "Rewritten source must still carry the .SurfaceSnap(...) call.\n" + rewritten);

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

    // 12. Snapped axis re-snaps, free axis persists: dragging the object off the floor on Y (driven)
    //     and to a new X (free) must re-snap Y geometrically and keep the free X. After Sync, the
    //     driven Y literal must not leak into the rewritten .Transform(pos:) call, and a rebuild from
    //     the rewritten source must still resolve to the dragged free-X and still re-snap Y correctly.
    [Test]
    public void SceneToCode_DraggedSnappedAxis_ReSnapsY_FreeXPersists()
    {
        File.WriteAllText(_builderPath, Source(FloorBody + CrateBody));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        RunBuild(scene);

        var crate = FindRoot(EditorSceneManager.GetActiveScene(), "Crate");
        var sizer = crate.GetComponent<FitSize>();
        var snapper = crate.GetComponent<SurfaceSnap>();
        sizer.Evaluate();
        snapper.Evaluate(); // baseline snap: bottom face flush on the floor (world Y top == 0.5)

        const float draggedX = 3f;
        crate.transform.position = new Vector3(draggedX, 10f, 0f); // drag: Y off-floor, X to a new free value
        snapper.Evaluate(); // re-snap

        var bounds = crate.GetComponent<Renderer>().bounds;
        Assert.AreEqual(0.5f, bounds.min.y, Tol, "Re-Evaluate must re-snap Y flush on the floor after the drag.");
        Assert.AreEqual(draggedX, crate.transform.position.x, Tol, "Re-Evaluate must leave the free X axis at its dragged value.");

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "The free-X drag is a real scene change; Sync must report Changed.");

        var rewritten = File.ReadAllText(_builderPath);
        var posCall = ExtractTransformPosCall(rewritten);
        Assert.IsNotNull(posCall, "Expected a .Transform(pos: ...) call in the rewritten source for the free-X drag.\n" + rewritten);
        StringAssert.DoesNotContain("10f", posCall, "The raw un-snapped drag value (Y=10) must never leak into the pos: argument.\n" + posCall);
        StringAssert.DoesNotContain("1.5f", posCall, "The resolved driven-Y snap value (1.5) must never leak into the pos: argument.\n" + posCall);
        StringAssert.Contains("3f", posCall, "The free X drag value must be present in the pos: argument.\n" + posCall);

        // Robust round-trip: rebuild from the rewritten source into a fresh scene — free X must
        // survive, and the SurfaceSnap must still correctly re-snap Y regardless of the source's
        // driven-Y placeholder.
        RunBuild(EditorSceneManager.GetActiveScene());
        var rebuiltCrate = FindRoot(EditorSceneManager.GetActiveScene(), "Crate");
        Assert.IsNotNull(rebuiltCrate, "Crate was not recreated by the rebuild.");
        Assert.AreEqual(draggedX, rebuiltCrate.transform.position.x, Tol, "Rebuild from the rewritten source must preserve the free X value.");

        rebuiltCrate.GetComponent<FitSize>().Evaluate();
        var rebuiltSurfaceSnap = rebuiltCrate.GetComponent<SurfaceSnap>();
        rebuiltSurfaceSnap.Evaluate();
        var rebuiltBounds = rebuiltCrate.GetComponent<Renderer>().bounds;
        Assert.AreEqual(0.5f, rebuiltBounds.min.y, Tol, "The rebuilt SurfaceSnap must still re-snap Y flush on the floor.");
    }

    // 13. Field edit round-trips: editing the FitSize height and toggling a SurfaceSnap flag directly on
    //     the live components must patch ONLY the corresponding .FitSize/.SurfaceSnap argument — nothing
    //     structural (no .Transform) — and a second Sync (no further edit) is a no-op.
    [Test]
    public void SceneToCode_EditedFitSizeHeightAndSurfaceSnapFlag_PatchesArgumentOnly()
    {
        File.WriteAllText(_builderPath, Source(CrateBody));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        RunBuild(scene);

        var crate = FindRoot(EditorSceneManager.GetActiveScene(), "Crate");
        Assert.IsNotNull(crate, "Crate was not created by SceneBuilderBuild.Run");
        crate.GetComponent<FitSize>().height = 3f;
        crate.GetComponent<SurfaceSnap>().left = true;

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Editing FitSize.height and SurfaceSnap.left is a real scene change; Sync must report Changed.");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains(".FitSize(height: 3f)", rewritten, "FitSize.height edit must patch the .FitSize(height:) argument.\n" + rewritten);
        StringAssert.DoesNotContain("height: 2f", rewritten, "The old FitSize height argument must not remain.\n" + rewritten);
        StringAssert.Contains("left: true", rewritten, "SurfaceSnap.left toggle must patch the .SurfaceSnap(...) call to include left: true.\n" + rewritten);

        // The Crate's own pre-existing, un-driven `.Transform(pos: (0f, 5f, 0f))` (from CrateBody) is
        // NOT something this test edits — it must remain verbatim. A pure field edit must not
        // introduce a NEW driven-channel write (a .Transform(scale:) leak, or a DIFFERENT
        // .Transform(pos:) than the one already authored) — mirrors item 11's scoped check.
        StringAssert.Contains(".Transform(pos: (0f, 5f, 0f))", rewritten,
            "The Crate's pre-existing, unrelated pos: argument must remain unchanged by a pure FitSize/SurfaceSnap field edit.\n" + rewritten);
        StringAssert.DoesNotContain(".Transform(scale:", rewritten,
            "A pure field edit must not introduce a .Transform(scale:) call.\n" + rewritten);

        var second = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(second.Changed, "NOT CONVERGED: a Sync immediately after the patch, with no further edit, reported Changed=true.");
    }

    // 14. Created-in-editor object with a SurfaceSnap: a GameObject added directly in the scene (not via
    //     the builder) with a SurfaceSnap attached must append a new .Add(...) statement carrying
    //     .SurfaceSnap(down: true) on scene->code sync, and the emitted source must compile. A second
    //     Sync (no further change) is a no-op.
    [Test]
    public void SceneToCode_CreatedObjectWithSurfaceSnap_AppendsSurfaceSnapCall_SecondSyncNoOp()
    {
        File.WriteAllText(_builderPath, Source(FloorBody));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        RunBuild(scene);

        var created = new GameObject("Crate");
        created.AddComponent<MeshFilter>();
        created.AddComponent<MeshRenderer>();
        var snapper = created.AddComponent<SurfaceSnap>();
        snapper.down = true;

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "A newly created object must be reported as a scene change.");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains(".Add(\"Crate\")", rewritten, "A new .Add(...) statement must appear for the created object.\n" + rewritten);
        StringAssert.Contains(".SurfaceSnap(down: true)", rewritten, "The created object's SurfaceSnap must emit the dedicated .SurfaceSnap(down: true) call.\n" + rewritten);
        StringAssert.DoesNotContain("Component<SceneBuilder.Authoring.SurfaceSnap>", rewritten,
            "SurfaceSnap must never be emitted via the generic .Component<>() form.\n" + rewritten);

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
        // .FitSize/.SurfaceSnap renderer's hand-formatted "name: value" — assert on structure, not on the
        // exact whitespace convention of this unrelated (pre-existing, non-spatial) code path.
        StringAssert.IsMatch(@"\.Transform\(\s*scale:\s*\(3f,\s*3f,\s*3f\)\s*\)", rewritten,
            "A disabled FitSize must release the scale channel so the manual scale is written as a .Transform(scale:).\n" + rewritten);
    }
}
