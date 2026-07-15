using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;
using SceneBuilder.Core.Model;
using GateFixtures;

// M4 MonoScript-GUID component identity round-trip gate tests. A user MonoBehaviour
// (GateFixtures.GateSampleBehaviour, backed by a real MonoScript asset) must:
//   * (scene->code) have its snapshot TypeRef anchored to the MonoScript's asset GUID, and appear as
//     a .Component<GateFixtures.GateSampleBehaviour> attach in the rewritten builder source;
//   * (code->scene) materialize as the real component with its serialized field applied;
//   * resolve by GUID even when the authored full name no longer matches (namespace/assembly churn).
// Drives the real adapter APIs (SceneBuilderBuild.Run / SceneBuilderSync.Run / SceneSnapshotReader /
// ComponentTypeResolver) against a live EditMode scene. Temp builder + sidecar in a system temp dir.
public class RoundTripMonoScriptTests
{
    private const string ScenePath = "Assets/GateTests/__RoundTripMonoScriptTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class RoundTripScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    private static GameObject FindRoot(Scene scene, string name)
    {
        return scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);
    }

    // The MonoScript asset GUID for GateSampleBehaviour, computed independently of the production
    // read path (via the AssetDatabase script index) so the test is a real cross-check.
    private static string ExpectedScriptGuid()
    {
        var candidate = AssetDatabase.FindAssets("t:MonoScript GateSampleBehaviour")
            .Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(p => p.EndsWith("GateSampleBehaviour.cs"));
        Assert.IsFalse(string.IsNullOrEmpty(candidate), "GateSampleBehaviour MonoScript asset not found");
        return AssetDatabase.AssetPathToGUID(candidate);
    }

    private static ComponentData FindComponent(SnapshotNode node, string typeFullName)
    {
        var hit = node.Components.FirstOrDefault(c => c.Type.FullName == typeFullName);
        if (hit != null)
        {
            return hit;
        }

        foreach (var child in node.Children)
        {
            var nested = FindComponent(child, typeFullName);
            if (nested != null)
            {
                return nested;
            }
        }

        return null;
    }

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_rtms_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "RoundTripScene.cs");
        _sidecarPath = Path.Combine(_dir, "RoundTripScene.sbmap.json");
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

    // scene->code: adding the custom GateSampleBehaviour (Health=7) to a managed object in the scene
    // anchors its snapshot TypeRef to the MonoScript GUID (the surface that exposes the anchor) AND
    // rewrites the builder source with a .Component<GateFixtures.GateSampleBehaviour> attach.
    [Test]
    public void SceneToCode_AddsCustomMonoBehaviour_AnchorsTypeRefToMonoScriptGuidAndAttaches()
    {
        File.WriteAllText(_builderPath, Source("        var hero = scene.Add(\"Hero\");"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var hero = FindRoot(EditorSceneManager.GetActiveScene(), "Hero");
        Assert.IsNotNull(hero, "Hero was not created by SceneBuilderBuild.Run");
        var behaviour = hero.AddComponent<GateSampleBehaviour>();
        behaviour.Health = 7;

        // The MonoScript-GUID anchor is exposed on the snapshot TypeRef the adapter reads.
        var snapshot = SceneSnapshotReader.Read(EditorSceneManager.GetActiveScene());
        var typeName = typeof(GateSampleBehaviour).FullName;
        var component = FindComponent(snapshot.Roots.First(n => n.Name == "Hero"), typeName);
        Assert.IsNotNull(component, "GateSampleBehaviour was not read into the scene snapshot");
        Assert.AreEqual(ExpectedScriptGuid(), component.Type.MonoScriptGuid,
            "Snapshot TypeRef for the user MonoBehaviour was not anchored to its MonoScript asset GUID");
        Assert.AreEqual(7, System.Convert.ToInt32(((ValueNode.Primitive)component.Fields["Health"]).Value),
            "Health=7 did not survive into the snapshot");

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite an added custom MonoBehaviour");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains(".Component<" + typeName + ">", rewritten,
            "Builder source did not gain a .Component<GateFixtures.GateSampleBehaviour> attach.\n" + rewritten);
    }

    // code->scene: authoring the custom component with a typed field setter materializes the REAL
    // GateSampleBehaviour on the object with Health==7 (proves GUID-or-name resolution + field set).
    [Test]
    public void CodeToScene_AuthoredCustomMonoBehaviourAndField_MaterializesWithValue()
    {
        var typeName = typeof(GateSampleBehaviour).FullName;

        // Phase 1: build the object alone so it is mapped in the sidecar.
        File.WriteAllText(_builderPath, Source("        var hero = scene.Add(\"Hero\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        // Phase 2: author the custom component + typed field setter onto the mapped object and rebuild.
        File.WriteAllText(_builderPath, Source(
            "        var hero = scene.Add(\"Hero\");\n" +
            "        hero.Component<" + typeName + ">(c => c.Set(r => r.Health, 7));"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var hero = FindRoot(EditorSceneManager.GetActiveScene(), "Hero");
        Assert.IsNotNull(hero, "Hero was not created by SceneBuilderBuild.Run");
        var behaviour = hero.GetComponent<GateSampleBehaviour>();
        Assert.IsNotNull(behaviour, "Authored GateSampleBehaviour was not materialized on Hero");
        Assert.AreEqual(7, behaviour.Health, "Authored typed-setter Health=7 did not materialize on the real component");
    }

    // ComponentTypeResolver resolves a MonoScript-GUID-anchored TypeRef by its GUID even when the
    // carried full name no longer matches the type (assembly/namespace churn) — the whole point of the
    // GUID anchor. The plain full-name path could never resolve "Stale.Renamed.Name".
    [Test]
    public void Resolver_GuidAnchoredTypeRef_ResolvesAcrossNameChurn()
    {
        var guid = ExpectedScriptGuid();
        var churned = new TypeRef("Stale.Renamed.Name", "SomeOtherAssembly", guid);

        var resolved = ComponentTypeResolver.Resolve(churned);

        Assert.AreEqual(typeof(GateSampleBehaviour), resolved,
            "GUID-anchored TypeRef did not resolve to GateSampleBehaviour across a changed full name");
    }
}
