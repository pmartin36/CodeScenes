using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;

// M4 (asset references) bidirectional round-trip gate tests. Each test DRIVES a real change on one
// side — an authored builder-source edit, or a live EditMode scene edit assigning/clearing a real
// Material asset — through the programmatic Build/Sync APIs and ASSERTS propagation on the other
// side, exercising the real path->GUID->objectReferenceValue behavior in a live editor. Mirrors
// RoundTripComponentTests: temp builder .cs + temp sidecar in a system temp dir, an EmptyScene per
// test, [SetUp]/[TearDown] cleanup. Real Material assets (Red.mat / Blue.mat) are created under
// Assets/GateTests/Fixtures so AssetDatabase can resolve path<->GUID, and deleted in [TearDown].
public class RoundTripAssetRefTests
{
    private const string ScenePath = "Assets/GateTests/__RoundTripAssetRefTemp.unity";
    private const string FixturesDir = "Assets/GateTests/Fixtures";
    private const string RedPath = FixturesDir + "/Red.mat";
    private const string BluePath = FixturesDir + "/Blue.mat";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    // Wrap a Build-body fragment in a minimal ISceneDefinition the Core Roslyn parser understands.
    // Mirrors a REAL user's file: `using SceneBuilder.Authoring;` always, plus the static AssetRefs
    // using exactly when the authored body itself calls Asset(...). A user who never hand-wrote an
    // Asset(...) call has no reason to carry that using — and that is precisely the file sync must
    // leave COMPILING when it introduces an Asset(...) of its own.
    private static string Source(string body)
    {
        var assetRefsUsing = body.Contains("Asset(")
            ? "using static SceneBuilder.Authoring.AssetRefs;\n"
            : "";

        return $@"
using SceneBuilder.Authoring;
{assetRefsUsing}public class RoundTripAssetScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";
    }

    private static GameObject FindRoot(Scene scene, string name)
    {
        return scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);
    }

    private static Material LoadMaterial(string path) => AssetDatabase.LoadAssetAtPath<Material>(path);

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_rtar_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "RoundTripAssetScene.cs");
        _sidecarPath = Path.Combine(_dir, "RoundTripAssetScene.sbmap.json");

        if (!AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "Fixtures");
        }

        AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), RedPath);
        AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), BluePath);
        AssetDatabase.SaveAssets();
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

        AssetDatabase.DeleteAsset(RedPath);
        AssetDatabase.DeleteAsset(BluePath);
        if (AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.DeleteAsset(FixturesDir);
        }
    }

    // 1. code->scene: authoring an Asset("...Red.mat") material on a MeshRenderer materializes the real
    //    asset onto the live renderer's shared material (path -> GUID -> objectReferenceValue).
    [Test]
    public void CodeToScene_AuthoredMaterial_MaterializesOnRenderer()
    {
        // Phase 1: build the object alone so it is mapped.
        File.WriteAllText(_builderPath, Source("        var surface = scene.Add(\"Surface\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        // Phase 2: author the renderer + material onto the existing object and rebuild.
        File.WriteAllText(_builderPath, Source(
            "        var surface = scene.Add(\"Surface\");\n" +
            "        surface.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Asset(\"" + RedPath + "\") }));"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var surface = FindRoot(EditorSceneManager.GetActiveScene(), "Surface");
        Assert.IsNotNull(surface, "Surface was not created by SceneBuilderBuild.Run");
        var mr = surface.GetComponent<MeshRenderer>();
        Assert.IsNotNull(mr, "Authored MeshRenderer was not materialized on Surface");
        Assert.AreEqual(LoadMaterial(RedPath), mr.sharedMaterial,
            "Authored Asset(Red.mat) did not materialize onto the renderer's shared material");
        Assert.AreEqual(RedPath, AssetDatabase.GetAssetPath(mr.sharedMaterial),
            "Materialized material does not resolve back to the Red.mat asset path");
    }

    // 2. scene->code: assigning a real material asset onto a mapped MeshRenderer in the scene writes an
    //    Asset("...Red.mat") setter back into the builder source (GUID -> re-derived DisplayPath).
    [Test]
    public void SceneToCode_AssignedMaterial_WritesAssetPath()
    {
        // Phase 1: build the object alone so it is mapped.
        File.WriteAllText(_builderPath, Source("        var surface = scene.Add(\"Surface\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        // Phase 2: author a BARE renderer onto the existing object and rebuild (maps the component,
        // no material field yet).
        File.WriteAllText(_builderPath, Source(
            "        var surface = scene.Add(\"Surface\");\n" +
            "        surface.Component<UnityEngine.MeshRenderer>();"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var surface = FindRoot(EditorSceneManager.GetActiveScene(), "Surface");
        Assert.IsNotNull(surface, "Surface was not created by SceneBuilderBuild.Run");
        var mr = surface.GetComponent<MeshRenderer>();
        Assert.IsNotNull(mr, "MeshRenderer was not materialized on Surface");

        // Assign the real Red.mat asset in the scene.
        mr.sharedMaterial = LoadMaterial(RedPath);

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite an assigned material");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("Asset(\"" + RedPath + "\")", rewritten,
            "Builder source did not gain an Asset(Red.mat) reference for the assigned material.\n" + rewritten);
    }

    // 3. swap (scene->code): assigning a DIFFERENT material asset over an existing authored one rewrites
    //    the Asset("...") argument to the new asset's path.
    [Test]
    public void SceneToCode_SwappedMaterial_UpdatesAssetPath()
    {
        // Phase 1: build the object alone so it is mapped.
        File.WriteAllText(_builderPath, Source("        var surface = scene.Add(\"Surface\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        // Phase 2: author the renderer WITH Red.mat and rebuild (maps component + assigns Red).
        File.WriteAllText(_builderPath, Source(
            "        var surface = scene.Add(\"Surface\");\n" +
            "        surface.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Asset(\"" + RedPath + "\") }));"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var surface = FindRoot(EditorSceneManager.GetActiveScene(), "Surface");
        var mr = surface.GetComponent<MeshRenderer>();
        Assert.IsNotNull(mr, "MeshRenderer was not materialized on Surface");
        Assert.AreEqual(LoadMaterial(RedPath), mr.sharedMaterial, "Phase-2 build did not assign Red.mat");

        // Swap to Blue.mat in the scene.
        mr.sharedMaterial = LoadMaterial(BluePath);

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite a swapped material");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("Asset(\"" + BluePath + "\")", rewritten,
            "Builder source did not update to the swapped-in Blue.mat path.\n" + rewritten);
        StringAssert.DoesNotContain(RedPath, rewritten,
            "Builder source still carries the old Red.mat path after the swap.\n" + rewritten);
    }

    // 4. clear to None (scene->code): clearing a material slot to None in the scene rewrites the source
    //    argument to the None form Asset(null). Uses a two-element material array so the field stays
    //    non-default (a single null slot can equal a fresh renderer's default and be filtered out).
    [Test]
    public void SceneToCode_ClearedMaterialToNone_WritesNoneForm()
    {
        // Phase 1: build the object alone so it is mapped.
        File.WriteAllText(_builderPath, Source("        var surface = scene.Add(\"Surface\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        // Phase 2: author the renderer with TWO Red.mat slots and rebuild.
        File.WriteAllText(_builderPath, Source(
            "        var surface = scene.Add(\"Surface\");\n" +
            "        surface.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Asset(\"" + RedPath + "\"), Asset(\"" + RedPath + "\") }));"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var surface = FindRoot(EditorSceneManager.GetActiveScene(), "Surface");
        var mr = surface.GetComponent<MeshRenderer>();
        Assert.IsNotNull(mr, "MeshRenderer was not materialized on Surface");
        Assert.AreEqual(2, mr.sharedMaterials.Length, "Phase-2 build did not assign two material slots");

        // Clear the FIRST slot to None (keep the array size at 2 so it stays non-default).
        var red = LoadMaterial(RedPath);
        mr.sharedMaterials = new Material[] { null, red };

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite a cleared material slot");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("Asset(null)", rewritten,
            "Builder source did not use the None form Asset(null) for the cleared material slot.\n" + rewritten);
    }

    // 6. built-in resources (round-trip): a Unity primitive's MeshFilter.m_Mesh and MeshRenderer
    //    material point at Library/unity default resources / Resources/unity_builtin_extra — BUILT-IN
    //    objects, not project assets. They have real GUIDs (0000...e000... / 0000...f000...) that
    //    AssetDatabase.LoadAssetAtPath CANNOT load, so the M4 resolver concluded "the asset was
    //    deleted" and threw — breaking Build for a plain Cube, the most common object anyone creates.
    //    A built-in must never be misclassified as deleted: Build must not throw, and the primitive's
    //    own built-in mesh/material must be left untouched in the scene.
    [Test]
    public void RoundTrip_ScenePrimitive_BuiltinResourcesDoNotBreakBuild()
    {
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Anchor\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        // The exact user repro: GameObject > 3D Object > Cube.
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = "Cube";

        var builtinMesh = cube.GetComponent<MeshFilter>().sharedMesh;
        Assert.IsNotNull(builtinMesh, "CreatePrimitive(Cube) produced no mesh — test premise invalid");
        Assert.IsTrue(
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(builtinMesh, out var meshGuid, out _)
            && meshGuid == "0000000000000000e000000000000000",
            "Cube mesh is not the well-known built-in resource — test premise invalid (guid was " + meshGuid + ")");

        // Scene->code: sync the primitive into the builder source.
        EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        // Code->scene: Build must NOT throw. A built-in reference is not a deleted asset.
        Assert.DoesNotThrow(
            () => SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene()),
            "Build threw on a scene containing a Unity primitive — a built-in resource was misclassified as a deleted asset.");

        // The primitive's built-in mesh must survive Build untouched (never cleared to None).
        var rebuiltCube = FindRoot(EditorSceneManager.GetActiveScene(), "Cube");
        Assert.IsNotNull(rebuiltCube, "Cube disappeared from the scene after Build");
        var mf = rebuiltCube.GetComponent<MeshFilter>();
        Assert.IsNotNull(mf, "Cube lost its MeshFilter after Build");
        Assert.AreEqual(builtinMesh, mf.sharedMesh,
            "Build cleared/changed the primitive's built-in mesh — the scene's own value must be left untouched");
    }

    // 7. authored built-in ref (code->scene): a builder file that ALREADY carries an
    //    Asset("Library/unity default resources") — exactly what an earlier sync wrote into the
    //    reporting user's file — must not throw on Build. A built-in is not a deleted asset. It is
    //    also not a clear: the live field must be left untouched, never nulled.
    [Test]
    public void CodeToScene_AuthoredBuiltinRef_DoesNotThrowAndDoesNotClear()
    {
        File.WriteAllText(_builderPath, Source(
            "        var cube = scene.Add(\"Cube\");\n" +
            "        cube.Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Asset(\"Library/unity default resources\")));"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();

        Assert.DoesNotThrow(
            () => SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene),
            "Build threw on an authored built-in resource reference — a built-in is not a deleted asset.");

        // Give the object a real built-in mesh, then rebuild: the authored built-in ref must LEAVE IT
        // ALONE rather than resolve-to-nothing and clear the slot.
        var cube = FindRoot(EditorSceneManager.GetActiveScene(), "Cube");
        Assert.IsNotNull(cube, "Cube was not created by SceneBuilderBuild.Run");
        var mf = cube.GetComponent<MeshFilter>();
        Assert.IsNotNull(mf, "Authored MeshFilter was not materialized on Cube");

        // Source a real built-in mesh from a throwaway primitive (the same object a user's Cube holds).
        var donor = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        var builtinMesh = donor.GetComponent<MeshFilter>().sharedMesh;
        Object.DestroyImmediate(donor);
        Assert.IsNotNull(builtinMesh, "Could not obtain a built-in mesh — test premise invalid");
        mf.sharedMesh = builtinMesh;

        Assert.DoesNotThrow(
            () => SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene()),
            "Rebuild threw on an authored built-in resource reference.");

        Assert.AreEqual(builtinMesh, cube.GetComponent<MeshFilter>().sharedMesh,
            "An authored built-in ref CLEARED the live mesh — it must leave the scene's own value untouched.");
    }

    // 8. CONVERGENCE (scene->code): a Sync immediately after a Sync, with NO scene change in between,
    //    must be a NO-OP. Build lowers authored Asset("path") refs to their (guid, fileId); Sync did
    //    not — so the source-side ref carried Guid="" while the snapshot's carried the real GUID, and
    //    AssetRef.Equals keys on (Guid, FileId) ONLY. A source ref could therefore NEVER equal a
    //    populated snapshot ref: the reconcile emitted a PatchComponentField + harvested the asset on
    //    EVERY sync, forever. It was masked only because the patch re-emitted text identical to the
    //    user's, so EditsApplied stayed 0 — one formatting divergence turns it into a perpetual source
    //    rewrite. `Changed` is the bit that lies, and sidecar BYTE-equality does not catch this (the
    //    bytes are already identical); the sidecar is rewritten unconditionally regardless.
    //
    //    This matters because code->scene is driven by the plugin's OWN file watcher: a sync that
    //    always writes is a watcher that always fires — a feedback loop aimed at the core feature.
    [Test]
    public void SceneToCode_ResyncWithNoSceneChange_IsANoOp()
    {
        // Phase 1: build the object alone so it is mapped.
        File.WriteAllText(_builderPath, Source("        var surface = scene.Add(\"Surface\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        // Phase 2: author the renderer WITH Red.mat and rebuild, so the scene and the code agree.
        File.WriteAllText(_builderPath, Source(
            "        var surface = scene.Add(\"Surface\");\n" +
            "        surface.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Asset(\"" + RedPath + "\") }));"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var surface = FindRoot(EditorSceneManager.GetActiveScene(), "Surface");
        var mr = surface.GetComponent<MeshRenderer>();
        Assert.IsNotNull(mr, "MeshRenderer was not materialized on Surface");
        Assert.AreEqual(LoadMaterial(RedPath), mr.sharedMaterial, "Phase-2 build did not assign Red.mat");

        // Sync once. The scene already matches the code, so this should already be a no-op.
        EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        // Sync AGAIN with NO scene change whatsoever. This MUST be a no-op.
        var second = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        Assert.IsFalse(second.Changed,
            "NOT CONVERGED: a Sync immediately after a Sync, with NO scene change, reported Changed=true. " +
            "The material asset-ref is re-harvested and re-patched on every sync because the source-side " +
            "ref was never lowered to its GUID.");
        Assert.AreEqual(0, second.PatchEdits,
            "NOT CONVERGED: the no-op re-sync's reconcile produced " + second.PatchEdits + " patch edit(s). " +
            "The reconcile must not produce an edit at all, independent of whether the emitted text " +
            "happened to match the existing source byte-for-byte.");
        Assert.AreEqual(0, second.AddedEntries, "No-op re-sync added sidecar entries");
        Assert.AreEqual(0, second.RemovedEntries, "No-op re-sync removed sidecar entries");
    }

    // 5. author None -> scene (code->scene): authoring Asset(null) over a previously-assigned material
    //    clears the live renderer's slot (SetAssetRef with a null GUID => objectReferenceValue = null).
    [Test]
    public void CodeToScene_AuthoredNone_ClearsSlot()
    {
        // Phase 1: build the object alone so it is mapped.
        File.WriteAllText(_builderPath, Source("        var surface = scene.Add(\"Surface\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        // Phase 2: author the renderer WITH Red.mat and rebuild (assigns Red).
        File.WriteAllText(_builderPath, Source(
            "        var surface = scene.Add(\"Surface\");\n" +
            "        surface.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Asset(\"" + RedPath + "\") }));"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var surface = FindRoot(EditorSceneManager.GetActiveScene(), "Surface");
        var mr = surface.GetComponent<MeshRenderer>();
        Assert.IsNotNull(mr, "MeshRenderer was not materialized on Surface");
        Assert.AreEqual(LoadMaterial(RedPath), mr.sharedMaterial, "Phase-2 build did not assign Red.mat");

        // Phase 3: author the None form and rebuild — the slot must clear.
        File.WriteAllText(_builderPath, Source(
            "        var surface = scene.Add(\"Surface\");\n" +
            "        surface.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Asset(null) }));"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        surface = FindRoot(EditorSceneManager.GetActiveScene(), "Surface");
        mr = surface.GetComponent<MeshRenderer>();
        Assert.IsNotNull(mr, "MeshRenderer disappeared after authoring the None form");
        Assert.IsNull(mr.sharedMaterial,
            "Authoring Asset(null) did not clear the renderer's shared material slot");
    }
}
