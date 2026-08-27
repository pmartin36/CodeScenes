using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using SceneBuilder.Authoring;
using SceneBuilder.Editor;

// The headline instanced-prop workflow: a nested-mesh prefab (mesh only in CHILD nodes, root has
// no Renderer) instanced via scene.Instance(...) and given .FitSize(...).AlignTo(...) sizes and
// rests by its WHOLE-hierarchy combined bounds, with the solver components actually landed on the
// instance root -- nothing silently dropped.
public class InstanceSolverWorkflowTests
{
    private const string FixturesDir = "Assets/GateTests/Fixtures_InstanceSolverWorkflow";
    private const string PrefabPath = FixturesDir + "/NestedProp.prefab";
    private const string ScenePath = "Assets/GateTests/__InstanceSolverWorkflowTemp.unity";
    private const float Tol = 1e-3f;

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
using static SceneBuilder.Authoring.AssetRefs;
public class InstanceSolverWorkflowScene : ISceneDefinition
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

    private static string InstanceBody(string prefabPath) =>
        "        scene.Instance(\"" + prefabPath + "\")\n" +
        "            .FitSize(width: 2f)\n" +
        "            .AlignTo(floor, y: AxisAlign.AbutMax);\n";

    private static GameObject FindRoot(Scene scene, string name) =>
        scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);

    private static Bounds CombinedRendererWorldBounds(GameObject root)
    {
        var renderers = root.GetComponentsInChildren<Renderer>();
        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
        return b;
    }

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_isw_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "InstanceSolverWorkflowScene.cs");
        _sidecarPath = Path.Combine(_dir, "InstanceSolverWorkflowScene.sbmap.json");

        if (!AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "Fixtures_InstanceSolverWorkflow");
        }

        // Empty root with two mesh-bearing CHILD cubes: the root itself carries no
        // Renderer/MeshFilter, so the built instance's own root is Renderer-less -- the exact
        // shape the whole-hierarchy combined-bounds fix exists to size and rest.
        var source = new GameObject("NestedProp_Source");
        var left = GameObject.CreatePrimitive(PrimitiveType.Cube);
        left.name = "Left";
        left.transform.SetParent(source.transform, worldPositionStays: false);
        left.transform.localPosition = new Vector3(-1f, 0f, 0f);
        var right = GameObject.CreatePrimitive(PrimitiveType.Cube);
        right.name = "Right";
        right.transform.SetParent(source.transform, worldPositionStays: false);
        right.transform.localPosition = new Vector3(1f, 0f, 0f);

        PrefabUtility.SaveAsPrefabAsset(source, PrefabPath);
        UnityEngine.Object.DestroyImmediate(source);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        if (File.Exists(ScenePath)) AssetDatabase.DeleteAsset(ScenePath);

        AssetDatabase.DeleteAsset(PrefabPath);
        if (AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.DeleteAsset(FixturesDir);
        }

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    [Test]
    public void Instance_NestedMeshProp_FitSizeAlignTo_SizesAndRestsByCombinedHierarchyBounds()
    {
        File.WriteAllText(_builderPath, Source(FloorBody + InstanceBody(PrefabPath)));

        var prevIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        GameObject instanceRoot;
        SceneBuilderBuild.BuildResult result;
        try
        {
            result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

            instanceRoot = FindRoot(EditorSceneManager.GetActiveScene(), "NestedProp");
            Assert.IsNotNull(instanceRoot, "The prefab instance was not created by the build.");

            var fitSize = instanceRoot.GetComponent<FitSize>();
            var alignTo = instanceRoot.GetComponent<AlignTo>();
            Assert.IsNotNull(fitSize, "FitSize on an instance receiver was not landed on the instance root -- silently dropped.");
            Assert.IsNotNull(alignTo, "AlignTo on an instance receiver was not landed on the instance root -- silently dropped.");

            fitSize.Evaluate();
            alignTo.Evaluate();
        }
        finally
        {
            LogAssert.ignoreFailingMessages = prevIgnore;
        }

        Assert.IsEmpty(result.Diagnostics,
            "Build reported diagnostics for solver verbs on a prefab instance: "
            + string.Join("; ", result.Diagnostics.Select(d => d.Message)));

        var floor = FindRoot(EditorSceneManager.GetActiveScene(), "Floor");
        float floorTop = floor.GetComponent<Renderer>().bounds.max.y;
        var combined = CombinedRendererWorldBounds(instanceRoot);

        Assert.AreEqual(2f, combined.size.x, Tol,
            "FitSize must drive the instance's WHOLE-hierarchy combined world width to the authored value.");
        Assert.AreEqual(floorTop, combined.min.y, Tol,
            "AlignTo must rest the instance's combined-bounds bottom flush on the floor top.");
    }
}
