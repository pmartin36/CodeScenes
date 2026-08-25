using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;

// A prefab-instance handle carries the same component surface a plain node does, in every
// authoring position: an inline `.Component<T>()` chained straight off `Instance(...)`, and a
// `.Component<T>()`/`.AddComponent<T>()` call on a handle captured in an earlier statement and
// used later. Both forms must actually attach the component to the built instance's root
// GameObject -- not just parse without error.
public class InstanceComponentSurfaceRoundTripTests
{
    private const string FixturesDir = "Assets/GateTests/Fixtures_InstanceComponentSurface";
    private const string PrefabPath = FixturesDir + "/ICS_Ball.prefab";
    private const string ScenePath = "Assets/GateTests/__InstanceComponentSurfaceTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
using UnityEngine;
public class InstanceComponentSurfaceScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_ics_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "InstanceComponentSurfaceScene.cs");
        _sidecarPath = Path.Combine(_dir, "InstanceComponentSurfaceScene.sbmap.json");

        if (!AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "Fixtures_InstanceComponentSurface");
        }

        var source = new GameObject("ICS_Ball_Source");
        PrefabUtility.SaveAsPrefabAsset(source, PrefabPath);
        Object.DestroyImmediate(source);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
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

        AssetDatabase.DeleteAsset(PrefabPath);
        if (AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.DeleteAsset(FixturesDir);
        }
    }

    private static GameObject FindRoot(Scene scene, string name) =>
        scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);

    [Test]
    public void Run_InlineInstanceComponent_AttachesComponentToInstanceRoot()
    {
        File.WriteAllText(_builderPath, Source(
            $"        scene.Instance(\"{PrefabPath}\").Component<Rigidbody>();"));

        var result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        Assert.IsEmpty(result.Diagnostics,
            "Build reported diagnostics for an inline .Component<T>() on a prefab instance: "
            + string.Join("; ", result.Diagnostics.Select(d => d.Message)));

        var instanceRoot = FindRoot(EditorSceneManager.GetActiveScene(), "ICS_Ball");
        Assert.IsNotNull(instanceRoot, "Instance root was not created");
        Assert.IsNotNull(instanceRoot.GetComponent<Rigidbody>(),
            "Inline .Component<T>() on a prefab instance did not attach the component");
    }

    [Test]
    public void Run_CapturedInstanceComponent_AttachesComponentToInstanceRoot()
    {
        File.WriteAllText(_builderPath, Source(
            "        var ball = scene.Instance(\"" + PrefabPath + "\");\n" +
            "        ball.Component<Rigidbody>();"));

        var result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        Assert.IsEmpty(result.Diagnostics,
            "Build reported diagnostics for a captured-handle .Component<T>() call: "
            + string.Join("; ", result.Diagnostics.Select(d => d.Message)));

        var instanceRoot = FindRoot(EditorSceneManager.GetActiveScene(), "ICS_Ball");
        Assert.IsNotNull(instanceRoot, "Instance root was not created");
        Assert.IsNotNull(instanceRoot.GetComponent<Rigidbody>(),
            "A captured-handle .Component<T>() call did not attach the component");
    }

    [Test]
    public void Run_CapturedInstanceAddComponent_AttachesComponentToInstanceRoot()
    {
        File.WriteAllText(_builderPath, Source(
            "        var ball = scene.Instance(\"" + PrefabPath + "\");\n" +
            "        ball.AddComponent<Rigidbody>();"));

        var result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        Assert.IsEmpty(result.Diagnostics,
            "Build reported diagnostics for a captured-handle .AddComponent<T>() call: "
            + string.Join("; ", result.Diagnostics.Select(d => d.Message)));

        var instanceRoot = FindRoot(EditorSceneManager.GetActiveScene(), "ICS_Ball");
        Assert.IsNotNull(instanceRoot, "Instance root was not created");
        Assert.IsNotNull(instanceRoot.GetComponent<Rigidbody>(),
            "A captured-handle .AddComponent<T>() call did not attach the component");
    }
}
