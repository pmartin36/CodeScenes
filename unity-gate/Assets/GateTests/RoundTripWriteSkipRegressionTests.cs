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

// b1-t2 (specs/completed/23-fitsize-surfacesnap-transform-authority.md) — the headline gate coverage
// the v1 scene-write suppression bug escaped. Per the spec's own bug report (specs/completed/23-...
// line 15-19): the materialize write-skip zeroed the Crate's authored Y, so the Crate built EMBEDDED
// in the floor — and SurfaceSnap's raycast/fallback-scan both search from the Crate's OWN current
// bounds toward a surface, so when the target surface ends up on the WRONG side of that (masked)
// starting position, neither can find it and the Crate is left embedded, never snapping. This means
// the bug's effect DOES survive to the final, fully-driven geometry (confirmed empirically, and why
// FloorBody below is a THICK floor, not a thin one — see its own comment) — so a plain "final observed
// geometry" assertion (no hand-positioned direct Evaluate(), no pre/post-drive bookkeeping) is the
// correct — and sufficient — regression check, matching the spec's own "Gate coverage" section.
//
// Synchronous EditMode does NOT tick [ExecuteAlways].Update() — every geometry-dependent assertion
// explicitly calls component.Evaluate() (RoundTripSpatialTests.cs's established pattern). FitSize's
// and SurfaceSnap's own [ExecuteAlways] OnValidate ALSO already self-drives synchronously inside
// SceneBuilderBuild.Run itself (as soon as PlanExecutor commits their SerializedObject) — the
// explicit Evaluate() calls below are the same defensive idempotent re-assert RoundTripSpatialSyncTests
// uses after every Build, not the sole drive mechanism.
public class RoundTripWriteSkipRegressionTests
{
    private const string ScenePath = "Assets/GateTests/__RoundTripWriteSkipRegressionTemp.unity";
    private const float Tol = 1e-3f;

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
using static SceneBuilder.Authoring.AssetRefs;
public class WriteSkipRegressionScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    // A static floor with a real collider: a THICK cube (scaled (10,4,10), top face at world Y=2), so
    // the v1 write-skip's masked-to-zero Y (see CrateBody below) leaves the Crate's post-FitSize
    // TOP face (0.6) still well BELOW the floor's top surface (2) — i.e. genuinely embedded, not just
    // resting slightly low. SurfaceSnap's downward raycast/fallback-scan both search from the object's
    // OWN current bounds toward the surface; when the surface is on the WRONG side of (above) the
    // object's own current extent, neither can find it (a 1-unit-thin floor would NOT reproduce this —
    // the Crate's own top face would still clear a thin floor's top even at the masked Y=0, and the
    // raycast would find the floor anyway, silently passing under the bug too — confirmed empirically).
    private const string FloorBody =
        "        var floor = scene.Add(\"Floor\")\n" +
        "            .Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"Cube\")))\n" +
        "            .Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Builtin(\"Default-Material\") }))\n" +
        "            .Component<UnityEngine.BoxCollider>()\n" +
        "            .Transform(scale: (10f, 4f, 10f));\n";

    // A Crate carrying an EXPLICIT authored transform ABOVE the floor PLUS FitSize + SurfaceSnap on
    // the same node — exactly the v1 write-skip scenario (specs/completed/23-...md line 15-19). Under
    // the bug, the authored Y is zeroed to the GameObject's default (0) — deep inside the floor's own
    // volume (floor spans y in [-2, 2]) — so the Crate starts fully EMBEDDED; SurfaceSnap's downward
    // raycast/fallback-scan both search from the Crate's OWN current bounds toward a surface and find
    // nothing (the floor's top is on the wrong side, above the Crate's own current extent), so it
    // never snaps and the Crate is left embedded.
    private const string CrateBody =
        "        var crate = scene.Add(\"Crate\")\n" +
        "            .Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"Cube\")))\n" +
        "            .Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Builtin(\"Default-Material\") }))\n" +
        "            .Transform(pos: (0f, 3f, 0f))\n" +
        "            .FitSize(height: 1.2f)\n" +
        "            .SurfaceSnap(down: true);\n";

    private static GameObject FindRoot(Scene scene, string name)
    {
        return scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);
    }

    // PlanExecutor adds the FitSize/SurfaceSnap MonoBehaviour (triggering its [ExecuteAlways]
    // OnValidate -> Evaluate synchronously) BEFORE the SetField pass populates the sibling
    // MeshFilter's mesh, so a transient "no MeshFilter/mesh to size" error is expected Console noise
    // on every Build that creates a FitSize — irrelevant here, matching the RoundTripSpatialTests.cs
    // / RoundTripSpatialSyncTests.cs precedent. Scoped to ONLY the Build call so a genuine error from
    // a later explicit Evaluate() still fails the test.
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

    // SerializedFieldBridge.GetDefaultFieldMap lazily instantiates a throwaway MonoBehaviour instance
    // the FIRST time a component type is snapshot-read, to compute a per-type default-field map —
    // cached for the rest of the domain's lifetime. For FitSize/SurfaceSnap this fires exactly ONCE
    // per type, non-deterministically on whichever [Test] happens to build one first. Warm it up
    // HERE, isolated from any real test's LogAssert window, matching RoundTripSpatialSyncTests.cs.
    [OneTimeSetUp]
    public void WarmSpatialDefaultFieldCache()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sb_rtwsr_warm_" + Guid.NewGuid().ToString("N"));
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
        _dir = Path.Combine(Path.GetTempPath(), "sb_rtwsr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "WriteSkipRegressionScene.cs");
        _sidecarPath = Path.Combine(_dir, "WriteSkipRegressionScene.sbmap.json");
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

    [Test]
    public void Build_AuthoredTransformAboveFloorWithFitSizeAndSurfaceSnap_DrivesToFlushRest()
    {
        File.WriteAllText(_builderPath, Source(FloorBody + CrateBody));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        RunBuild(scene);

        var crate = FindRoot(EditorSceneManager.GetActiveScene(), "Crate");
        Assert.IsNotNull(crate, "Crate was not created by SceneBuilderBuild.Run");
        var floor = FindRoot(EditorSceneManager.GetActiveScene(), "Floor");
        float floorTop = floor.GetComponent<Renderer>().bounds.max.y;

        // Drive the real component path (execution order: FitSize(-100) then SurfaceSnap(-90)) —
        // idempotent re-assert on top of whatever self-drive already happened inside Run, matching
        // RoundTripSpatialSyncTests.cs's established pattern.
        crate.GetComponent<FitSize>().Evaluate();
        crate.GetComponent<SurfaceSnap>().Evaluate();

        // REGRESSION ASSERTION: under the v1 write-skip, the authored Y is zeroed on materialize, so
        // the Crate starts embedded deep inside the (thick) floor and neither SurfaceSnap's downward
        // raycast nor its collider-less fallback scan can find the floor's top surface (it is on the
        // wrong side — above — the Crate's own current bounds) — it stays embedded, never flush. Under
        // the fix, the authored Y materializes in full (well above the floor), so SurfaceSnap resolves
        // the floor normally and the Crate ends up ~1.2 tall, bottom face flush on the floor's top.
        var bounds = crate.GetComponent<Renderer>().bounds;
        Assert.AreEqual(1.2f, bounds.size.y, Tol, "FitSize must drive world height to ~1.2.");
        Assert.AreEqual(floorTop, bounds.min.y, Tol,
            "Down-snap must rest the bottom face flush on the floor's top face " +
            "(the v1 write-skip left the Crate embedded, never snapping).");
    }
}
