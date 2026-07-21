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

// M-Spatial (FitSize/SurfaceSnap) capture-threshold + sticky-detach gate coverage — spec 23's
// "Gate coverage" item "drag within threshold -> snaps back; drag beyond threshold -> detaches"
// (b4 of specs/completed/23-fitsize-surfacesnap-transform-authority.md). b4-t1 already landed the
// production threshold/detach logic in com.codescenes/Runtime/SurfaceSnap.cs; this file IS the
// dedicated gate coverage for it (the beyond-threshold sticky-detach branch has no other coverage —
// see b4-t1 validator.md), not a RED test driving new production. A failure here is a genuine
// regression and routes back to SurfaceSnap.cs.
//
// New file (not an extension of RoundTripSpatialSyncTests.cs / RoundTripSpatialTests.cs) to avoid a
// TOUCHES collision with the field-rename edits those files carry from sibling tasks.
//
// Drives the REAL code->scene build path (SceneBuilderBuild.Run) against a live editor scene, then
// drives SurfaceSnap via explicit Evaluate() calls (synchronous EditMode does not tick
// [ExecuteAlways].Update()) and asserts OBSERVED geometry (Renderer.bounds / transform.position),
// never labels.
public class RoundTripSpatialThresholdTests
{
    private const string ScenePath = "Assets/GateTests/__RoundTripSpatialThresholdTemp.unity";
    private const float Tol = 1e-3f;

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
using static SceneBuilder.Authoring.AssetRefs;
public class RoundTripSpatialThresholdScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    // A static floor: a Cube scaled (10,1,10) so its top face sits at world Y = 0.5.
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

    // See RoundTripSpatialSyncTests.RunBuild: PlanExecutor adds the FitSize/SurfaceSnap MonoBehaviour
    // (triggering its [ExecuteAlways] OnValidate -> Evaluate synchronously) before the sibling
    // MeshFilter's mesh is populated, so a transient "no mesh yet" Console error is expected noise on
    // every Build that creates a FitSize. Scoped to ONLY the Build call so a genuine error from a
    // later explicit Evaluate() still fails the test.
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

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_rtst_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "RoundTripSpatialThresholdScene.cs");
        _sidecarPath = Path.Combine(_dir, "RoundTripSpatialThresholdScene.sbmap.json");
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
        // LATER suite's SurfaceSnap fallback-scan.
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    private GameObject BuildFloorAndCrate(out FitSize sizer, out SurfaceSnap snapper)
    {
        File.WriteAllText(_builderPath, Source(FloorBody + CrateBody));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        RunBuild(scene);

        var crate = FindRoot(EditorSceneManager.GetActiveScene(), "Crate");
        Assert.IsNotNull(crate, "Crate was not created by SceneBuilderBuild.Run");

        sizer = crate.GetComponent<FitSize>();
        snapper = crate.GetComponent<SurfaceSnap>();
        sizer.Evaluate();
        snapper.Evaluate(); // baseline snap: bottom face flush on the floor (world Y top == 0.5)

        var baselineBounds = crate.GetComponent<Renderer>().bounds;
        Assert.AreEqual(0.5f, baselineBounds.min.y, Tol, "Baseline snap must rest flush on the floor before the drag under test.");
        Assert.AreEqual(1.5f, crate.transform.position.y, Tol, "Baseline snapped Y position must be 1.5 (half-height 1 above floor top 0.5).");

        return crate;
    }

    // Drag WITHIN captureThreshold (default 2.5): a manual displacement on the snapped Y axis of 2.0
    // units (baseline 1.5 -> 3.5; dragSq 4.0 < threshold^2 6.25) is overridden — the next Evaluate()
    // re-snaps flush (constraint wins) and the component stays enabled.
    [Test]
    public void SurfaceSnap_DragWithinThreshold_ReSnapsFlush()
    {
        var crate = BuildFloorAndCrate(out _, out var snapper);

        crate.transform.position = new Vector3(0f, 3.5f, 0f); // dy = 2.0, within the 2.5 captureThreshold
        snapper.Evaluate();

        var bounds = crate.GetComponent<Renderer>().bounds;
        Assert.AreEqual(0.5f, bounds.min.y, Tol, "A within-threshold drag must re-snap flush on the floor.");
        Assert.IsTrue(snapper.enabled, "A within-threshold drag must not detach the component.");
    }

    // Drag BEYOND captureThreshold (default 2.5): a manual displacement of 3.0 units (baseline 1.5 ->
    // 4.5; dragSq 9.0 > threshold^2 6.25) detaches STICKILY on the next Evaluate() — the object stays
    // exactly where dragged, the component disables itself, and it does NOT re-snap even after being
    // dragged back into range; only re-enabling restores snapping.
    [Test]
    public void SurfaceSnap_DragBeyondThreshold_DetachesStickily_UntilReEnabled()
    {
        var crate = BuildFloorAndCrate(out _, out var snapper);

        crate.transform.position = new Vector3(0f, 4.5f, 0f); // dy = 3.0, beyond the 2.5 captureThreshold
        snapper.Evaluate();

        Assert.IsFalse(snapper.enabled, "A beyond-threshold drag must sticky-detach (disable) the component.");
        Assert.AreEqual(4.5f, crate.transform.position.y, Tol, "A beyond-threshold drag must leave the object exactly where dragged.");
        var boundsAfterDetach = crate.GetComponent<Renderer>().bounds;
        Assert.AreEqual(3.5f, boundsAfterDetach.min.y, Tol, "The detached object must NOT re-snap to the floor.");

        // Drag back into range while still detached: sticky means it does NOT re-snap merely by
        // being back within threshold distance of where it would resolve.
        crate.transform.position = new Vector3(0f, 2f, 0f);
        snapper.Evaluate(); // no-op: Evaluate() guards on isActiveAndEnabled while enabled == false

        Assert.AreEqual(2f, crate.transform.position.y, Tol, "STICKY: dragging back into range must not implicitly re-snap while detached.");
        var boundsStillDetached = crate.GetComponent<Renderer>().bounds;
        Assert.AreEqual(1f, boundsStillDetached.min.y, Tol, "STICKY: the object must remain un-snapped (not flush at 0.5) until re-enabled.");

        // Re-enable: OnEnable() -> ResetBaseline() clears the drag baseline, restoring snapping.
        snapper.enabled = true;
        snapper.Evaluate();

        var boundsAfterReEnable = crate.GetComponent<Renderer>().bounds;
        Assert.AreEqual(0.5f, boundsAfterReEnable.min.y, Tol, "Re-enabling the component must restore flush snapping on the next Evaluate().");
    }
}
