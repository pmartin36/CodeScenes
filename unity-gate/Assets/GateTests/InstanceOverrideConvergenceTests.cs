using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SceneBuilder.Editor;

// An authored instance-override statement targeting an adapter-excluded serialized path
// (SpriteRenderer.m_Size is recomputed from the sprite's bounds, never author intent) must reach a
// FIXED POINT: once the diff gate suppresses the op, a rebuild against the SAME live scene must
// keep emitting zero ops for it, not re-emit it every build because the live snapshot never carries
// the excluded path as an override.
public class InstanceOverrideConvergenceTests
{
    private const string FixturesDir = "Assets/GateTests/Fixtures_InstanceOverrideConvergence";
    private const string PrefabPath = FixturesDir + "/IOC_Sprite.prefab";
    private const string ScenePath = "Assets/GateTests/__InstanceOverrideConvergenceTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class InstanceOverrideConvergenceScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_ioc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "InstanceOverrideConvergenceScene.cs");
        _sidecarPath = Path.Combine(_dir, "InstanceOverrideConvergenceScene.sbmap.json");

        if (!AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "Fixtures_InstanceOverrideConvergence");
        }

        var source = new GameObject("IOC_Sprite");
        source.AddComponent<SpriteRenderer>();
        PrefabUtility.SaveAsPrefabAsset(source, PrefabPath);
        UnityEngine.Object.DestroyImmediate(source);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        AssetDatabase.DeleteAsset(PrefabPath);
        if (AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.DeleteAsset(FixturesDir);
        }

        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }
    }

    [Test]
    public void Build_AuthoredOverrideOnExcludedSpriteRendererSize_SecondBuildEmitsZeroOpsForIt()
    {
        File.WriteAllText(_builderPath, Source(
            $"        scene.Instance(\"{PrefabPath}\").Override(e => e.Set<UnityEngine.SpriteRenderer>(\"m_Size\", new UnityEngine.Vector2(3f, 3f)));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();

        var first = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(first.Diagnostics,
            "first build refused: " + string.Join(";", first.Diagnostics.Select(d => d.Message)));

        var second = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(second.Diagnostics,
            "second build refused: " + string.Join(";", second.Diagnostics.Select(d => d.Message)));

        Assert.AreEqual(0, second.PlanOpCount,
            "second build against the unchanged scene was not a 0-op converged build -- the "
            + "excluded-field instance override is being re-emitted every build instead of "
            + "converging to a fixed point");
    }
}
