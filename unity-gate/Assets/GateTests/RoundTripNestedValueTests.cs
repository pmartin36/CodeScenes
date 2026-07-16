using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;

// M-Nested b2-t2: the headline bidirectional round-trip for [Serializable] struct/class fields at the
// Unity boundary. Proves, against a LIVE editor scene: (#1) code->scene materializes the real struct
// via WriteProperty's Nested arm; (#2) scene->code emits a compilable, fully-qualified typed
// initializer (the exact shape that shipped as the uncompilable "new object { ... }" before b1-t1's
// fix); (#3) a second sync of an unchanged scene is a genuine no-op (FQN byte-stability). Structured
// exactly like RoundTripComponentTests.cs (temp builder .cs + temp sidecar in a system temp dir).
public class RoundTripNestedValueTests
{
    private const string ScenePath = "Assets/GateTests/__RoundTripNestedValueTemp.unity";

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

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_rtnv_" + System.Guid.NewGuid().ToString("N"));
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

    // #1 headline (code->scene): authoring a nested-struct object initializer via .Set(...) materializes
    // the REAL struct on the live component — the actual serialized field values, not a label.
    [Test]
    public void CodeToScene_AuthoredNestedStruct_MaterializesSerializedValues()
    {
        File.WriteAllText(_builderPath, Source(
            "        var enemy = scene.Add(\"Enemy\");\n" +
            "        enemy.Component<GateFixtures.GateSampleBehaviour>(c => c.Set(\"Damage\", new GateFixtures.Damage { amount = 5f, kind = 1 }));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var enemy = FindRoot(EditorSceneManager.GetActiveScene(), "Enemy");
        Assert.IsNotNull(enemy, "Enemy was not created by SceneBuilderBuild.Run");
        var behaviour = enemy.GetComponent<GateFixtures.GateSampleBehaviour>();
        Assert.IsNotNull(behaviour, "Authored GateSampleBehaviour was not materialized on Enemy");
        Assert.AreEqual(5f, behaviour.Damage.amount, "Authored nested struct amount=5 did not materialize");
        Assert.AreEqual(1, behaviour.Damage.kind, "Authored nested struct kind=1 did not materialize");
    }

    // #2 the bug fix (scene->code): editing the live struct and syncing emits a fully-qualified typed
    // initializer that COMPILES (inherited by SyncAndAssertCompiles) — never the uncompilable "new object".
    [Test]
    public void SceneToCode_ChangedNestedStruct_EmitsFullyQualifiedInitializerThatCompiles()
    {
        // Phase 1: build the object alone so it is mapped in the sidecar.
        File.WriteAllText(_builderPath, Source("        var enemy = scene.Add(\"Enemy\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        // Phase 2: author the component with a seed value and rebuild (maps the component + field).
        File.WriteAllText(_builderPath, Source(
            "        var enemy = scene.Add(\"Enemy\");\n" +
            "        enemy.Component<GateFixtures.GateSampleBehaviour>(c => c.Set(\"Damage\", new GateFixtures.Damage { amount = 1f, kind = 2 }));"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var enemy = FindRoot(EditorSceneManager.GetActiveScene(), "Enemy");
        Assert.IsNotNull(enemy, "Enemy was not created by SceneBuilderBuild.Run");
        var behaviour = enemy.GetComponent<GateFixtures.GateSampleBehaviour>();
        Assert.IsNotNull(behaviour, "Authored GateSampleBehaviour was not materialized on Enemy");
        Assert.AreEqual(1f, behaviour.Damage.amount, "Seed nested struct amount=1 did not materialize");

        // The user edits the live struct field directly in the scene.
        behaviour.Damage = new GateFixtures.Damage { amount = 5f, kind = 1 };

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite an edited nested struct field");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("new GateFixtures.Damage", rewritten,
            "Builder source did not emit the fully-qualified typed initializer.\n" + rewritten);
        StringAssert.DoesNotContain("new object", rewritten,
            "Builder source emitted the uncompilable \"new object\" shape.\n" + rewritten);
    }

    // #3 stability (scene->code): a second sync of the unedited, converged scene is a genuine no-op —
    // proves the FQN emit form is byte-stable against the adapter's reflection-FullName read.
    [Test]
    public void SceneToCode_ResyncUnchangedNestedStruct_IsANoOp()
    {
        // Phase 1: build the object alone so it is mapped in the sidecar.
        File.WriteAllText(_builderPath, Source("        var enemy = scene.Add(\"Enemy\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        // Phase 2: author the component with a seed value and rebuild (maps the component + field).
        File.WriteAllText(_builderPath, Source(
            "        var enemy = scene.Add(\"Enemy\");\n" +
            "        enemy.Component<GateFixtures.GateSampleBehaviour>(c => c.Set(\"Damage\", new GateFixtures.Damage { amount = 1f, kind = 2 }));"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var enemy = FindRoot(EditorSceneManager.GetActiveScene(), "Enemy");
        Assert.IsNotNull(enemy, "Enemy was not created by SceneBuilderBuild.Run");
        var behaviour = enemy.GetComponent<GateFixtures.GateSampleBehaviour>();
        behaviour.Damage = new GateFixtures.Damage { amount = 5f, kind = 1 };

        EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var second = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        Assert.IsFalse(second.Changed, "NOT CONVERGED: re-syncing an unchanged nested struct field reported Changed=true.");
        Assert.AreEqual(0, second.PatchEdits,
            "NOT CONVERGED: the no-op re-sync's reconcile produced " + second.PatchEdits + " patch edit(s).");
        Assert.AreEqual(0, second.AddedEntries, "No-op re-sync added sidecar entries");
        Assert.AreEqual(0, second.RemovedEntries, "No-op re-sync removed sidecar entries");
    }

    // #4 nested-in-nested (M-Nested b2-t3): a struct field (Outer) whose OWN field (inner) is itself a
    // serializable struct (Inner) must emit a RECURSIVELY typed initializer — proves the Nested emit/parse
    // machinery composes with itself, not just one level deep.
    [Test]
    public void SceneToCode_NestedInNestedStruct_EmitsRecursiveTypedInitializerAndNoOpsOnResync()
    {
        // Phase 1: build the object alone so it is mapped in the sidecar.
        File.WriteAllText(_builderPath, Source("        var enemy = scene.Add(\"Enemy\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        // Phase 2: author the component with a seed value and rebuild (maps the component + field).
        File.WriteAllText(_builderPath, Source(
            "        var enemy = scene.Add(\"Enemy\");\n" +
            "        enemy.Component<GateFixtures.GateSampleBehaviour>(c => c.Set(\"Outer\", new GateFixtures.Outer { inner = new GateFixtures.Inner { x = 1f }, y = 2f }));"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var enemy = FindRoot(EditorSceneManager.GetActiveScene(), "Enemy");
        Assert.IsNotNull(enemy, "Enemy was not created by SceneBuilderBuild.Run");
        var behaviour = enemy.GetComponent<GateFixtures.GateSampleBehaviour>();
        Assert.IsNotNull(behaviour, "Authored GateSampleBehaviour was not materialized on Enemy");
        Assert.AreEqual(1f, behaviour.Outer.inner.x, "Seed nested-in-nested inner.x=1 did not materialize");

        // The user edits the live, doubly-nested struct field directly in the scene.
        behaviour.Outer = new GateFixtures.Outer { inner = new GateFixtures.Inner { x = 3f }, y = 7f };

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite an edited nested-in-nested struct field");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("new GateFixtures.Outer { inner = new GateFixtures.Inner", rewritten,
            "Builder source did not emit the recursively-typed nested-in-nested initializer.\n" + rewritten);
        StringAssert.DoesNotContain("new object", rewritten,
            "Builder source emitted the uncompilable \"new object\" shape.\n" + rewritten);

        var second = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(second.Changed, "NOT CONVERGED: re-syncing an unchanged nested-in-nested field reported Changed=true.");
        Assert.AreEqual(0, second.PatchEdits,
            "NOT CONVERGED: the no-op re-sync's reconcile produced " + second.PatchEdits + " patch edit(s).");
    }

    // #5 list of nested (M-Nested b2-t3): an array field (Volley: Damage[]) built with two struct elements
    // must emit `new[] { new GateFixtures.Damage { ... }, new GateFixtures.Damage { ... } }` — the array's
    // element type inferred from the first Nested item, not a bare "new object[]".
    [Test]
    public void SceneToCode_ListOfNestedStructs_EmitsTypedArrayThatCompilesAndNoOpsOnResync()
    {
        // Phase 1: build the object alone so it is mapped in the sidecar.
        File.WriteAllText(_builderPath, Source("        var enemy = scene.Add(\"Enemy\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        // Phase 2: author the component with a seed two-element list and rebuild (maps the field).
        File.WriteAllText(_builderPath, Source(
            "        var enemy = scene.Add(\"Enemy\");\n" +
            "        enemy.Component<GateFixtures.GateSampleBehaviour>(c => c.Set(\"Volley\", new[] { new GateFixtures.Damage { amount = 1f, kind = 1 }, new GateFixtures.Damage { amount = 2f, kind = 2 } }));"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var enemy = FindRoot(EditorSceneManager.GetActiveScene(), "Enemy");
        Assert.IsNotNull(enemy, "Enemy was not created by SceneBuilderBuild.Run");
        var behaviour = enemy.GetComponent<GateFixtures.GateSampleBehaviour>();
        Assert.IsNotNull(behaviour, "Authored GateSampleBehaviour was not materialized on Enemy");
        Assert.AreEqual(2, behaviour.Volley.Length, "Seed two-element Volley did not materialize");
        Assert.AreEqual(1f, behaviour.Volley[0].amount, "Seed Volley[0].amount=1 did not materialize");
        Assert.AreEqual(2f, behaviour.Volley[1].amount, "Seed Volley[1].amount=2 did not materialize");

        // The user edits the live array elements directly in the scene.
        behaviour.Volley = new[]
        {
            new GateFixtures.Damage { amount = 5f, kind = 1 },
            new GateFixtures.Damage { amount = 6f, kind = 2 },
        };

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite an edited Damage[] list field");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("new[] { new GateFixtures.Damage", rewritten,
            "Builder source did not emit the typed array of nested structs.\n" + rewritten);
        StringAssert.DoesNotContain("new object", rewritten,
            "Builder source emitted the uncompilable \"new object\" shape.\n" + rewritten);

        var second = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(second.Changed, "NOT CONVERGED: re-syncing an unchanged Damage[] list field reported Changed=true.");
        Assert.AreEqual(0, second.PatchEdits,
            "NOT CONVERGED: the no-op re-sync's reconcile produced " + second.PatchEdits + " patch edit(s).");
    }

    // #6 the read guard (M-Nested b2-t4): a generic serializable field (Pair<int>) cannot be resolved to a
    // concrete non-generic managed type, so ReadNested's guard must read it as Unsupported and the
    // component-level skip machinery must drop it entirely — never emit a backtick-arity type
    // (e.g. `` Pair`1 ``) or "new object", and the rest of the component still emits/compiles.
    [Test]
    public void SceneToCode_GenericFieldUnresolvable_IsSkippedAndStillCompiles()
    {
        // Phase 1: build the object alone so it is mapped in the sidecar.
        File.WriteAllText(_builderPath, Source("        var enemy = scene.Add(\"Enemy\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var enemy = FindRoot(EditorSceneManager.GetActiveScene(), "Enemy");
        Assert.IsNotNull(enemy, "Enemy was not created by SceneBuilderBuild.Run");
        var behaviour = enemy.AddComponent<GateFixtures.GateSampleBehaviour>();

        // Assign a NON-default value to the generic field via SerializedObject, so the test is
        // unambiguously about a generic field carrying real data (the guard fires even for a
        // default Pair, but this makes non-vacuity explicit).
        var so = new SerializedObject(behaviour);
        so.FindProperty("Pair.value").intValue = 7;
        so.ApplyModifiedProperties();

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite the component attach");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains(".Component<GateFixtures.GateSampleBehaviour>", rewritten,
            "Builder source did not emit the component attach.\n" + rewritten);
        StringAssert.DoesNotContain("`", rewritten,
            "Builder source emitted a backtick-arity type for the unresolvable generic field.\n" + rewritten);
        StringAssert.DoesNotContain("new object", rewritten,
            "Builder source emitted the uncompilable \"new object\" shape.\n" + rewritten);
        StringAssert.DoesNotContain("new GateFixtures.Pair", rewritten,
            "Builder source emitted the generic field as a typed Nested initializer instead of skipping it.\n" + rewritten);
        StringAssert.DoesNotContain(".Set(\"Pair\"", rewritten,
            "Builder source emitted a .Set(\"Pair\", ...) call for an unresolvable generic field.\n" + rewritten);
    }
}
