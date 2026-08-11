using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Serialization;

// Bidirectional round-trip stability + determinism audit against the real editor boundary
// (GlobalObjectId is a Unity-only concept, invisible to the headless POCO fixtures). Drives
// a transform-only two-root scene through repeated move -> Sync -> Build cycles and asserts
// identity never churns, the sidecar never drifts, and a settled loop converges to zero edits.
public class RoundTripStabilityAuditTests
{
    private const string ScenePath = "Assets/GateTests/__RoundTripStabilityAuditTemp.unity";
    private const string TargetName = "StabilityTarget";
    private const string StaticName = "StabilityAnchor";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string SourceAt(float x, float y, float z) => $@"
using SceneBuilder.Authoring;
public class StabilityScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
        scene.Add(""{TargetName}"").Transform(pos: ({x}f, {y}f, {z}f));
        scene.Add(""{StaticName}"").Transform(pos: (0f, 0f, 0f));
    }}
}}";

    private static GameObject FindRoot(Scene scene, string name) =>
        scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_rtsa_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "StabilityScene.cs");
        _sidecarPath = Path.Combine(_dir, "StabilityScene.sbmap.json");
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
    }

    private static IdentityMapEntry TargetEntry(string sidecarPath) =>
        IdentityMapJson.Deserialize(File.ReadAllText(sidecarPath))
            .Entries.First(e => e.Kind == "GameObject" && e.Name == TargetName);

    [Test]
    public void RoundTrip_TenCycles_IdentityStable_And_NoDiffNoise()
    {
        File.WriteAllText(_builderPath, SourceAt(0f, 0f, 0f));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var seedSidecarBytes = File.ReadAllText(_sidecarPath);
        var seed = TargetEntry(_sidecarPath);
        var seedLogicalId = seed.LogicalId;
        var seedGlobalObjectId = seed.GlobalObjectId;
        Assert.IsNotEmpty(seedLogicalId, "Seed build produced an empty LogicalId for the target.");
        Assert.IsNotEmpty(seedGlobalObjectId, "Seed build produced an empty GlobalObjectId for the target.");

        for (int i = 1; i <= 10; i++)
        {
            var live = EditorSceneManager.GetActiveScene();
            var target = FindRoot(live, TargetName);
            Assert.IsNotNull(target, $"Target object missing from the live scene at cycle {i}.");
            target.transform.position = new Vector3(i, i, i);

            SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
            SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

            var entry = TargetEntry(_sidecarPath);
            Assert.AreEqual(seedLogicalId, entry.LogicalId,
                $"LogicalId churned on cycle {i} — a pure transform move must never re-derive identity.");
            Assert.AreEqual(seedGlobalObjectId, entry.GlobalObjectId,
                $"GlobalObjectId churned on cycle {i} — a pure transform move must never re-derive identity.");
        }

        Assert.AreEqual(seedSidecarBytes, File.ReadAllText(_sidecarPath),
            "Sidecar drifted from its seed bytes after 10 pure-transform-move cycles.");

        var finalSource = File.ReadAllText(_builderPath);
        StringAssert.Contains("(10f, 10f, 10f)", finalSource,
            "Final builder source does not reflect the last intended position.\n" + finalSource);
        Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(finalSource, $"Add\\(\"{TargetName}\"\\)").Count,
            "Target statement was duplicated across the 10 cycles — accumulated statement noise.\n" + finalSource);
        Assert.AreEqual(2, System.Text.RegularExpressions.Regex.Matches(finalSource, "Transform\\(pos:").Count,
            "Unexpected number of Transform(pos:) statements after 10 cycles — accumulated statement noise.\n" + finalSource);

        var settled = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.AreEqual(0, settled.PatchEdits,
            "A settled loop with no further mutation must reconcile to zero patch edits.");
        Assert.IsFalse(settled.Changed,
            "A settled loop with no further mutation must not report a write.");
        Assert.AreEqual(finalSource, File.ReadAllText(_builderPath),
            "Idempotence-tail sync rewrote the source despite PatchEdits == 0.");
        Assert.AreEqual(seedSidecarBytes, File.ReadAllText(_sidecarPath),
            "Idempotence-tail sync rewrote the sidecar despite no scene change.");
    }

    [Test]
    public void RebuildSameScene_ByteIdenticalSidecar()
    {
        File.WriteAllText(_builderPath, SourceAt(1f, 2f, 3f));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        var firstSidecar = File.ReadAllText(_sidecarPath);

        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        var secondSidecar = File.ReadAllText(_sidecarPath);

        Assert.AreEqual(firstSidecar, secondSidecar,
            "Rebuilding the same scene against the persisted sidecar must rewrite it byte-identically.");
    }
}
