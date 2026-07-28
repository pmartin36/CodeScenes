using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using SceneBuilder.Editor;
using SceneBuilder.Core.Validation;

// b7-t2 (specs/28-typed-asset-catalogs.md): the bidirectional round-trip at the LIVE Unity
// boundary for the typed asset catalog — rename/move auto-patch, the stale-reference safety net,
// and scene->code emit-typed-by-default. Mirrors PrefabFacadeRenameTests' structure (builder under
// SceneBuilderPaths.EnsureBuildersDirectory so the real AssetCatalogGenerator patch pass, which
// only walks BuildersDirectory, actually fires) and reuses AssetCatalogBuildFixtures (shared with
// b7-t1) verbatim. Per research.md: BuilderCompileCheck cannot resolve `Assets.*` from the RAW
// source tree alone — but (iteration 2) it now sources a compilable `Assets` stub from the CURRENT
// on-disk AssetCatalog (see AssetCatalogCompileCheckTests), so #6/#7's "still compiles" is proven
// by a clean zero-diagnostic re-Build + preserved identity, #8's safety net is proven at the Build
// resolution boundary (SB2203), and #9's scene->code emit is proven to compile HONESTLY — no
// LogAssert.ignoreFailingMessages bracket needed, unlike the still-unaddressed `Prefabs.*` case.
public class AssetCatalogSyncTests
{
    private const string ScenePath = "Assets/GateTests/__AssetCatalogSyncTemp.unity";

    private string _manifestPath;
    private bool _manifestExistedBefore;
    private byte[] _manifestBytesBefore;

    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class AssetCatalogSyncGateScene : ISceneDefinition
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
        _manifestPath = SceneBuilderPaths.AssetManifestPath;
        _manifestExistedBefore = File.Exists(_manifestPath);
        _manifestBytesBefore = _manifestExistedBefore ? File.ReadAllBytes(_manifestPath) : null;

        AssetCatalogBuildFixtures.Create();

        var buildersDir = SceneBuilderPaths.EnsureBuildersDirectory();
        var name = "AssetCatalogSyncGate_" + Guid.NewGuid().ToString("N");
        _builderPath = Path.Combine(buildersDir, name + ".cs");
        _sidecarPath = Path.Combine(buildersDir, name + ".sbmap.json");

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_builderPath))
        {
            File.Delete(_builderPath);
        }
        if (File.Exists(_sidecarPath))
        {
            File.Delete(_sidecarPath);
        }

        AssetCatalogBuildFixtures.Delete();

        if (AssetDatabase.IsValidFolder(AssetCatalogBuildFixtures.Dir + "/Foundations"))
        {
            AssetDatabase.DeleteAsset(AssetCatalogBuildFixtures.Dir + "/Foundations");
        }

        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }

        if (_manifestExistedBefore)
        {
            File.WriteAllBytes(_manifestPath, _manifestBytesBefore);
        }
        else if (File.Exists(_manifestPath))
        {
            File.Delete(_manifestPath);
        }
    }

    // Checklist #6: renaming the catalogued Environment/Rocks/Stone.mat -> Granite.mat fires the
    // real postprocessor -> regen -> AssetReferencePatcher chain, rewriting the live builder's LEAF
    // token in place; a clean re-Build resolves to the SAME material.
    [Test]
    public void RenameMaterial_PatchesLiveBuilderLeaf_ReBuildsToSameMaterial()
    {
        File.WriteAllText(_builderPath, Source(
            "        var rock = scene.Add(\"Rock\");\n" +
            "        rock.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { " + AssetCatalogBuildFixtures.TypedRocksStone + " }));"));

        var scene = EditorSceneManager.GetActiveScene();
        var setupResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(setupResult.Diagnostics,
            "Setup build reported diagnostics: " + string.Join("; ", setupResult.Diagnostics.Select(d => d.Message)));

        var rockBefore = FindRoot(EditorSceneManager.GetActiveScene(), "Rock");
        Assert.IsNotNull(rockBefore, "Setup: Rock was not created by SceneBuilderBuild.Run");
        var materialGuidBefore = AssetDatabase.AssetPathToGUID(AssetCatalogBuildFixtures.RocksMatPath);

        var renameError = AssetDatabase.RenameAsset(AssetCatalogBuildFixtures.RocksMatPath, "Granite");
        Assert.IsEmpty(renameError, "Setup: RenameAsset failed: " + renameError);
        AssetDatabase.Refresh();

        var patched = File.ReadAllText(_builderPath);
        var typedGranite = "Assets.Materials." + AssetCatalogBuildFixtures.RootPrefix + ".Environment.Rocks.Granite";
        StringAssert.Contains(typedGranite, patched,
            "The rename patch did not rewrite the builder's leaf token to the renamed accessor.\n" + patched);
        StringAssert.DoesNotContain("Environment.Rocks.Stone", patched,
            "Stale 'Stone' accessor survived the rename patch.\n" + patched);

        var rebuildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(rebuildResult.Diagnostics,
            "Rebuild against the patched typed builder reported diagnostics: "
            + string.Join("; ", rebuildResult.Diagnostics.Select(d => d.Message)));

        var rockAfter = FindRoot(EditorSceneManager.GetActiveScene(), "Rock");
        Assert.IsNotNull(rockAfter, "Rock disappeared after the rebuild");
        var materialAfter = rockAfter.GetComponent<MeshRenderer>().sharedMaterial;
        Assert.IsNotNull(materialAfter, "Rebuild left the renderer's material slot empty");
        Assert.AreEqual(materialGuidBefore, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(materialAfter)),
            "Rebuild resolved to a DIFFERENT asset GUID than before the rename — the rename must preserve identity.");
    }

    // Checklist #7: moving the (already-renamed) Granite.mat into a new Foundations/ folder rewrites
    // the live builder's FOLDER SEGMENTS in place — the milestone's core novelty over the flat
    // façade shape. Asserts the source WAS patched (not left unchanged); a clean re-Build resolves
    // to the SAME material.
    [Test]
    public void MoveMaterial_RewritesFolderSegmentsInPlace_ReBuildsToSameMaterial()
    {
        File.WriteAllText(_builderPath, Source(
            "        var rock = scene.Add(\"Rock\");\n" +
            "        rock.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { " + AssetCatalogBuildFixtures.TypedRocksStone + " }));"));

        var scene = EditorSceneManager.GetActiveScene();
        var setupResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(setupResult.Diagnostics,
            "Setup build reported diagnostics: " + string.Join("; ", setupResult.Diagnostics.Select(d => d.Message)));

        var materialGuidBefore = AssetDatabase.AssetPathToGUID(AssetCatalogBuildFixtures.RocksMatPath);

        Assert.IsEmpty(AssetDatabase.RenameAsset(AssetCatalogBuildFixtures.RocksMatPath, "Granite"),
            "Setup: RenameAsset (Stone -> Granite) failed.");
        AssetDatabase.Refresh();

        var renamedPath = AssetCatalogBuildFixtures.Dir + "/Environment/Rocks/Granite.mat";
        AssetDatabase.CreateFolder(AssetCatalogBuildFixtures.Dir, "Foundations");
        var movedPath = AssetCatalogBuildFixtures.Dir + "/Foundations/Granite.mat";
        var moveError = AssetDatabase.MoveAsset(renamedPath, movedPath);
        Assert.IsEmpty(moveError, "Setup: MoveAsset (Rocks -> Foundations) failed: " + moveError);
        AssetDatabase.Refresh();

        var patched = File.ReadAllText(_builderPath);
        var typedFoundationsGranite = "Assets.Materials." + AssetCatalogBuildFixtures.RootPrefix + ".Foundations.Granite";
        StringAssert.Contains(typedFoundationsGranite, patched,
            "The move patch did not rewrite the builder's folder segments to the new location.\n" + patched);
        StringAssert.DoesNotContain("Environment.Rocks.Granite", patched,
            "The stale pre-move folder chain survived the move patch — the source must be PATCHED, not left unchanged.\n" + patched);

        var rebuildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(rebuildResult.Diagnostics,
            "Rebuild against the move-patched typed builder reported diagnostics: "
            + string.Join("; ", rebuildResult.Diagnostics.Select(d => d.Message)));

        var rockAfter = FindRoot(EditorSceneManager.GetActiveScene(), "Rock");
        Assert.IsNotNull(rockAfter, "Rock disappeared after the rebuild");
        var materialAfter = rockAfter.GetComponent<MeshRenderer>().sharedMaterial;
        Assert.IsNotNull(materialAfter, "Rebuild left the renderer's material slot empty");
        Assert.AreEqual(materialGuidBefore, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(materialAfter)),
            "Rebuild resolved to a DIFFERENT asset GUID than before the move — the move must preserve identity.");
    }

    // Checklist #8: a builder hand-edited back to a chain that is stale AFTER a real rename+move
    // (bypassing the patcher) must be REFUSED by Build with a located SB2203 diagnostic, and the
    // scene must be left untouched — the safety net that stands in for the (headless-proven, b5-t1)
    // compile error the editor AppDomain cannot itself reproduce for `Assets.*`.
    [Test]
    public void StaleTypedRefAfterMove_RefusesBuild_WithLocatedSB2203()
    {
        Assert.IsEmpty(AssetDatabase.RenameAsset(AssetCatalogBuildFixtures.RocksMatPath, "Granite"),
            "Setup: RenameAsset (Stone -> Granite) failed.");
        AssetDatabase.Refresh();

        var renamedPath = AssetCatalogBuildFixtures.Dir + "/Environment/Rocks/Granite.mat";
        AssetDatabase.CreateFolder(AssetCatalogBuildFixtures.Dir, "Foundations");
        Assert.IsEmpty(AssetDatabase.MoveAsset(renamedPath, AssetCatalogBuildFixtures.Dir + "/Foundations/Granite.mat"),
            "Setup: MoveAsset (Rocks -> Foundations) failed.");
        AssetDatabase.Refresh();

        // Hand-write the builder back to the now-stale pre-rename/move chain, WITHOUT going through
        // the patcher.
        File.WriteAllText(_builderPath, Source(
            "        var stale = scene.Add(\"StaleWidget\");\n" +
            "        stale.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { " + AssetCatalogBuildFixtures.TypedRocksStone + " }));"));

        var scene = EditorSceneManager.GetActiveScene();
        var result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        Assert.IsNotEmpty(result.Diagnostics, "A stale post-rename/move accessor must refuse the build, not silently succeed.");
        var diagnostic = result.Diagnostics.FirstOrDefault(d => d.Code == DiagnosticCodes.UnknownFacadeReference);
        Assert.IsNotNull(diagnostic,
            "Expected a " + DiagnosticCodes.UnknownFacadeReference + " (UnknownFacadeReference) diagnostic.\n"
            + string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        Assert.Greater(diagnostic.Line, 0, "Diagnostic must be located (Line > 0).");

        var root = FindRoot(EditorSceneManager.GetActiveScene(), "StaleWidget");
        Assert.IsNull(root, "Scene must be untouched on a refused build.");
    }

    // Checklist #9: a scene edit assigning a catalogued material emits the FULL typed
    // Assets.<Group>.<folders...>.<Member> chain (never the raw Asset("path") string form); a scene
    // edit assigning a built-in mesh still falls back to Builtin(...) (built-ins are never
    // catalogued). Sync's own internal BuilderCompileCheck must resolve the generator-emitted
    // `Assets.*` root from the current on-disk AssetCatalog (see AssetCatalogCompileCheckTests), so
    // — unlike the still-unaddressed `Prefabs.*` limitation — this Sync call needs NO
    // LogAssert.ignoreFailingMessages bracket: a clean sync honestly proves the typed emit compiles.
    [Test]
    public void SceneEdit_CatalogedMaterial_EmitsTypedChain_BuiltinMeshEmitsBuiltin()
    {
        File.WriteAllText(_builderPath, Source(
            "        var matWidget = scene.Add(\"MatWidget\");\n" +
            "        matWidget.Component<UnityEngine.MeshRenderer>();\n" +
            "        var meshWidget = scene.Add(\"MeshWidget\");\n" +
            "        meshWidget.Component<UnityEngine.MeshFilter>();"));

        var scene = EditorSceneManager.GetActiveScene();
        var setupResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(setupResult.Diagnostics,
            "Setup build reported diagnostics: " + string.Join("; ", setupResult.Diagnostics.Select(d => d.Message)));

        var matWidget = FindRoot(EditorSceneManager.GetActiveScene(), "MatWidget");
        var meshWidget = FindRoot(EditorSceneManager.GetActiveScene(), "MeshWidget");
        Assert.IsNotNull(matWidget, "Setup: MatWidget was not created by SceneBuilderBuild.Run");
        Assert.IsNotNull(meshWidget, "Setup: MeshWidget was not created by SceneBuilderBuild.Run");

        matWidget.GetComponent<MeshRenderer>().sharedMaterial =
            AssetDatabase.LoadAssetAtPath<Material>(AssetCatalogBuildFixtures.RocksMatPath);

        var donor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var builtinMesh = donor.GetComponent<MeshFilter>().sharedMesh;
        UnityEngine.Object.DestroyImmediate(donor);
        Assert.IsNotNull(builtinMesh, "Setup: could not obtain a built-in Cube mesh — test premise invalid");
        meshWidget.GetComponent<MeshFilter>().sharedMesh = builtinMesh;

        var syncResult = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(syncResult.Changed, "Sync reported no change despite an assigned catalogued material and a built-in mesh");
        Assert.IsEmpty(syncResult.CompileErrors,
            "Sync's own internal compile check reported errors on its typed emission — the "
            + "generator-emitted `Assets` root must resolve honestly from the current catalog.\n"
            + string.Join("; ", syncResult.CompileErrors.Select(e => e.ToString())));

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains(AssetCatalogBuildFixtures.TypedRocksStone, rewritten,
            "Scene->code sync of a catalogued material did not emit the typed Assets.<...> chain.\n" + rewritten);
        StringAssert.DoesNotContain("Asset(\"" + AssetCatalogBuildFixtures.RocksMatPath, rewritten,
            "Scene->code sync emitted the raw string Asset(path) form instead of the typed catalog chain.\n" + rewritten);
        StringAssert.Contains("Builtin(", rewritten,
            "Scene->code sync of a built-in mesh did not fall back to Builtin(...).\n" + rewritten);
    }
}
