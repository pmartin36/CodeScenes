using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using SceneBuilder.Editor;

// A bare sub-asset name (`Asset(path, "start")`) that matches several sub-objects of DIFFERENT
// Unity types resolves to the one assignable to the target field's expected type, rather than
// refusing the build as ambiguous. Covers both a NATIVE serialized field (MeshFilter.m_Mesh has no
// managed C# field — its expected type comes only from the PPtr short name) and a MANAGED field on
// a plain user MonoBehaviour (resolved by reflection). A genuinely ambiguous pair — two sub-objects
// of the SAME expected type sharing a name — still refuses with a located diagnostic.
public class SubAssetTypeDisambiguationTests
{
    private const string ModelsDir = "Assets/Models";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;
    private string _scenePath;
    private readonly List<string> _createdAssetPaths = new();
    private bool _createdModelsDir;

    private static string Source(string body)
    {
        return $@"
using SceneBuilder.Authoring;
using static SceneBuilder.Authoring.AssetRefs;
public class SubAssetTypeScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";
    }

    private static GameObject FindRoot(UnityEngine.SceneManagement.Scene scene, string name) =>
        scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_satd_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "SubAssetTypeScene.cs");
        _sidecarPath = Path.Combine(_dir, "SubAssetTypeScene.sbmap.json");
        _scenePath = ModelsDir + "/__SubAssetTypeScene.unity";

        _createdModelsDir = !AssetDatabase.IsValidFolder(ModelsDir);
        if (_createdModelsDir)
        {
            AssetDatabase.CreateFolder("Assets", "Models");
        }
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        foreach (var path in _createdAssetPaths)
        {
            var guid = AssetDatabase.AssetPathToGUID(path);
            var livePath = string.IsNullOrEmpty(guid) ? path : AssetDatabase.GUIDToAssetPath(guid);
            if (!string.IsNullOrEmpty(livePath))
            {
                AssetDatabase.DeleteAsset(livePath);
            }
        }
        _createdAssetPaths.Clear();

        if (File.Exists(_scenePath))
        {
            AssetDatabase.DeleteAsset(_scenePath);
        }

        if (_createdModelsDir && AssetDatabase.IsValidFolder(ModelsDir))
        {
            AssetDatabase.DeleteAsset(ModelsDir);
        }
    }

    // A container asset carrying a Mesh AND a Material, both named "start" — the same bare name
    // resolves to two different Unity types.
    private string CreateMixedTypeContainer(string assetName, out Mesh mesh, out Material material)
    {
        var path = $"{ModelsDir}/{assetName}.asset";
        var main = new Mesh { name = "MainContainer" };
        AssetDatabase.CreateAsset(main, path);

        mesh = new Mesh { name = "start" };
        AssetDatabase.AddObjectToAsset(mesh, main);

        material = new Material(Shader.Find("Standard")) { name = "start" };
        AssetDatabase.AddObjectToAsset(material, main);

        AssetDatabase.ImportAsset(path);
        AssetDatabase.SaveAssets();
        _createdAssetPaths.Add(path);
        return path;
    }

    // A container asset carrying two Meshes, both named "start" — a genuine same-type ambiguity.
    private string CreateDuplicateMeshContainer(string assetName)
    {
        var path = $"{ModelsDir}/{assetName}.asset";
        var main = new Mesh { name = "MainContainer" };
        AssetDatabase.CreateAsset(main, path);

        AssetDatabase.AddObjectToAsset(new Mesh { name = "start" }, main);
        AssetDatabase.AddObjectToAsset(new Mesh { name = "start" }, main);

        AssetDatabase.ImportAsset(path);
        AssetDatabase.SaveAssets();
        _createdAssetPaths.Add(path);
        return path;
    }

    // MeshFilter.m_Mesh has no managed C# field (native PPtr<Mesh>-only serialization) — its
    // expected type can only come from the serialized property's PPtr short name.
    [Test]
    public void NativeMeshField_TwoTypesShareName_ResolvesToAssignableType_NoAmbiguousRefusal()
    {
        var path = CreateMixedTypeContainer("MixedNative", out var mesh, out _);

        File.WriteAllText(_builderPath, Source(
            "        var widget = scene.Add(\"Widget\");\n" +
            "        widget.Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Asset(\"" + path + "\", \"start\")));"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        var result = SceneBuilderBuild.Run(_builderPath, _scenePath, _sidecarPath, scene);

        Assert.IsEmpty(result.Diagnostics,
            "Build refused a type-disambiguated m_Mesh sub-asset reference as ambiguous: "
            + string.Join(" | ", result.Diagnostics.Select(d => d.Message)));

        var widget = FindRoot(EditorSceneManager.GetActiveScene(), "Widget");
        Assert.IsNotNull(widget, "Widget was not created by SceneBuilderBuild.Run");
        var mf = widget.GetComponent<MeshFilter>();
        Assert.IsNotNull(mf, "Authored MeshFilter was not materialized on Widget");
        Assert.AreEqual(mesh, mf.sharedMesh,
            "Build did not resolve the type-ambiguous sub-asset name to the Mesh assignable to m_Mesh.");
    }

    // A managed field on a plain MonoBehaviour resolves through reflection, with no throwaway
    // component instance needed — the fast rung of expected-type derivation.
    [Test]
    public void ManagedMaterialField_TwoTypesShareName_ResolvesToAssignableType_NoAmbiguousRefusal()
    {
        var path = CreateMixedTypeContainer("MixedManaged", out _, out var material);

        File.WriteAllText(_builderPath, Source(
            "        var widget = scene.Add(\"Widget\");\n" +
            "        widget.Component<SubAssetTypeMaterialHolder>(c => c.Set(\"mat\", Asset(\"" + path + "\", \"start\")));"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        var result = SceneBuilderBuild.Run(_builderPath, _scenePath, _sidecarPath, scene);

        Assert.IsEmpty(result.Diagnostics,
            "Build refused a type-disambiguated managed Material field reference as ambiguous: "
            + string.Join(" | ", result.Diagnostics.Select(d => d.Message)));

        var widget = FindRoot(EditorSceneManager.GetActiveScene(), "Widget");
        Assert.IsNotNull(widget, "Widget was not created by SceneBuilderBuild.Run");
        var holder = widget.GetComponent<SubAssetTypeMaterialHolder>();
        Assert.IsNotNull(holder, "Authored SubAssetTypeMaterialHolder was not materialized on Widget");
        Assert.AreEqual(material, holder.mat,
            "Build did not resolve the type-ambiguous sub-asset name to the Material assignable to the field.");
    }

    // The type filter narrows only when it leaves a non-empty candidate set — two sub-objects of
    // the SAME expected type sharing a name is a genuine ambiguity it must not paper over.
    [Test]
    public void NativeMeshField_TwoSameTypeCandidates_StillRefusesWithLocatedAmbiguousDiagnostic()
    {
        var path = CreateDuplicateMeshContainer("DuplicateMesh");

        File.WriteAllText(_builderPath, Source(
            "        var widget = scene.Add(\"Widget\");\n" +
            "        widget.Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Asset(\"" + path + "\", \"start\")));"));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        var result = SceneBuilderBuild.Run(_builderPath, _scenePath, _sidecarPath, scene);

        Assert.IsNotEmpty(result.Diagnostics,
            "Build silently resolved a genuinely ambiguous sub-asset name (two same-typed candidates).");
        var text = string.Join(" | ", result.Diagnostics.Select(d => d.Message)).ToLowerInvariant();
        StringAssert.Contains("ambiguous", text,
            "Refusal diagnostic did not report the sub-asset name as ambiguous.\n" + text);

        Assert.IsNull(FindRoot(EditorSceneManager.GetActiveScene(), "Widget"),
            "Build mutated the scene despite refusing a genuinely ambiguous sub-asset name.");
    }
}
