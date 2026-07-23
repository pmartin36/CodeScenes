using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SceneBuilder.Editor;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;

// b6-t4 (specs/25-typed-prefab-facades.md): checklist #5/#6 at the LIVE Unity boundary, against the
// canonical Tank/Turret fixture (b6-t1) with a REAL regenerated manifest. Per research.md, the
// façade types are emitted into NO loaded editor assembly, so this asserts at the RESOLUTION
// boundary (manifest -> FacadeCatalog -> BuilderParser.Parse -> ScopedOverride), same shape as
// b6-t3, plus a live SceneBuilderBuild.Run to prove the selectors resolve clean through the
// shipping editor path. Test-only; no production code expected (research.md: "no production edits
// expected").
public class PrefabFacadeDuplicateSiblingTests
{
    private const string Dir = "Assets/GateTests/Fixtures_PrefabFacadeDupSibling";
    private const string ScenePath = "Assets/GateTests/__PrefabFacadeDupSiblingTemp.unity";

    private string _manifestPath;
    private bool _manifestExistedBefore;
    private byte[] _manifestBytesBefore;

    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class PrefabFacadeDupSiblingGateScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    [SetUp]
    public void SetUp()
    {
        _manifestPath = SceneBuilderPaths.FacadeManifestPath;
        _manifestExistedBefore = File.Exists(_manifestPath);
        _manifestBytesBefore = _manifestExistedBefore ? File.ReadAllBytes(_manifestPath) : null;

        var buildersDir = SceneBuilderPaths.EnsureBuildersDirectory();
        var name = "PrefabFacadeDupSiblingGate_" + Guid.NewGuid().ToString("N");
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

        PrefabFacadeFixture.Delete(Dir);

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

    [Test]
    public void FrontWheel_ResolvesToRealName_NotSanitizedIdentifier()
    {
        PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();
        var catalog = FacadeCatalogLoader.Load();
        Assert.IsNotNull(catalog, "Setup: no facade manifest was loaded after Regenerate().");

        File.WriteAllText(_builderPath, Source(
            "        scene.Instance(Prefabs.Tank).On(t => t.Chassis.FrontWheel, w => { });"));

        var parseResult = BuilderParser.Parse(File.ReadAllText(_builderPath), null, catalog);
        Assert.IsEmpty(parseResult.Ambiguities,
            "Parse reported conflicts: " + string.Join("; ", parseResult.Ambiguities.Select(c => c.Reason)));

        var root = parseResult.Model.Roots.Single();
        Assert.IsInstanceOf<PrefabInstanceNode>(root);
        var instance = (PrefabInstanceNode)root;
        Assert.IsNotNull(instance.ScopedOverrides, "Expected one resolved ScopedOverride for the FrontWheel selector.");
        Assert.AreEqual(1, instance.ScopedOverrides!.Length);
        Assert.AreEqual("Chassis/Front Wheel", instance.ScopedOverrides[0].ChildPath,
            "The resolved ChildPath must round-trip the RealName ('Front Wheel', with the space), not the sanitized identifier ('FrontWheel').");

        var scene = EditorSceneManager.GetActiveScene();
        var buildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(buildResult.Diagnostics,
            "Live SceneBuilderBuild.Run reported diagnostics for the FrontWheel selector: "
            + string.Join("; ", buildResult.Diagnostics.Select(d => d.Message)));
    }

    [Test]
    public void DuplicateWheelSiblings_ResolveToDistinctRealDurableIds()
    {
        var handles = PrefabFacadeFixture.Create(Dir);
        PrefabFacadeManifestGenerator.Regenerate();
        var catalog = FacadeCatalogLoader.Load();
        Assert.IsNotNull(catalog, "Setup: no facade manifest was loaded after Regenerate().");

        // Ground truth: the durable fileIds of Chassis's two "Wheel" children, read the same way
        // the manifest reader does (PrefabHierarchyReader.cs:83).
        var tankAsset = AssetDatabase.LoadAssetAtPath<GameObject>(handles.TankPath);
        var chassis = tankAsset.transform.Find("Chassis");
        Assert.IsNotNull(chassis, "Setup: fixture Tank prefab has no 'Chassis' child.");
        var wheelIds = chassis.Cast<Transform>()
            .Where(c => c.gameObject.name == "Wheel")
            .Select(c =>
            {
                Assert.IsTrue(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(c.gameObject, out _, out var id),
                    "Setup: could not read a durable fileID for a 'Wheel' child.");
                return id;
            })
            .ToArray();
        Assert.AreEqual(2, wheelIds.Length, "Setup: fixture Chassis must have exactly two 'Wheel' children.");

        File.WriteAllText(_builderPath, Source(
            "        scene.Instance(Prefabs.Tank).On(t => t.Chassis.Wheel, w => { }).On(t => t.Chassis.Wheel2, w => { });"));

        var parseResult = BuilderParser.Parse(File.ReadAllText(_builderPath), null, catalog);
        Assert.IsEmpty(parseResult.Ambiguities,
            "Parse reported conflicts: " + string.Join("; ", parseResult.Ambiguities.Select(c => c.Reason)));

        var root = parseResult.Model.Roots.Single();
        Assert.IsInstanceOf<PrefabInstanceNode>(root);
        var instance = (PrefabInstanceNode)root;
        Assert.IsNotNull(instance.ScopedOverrides, "Expected two resolved ScopedOverrides for Wheel/Wheel2.");
        Assert.AreEqual(2, instance.ScopedOverrides!.Length);

        Assert.IsTrue(instance.ScopedOverrides.All(o => o.ChildPath == "Chassis/Wheel"),
            "Both Wheel/Wheel2 selectors must resolve to the same duplicate-name path 'Chassis/Wheel'.\n"
            + string.Join(", ", instance.ScopedOverrides.Select(o => o.ChildPath)));

        var resolvedIds = instance.ScopedOverrides.Select(o => o.LocalId).ToArray();
        Assert.AreNotEqual(resolvedIds[0], resolvedIds[1], "Wheel and Wheel2 must resolve to distinct, non-zero LocalIds.");
        Assert.IsTrue(resolvedIds.All(id => id != 0), "A resolved LocalId must never be 0.");
        CollectionAssert.AreEquivalent(wheelIds, resolvedIds,
            "The {Wheel, Wheel2} resolved LocalId set must equal the two real wheel objects' durable fileIds — "
            + "refutes both selectors silently resolving to the SAME (e.g. first) wheel.");

        var scene = EditorSceneManager.GetActiveScene();
        var buildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsEmpty(buildResult.Diagnostics,
            "Live SceneBuilderBuild.Run reported diagnostics for the Wheel/Wheel2 selectors: "
            + string.Join("; ", buildResult.Diagnostics.Select(d => d.Message)));
    }
}
