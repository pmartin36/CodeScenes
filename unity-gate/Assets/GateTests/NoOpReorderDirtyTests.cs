using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Plan;

// A struct field's member ORDER carries no value on its own: SceneBuilder.Core.Model.FieldMap
// compares Nested values by key, not by position, so rewriting a converged builder source with the
// same struct members in a different order is a no-op at the plan level. `Scene.isDirty` does not
// track a SerializedObject write in batchmode, so it cannot carry that verdict; the instrument here
// is EditorUtility.IsDirty on the target component, backed by a changed-value positive control that
// proves the instrument actually moves.
public class NoOpReorderDirtyTests
{
    private const string ScenePath = "Assets/GateTests/__NoOpReorderDirtyTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class NoOpReorderScene : ISceneDefinition
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

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_nord_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "NoOpReorderScene.cs");
        _sidecarPath = Path.Combine(_dir, "NoOpReorderScene.sbmap.json");
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

    [Test]
    public void Rebuild_AfterValuePreservingStructMemberReorder_AppliesZeroOpsAndLeavesTheComponentClean()
    {
        File.WriteAllText(_builderPath, Source(
            "        var enemy = scene.Add(\"Enemy\");\n" +
            "        enemy.Component<GateFixtures.GateSampleBehaviour>(c => c.Set(\"Damage\", new GateFixtures.Damage { amount = 5f, kind = 1 }));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        // Fixture control: an immediate rebuild of the just-converged source, member order
        // unchanged, is already zero ops — establishes the baseline the reorder rebuild is
        // compared against below.
        var convergedRebuild = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.AreEqual(0, convergedRebuild.PlanOpCount, "A freshly converged rebuild must already apply zero ops.");

        var reordered = Source(
            "        var enemy = scene.Add(\"Enemy\");\n" +
            "        enemy.Component<GateFixtures.GateSampleBehaviour>(c => c.Set(\"Damage\", new GateFixtures.Damage { kind = 1, amount = 5f }));");
        File.WriteAllText(_builderPath, reordered);

        var reorderRebuild = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        Assert.AreEqual(0, reorderRebuild.PlanOpCount,
            "A value-preserving struct member reorder must apply zero plan ops.");

        var enemy = FindRoot(EditorSceneManager.GetActiveScene(), "Enemy");
        Assert.IsNotNull(enemy, "Enemy was not present after the reorder rebuild");
        var behaviour = enemy.GetComponent<GateFixtures.GateSampleBehaviour>();
        Assert.IsNotNull(behaviour, "GateSampleBehaviour was not present after the reorder rebuild");
        Assert.IsFalse(EditorUtility.IsDirty(behaviour),
            "A zero-op reorder rebuild left the target component dirty.");
        Assert.AreEqual(5f, behaviour.Damage.amount, "Reorder rebuild changed the live amount value.");
        Assert.AreEqual(1, behaviour.Damage.kind, "Reorder rebuild changed the live kind value.");
        Assert.AreEqual(reordered, File.ReadAllText(_builderPath),
            "The reorder rebuild rewrote the builder source it was given.");
    }

    [Test]
    public void PlanExecutorSetField_CarryingTheLiveValue_LeavesTheComponentClean_ChangedValueDirtiesIt()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();

        var go = new GameObject("Enemy");
        var behaviour = go.AddComponent<GateFixtures.GateSampleBehaviour>();
        var seed = new SerializedObject(behaviour);
        seed.FindProperty("Health").intValue = 7;
        seed.ApplyModifiedProperties();

        EditorSceneManager.SaveScene(scene, ScenePath);
        Assert.IsFalse(EditorUtility.IsDirty(behaviour), "Component must be clean immediately after the scene save.");

        var goid = GlobalObjectId.GetGlobalObjectIdSlow(go).ToString();
        var componentGoid = GlobalObjectId.GetGlobalObjectIdSlow(behaviour).ToString();
        var map = new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry { LogicalId = "Enemy", GlobalObjectId = goid, Kind = "GameObject" },
                new IdentityMapEntry
                {
                    LogicalId = "Enemy/GateFixtures.GateSampleBehaviour#0", GlobalObjectId = componentGoid,
                    Kind = "Component", ComponentType = "GateFixtures.GateSampleBehaviour", ParentLogicalId = "Enemy",
                },
            },
        };

        var samePlan = new Plan
        {
            Ops = new PlanOp[]
            {
                new SetField
                {
                    LogicalId = "Enemy/GateFixtures.GateSampleBehaviour#0", Path = "Health",
                    Value = ValueNode.Primitive.Int(7),
                },
            },
        };
        PlanExecutor.Execute(samePlan, map, scene);

        Assert.AreEqual(7, behaviour.Health, "A SetField carrying the live value must not change it.");
        Assert.IsFalse(EditorUtility.IsDirty(behaviour),
            "A SetField carrying the same value the component already holds dirtied it.");

        // Positive control: the SAME op shape, carrying a DIFFERENT value, must both change the
        // value and dirty the component — without this the assertion above proves nothing about
        // the instrument.
        var changedPlan = new Plan
        {
            Ops = new PlanOp[]
            {
                new SetField
                {
                    LogicalId = "Enemy/GateFixtures.GateSampleBehaviour#0", Path = "Health",
                    Value = ValueNode.Primitive.Int(9),
                },
            },
        };
        PlanExecutor.Execute(changedPlan, map, scene);

        Assert.AreEqual(9, behaviour.Health, "A SetField carrying a different value did not change it.");
        Assert.IsTrue(EditorUtility.IsDirty(behaviour),
            "A SetField that genuinely changed the value did not dirty the component.");
    }
}
