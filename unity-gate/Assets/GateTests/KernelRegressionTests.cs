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

// Kernel-migration zero-regression gate (spec 45, bucket b2): AlignTo and FitSize route their
// face/extent math through the shared ProjectedExtent kernel instead of open-coding world-AABB reads.
public class KernelRegressionTests
{
    private const string ScenePath = "Assets/GateTests/__KernelRegressionTemp.unity";
    private const float Tol = 1e-3f;

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
using static SceneBuilder.Authoring.AssetRefs;
public class KernelRegressionScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    private const string FloorBody =
        "        var floor = scene.Add(\"Floor\")\n" +
        "            .Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"Cube\")))\n" +
        "            .Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Builtin(\"Default-Material\") }))\n" +
        "            .Transform(scale: (10f, 1f, 10f));\n";

    private const string CrateBody =
        "        var crate = scene.Add(\"Crate\")\n" +
        "            .Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"Cube\")))\n" +
        "            .Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Builtin(\"Default-Material\") }))\n" +
        "            .Transform(pos: (0f, 5f, 0f))\n" +
        "            .FitSize(height: 1.2f)\n" +
        "            .AlignTo(floor, y: AxisAlign.AbutMax);\n";

    private static GameObject FindRoot(Scene scene, string name) =>
        scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_kernel_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "KernelRegressionScene.cs");
        _sidecarPath = Path.Combine(_dir, "KernelRegressionScene.sbmap.json");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        if (File.Exists(ScenePath)) AssetDatabase.DeleteAsset(ScenePath);
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    // Zero-regression pin: a crate authored .FitSize(height:1.2f).AlignTo(floor, y: AxisAlign.AbutMax)
    // above a floor must reach world height 1.2 with its bottom face flush on the floor top, and its
    // free X/Z untouched, whether the underlying face/extent math is open-coded AABB reads or routed
    // through the shared kernel. The one-time ComponentDefaultTemplate probe-object warm-up cost for
    // FitSize/AlignTo is absorbed here (LogAssert.ignoreFailingMessages) so this suite is not
    // order-dependent on a sibling suite's own warm-up having already run first.
    [Test]
    public void KernelSwap_UntiltedFitSizeAlignToCrate_HeightAndFlushWithinTolerance()
    {
        File.WriteAllText(_builderPath, Source(FloorBody + CrateBody));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();

        var prevIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        GameObject crate;
        try
        {
            SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
            crate = FindRoot(EditorSceneManager.GetActiveScene(), "Crate");
            Assert.IsNotNull(crate, "Crate was not created by SceneBuilderBuild.Run");

            crate.GetComponent<FitSize>().Evaluate();
            crate.GetComponent<AlignTo>().Evaluate();
        }
        finally
        {
            LogAssert.ignoreFailingMessages = prevIgnore;
        }

        var floor = FindRoot(EditorSceneManager.GetActiveScene(), "Floor");
        float floorTop = floor.GetComponent<Renderer>().bounds.max.y;
        var bounds = crate.GetComponent<Renderer>().bounds;

        Assert.AreEqual(1.2f, bounds.size.y, Tol, "FitSize must still drive the WORLD height to the authored value.");
        Assert.AreEqual(floorTop, bounds.min.y, Tol, "AlignTo must still rest the crate's bottom face flush on the floor top.");
        Assert.AreEqual(0f, crate.transform.position.x, Tol, "The crate's free X must stay untouched by the kernel swap.");
        Assert.AreEqual(0f, crate.transform.position.z, Tol, "The crate's free Z must stay untouched by the kernel swap.");
    }

    // Delegation guard: the Bounds/rotation/scale overload FitSize will use must agree with the existing
    // Renderer overload for a real (scaled, rotated) renderer, within the same tolerance every spatial
    // gate assertion in this suite uses.
    [Test]
    public void KernelOverloads_AgreeForRealRenderer()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            go.transform.localScale = new Vector3(2f, 3f, 4f);
            go.transform.rotation = Quaternion.Euler(0f, 30f, 15f);

            var r = go.GetComponent<Renderer>();
            var d = Vector3.up;

            float viaRenderer = ProjectedExtent.HalfExtentAlong(r, d);
            float viaBounds = ProjectedExtent.HalfExtentAlong(r.localBounds, r.transform.rotation, r.transform.lossyScale, d);

            Assert.AreEqual(viaRenderer, viaBounds, Tol,
                "The Bounds/rotation/scale overload must agree with the Renderer overload for the same object.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }
}
