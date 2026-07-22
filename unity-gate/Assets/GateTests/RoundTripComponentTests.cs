using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;

// M3 (components + serialized fields) bidirectional round-trip gate tests. Each test DRIVES a real
// change on one side (a live EditMode scene edit, or an authored builder-source edit) through the
// programmatic Build/Sync APIs (SceneBuilderBuild.Run / SceneBuilderSync.Run) and ASSERTS propagation
// on the other side. Follows RoundTripTransformTests / RoundTripStructuralTests exactly: a temp
// builder .cs + temp sidecar in a system temp dir (never the real Assets/SceneBuilder/DemoScene.cs),
// an EmptyScene created per test, and [TearDown] cleanup. Covers component add (handled + handle-less),
// multi-component attach, field set, component remove, default-field noise filtering, unsupported/asset
// field skipping (the cube-dump regression), and a code->scene component+field materialize.
public class RoundTripComponentTests
{
    // Scene must save under Assets (EditorSceneManager.SaveScene is project-relative); the builder
    // + sidecar live in a system temp dir so Unity never tries to import/compile the builder .cs.
    private const string ScenePath = "Assets/GateTests/__RoundTripComponentTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    // Wrap a Build-body fragment in a minimal ISceneDefinition the Core Roslyn parser understands.
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
        _dir = Path.Combine(Path.GetTempPath(), "sb_rtc_" + System.Guid.NewGuid().ToString("N"));
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

    // 1. Add component (scene->code): adding a Rigidbody to a managed (handled) object in the scene
    //    appends a .Component<UnityEngine.Rigidbody>() call onto its existing handle statement.
    [Test]
    public void SceneToCode_AddComponentOnHandledObject_AppendsComponentCall()
    {
        File.WriteAllText(_builderPath, Source("        var box = scene.Add(\"Box\");"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var box = FindRoot(EditorSceneManager.GetActiveScene(), "Box");
        Assert.IsNotNull(box, "Box was not created by SceneBuilderBuild.Run");
        box.AddComponent<Rigidbody>();

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite an added component");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains(".Component<UnityEngine.Rigidbody>", rewritten,
            "Builder source did not gain a .Component<UnityEngine.Rigidbody> attach.\n" + rewritten);
    }

    // 2. Handle-less component attach (scene->code) — REGRESSION. Adding a component to a HANDLE-LESS
    //    object must NOT throw; Sync introduces a handle for the object and attaches the component onto it.
    [Test]
    public void SceneToCode_AddComponentOnHandlelessObject_IntroducesHandleAndAttaches()
    {
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Weapon\");"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var weapon = FindRoot(EditorSceneManager.GetActiveScene(), "Weapon");
        Assert.IsNotNull(weapon, "Weapon was not created by SceneBuilderBuild.Run");
        weapon.AddComponent<Rigidbody>();

        SceneBuilderSync.SyncResult result = null;
        Assert.DoesNotThrow(
            () => result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene()),
            "Sync threw when attaching a component to a handle-less object.");
        Assert.IsTrue(result.Changed, "Sync reported no change despite an added component");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("= scene.Add(\"Weapon\")", rewritten,
            "Builder source did not introduce a handle for the handle-less Weapon.\n" + rewritten);
        StringAssert.Contains(".Component<UnityEngine.Rigidbody>", rewritten,
            "Builder source did not attach the Rigidbody onto the introduced handle.\n" + rewritten);
    }

    // 3. Multiple components on a handle-less object (scene->code): both components attach under ONE
    //    introduced handle, no throw.
    [Test]
    public void SceneToCode_MultipleComponentsOnHandlelessObject_AttachBothUnderOneHandle()
    {
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Weapon\");"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var weapon = FindRoot(EditorSceneManager.GetActiveScene(), "Weapon");
        Assert.IsNotNull(weapon, "Weapon was not created by SceneBuilderBuild.Run");
        weapon.AddComponent<Rigidbody>();
        weapon.AddComponent<BoxCollider>();

        SceneBuilderSync.SyncResult result = null;
        Assert.DoesNotThrow(
            () => result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene()),
            "Sync threw when attaching multiple components to a handle-less object.");
        Assert.IsTrue(result.Changed, "Sync reported no change despite two added components");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains(".Component<UnityEngine.Rigidbody>", rewritten,
            "Builder source did not attach the Rigidbody.\n" + rewritten);
        StringAssert.Contains(".Component<UnityEngine.BoxCollider>", rewritten,
            "Builder source did not attach the BoxCollider.\n" + rewritten);
        StringAssert.Contains("= scene.Add(\"Weapon\")", rewritten,
            "Builder source did not introduce a single handle for the handle-less Weapon.\n" + rewritten);
    }

    // 4. Set field (scene->code): changing a mapped component's field value in the scene rewrites only
    //    the value argument in the existing .Set(...) call. The component is authored onto an
    //    already-built (mapped) object via a two-phase build — the realistic workflow.
    [Test]
    public void SceneToCode_ChangedFieldValue_RewritesSetArgument()
    {
        // Phase 1: build the object alone so it is mapped in the sidecar.
        File.WriteAllText(_builderPath, Source("        var box = scene.Add(\"Box\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        // Phase 2: author the component + field onto the existing object and rebuild.
        File.WriteAllText(_builderPath, Source(
            "        var box = scene.Add(\"Box\");\n" +
            "        box.Component<UnityEngine.Rigidbody>(c => c.Set(\"m_Mass\", 2f));"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var box = FindRoot(EditorSceneManager.GetActiveScene(), "Box");
        Assert.IsNotNull(box, "Box was not created by SceneBuilderBuild.Run");
        var rb = box.GetComponent<Rigidbody>();
        Assert.IsNotNull(rb, "Authored Rigidbody was not materialized on Box");
        Assert.AreEqual(2f, rb.mass, "Authored m_Mass=2 was not materialized");

        rb.mass = 5f;

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite an edited field value");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("5f", rewritten,
            "Builder source did not pick up the new mass value.\n" + rewritten);
        StringAssert.DoesNotContain("2f", rewritten,
            "Builder source still carries the old mass value.\n" + rewritten);
    }

    // 5. Remove component (scene->code): destroying a mapped component in the scene deletes its
    //    .Component<T>() statement from source. The component is authored onto an already-built
    //    (mapped) object via a two-phase build so it carries a mapped GlobalObjectId.
    [Test]
    public void SceneToCode_RemovedComponent_DeletesComponentStatement()
    {
        // Phase 1: build the object alone so it is mapped in the sidecar.
        File.WriteAllText(_builderPath, Source("        var box = scene.Add(\"Box\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        // Phase 2: author the component onto the existing object and rebuild (maps the component).
        File.WriteAllText(_builderPath, Source(
            "        var box = scene.Add(\"Box\");\n" +
            "        box.Component<UnityEngine.Rigidbody>();"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var box = FindRoot(EditorSceneManager.GetActiveScene(), "Box");
        Assert.IsNotNull(box, "Box was not created by SceneBuilderBuild.Run");
        var rb = box.GetComponent<Rigidbody>();
        Assert.IsNotNull(rb, "Authored Rigidbody was not materialized on Box");
        Object.DestroyImmediate(rb);

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite a removed component");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.DoesNotContain(".Component<UnityEngine.Rigidbody>", rewritten,
            "Builder source still declares the removed component.\n" + rewritten);
    }

    // 6. Default-field noise filtered (scene->code) — REGRESSION. A FRESH, all-default Rigidbody must
    //    attach as a bare .Component<...>() with NO default field setters dumped.
    [Test]
    public void SceneToCode_FreshDefaultComponent_EmitsNoDefaultFieldSetters()
    {
        File.WriteAllText(_builderPath, Source("        var box = scene.Add(\"Box\");"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var box = FindRoot(EditorSceneManager.GetActiveScene(), "Box");
        Assert.IsNotNull(box, "Box was not created by SceneBuilderBuild.Run");
        box.AddComponent<Rigidbody>();

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite an added component");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains(".Component<UnityEngine.Rigidbody>", rewritten,
            "Builder source did not gain the Rigidbody attach.\n" + rewritten);
        // A fresh Rigidbody equals its throwaway default on every field, so nothing survives the
        // default-filter — no .Set(...) noise, and specifically no m_Mass dump.
        StringAssert.DoesNotContain(".Set(", rewritten,
            "Builder source dumped default field setters for a fresh component.\n" + rewritten);
        StringAssert.DoesNotContain("m_Mass", rewritten,
            "Builder source dumped a default m_Mass setter for a fresh component.\n" + rewritten);
    }

    // 7. Unsupported / asset-reference fields don't emit garbage (scene->code) — REGRESSION (the
    //    cube-dump bug). A MeshRenderer carries object-reference fields (m_Materials, probe anchors,
    //    ...) — INCLUDING an object-reference LIST once a material is assigned. Those must be SKIPPED,
    //    never rendered as bare `ObjectReference` / `LayerMask` value tokens (which are uncompilable).
    [Test]
    public void SceneToCode_UnsupportedAndAssetFields_AreSkippedNotDumped()
    {
        File.WriteAllText(_builderPath, Source("        var surface = scene.Add(\"Surface\");"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var surface = FindRoot(EditorSceneManager.GetActiveScene(), "Surface");
        Assert.IsNotNull(surface, "Surface was not created by SceneBuilderBuild.Run");
        var mr = surface.AddComponent<MeshRenderer>();
        // Assign a non-default material so m_Materials is a NON-default object-reference LIST — the exact
        // shape that produced `.Set("m_Materials", new[] { ObjectReference })` garbage before the skip.
        mr.sharedMaterial = new Material(Shader.Find("Standard"));

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite an added component");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains(".Component<UnityEngine.MeshRenderer>", rewritten,
            "Builder source did not gain the MeshRenderer attach.\n" + rewritten);
        // The exact uncompilable tokens the cube-dump bug produced as VALUE arguments.
        StringAssert.DoesNotContain("ObjectReference", rewritten,
            "Builder source dumped a bare ObjectReference token (unsupported field not skipped).\n" + rewritten);
        StringAssert.DoesNotContain(", LayerMask", rewritten,
            "Builder source dumped a bare LayerMask value token (unsupported field not skipped).\n" + rewritten);
    }

    // 7b. DELETE CASCADE (scene->code) — REGRESSION. Deleting a GameObject that CARRIES components must
    //     remove its `var box = scene.Add(...)` statement AND every `box.Component<...>(...)` statement
    //     authored on that handle. Keeping the component calls while dropping the handle declaration
    //     emits `box.Component<...>()` with no `box` in scope — CS0103, a file the user cannot compile.
    //     Routed through SyncAndAssertCompiles, so the orphan is caught as a compile error, not just a
    //     string mismatch.
    [Test]
    public void SceneToCode_RemovedObjectWithComponents_RemovesAddAndComponentStatements()
    {
        // Phase 1: build the object alone so it is mapped in the sidecar.
        File.WriteAllText(_builderPath, Source("        var box = scene.Add(\"Box\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        // Phase 2: author components onto the existing object and rebuild (maps the components).
        File.WriteAllText(_builderPath, Source(
            "        var box = scene.Add(\"Box\");\n" +
            "        box.Component<UnityEngine.Rigidbody>(c => c.Set(r => r.mass, 5f));\n" +
            "        box.Component<UnityEngine.BoxCollider>();"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var box = FindRoot(EditorSceneManager.GetActiveScene(), "Box");
        Assert.IsNotNull(box, "Box was not created by SceneBuilderBuild.Run");
        Assert.IsNotNull(box.GetComponent<Rigidbody>(), "Authored Rigidbody was not materialized on Box");
        Assert.IsNotNull(box.GetComponent<BoxCollider>(), "Authored BoxCollider was not materialized on Box");

        // The user deletes the whole object in the scene.
        Object.DestroyImmediate(box);

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite a deleted GameObject");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.DoesNotContain("scene.Add(\"Box\")", rewritten,
            "Builder source still declares the deleted Box.\n" + rewritten);
        // The orphans: component statements bound to the handle that no longer exists.
        StringAssert.DoesNotContain(".Component<UnityEngine.Rigidbody>", rewritten,
            "Builder source kept the deleted object's Rigidbody statement (orphaned handle reference).\n" + rewritten);
        StringAssert.DoesNotContain(".Component<UnityEngine.BoxCollider>", rewritten,
            "Builder source kept the deleted object's BoxCollider statement (orphaned handle reference).\n" + rewritten);
        StringAssert.DoesNotContain("box", rewritten,
            "Builder source still references the removed `box` handle.\n" + rewritten);
    }

    // 7b. Same as (7), but the components are INLINE in the object's chain rather than on separate
    //     statements — the everyday authoring shape (DemoScene's beacon/floor/Crate all look like
    //     this). Deletion makes DetectRemovals emit a RemoveStatement for the owner AND one per
    //     component, and for inline components every anchor resolves to the SAME physical statement.
    //     The applier must remove that statement once and skip the now-detached per-component removes;
    //     the un-guarded path threw NullReferenceException (GetCurrentNode on an already-removed node).
    [Test]
    public void SceneToCode_RemovedObjectWithInlineComponents_RemovesWholeStatementNoCrash()
    {
        // Phase 1: build the object alone so it is mapped in the sidecar.
        File.WriteAllText(_builderPath, Source("        var box = scene.Add(\"Box\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        // Phase 2: author the components INLINE in the chain and rebuild (maps the components).
        File.WriteAllText(_builderPath, Source(
            "        var box = scene.Add(\"Box\")\n" +
            "            .Component<UnityEngine.Rigidbody>(c => c.Set(r => r.mass, 5f))\n" +
            "            .Component<UnityEngine.BoxCollider>();"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var box = FindRoot(EditorSceneManager.GetActiveScene(), "Box");
        Assert.IsNotNull(box, "Box was not created by SceneBuilderBuild.Run");
        Assert.IsNotNull(box.GetComponent<Rigidbody>(), "Authored Rigidbody was not materialized on Box");
        Assert.IsNotNull(box.GetComponent<BoxCollider>(), "Authored BoxCollider was not materialized on Box");

        // The user deletes the whole object in the scene.
        Object.DestroyImmediate(box);

        // Must not throw, and the emitted source must compile with the whole `var box = ...` chain gone.
        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite a deleted GameObject");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.DoesNotContain("scene.Add(\"Box\")", rewritten,
            "Builder source still declares the deleted Box.\n" + rewritten);
        StringAssert.DoesNotContain(".Component<UnityEngine.Rigidbody>", rewritten,
            "Builder source kept the deleted object's inline Rigidbody.\n" + rewritten);
        StringAssert.DoesNotContain(".Component<UnityEngine.BoxCollider>", rewritten,
            "Builder source kept the deleted object's inline BoxCollider.\n" + rewritten);
        StringAssert.DoesNotContain("box", rewritten,
            "Builder source still references the removed `box` handle.\n" + rewritten);

        // Convergence: a second sync against the same (already-consistent) scene is a no-op.
        var second = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(second.Changed, "Second sync mutated the builder — deletion did not converge.");
    }

    // 8. Component + field (code->scene): authoring a typed setter materializes the real component with
    //    the field value applied (member selector `r => r.mass` resolved to the serialized path m_Mass by
    //    the adapter's AuthoredPathResolver). Two-phase build: the object is built first, then the
    //    component+field is authored onto the mapped object and rebuilt.
    [Test]
    public void CodeToScene_AuthoredComponentAndTypedField_MaterializesWithValue()
    {
        // Phase 1: build the object alone so it is mapped.
        File.WriteAllText(_builderPath, Source("        var box = scene.Add(\"Box\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        // Phase 2: author the component + typed field setter and rebuild onto the existing object.
        File.WriteAllText(_builderPath, Source(
            "        var box = scene.Add(\"Box\");\n" +
            "        box.Component<UnityEngine.Rigidbody>(c => c.Set(r => r.mass, 5f));"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var box = FindRoot(EditorSceneManager.GetActiveScene(), "Box");
        Assert.IsNotNull(box, "Box was not created by SceneBuilderBuild.Run");
        var rb = box.GetComponent<Rigidbody>();
        Assert.IsNotNull(rb, "Authored Rigidbody was not materialized on Box");
        Assert.AreEqual(5f, rb.mass, "Authored typed-setter mass=5 did not materialize on the real Rigidbody");
    }

    // 9. One-pass create-with-component (code->scene) — REGRESSION for the EmitCreate component gap.
    //    A brand-new object that carries a component + typed field, built in a SINGLE build onto a fresh
    //    scene (NOT the two-phase workaround of #8), must materialize the real component with its value.
    [Test]
    public void CodeToScene_NewObjectWithComponent_OnePass_MaterializesWithValue()
    {
        File.WriteAllText(_builderPath, Source(
            "        var box = scene.Add(\"Box\");\n" +
            "        box.Component<UnityEngine.Rigidbody>(c => c.Set(r => r.mass, 5f));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var box = FindRoot(EditorSceneManager.GetActiveScene(), "Box");
        Assert.IsNotNull(box, "Box was not created by SceneBuilderBuild.Run");
        var rb = box.GetComponent<Rigidbody>();
        Assert.IsNotNull(rb, "Component on a brand-new object was not materialized in a one-pass build");
        Assert.AreEqual(5f, rb.mass, "Authored mass=5 did not materialize on the one-pass-created Rigidbody");
    }
}
