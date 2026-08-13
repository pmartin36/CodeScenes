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

// The adapter WRITE path for an ObjectRef/AssetRef held INSIDE a [SerializeReference] instance's
// own fields (specs/10-m9-serializereference.md, primary-risk depth): a managed instance carries its
// refs inline in SetManagedReference.Fields rather than as separate SetReference/SetAssetRef ops, so
// the writer must resolve them itself, one level and recursively through a nested managed reference,
// through the SAME resolvers the top-level SetReference/SetAssetRef ops use.
public class ManagedReferenceNestedRefTests
{
    private const string FixturesDir = "Assets/GateTests/Fixtures";
    private const string AssetPath = FixturesDir + "/ManagedRefAsset.mat";
    private const string RoundTripScenePath = "Assets/GateTests/__ManagedReferenceNestedRefRoundTripTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string RoundTripSource(string body) => $@"
using SceneBuilder.Authoring;
public class ManagedReferenceNestedRefRoundTripScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    private static GameObject FindRoot(Scene scene, string name) =>
        scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);

    [SetUp]
    public void SetUp()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        if (!AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "Fixtures");
        }

        AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), AssetPath);
        AssetDatabase.SaveAssets();

        _dir = Path.Combine(Path.GetTempPath(), "sb_mrnr_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "ManagedReferenceNestedRefRoundTripScene.cs");
        _sidecarPath = Path.Combine(_dir, "ManagedReferenceNestedRefRoundTripScene.sbmap.json");
    }

    [TearDown]
    public void TearDown()
    {
        AssetDatabase.DeleteAsset(AssetPath);
        if (AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.DeleteAsset(FixturesDir);
        }

        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        if (File.Exists(RoundTripScenePath))
        {
            AssetDatabase.DeleteAsset(RoundTripScenePath);
        }
    }

    private static FieldMap Fields(params (string key, ValueNode value)[] entries) =>
        new(System.Array.ConvertAll(entries,
            e => new System.Collections.Generic.KeyValuePair<string, ValueNode>(e.key, e.value)));

    [Test]
    public void SetManagedReference_ObjectRefChildField_ResolvesToLiveTarget()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new CreateObject { LogicalId = "Host", Name = "Host" },
                new AddComponent { LogicalId = "Host/AiBrain#0", Type = new TypeRef("AiBrain") },
                new CreateObject { LogicalId = "Target", Name = "Target" },
                new SetManagedReference
                {
                    LogicalId = "Host/AiBrain#0",
                    Path = "strategy",
                    ConcreteType = new TypeRef("Aggressive"),
                    Fields = Fields(
                        ("range", ValueNode.Primitive.Float(5f)),
                        ("target", new ValueNode.ObjectRef("Target"))),
                },
            },
        };

        PlanExecutor.Execute(plan, new IdentityMap(), scene);

        var brain = GameObject.Find("Host").GetComponent<AiBrain>();
        var strategy = (Aggressive)new SerializedObject(brain).FindProperty("strategy").managedReferenceValue;
        Assert.AreEqual(5f, strategy.range);
        Assert.AreEqual(GameObject.Find("Target"), strategy.target,
            "an ObjectRef held inside a managed instance's own field must resolve to the live target");
    }

    [Test]
    public void SetManagedReference_AssetRefChildField_ResolvesToLiveAsset()
    {
        var scene = EditorSceneManager.GetActiveScene();
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
            AssetDatabase.LoadAssetAtPath<Material>(AssetPath), out var guid, out var fileId);

        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new CreateObject { LogicalId = "Host", Name = "Host" },
                new AddComponent { LogicalId = "Host/AiBrain#0", Type = new TypeRef("AiBrain") },
                new SetManagedReference
                {
                    LogicalId = "Host/AiBrain#0",
                    Path = "strategy",
                    ConcreteType = new TypeRef("Aggressive"),
                    Fields = Fields(
                        ("someAsset", new ValueNode.AssetRef(new AssetRef { Guid = guid, FileId = fileId }))),
                },
            },
        };

        PlanExecutor.Execute(plan, new IdentityMap(), scene);

        var brain = GameObject.Find("Host").GetComponent<AiBrain>();
        var strategy = (Aggressive)new SerializedObject(brain).FindProperty("strategy").managedReferenceValue;
        Assert.AreEqual(AssetDatabase.LoadAssetAtPath<Material>(AssetPath), strategy.someAsset,
            "an AssetRef held inside a managed instance's own field must resolve to the live asset");
    }

    [Test]
    public void SetManagedReference_ObjectRefInsideNestedManagedRef_ResolvesRecursively()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new CreateObject { LogicalId = "Host", Name = "Host" },
                new AddComponent { LogicalId = "Host/AiBrain#0", Type = new TypeRef("AiBrain") },
                new CreateObject { LogicalId = "Target", Name = "Target" },
                new SetManagedReference
                {
                    LogicalId = "Host/AiBrain#0",
                    Path = "strategy",
                    ConcreteType = new TypeRef("Composite"),
                    Fields = Fields(
                        ("primary", new ValueNode.ManagedReference(new TypeRef("Aggressive"),
                            Fields(("target", new ValueNode.ObjectRef("Target")))))),
                },
            },
        };

        PlanExecutor.Execute(plan, new IdentityMap(), scene);

        var brain = GameObject.Find("Host").GetComponent<AiBrain>();
        var composite = (Composite)new SerializedObject(brain).FindProperty("strategy").managedReferenceValue;
        var primary = (Aggressive)composite.primary;
        Assert.AreEqual(GameObject.Find("Target"), primary.target,
            "an ObjectRef inside a NESTED managed reference's fields must resolve recursively");
    }

    // scene->code: an ObjectRef held inside a [SerializeReference] instance's own field, rewired live
    // in the editor, patches the authored `new Aggressive { ... }` span to the new handle and converges.
    [Test]
    public void RoundTrip_RewireObjectRefInsideManagedRef_PatchesSourceAndConverges()
    {
        File.WriteAllText(_builderPath, RoundTripSource(
            "        var host = scene.Add(\"Host\");\n" +
            "        var doorA = scene.Add(\"DoorA\");\n" +
            "        var doorB = scene.Add(\"DoorB\");\n" +
            "        host.Component<AiBrain>(c => c.SetRef(x => x.strategy, new Aggressive { target = doorA }));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, RoundTripScenePath, _sidecarPath, scene);

        var host = FindRoot(EditorSceneManager.GetActiveScene(), "Host");
        var doorA = FindRoot(EditorSceneManager.GetActiveScene(), "DoorA");
        var doorB = FindRoot(EditorSceneManager.GetActiveScene(), "DoorB");
        Assert.IsNotNull(host, "Host was not created by SceneBuilderBuild.Run");
        Assert.IsNotNull(doorA, "DoorA was not created by SceneBuilderBuild.Run");
        Assert.IsNotNull(doorB, "DoorB was not created by SceneBuilderBuild.Run");

        var brain = host.GetComponent<AiBrain>();
        var strategyProp = new SerializedObject(brain).FindProperty("strategy");
        Assert.AreEqual(doorA, ((Aggressive)strategyProp.managedReferenceValue).target,
            "Materialize did not resolve the authored nested target to DoorA");

        strategyProp.serializedObject.Update();
        var targetProp = strategyProp.FindPropertyRelative("target");
        targetProp.objectReferenceValue = doorB;
        targetProp.serializedObject.ApplyModifiedPropertiesWithoutUndo();

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite rewiring the nested managed-ref target live in the scene");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("target = doorB", rewritten,
            "Builder source did not re-render the nested `new Aggressive { ... }` span with the new target.\n" + rewritten);
        StringAssert.DoesNotContain("target = doorA", rewritten,
            "Builder source still carries the old nested target.\n" + rewritten);

        var second = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(second.Changed,
            "NOT CONVERGED: a Sync immediately after the nested-ref patch, with no further scene change, reported Changed=true.");
        Assert.AreEqual(0, second.PatchEdits,
            "NOT CONVERGED: the no-op re-sync's reconcile produced " + second.PatchEdits + " patch edit(s).");
    }
}
