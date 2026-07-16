using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Validation;

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
    //    objects, not project assets. Scene->code must read them as Builtin(...) refs (never skip or
    //    drop the field), and code->scene must apply that ref back losslessly: Build must not throw,
    //    and the primitive's own built-in mesh must be left untouched in the scene.
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

        // Scene->code: sync the primitive into the builder source. The built-in refs must be read as
        // Builtin(...) calls, not silently skipped/dropped.
        EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("Builtin(\"Cube\")", rewritten,
            "Builder source did not gain a Builtin(\"Cube\") ref for the primitive's built-in mesh.\n" + rewritten);
        StringAssert.Contains("Builtin(\"Default-Material\")", rewritten,
            "Builder source did not gain a Builtin(\"Default-Material\") ref for the primitive's built-in material.\n" + rewritten);

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

    // b3-t4 read direction, bare name: a live primitive's MeshFilter.m_Mesh (a built-in with a
    // UNIQUE bare name) reads back as a populated AssetRef with IsBuiltin=true and a BARE
    // DisplayPath — TypeHint must stay "" (anti-churn, confirmation #8), never
    // ValueNode.Unsupported.
    [Test]
    public void SceneToCode_LivePrimitiveMesh_ReadsBareBuiltinRef()
    {
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            var mf = cube.GetComponent<MeshFilter>();
            var so = new SerializedObject(mf);
            var meshProp = so.FindProperty("m_Mesh");
            Assert.IsNotNull(meshProp, "m_Mesh property not found on MeshFilter");

            var node = AssetReferenceResolver.ReadObjectReference(meshProp);

            Assert.IsInstanceOf<ValueNode.AssetRef>(node,
                "Built-in mesh read did not produce an AssetRef node (got " + node.GetType().Name + ")");
            var assetRef = ((ValueNode.AssetRef)node).Ref;
            Assert.IsNotNull(assetRef, "Built-in mesh read produced a null (None) AssetRef");
            Assert.IsTrue(assetRef.IsBuiltin, "Built-in mesh read did not set IsBuiltin=true");
            Assert.AreEqual("Cube", assetRef.DisplayPath, "Built-in mesh read did not derive DisplayPath 'Cube'");
            Assert.AreEqual("", assetRef.TypeHint,
                "Bare unambiguous built-in name must NOT stamp TypeHint (anti-churn, confirmation #8)");

            Assert.IsTrue(
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mf.sharedMesh, out var liveGuid, out var liveFileId),
                "Could not derive the live mesh's own (guid, fileId) — test premise invalid");
            Assert.AreEqual(liveGuid, assetRef.Guid, "AssetRef.Guid does not match the live mesh's own GUID");
            Assert.AreEqual(liveFileId, assetRef.FileId, "AssetRef.FileId does not match the live mesh's own fileId");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(cube);
        }
    }

    // b3-t4 read direction, ambiguous name: a live UnityEngine.UI.Image's m_Sprite holding the
    // built-in "UISprite" Sprite (which also names a Texture2D) must read back QUALIFIED —
    // TypeHint == "Sprite" — because the bare name is ambiguous.
    [Test]
    public void SceneToCode_LiveImageSpriteWithAmbiguousName_ReadsQualifiedBuiltinRef()
    {
        var go = new GameObject("SceneToCode_LiveImageSpriteWithAmbiguousName_ReadsQualifiedBuiltinRef.Image");
        try
        {
            var image = go.AddComponent<UnityEngine.UI.Image>();
            var uiSprite = BuiltinCatalog.Resolve("UISprite", "Sprite", out _);
            Assert.IsNotNull(uiSprite, "BuiltinCatalog could not resolve the built-in 'UISprite' Sprite — test premise invalid");
            image.sprite = (Sprite)uiSprite;

            var so = new SerializedObject(image);
            var spriteProp = so.FindProperty("m_Sprite");
            Assert.IsNotNull(spriteProp, "Image.m_Sprite property not found");

            var node = AssetReferenceResolver.ReadObjectReference(spriteProp);

            Assert.IsInstanceOf<ValueNode.AssetRef>(node,
                "Ambiguous-name built-in sprite read did not produce an AssetRef node (got " + node.GetType().Name + ")");
            var assetRef = ((ValueNode.AssetRef)node).Ref;
            Assert.IsNotNull(assetRef, "Ambiguous-name built-in sprite read produced a null (None) AssetRef");
            Assert.IsTrue(assetRef.IsBuiltin, "Built-in sprite read did not set IsBuiltin=true");
            Assert.AreEqual("UISprite", assetRef.DisplayPath, "Built-in sprite read did not derive DisplayPath 'UISprite'");
            Assert.AreEqual("Sprite", assetRef.TypeHint,
                "Ambiguous built-in name must stamp TypeHint to disambiguate (Sprite vs Texture2D)");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    // b3-t4 read direction, version-removed: a built-in (guid, fileId) pair that derives no live
    // catalog object (e.g. this editor version no longer ships that fileId) must THROW a located
    // error naming object/component/field — never ValueNode.Unsupported, never a silent skip.
    // ReadObjectReference can only ever derive a fileId from a LIVE object, so this case is driven
    // directly through the ReadBuiltinRef seam with a FABRICATED fileId (adversarially chosen: the
    // premise that it derives no object is asserted first, per ASSUMPTIONS).
    [Test]
    public void SceneToCode_BuiltinFileIdThatDerivesNoObject_ThrowsLocatedError()
    {
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            var mf = cube.GetComponent<MeshFilter>();
            var so = new SerializedObject(mf);
            var meshProp = so.FindProperty("m_Mesh");
            Assert.IsNotNull(meshProp, "m_Mesh property not found on MeshFilter");

            const long fabricatedFileId = 999999999L;
            Assert.IsFalse(
                BuiltinCatalog.TryDeriveName(BuiltinCatalog.BuiltinResourcesGuid, fabricatedFileId, out _, out _, out _),
                "Fabricated fileId derives a real catalog object — the premise for this test is invalid.");

            var ex = Assert.Throws<InvalidOperationException>(
                () => AssetReferenceResolver.ReadBuiltinRef(BuiltinCatalog.BuiltinResourcesGuid, fabricatedFileId, meshProp),
                "A built-in (guid, fileId) that derives no object must throw the located error, never return " +
                "ValueNode.Unsupported and never return a node at all.");

            StringAssert.Contains("Cube", ex.Message, "Error does not name the object.\n" + ex.Message);
            StringAssert.Contains("MeshFilter", ex.Message, "Error does not name the component.\n" + ex.Message);
            StringAssert.Contains("m_Mesh", ex.Message, "Error does not name the field.\n" + ex.Message);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(cube);
        }
    }

    // 7. authored container path (code->scene): a builder file carrying the OLD, now-deleted form
    //    Asset("Library/unity default resources") must be REFUSED with a loud, located error pointing
    //    at Builtin(...) as the fix (spec confirmation #11) — never the old warn-and-continue. Build
    //    must refuse BEFORE touching the scene, so a real built-in mesh already on the field survives
    //    untouched (not cleared). The old order (Build first with the ref already authored) is
    //    unreachable under the new contract because that first Build now throws — so this test
    //    assigns the donor mesh FIRST, then introduces the container-path ref and asserts the throw.
    [Test]
    public void CodeToScene_AuthoredContainerPath_ThrowsLocatedErrorPointingAtBuiltin()
    {
        // Phase 1: build Cube + a bare MeshFilter (no ref yet) so the field is mapped.
        File.WriteAllText(_builderPath, Source(
            "        var cube = scene.Add(\"Cube\");\n" +
            "        cube.Component<UnityEngine.MeshFilter>();"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var cube = FindRoot(EditorSceneManager.GetActiveScene(), "Cube");
        Assert.IsNotNull(cube, "Cube was not created by SceneBuilderBuild.Run");
        var mf = cube.GetComponent<MeshFilter>();
        Assert.IsNotNull(mf, "Authored MeshFilter was not materialized on Cube");

        // Give the live field a real built-in mesh directly in the scene (the donor technique).
        var donor = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        var builtinMesh = donor.GetComponent<MeshFilter>().sharedMesh;
        UnityEngine.Object.DestroyImmediate(donor);
        Assert.IsNotNull(builtinMesh, "Could not obtain a built-in mesh — test premise invalid");
        mf.sharedMesh = builtinMesh;

        // Phase 2: author the OLD container-path form and rebuild. Must THROW, not warn-and-continue.
        File.WriteAllText(_builderPath, Source(
            "        var cube = scene.Add(\"Cube\");\n" +
            "        cube.Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Asset(\"Library/unity default resources\")));"));

        // Since b3-t2: collect-all-refuse, not throw-on-first — the container path is a collected
        // SB2101 diagnostic on BuildResult. Messages are generic (Code + Line only per research
        // Verdict #2) — do not assert object/component/field names or the old "Builtin" fix hint.
        var result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var diagnostic = result.Diagnostics.FirstOrDefault(d => d.Code == DiagnosticCodes.AssetPathNotFound);
        Assert.IsNotNull(diagnostic,
            "Build did not report a diagnostic for an authored container path — the old "
            + "warn-and-continue branch must be gone. Got: "
            + string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));

        // Build refused BEFORE touching the scene: the live mesh must survive untouched, not cleared.
        Assert.AreEqual(builtinMesh, cube.GetComponent<MeshFilter>().sharedMesh,
            "Build must leave the live mesh untouched when refusing a container-path reference, not clear it.");
    }

    // 9. authored Builtin(...) (code->scene): the authoring form itself lowers to the real container
    //    GUID + fileId, with the authored TypeHint/DisplayPath preserved verbatim (anti-churn — a
    //    resolved TypeHint must never get stamped back in, or a bare Builtin("Cube") would drift to
    //    Builtin("Cube","Mesh") on every sync).
    [Test]
    public void Load_AuthoredBuiltinCube_LowersToContainerGuidAndFileId()
    {
        var source = Source(
            "        var widget = scene.Add(\"Widget\");\n" +
            "        widget.Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"Cube\")));");

        var loaded = DesiredModelLoader.Load(source, null);

        var widget = loaded.Desired.Roots.FirstOrDefault(g => g.Name == "Widget");
        Assert.IsNotNull(widget, "Widget root not found in the desired model");
        var mf = widget.Components.FirstOrDefault(c => c.Type.FullName == "UnityEngine.MeshFilter");
        Assert.IsNotNull(mf, "MeshFilter component not found on Widget");
        Assert.IsTrue(mf.Fields.TryGetValue("m_Mesh", out var value), "m_Mesh field not found on MeshFilter");
        var assetRef = ((ValueNode.AssetRef)value).Ref;
        Assert.IsNotNull(assetRef, "m_Mesh lowered to a null AssetRef");
        Assert.AreEqual("0000000000000000e000000000000000", assetRef.Guid,
            "Builtin(\"Cube\") did not lower to the built-in resources container GUID");
        Assert.AreEqual(10202L, assetRef.FileId, "Builtin(\"Cube\") did not lower to fileId 10202");
        Assert.AreEqual("", assetRef.TypeHint,
            "Lowering must NOT stamp the resolved TypeHint — the authored bare form must stay bare (anti-churn)");
        Assert.AreEqual("Cube", assetRef.DisplayPath, "Lowering must preserve the authored DisplayPath verbatim");
    }

    // 10. unresolvable built-in name (code->scene): spec confirmation #9 — a misspelled Builtin(...)
    //    name must produce a loud, located error naming the object, component, field and the bad
    //    name, WITH near-miss suggestions. Never a silent skip.
    [Test]
    public void Load_AuthoredBuiltinMisspelledName_ThrowsLocatedErrorWithSuggestions()
    {
        var source = Source(
            "        var widget = scene.Add(\"Widget\");\n" +
            "        widget.Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"Cub\")));");

        var ex = Assert.Throws<InvalidOperationException>(() => DesiredModelLoader.Load(source, null));

        StringAssert.Contains("Widget", ex.Message, "Error does not name the object.\n" + ex.Message);
        StringAssert.Contains("MeshFilter", ex.Message, "Error does not name the component.\n" + ex.Message);
        StringAssert.Contains("m_Mesh", ex.Message, "Error does not name the field.\n" + ex.Message);
        StringAssert.Contains("Cub", ex.Message, "Error does not name the bad name.\n" + ex.Message);
        StringAssert.Contains("Cube", ex.Message, "Error does not suggest the near-miss 'Cube'.\n" + ex.Message);
    }

    // 11. unqualifiable ambiguity (code->scene): spec confirmation #10 — a bare Builtin(...) name that
    //    matches more than one type (e.g. "UISprite" matches both a Sprite and a Texture2D) must
    //    produce a located error naming BOTH candidate types and telling the author to qualify.
    //    Needs no uGUI: the validator throws on the NAME before any field-type checking, so any
    //    Object-reference field exercises it.
    [Test]
    public void Load_AuthoredBareAmbiguousBuiltin_ThrowsLocatedErrorNamingBothCandidateTypes()
    {
        var source = Source(
            "        var widget = scene.Add(\"Widget\");\n" +
            "        widget.Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"UISprite\")));");

        var ex = Assert.Throws<InvalidOperationException>(() => DesiredModelLoader.Load(source, null));

        StringAssert.Contains("Sprite", ex.Message, "Error does not name the Sprite candidate.\n" + ex.Message);
        StringAssert.Contains("Texture2D", ex.Message, "Error does not name the Texture2D candidate.\n" + ex.Message);
    }

    // 12. never-harvests (code->scene): spec §IdentityMap — LoweringResolver's harvest must skip
    //    IsBuiltin refs. A resolved Builtin("Cube") alongside a resolved project-asset ref must
    //    harvest ONLY the project asset (non-vacuous), never a built-in container GUID, into BOTH
    //    Load(...).HarvestedAssets and the sidecar Build writes. Build must also actually RESOLVE the
    //    built-in ref (a live mesh gets assigned) — not just leave it Unresolved/skipped, which would
    //    make the harvest-exclusion assertions trivially (and misleadingly) true.
    [Test]
    public void Build_BuiltinRefBesideProjectAsset_HarvestsOnlyTheProjectAsset()
    {
        var body =
            "        var cube = scene.Add(\"Cube\");\n" +
            "        cube.Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"Cube\")));\n" +
            "        cube.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Asset(\"" + RedPath + "\") }));";
        File.WriteAllText(_builderPath, Source(body));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();

        var redGuid = AssetDatabase.AssetPathToGUID(RedPath);
        var loaded = DesiredModelLoader.Load(File.ReadAllText(_builderPath), null);
        Assert.IsTrue(loaded.HarvestedAssets.Any(a => a.Guid == redGuid),
            "Harvested assets did not include the sibling project asset (Red.mat) — the exclusion check would be vacuous.");
        Assert.IsFalse(
            loaded.HarvestedAssets.Any(a =>
                a.Guid == "0000000000000000e000000000000000" || a.Guid == "0000000000000000f000000000000000"),
            "A built-in ref was harvested into Assets[] — built-ins have no project path to track.");

        var result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        Assert.IsTrue(result.Map.Assets.Any(a => a.Guid == redGuid),
            "The written sidecar's Assets[] is missing the sibling Red.mat entry.");
        Assert.IsFalse(
            result.Map.Assets.Any(a =>
                a.Guid == "0000000000000000e000000000000000" || a.Guid == "0000000000000000f000000000000000"),
            "The written sidecar's Assets[] contains a built-in container GUID entry.");

        // Confirm the built-in ref was actually RESOLVED and assigned (not silently skipped as
        // Unresolved) — otherwise the exclusion assertions above hold vacuously.
        var rebuiltCube = FindRoot(EditorSceneManager.GetActiveScene(), "Cube");
        Assert.IsNotNull(rebuiltCube, "Cube was not created by SceneBuilderBuild.Run");
        var mf = rebuiltCube.GetComponent<MeshFilter>();
        Assert.IsNotNull(mf.sharedMesh,
            "Builtin(\"Cube\") did not assign a mesh — the reference was skipped as unresolved instead of being resolved.");
        Assert.IsTrue(
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mf.sharedMesh, out var meshGuid, out var meshFileId)
            && meshGuid == "0000000000000000e000000000000000" && meshFileId == 10202,
            "MeshFilter.sharedMesh is not the built-in Cube mesh (guid=" + meshGuid + ", fileId=" + meshFileId + ")");
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
