using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using SceneBuilder.Editor;

// M-Builtin scene->code round-trip gate tests: #3 (no unsupported-skip warning for a scene
// primitive's built-in refs), #4 (swapping a built-in mesh updates the emitted name), #5
// (built-in <-> project-asset material convergence, bidirectional), #7 (the qualified
// Builtin(name, typeHint) form, Build assigns the Sprite not the Texture2D, Sync emits it),
// #8 (the anti-churn pin: re-syncing an unchanged scene primitive is a genuine no-op and never
// stamps a TypeHint on an unambiguous name), #12 (a built-in mesh ref survives scene save/reload
// and a post-reload resync is a no-op). Every oracle is INDEPENDENT of BuiltinCatalog (the
// resolver under test) — see BuiltinRefGateHarness's PrimitiveMesh/PrimitiveMaterial and this
// file's own AssetDatabase.GetBuiltinExtraResource lookup for #7.
public class RoundTripBuiltinRefSceneToCodeTests : BuiltinRefGateHarness
{
    // #3: `GameObject > 3D Object > Cube` in the editor, then Sync. The MeshFilter/MeshRenderer
    // built-in refs must be read as Builtin(...) calls — never silently skipped. Two syncs are
    // required for non-vacuity: Skipped is populated only by the field-diff pass, which only runs
    // for components matched in BOTH source and snapshot, so the FIRST sync (which is what maps
    // the MeshFilter/MeshRenderer into the source for the first time) never reaches m_Mesh at all
    // and would report "no warning" even against the OLD broken read. The second sync is where the
    // pass actually evaluates m_Mesh/m_Materials.
    [Test]
    public void SceneToCode_ScenePrimitive_ReadsAsBuiltinRefsWithNoUnsupportedSkip()
    {
        File.WriteAllText(BuilderPath, Source("        scene.Add(\"Anchor\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(BuilderPath, ScenePath, SidecarPath, scene);

        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = "Cube";

        var warnings = new List<string>();
        void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Warning)
            {
                warnings.Add(condition);
            }
        }

        Application.logMessageReceived += OnLog;
        try
        {
            EmittedCodeCompiles.SyncAndAssertCompiles(BuilderPath, SidecarPath, EditorSceneManager.GetActiveScene());
            EmittedCodeCompiles.SyncAndAssertCompiles(BuilderPath, SidecarPath, EditorSceneManager.GetActiveScene());
        }
        finally
        {
            Application.logMessageReceived -= OnLog;
        }

        var rewritten = File.ReadAllText(BuilderPath);
        StringAssert.Contains("Builtin(\"Cube\")", rewritten,
            "Non-vacuity premise failed: source did not gain Builtin(\"Cube\").\n" + rewritten);
        StringAssert.Contains("Builtin(\"Default-Material\")", rewritten,
            "Non-vacuity premise failed: source did not gain Builtin(\"Default-Material\").\n" + rewritten);

        foreach (var w in warnings)
        {
            var isFieldSkip = w.Contains("Unsupported field") && (w.Contains("m_Mesh") || w.Contains("m_Materials"));
            Assert.IsFalse(isFieldSkip,
                "A Console warning reported m_Mesh/m_Materials as Unsupported/skipped across two syncs: " + w);
        }
    }

    // #4: with Builtin("Cube") authored, dragging the built-in Sphere mesh into the MeshFilter
    // slot and syncing must update the emitted name to Builtin("Sphere") — a REWRITE, not an
    // append (the negative assertion is the non-vacuity device).
    [Test]
    public void SceneToCode_SwappedBuiltinMesh_UpdatesBuiltinName()
    {
        var expectedSphere = PrimitiveMesh(PrimitiveType.Sphere);

        File.WriteAllText(BuilderPath, Source(
            "        var widget = scene.Add(\"Widget\");\n" +
            "        widget.Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"Cube\")));"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(BuilderPath, ScenePath, SidecarPath, scene);

        var widget = FindRoot(EditorSceneManager.GetActiveScene(), "Widget");
        Assert.IsNotNull(widget, "Widget was not created by SceneBuilderBuild.Run");
        var mf = widget.GetComponent<MeshFilter>();
        Assert.IsNotNull(mf, "Authored MeshFilter was not materialized on Widget");
        Assert.AreEqual(PrimitiveMesh(PrimitiveType.Cube), mf.sharedMesh,
            "Phase-1 build did not assign the Cube mesh — test premise invalid");

        mf.sharedMesh = expectedSphere;

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(BuilderPath, SidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite a swapped built-in mesh");

        var rewritten = File.ReadAllText(BuilderPath);
        StringAssert.Contains("Builtin(\"Sphere\")", rewritten,
            "Builder source did not update to the swapped-in Sphere mesh.\n" + rewritten);
        StringAssert.DoesNotContain("Builtin(\"Cube\")", rewritten,
            "Builder source still carries the old Builtin(\"Cube\") after the swap.\n" + rewritten);
    }

    // #5 (bidirectional): a built-in Default-Material swapped to a project asset (Red.mat) syncs
    // to Asset("...Red.mat"); re-authoring Builtin("Default-Material") and rebuilding returns the
    // slot to the SAME built-in material object, by reference.
    [Test]
    public void RoundTrip_BuiltinMaterialToProjectAssetAndBack_Converges()
    {
        var expectedBuiltin = PrimitiveMaterial(PrimitiveType.Cube);

        File.WriteAllText(BuilderPath, Source(
            "        var widget = scene.Add(\"Widget\");\n" +
            "        widget.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Builtin(\"Default-Material\") }));"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(BuilderPath, ScenePath, SidecarPath, scene);

        var widget = FindRoot(EditorSceneManager.GetActiveScene(), "Widget");
        Assert.IsNotNull(widget, "Widget was not created by SceneBuilderBuild.Run");
        var mr = widget.GetComponent<MeshRenderer>();
        Assert.IsNotNull(mr, "Authored MeshRenderer was not materialized on Widget");
        Assert.AreEqual(expectedBuiltin, mr.sharedMaterial,
            "Phase-1 build did not assign the built-in Default-Material — test premise invalid");

        // Scene->code: swap to a project asset.
        mr.sharedMaterial = LoadMaterial(RedPath);
        var toAsset = EmittedCodeCompiles.SyncAndAssertCompiles(BuilderPath, SidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(toAsset.Changed, "Sync reported no change despite swapping to a project asset material");

        var afterAssetSync = File.ReadAllText(BuilderPath);
        StringAssert.Contains("Asset(\"" + RedPath + "\")", afterAssetSync,
            "Builder source did not gain Asset(Red.mat) after the built-in->asset swap.\n" + afterAssetSync);
        StringAssert.DoesNotContain("Builtin(\"Default-Material\")", afterAssetSync,
            "Builder source still carries Builtin(\"Default-Material\") after the swap to a project asset.\n" + afterAssetSync);

        // Code->scene: re-author the built-in and rebuild — the slot must return to the SAME
        // built-in material object.
        File.WriteAllText(BuilderPath, Source(
            "        var widget = scene.Add(\"Widget\");\n" +
            "        widget.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Builtin(\"Default-Material\") }));"));
        SceneBuilderBuild.Run(BuilderPath, ScenePath, SidecarPath, EditorSceneManager.GetActiveScene());

        var rebuiltWidget = FindRoot(EditorSceneManager.GetActiveScene(), "Widget");
        Assert.IsNotNull(rebuiltWidget, "Widget disappeared after rebuilding with Builtin(\"Default-Material\")");
        var rebuiltMr = rebuiltWidget.GetComponent<MeshRenderer>();
        Assert.IsNotNull(rebuiltMr, "MeshRenderer disappeared after rebuilding with Builtin(\"Default-Material\")");
        Assert.AreEqual(expectedBuiltin, rebuiltMr.sharedMaterial,
            "Re-authoring Builtin(\"Default-Material\") did not return the slot to the built-in material.");
    }

    // #7 (both halves): the QUALIFIED Builtin(name, typeHint) form. Build must assign the Sprite
    // (10905), never the same-named Texture2D (10904) — read via SerializedProperty, not the typed
    // .sprite accessor, which cannot even hold a Texture2D and would mask a wrong-type assignment
    // as a plain null. Sync (through SyncAndAssertCompiles — the only binding site for b3-t1's
    // 2-arg overload) must emit the qualified form when the built-in name is ambiguous.
    [Test]
    public void RoundTrip_BuiltinSpriteWithAmbiguousName_AssignsSpriteAndEmitsQualifiedForm()
    {
        var expectedSprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        Assert.IsNotNull(expectedSprite, "Could not resolve the built-in UISprite Sprite oracle — test premise invalid");

        // Half A (Build): author the QUALIFIED form directly.
        File.WriteAllText(BuilderPath, Source(
            "        var icon = scene.Add(\"Icon\");\n" +
            "        icon.Component<UnityEngine.UI.Image>(c => c.Set(\"m_Sprite\", Builtin(\"UISprite\", \"Sprite\")));"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(BuilderPath, ScenePath, SidecarPath, scene);

        var icon = FindRoot(EditorSceneManager.GetActiveScene(), "Icon");
        Assert.IsNotNull(icon, "Icon was not created by SceneBuilderBuild.Run");
        var image = icon.GetComponent<UnityEngine.UI.Image>();
        Assert.IsNotNull(image, "Authored Image was not materialized on Icon");

        var so = new SerializedObject(image);
        var spriteProp = so.FindProperty("m_Sprite");
        Assert.IsNotNull(spriteProp, "Image.m_Sprite property not found");
        Assert.IsNotNull(spriteProp.objectReferenceValue,
            "Builtin(\"UISprite\", \"Sprite\") did not assign m_Sprite — the reference was left unresolved/skipped.");
        Assert.IsInstanceOf<Sprite>(spriteProp.objectReferenceValue,
            "Builtin(\"UISprite\", \"Sprite\") assigned a " + spriteProp.objectReferenceValue.GetType().Name +
            " — the wrong same-named object (Texture2D 10904) was resolved instead of the Sprite (10905).");
        Assert.AreEqual(expectedSprite, spriteProp.objectReferenceValue,
            "Assigned Sprite is not the SAME object as the live UI/Skin/UISprite.psd oracle.");

        // Half B (Sync): author a BARE Image (no m_Sprite), assign the built-in Sprite live, then
        // sync through the compile-checked seam.
        File.WriteAllText(BuilderPath, Source(
            "        var icon = scene.Add(\"Icon\");\n" +
            "        icon.Component<UnityEngine.UI.Image>();"));
        SceneBuilderBuild.Run(BuilderPath, ScenePath, SidecarPath, EditorSceneManager.GetActiveScene());

        var bareIcon = FindRoot(EditorSceneManager.GetActiveScene(), "Icon");
        Assert.IsNotNull(bareIcon, "Icon disappeared after rebuilding with a bare Image");
        var bareImage = bareIcon.GetComponent<UnityEngine.UI.Image>();
        Assert.IsNotNull(bareImage, "Bare Image was not materialized on Icon");
        bareImage.sprite = expectedSprite;

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(BuilderPath, SidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite assigning the ambiguous built-in Sprite");

        var rewritten = File.ReadAllText(BuilderPath);
        StringAssert.Contains("Builtin(\"UISprite\", \"Sprite\")", rewritten,
            "Builder source did not emit the qualified Builtin(\"UISprite\", \"Sprite\") form.\n" + rewritten);
        StringAssert.DoesNotContain("Builtin(\"UISprite\")", rewritten,
            "Builder source contains the BARE Builtin(\"UISprite\") form — the ambiguous name was not qualified.\n" + rewritten);
    }

    // #8, the anti-churn pin: syncing an editor-created scene primitive twice with NO edit between
    // must be a genuine no-op — Changed==false, PatchEdits==0 (the honest bit: a re-emit that
    // happens to match byte-for-byte would still pass a text-only check), AddedEntries==0,
    // RemovedEntries==0 — and the emitted name must stay BARE, never Builtin("Cube", "Mesh").
    [Test]
    public void SceneToCode_ResyncUnchangedScenePrimitive_IsANoOpAndKeepsBareName()
    {
        File.WriteAllText(BuilderPath, Source("        scene.Add(\"Anchor\");"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(BuilderPath, ScenePath, SidecarPath, scene);

        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = "Cube";

        EmittedCodeCompiles.SyncAndAssertCompiles(BuilderPath, SidecarPath, EditorSceneManager.GetActiveScene());
        var afterFirst = File.ReadAllText(BuilderPath);
        StringAssert.Contains("Builtin(\"Cube\")", afterFirst,
            "Non-vacuity premise failed: first sync did not gain Builtin(\"Cube\").\n" + afterFirst);
        StringAssert.DoesNotContain("Builtin(\"Cube\", \"Mesh\")", afterFirst,
            "First sync stamped a TypeHint on the unambiguous name 'Cube'.\n" + afterFirst);

        var second = EmittedCodeCompiles.SyncAndAssertCompiles(BuilderPath, SidecarPath, EditorSceneManager.GetActiveScene());

        Assert.IsFalse(second.Changed, "NOT CONVERGED: re-syncing an unchanged scene primitive reported Changed=true.");
        Assert.AreEqual(0, second.PatchEdits,
            "NOT CONVERGED: the no-op re-sync's reconcile produced " + second.PatchEdits + " patch edit(s).");
        Assert.AreEqual(0, second.AddedEntries, "No-op re-sync added sidecar entries");
        Assert.AreEqual(0, second.RemovedEntries, "No-op re-sync removed sidecar entries");

        var afterSecond = File.ReadAllText(BuilderPath);
        StringAssert.DoesNotContain("Builtin(\"Cube\", \"Mesh\")", afterSecond,
            "Second sync stamped a TypeHint on the unambiguous name 'Cube'.\n" + afterSecond);
    }

    // #12: a built-in mesh ref survives a scene save/reload round trip (the on-disk YAML carries
    // the well-known fileID/guid), and a resync of the reopened scene is a no-op. The (guid,
    // fileId) literal is derived from the live oracle and asserted as a documented premise, per
    // A3 — a literal in a test asserting real YAML is test data, not a production hardcode.
    [Test]
    public void RoundTrip_BuiltinMeshSurvivesSceneSaveAndReload_AndResyncIsANoOp()
    {
        var expectedCube = PrimitiveMesh(PrimitiveType.Cube);
        Assert.IsTrue(
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(expectedCube, out var guid, out var fileId),
            "Could not derive the live Cube mesh's own (guid, fileId) — test premise invalid");
        Assert.AreEqual("0000000000000000e000000000000000", guid,
            "Cube mesh's container GUID is not the well-known built-in resources container — test premise invalid");
        Assert.AreEqual(10202, fileId,
            "Cube mesh's fileId is not the well-known value (spec §Research) — test premise invalid");

        // NOT the shared Source(...) helper here: it deliberately omits the AssetRefs `using` for a
        // Builtin(...)-only body (see BuiltinRefGateHarness.Source doc — that omission is what proves
        // EnsureAssetRefsUsing on a REAL sync-produced patch). This test's later no-op Sync compile-checks
        // the file as-is with NO patch ever applied to it, so the hand-authored fixture must itself be
        // valid, standalone-compilable C# — exactly what a real user's file needs — hence the explicit
        // using here.
        File.WriteAllText(BuilderPath, @"
using SceneBuilder.Authoring;
using static SceneBuilder.Authoring.AssetRefs;
public class RoundTripBuiltinScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var widget = scene.Add(""Widget"");
        widget.Component<UnityEngine.MeshFilter>(c => c.Set(""m_Mesh"", Builtin(""Cube"")));
    }
}");
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(BuilderPath, ScenePath, SidecarPath, scene);

        var reopened = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var widget = FindRoot(reopened, "Widget");
        Assert.IsNotNull(widget, "Widget was not found after reopening the saved scene");
        var mf = widget.GetComponent<MeshFilter>();
        Assert.IsNotNull(mf, "MeshFilter was not found on Widget after reopening the saved scene");
        Assert.AreEqual(expectedCube, mf.sharedMesh,
            "Built-in mesh reference did not survive the scene save/reload round trip.");

        var yaml = File.ReadAllText(ScenePath);
        StringAssert.Contains($"m_Mesh: {{fileID: {fileId}, guid: {guid}", yaml,
            "Saved scene YAML does not carry the built-in mesh's (fileID, guid) pair.");

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(BuilderPath, SidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(result.Changed, "NOT CONVERGED: resyncing the reopened, unedited scene reported Changed=true.");
        Assert.AreEqual(0, result.PatchEdits,
            "NOT CONVERGED: resyncing the reopened, unedited scene produced " + result.PatchEdits + " patch edit(s).");
        Assert.AreEqual(0, result.AddedEntries, "No-op reload resync added sidecar entries");
        Assert.AreEqual(0, result.RemovedEntries, "No-op reload resync removed sidecar entries");
    }
}
