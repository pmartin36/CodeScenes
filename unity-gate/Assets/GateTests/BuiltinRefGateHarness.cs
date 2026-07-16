using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;

// b4: shared harness for the built-in-resource round-trip gate tests. Mirrors
// RoundTripAssetRefTests.cs's private harness (ScenePath/FixturesDir/Source/FindRoot/LoadMaterial +
// [SetUp]/[TearDown]) but as a shared abstract base so RoundTripBuiltinRefCodeToSceneTests,
// RoundTripBuiltinRefSceneToCodeTests, RoundTripBuiltinRefErrorTests and RoundTripBuiltinRefEmittedCodeTests
// derive from ONE copy instead of four. Owns its OWN ScenePath (distinct from
// __RoundTripAssetRefTemp.unity) so the two suites never collide on the same scene asset; shares
// FixturesDir by convention (each test file creates/deletes its own assets there — safe because NUnit
// runs an assembly's tests sequentially).
public abstract class BuiltinRefGateHarness
{
    protected const string ScenePath = "Assets/GateTests/__RoundTripBuiltinRefTemp.unity";
    protected const string FixturesDir = "Assets/GateTests/Fixtures";
    protected const string RedPath = FixturesDir + "/Red.mat";

    protected string BuilderPath { get; private set; }
    protected string SidecarPath { get; private set; }

    private string _dir;

    // Wrap a Build-body fragment in a minimal ISceneDefinition the Core Roslyn parser understands.
    // VERBATIM copy of RoundTripAssetRefTests.cs's Source(...): keys on body.Contains("Asset(") ONLY.
    // A Builtin(...)-only body deliberately gets NO AssetRefs using — do not extend this to "Builtin(".
    protected static string Source(string body)
    {
        var assetRefsUsing = body.Contains("Asset(")
            ? "using static SceneBuilder.Authoring.AssetRefs;\n"
            : "";

        return $@"
using SceneBuilder.Authoring;
{assetRefsUsing}public class RoundTripBuiltinScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";
    }

    protected static GameObject FindRoot(Scene scene, string name)
    {
        return scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);
    }

    protected static Material LoadMaterial(string path) => AssetDatabase.LoadAssetAtPath<Material>(path);

    // The INDEPENDENT oracle: create the real primitive, take its shared built-in mesh, destroy the
    // donor. The mesh is container-owned and outlives the donor GameObject. Never implemented via
    // BuiltinCatalog — that would compare the resolver under test against itself.
    protected static Mesh PrimitiveMesh(PrimitiveType type)
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

    protected static Material PrimitiveMaterial(PrimitiveType type)
    {
        var donor = GameObject.CreatePrimitive(type);
        try
        {
            var material = donor.GetComponent<MeshRenderer>().sharedMaterial;
            Assert.IsNotNull(material, "CreatePrimitive(" + type + ") produced no material — test premise invalid");
            return material;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(donor);
        }
    }

    [SetUp]
    public void HarnessSetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_rtbr_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        BuilderPath = Path.Combine(_dir, "RoundTripBuiltinScene.cs");
        SidecarPath = Path.Combine(_dir, "RoundTripBuiltinScene.sbmap.json");

        if (!AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "Fixtures");
        }

        AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), RedPath);
        AssetDatabase.SaveAssets();
    }

    [TearDown]
    public void HarnessTearDown()
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
        if (AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.DeleteAsset(FixturesDir);
        }
    }
}
