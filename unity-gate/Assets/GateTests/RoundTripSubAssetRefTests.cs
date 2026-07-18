using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using SceneBuilder.Editor;
using SceneBuilder.Core.Model;

// M-SubAsset (specs/21-project-subasset-refs.md) bidirectional round-trip gate. b4-t1 lands the
// fixture + the harness the whole class shares: a GENUINE Unity sub-object (a second Mesh added to
// a container .asset via AddObjectToAsset — NOT a bare CreateAsset, which only ever produces a MAIN
// asset and would make every downstream test in this class vacuous), plus one self-validating test
// that (a) proves the fixture really is a distinct sub-object, and (b) proves both the 1-arg and
// 2-arg Asset(...) authoring forms resolve, through the real adapter, to the correct object.
public class RoundTripSubAssetRefTests
{
    private const string ModelsDir = "Assets/Models";
    private const string AssetPath = ModelsDir + "/Barrel.subasset.asset";
    private const string ScenePath = ModelsDir + "/__RoundTripSubAssetScene.unity";

    private Mesh _main;
    private Mesh _sub;
    private bool _createdModelsDir;

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    // Mirrors RoundTripAssetRefTests.Source / BuiltinRefGateHarness — do not invent a new pattern.
    private static string Source(string body)
    {
        var assetRefsUsing = body.Contains("Asset(")
            ? "using static SceneBuilder.Authoring.AssetRefs;\n"
            : "";

        return $@"
using SceneBuilder.Authoring;
{assetRefsUsing}public class RoundTripSubAssetScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";
    }

    private static GameObject FindRoot(UnityEngine.SceneManagement.Scene scene, string name)
    {
        return scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);
    }

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_rtsar_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "RoundTripSubAssetScene.cs");
        _sidecarPath = Path.Combine(_dir, "RoundTripSubAssetScene.sbmap.json");

        _createdModelsDir = !AssetDatabase.IsValidFolder(ModelsDir);
        if (_createdModelsDir)
        {
            AssetDatabase.CreateFolder("Assets", "Models");
        }

        _main = new Mesh { name = "BarrelMain" };
        AssetDatabase.CreateAsset(_main, AssetPath);

        _sub = new Mesh { name = "BarrelMesh" };
        AssetDatabase.AddObjectToAsset(_sub, _main);

        AssetDatabase.ImportAsset(AssetPath);
        AssetDatabase.SaveAssets();
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        var guid = AssetDatabase.AssetPathToGUID(AssetPath);
        var livePath = string.IsNullOrEmpty(guid) ? AssetPath : AssetDatabase.GUIDToAssetPath(guid);
        if (!string.IsNullOrEmpty(livePath))
        {
            AssetDatabase.DeleteAsset(livePath);
        }

        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }

        if (_createdModelsDir && AssetDatabase.IsValidFolder(ModelsDir))
        {
            AssetDatabase.DeleteAsset(ModelsDir);
        }
    }

    // Fixture integrity + resolution proof: asserts the fixture is a REAL sub-object (shared GUID,
    // distinct fileId from the main asset), and that both authoring forms —
    // Asset(path,"BarrelMesh") and Asset(path) — resolve through the real adapter (DesiredModelLoader
    // -> AssetRefLowering -> AssetReferenceResolver) to the sub-object and main-object's OWN derived
    // ids respectively (never a hard-coded fileId, so the assertion survives editor-version churn).
    [Test]
    public void Fixture_SubAssetAndMainResolveToDistinctObjects()
    {
        // (a) fixture integrity.
        Assert.AreEqual(_main, AssetDatabase.LoadMainAssetAtPath(AssetPath),
            "LoadMainAssetAtPath did not return the container's main object — fixture premise invalid");
        Assert.AreNotEqual(_sub, AssetDatabase.LoadMainAssetAtPath(AssetPath),
            "LoadMainAssetAtPath returned the SUB object, not main — fixture premise invalid");

        Assert.IsTrue(
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(_main, out var mainGuid, out var mainFileId),
            "Could not derive (guid, fileId) for the main asset");
        Assert.IsTrue(
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(_sub, out var subGuid, out var subFileId),
            "Could not derive (guid, fileId) for the sub asset");

        Assert.AreEqual(mainGuid, subGuid,
            "Sub-object does not share the container's GUID — fixture premise invalid");
        Assert.AreNotEqual(mainFileId, subFileId,
            "Sub-object's fileId equals the main asset's fileId — not a genuine distinct sub-object");

        // (b) resolution: 2-arg form resolves to the sub-object.
        var subSource = Source(
            "        var widget = scene.Add(\"Widget\");\n" +
            "        widget.Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Asset(\"" + AssetPath + "\", \"BarrelMesh\")));");
        var subLoaded = DesiredModelLoader.Load(subSource, null);
        var subRef = LoadedMeshRef(subLoaded, "Widget");
        Assert.AreEqual(subGuid, subRef.Guid, "Asset(path,\"BarrelMesh\") did not resolve to the sub-object's GUID");
        Assert.AreEqual(subFileId, subRef.FileId, "Asset(path,\"BarrelMesh\") did not resolve to the sub-object's fileId");

        // (b) resolution: 1-arg form resolves to the main object.
        var mainSource = Source(
            "        var widget = scene.Add(\"Widget\");\n" +
            "        widget.Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Asset(\"" + AssetPath + "\")));");
        var mainLoaded = DesiredModelLoader.Load(mainSource, null);
        var mainRef = LoadedMeshRef(mainLoaded, "Widget");
        Assert.AreEqual(mainGuid, mainRef.Guid, "Asset(path) did not resolve to the main object's GUID");
        Assert.AreEqual(mainFileId, mainRef.FileId, "Asset(path) did not resolve to the main object's fileId");
    }

    // Checklist #1: Build assigns the SUB-mesh, by object identity. Authoring
    // Asset(path,"BarrelMesh") on a MeshFilter must materialize the live MeshFilter.sharedMesh to the
    // sub-object (_sub), NOT the container's main object (_main). This is the direct catch for the
    // main-asset-collapse bug: a lowering that dropped the sub-name would assign _main (a type
    // mismatch that leaves the slot null) or the wrong object.
    [Test]
    public void CodeToScene_SubAssetMesh_BuildAssignsTheSubMeshByIdentity()
    {
        File.WriteAllText(_builderPath, Source(
            "        var widget = scene.Add(\"Widget\");\n" +
            "        widget.Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Asset(\"" + AssetPath + "\", \"BarrelMesh\")));"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var widget = FindRoot(EditorSceneManager.GetActiveScene(), "Widget");
        Assert.IsNotNull(widget, "Widget was not created by SceneBuilderBuild.Run");
        var mf = widget.GetComponent<MeshFilter>();
        Assert.IsNotNull(mf, "Authored MeshFilter was not materialized on Widget");
        Assert.AreEqual(_sub, mf.sharedMesh,
            "Build did not assign the sub-mesh (_sub) — the sub-asset name was dropped or resolved wrong.");
        Assert.AreNotEqual(_main, mf.sharedMesh,
            "Build collapsed the sub-asset ref to the container's MAIN object (_main).");
    }

    // Checklist #5 (the round-trip make-or-break — PROVES b3-t3's read-side SubAsset emit): a
    // MeshFilter HAND-ASSIGNED in the scene to the sub-object must Sync back as the 2-arg
    // Asset(path,"BarrelMesh") form — never the bare 1-arg Asset(path) that re-lowers to the
    // main asset and mis-assigns on the next Build. The bare MeshFilter is authored first (no
    // m_Mesh) so the field is genuinely produced by the read side, then a single sync — the
    // component is matched in both source and snapshot, so the field-diff pass reaches m_Mesh.
    [Test]
    public void SceneToCode_HandAssignedSubMesh_EmitsTwoArgAssetForm()
    {
        File.WriteAllText(_builderPath, Source(
            "        var widget = scene.Add(\"Widget\");\n" +
            "        widget.Component<UnityEngine.MeshFilter>();"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var widget = FindRoot(EditorSceneManager.GetActiveScene(), "Widget");
        Assert.IsNotNull(widget, "Widget was not created by SceneBuilderBuild.Run");
        var mf = widget.GetComponent<MeshFilter>();
        Assert.IsNotNull(mf, "Bare MeshFilter was not materialized on Widget");
        mf.sharedMesh = _sub;

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite a hand-assigned sub-mesh");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("Asset(\"" + AssetPath + "\", \"BarrelMesh\")", rewritten,
            "Sync did not emit the 2-arg Asset(path, \"BarrelMesh\") form for the hand-assigned sub-mesh.\n" + rewritten);
        StringAssert.DoesNotContain("Asset(\"" + AssetPath + "\")", rewritten,
            "Sync emitted the bare 1-arg Asset(path) form — the sub-object name was dropped on read.\n" + rewritten);
    }

    // Checklist #4: re-syncing an unchanged authored sub-mesh is a genuine no-op — the second sync's
    // Changed==false and the source NEVER degrades to the main-asset-collapsing 1-arg form. Per the
    // sibling built-in test's non-vacuity note, the field-diff pass only evaluates m_Mesh on the
    // SECOND sync (the first maps the component into the snapshot), so two syncs are required to
    // exercise the AuthoredTextIsCurrent + read-side SubAsset anti-churn rule.
    [Test]
    public void SceneToCode_ResyncUnchangedSubMesh_IsANoOpAndKeepsTwoArgForm()
    {
        File.WriteAllText(_builderPath, Source(
            "        var widget = scene.Add(\"Widget\");\n" +
            "        widget.Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Asset(\"" + AssetPath + "\", \"BarrelMesh\")));"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        var afterFirst = File.ReadAllText(_builderPath);
        StringAssert.Contains("Asset(\"" + AssetPath + "\", \"BarrelMesh\")", afterFirst,
            "Non-vacuity premise failed: authored source lost the 2-arg form after the first sync.\n" + afterFirst);

        var second = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(second.Changed,
            "NOT CONVERGED: re-syncing an unchanged authored sub-mesh reported Changed=true.");
        Assert.AreEqual(0, second.PatchEdits,
            "NOT CONVERGED: the no-op re-sync's reconcile produced " + second.PatchEdits + " patch edit(s).");

        var afterSecond = File.ReadAllText(_builderPath);
        StringAssert.Contains("Asset(\"" + AssetPath + "\", \"BarrelMesh\")", afterSecond,
            "The 2-arg form was lost on the no-op re-sync.\n" + afterSecond);
        Assert.IsFalse(
            System.Text.RegularExpressions.Regex.IsMatch(afterSecond, @"Asset\(""" + System.Text.RegularExpressions.Regex.Escape(AssetPath) + @"""\)"),
            "The source degraded to the bare 1-arg Asset(path) form on the no-op re-sync.\n" + afterSecond);
    }

    // Checklist #7: an unknown sub-asset name is a LOUD, LOCATED refusal — Build's collect-all
    // planning walk (never a throw, never a silent collapse to main) returns a diagnostic naming the
    // bad name AND listing the available sub-object names at that path, and REFUSES: the scene is left
    // untouched (the Widget is never created).
    [Test]
    public void CodeToScene_UnknownSubAssetName_RefusesWithLocatedDiagnosticListingAvailableNames()
    {
        File.WriteAllText(_builderPath, Source(
            "        var widget = scene.Add(\"Widget\");\n" +
            "        widget.Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Asset(\"" + AssetPath + "\", \"NoSuchName\")));"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();

        var result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        Assert.IsNotEmpty(result.Diagnostics,
            "Build silently succeeded on an unknown sub-asset name instead of refusing with a diagnostic.");
        var text = string.Join("\n", result.Diagnostics.Select(d => d.Message + " " + d.Suggestion));
        StringAssert.Contains("NoSuchName", text,
            "Refusal diagnostic did not name the bad sub-asset 'NoSuchName'.\n" + text);
        StringAssert.Contains("BarrelMesh", text,
            "Refusal diagnostic did not list the AVAILABLE sub-asset name 'BarrelMesh'.\n" + text);

        Assert.IsNull(FindRoot(EditorSceneManager.GetActiveScene(), "Widget"),
            "Build mutated the scene (created Widget) despite refusing the unknown sub-asset name.");
    }

    // Checklist #2/#3: swap a MeshFilter between the project sub-mesh and a built-in (Sphere) and
    // back — the two ref families converge in one slot. Sync of the built-in swap emits Builtin("Sphere")
    // and drops the 2-arg Asset form; re-authoring the sub-mesh + Build returns the slot to _sub.
    [Test]
    public void RoundTrip_SubMeshToBuiltinAndBack_Converges()
    {
        var sphere = PrimitiveMesh(PrimitiveType.Sphere);

        File.WriteAllText(_builderPath, Source(
            "        var widget = scene.Add(\"Widget\");\n" +
            "        widget.Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Asset(\"" + AssetPath + "\", \"BarrelMesh\")));"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var widget = FindRoot(EditorSceneManager.GetActiveScene(), "Widget");
        var mf = widget.GetComponent<MeshFilter>();
        Assert.AreEqual(_sub, mf.sharedMesh, "Phase-1 build did not assign the sub-mesh — test premise invalid");

        // Scene->code: swap to the built-in Sphere.
        mf.sharedMesh = sphere;
        var toBuiltin = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(toBuiltin.Changed, "Sync reported no change despite swapping to the built-in Sphere mesh");

        var afterSwap = File.ReadAllText(_builderPath);
        StringAssert.Contains("Builtin(\"Sphere\")", afterSwap,
            "Builder source did not gain Builtin(\"Sphere\") after the sub-mesh->built-in swap.\n" + afterSwap);
        StringAssert.DoesNotContain("Asset(\"" + AssetPath + "\", \"BarrelMesh\")", afterSwap,
            "Builder source still carries the sub-mesh Asset(...) after the swap to a built-in.\n" + afterSwap);

        // Code->scene: re-author the sub-mesh and rebuild — the slot returns to _sub.
        File.WriteAllText(_builderPath, Source(
            "        var widget = scene.Add(\"Widget\");\n" +
            "        widget.Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Asset(\"" + AssetPath + "\", \"BarrelMesh\")));"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var rebuilt = FindRoot(EditorSceneManager.GetActiveScene(), "Widget");
        var rebuiltMf = rebuilt.GetComponent<MeshFilter>();
        Assert.AreEqual(_sub, rebuiltMf.sharedMesh,
            "Re-authoring the sub-mesh did not return the slot to _sub.");
    }

    // Checklist #9: the 1-arg Asset("Red.mat") main-asset form still assigns the material main asset
    // and round-trips as the 1-arg form (never gaining a spurious sub-name) — proving the 2-arg
    // read-side discriminator (LoadMainAssetAtPath != obj) does NOT mis-tag a plain main asset.
    [Test]
    public void RoundTrip_MainAssetMaterial_StillOneArgFormAndNoSpuriousSubName()
    {
        var matPath = ModelsDir + "/RegRed.mat";
        AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), matPath);
        AssetDatabase.SaveAssets();
        try
        {
            var expected = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            Assert.IsNotNull(expected, "Regression material fixture did not import — test premise invalid");

            File.WriteAllText(_builderPath, Source(
                "        var widget = scene.Add(\"Widget\");\n" +
                "        widget.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Asset(\"" + matPath + "\") }));"));
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var scene = EditorSceneManager.GetActiveScene();
            SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

            var widget = FindRoot(EditorSceneManager.GetActiveScene(), "Widget");
            var mr = widget.GetComponent<MeshRenderer>();
            Assert.AreEqual(expected, mr.sharedMaterial,
                "Build did not assign the material main asset for the 1-arg Asset(path) form.");

            // A no-op re-sync must not stamp a sub-name onto the main asset.
            EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
            var second = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
            Assert.IsFalse(second.Changed, "NOT CONVERGED: re-syncing an unchanged main-asset material reported Changed=true.");

            var rewritten = File.ReadAllText(_builderPath);
            StringAssert.Contains("Asset(\"" + matPath + "\")", rewritten,
                "Main-asset ref lost its 1-arg Asset(path) form.\n" + rewritten);
            StringAssert.DoesNotContain(matPath + "\", \"", rewritten,
                "Main-asset ref gained a spurious 2-arg sub-name — the read-side discriminator mis-tagged a main asset.\n" + rewritten);
        }
        finally
        {
            AssetDatabase.DeleteAsset(matPath);
        }
    }

    // Independent built-in mesh oracle (mirrors BuiltinRefGateHarness.PrimitiveMesh — this class does
    // not derive from that harness): create the primitive, take its container-owned shared mesh,
    // destroy the donor.
    private static Mesh PrimitiveMesh(PrimitiveType type)
    {
        var donor = GameObject.CreatePrimitive(type);
        try
        {
            var mesh = donor.GetComponent<MeshFilter>().sharedMesh;
            Assert.IsNotNull(mesh, "CreatePrimitive(" + type + ") produced no mesh — test premise invalid");
            return mesh;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(donor);
        }
    }

    private static AssetRef LoadedMeshRef(DesiredModelLoader.Loaded loaded, string rootName)
    {
        var root = loaded.Desired.Roots.FirstOrDefault(g => g.Name == rootName);
        Assert.IsNotNull(root, rootName + " root not found in the desired model");
        var mf = root.Components.FirstOrDefault(c => c.Type.FullName == "UnityEngine.MeshFilter");
        Assert.IsNotNull(mf, "MeshFilter component not found on " + rootName);
        Assert.IsTrue(mf.Fields.TryGetValue("m_Mesh", out var value), "m_Mesh field not found on MeshFilter");
        var assetRef = ((ValueNode.AssetRef)value).Ref;
        Assert.IsNotNull(assetRef, "m_Mesh lowered to a null AssetRef");
        return assetRef;
    }
}
