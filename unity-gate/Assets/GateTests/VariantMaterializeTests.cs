using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using SceneBuilder.Editor;
using SceneBuilder.Core.Reconcile;
using UnityAddedComponent = UnityEditor.SceneManagement.AddedComponent;

// A prefab variant (IPrefabVariantDefinition: Base PrefabRef + Build(VariantRoot)) materializes
// through the SAME PrefabBuildSyncTarget.Build seam a flat/nested prefab uses -- InstantiatePrefab(base)
// connected as the root, then the M10 override plumbing applies the authored overrides on it -- and
// PrefabUtility reports the persisted asset as a genuine Variant whose override layer survives on
// GetPropertyModifications, inherits later base changes it did not itself override, and round-trips a
// live editor edit back into the variant's own builder .cs (never the base's).
public class VariantMaterializeTests
{
    private const string FixturesDir = "Assets/GateTests/Fixtures_VariantMaterialize";
    private const string HumanBasePath = FixturesDir + "/VMHumanBase.prefab";
    private const string CodeBasePath = FixturesDir + "/VMCodeBase.prefab";
    private const string VariantPath = FixturesDir + "/VMVariant.prefab";
    private const string VariantClassName = "VMVariant";

    private string _manifestPath;
    private bool _manifestExistedBefore;
    private byte[] _manifestBytesBefore;

    private string _dir;
    private string _codeBaseBuilderPath;
    private string _codeBaseSidecarPath;
    private string _variantBuilderPath;
    private string _variantSidecarPath;

    private static string VariantSource(string basePropertyName, string body) => $@"
using SceneBuilder.Authoring;
public class {VariantClassName} : IPrefabVariantDefinition
{{
    public PrefabRef Base => Prefabs.{basePropertyName};

    public void Build(VariantRoot root)
    {{
{body}
    }}
}}";

    private static string FlatPrefabSource(string className, string rootName, string body) => $@"
using SceneBuilder.Authoring;
public class {className} : IPrefabDefinition
{{
    public void Build(PrefabRoot root)
    {{
        root.Add(""{rootName}"", w =>
        {{
{body}
        }});
    }}
}}";

    [SetUp]
    public void SetUp()
    {
        _manifestPath = SceneBuilderPaths.FacadeManifestPath;
        _manifestExistedBefore = File.Exists(_manifestPath);
        _manifestBytesBefore = _manifestExistedBefore ? File.ReadAllBytes(_manifestPath) : null;

        _dir = Path.Combine(Path.GetTempPath(), "sb_vm_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _codeBaseBuilderPath = Path.Combine(_dir, "VMCodeBaseScene.cs");
        _codeBaseSidecarPath = Path.Combine(_dir, "VMCodeBaseScene.sbmap.json");
        _variantBuilderPath = Path.Combine(_dir, VariantClassName + ".cs");
        _variantSidecarPath = Path.Combine(_dir, VariantClassName + ".sbmap.json");

        if (!AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "Fixtures_VariantMaterialize");
        }

        // Human base: a real prefab asset made directly via SaveAsPrefabAsset, not authored by
        // SceneBuilder at all.
        var humanRoot = new GameObject("VMHumanBase");
        var humanLight = humanRoot.AddComponent<Light>();
        humanLight.intensity = 1f;
        humanRoot.AddComponent<BoxCollider>();
        PrefabUtility.SaveAsPrefabAsset(humanRoot, HumanBasePath);
        UnityEngine.Object.DestroyImmediate(humanRoot);

        // Code base: the same shape, authored through SceneBuilder itself.
        File.WriteAllText(_codeBaseBuilderPath, FlatPrefabSource("VMCodeBaseScene", "VMCodeBase",
            "            w.Component<UnityEngine.Light>(c => c.Set(\"m_Intensity\", 1f));\n" +
            "            w.Component<UnityEngine.BoxCollider>();"));
        var codeBaseBuild = PrefabBuildSyncTarget.Build(_codeBaseBuilderPath, CodeBasePath, _codeBaseSidecarPath);
        Assert.IsEmpty(codeBaseBuild.Diagnostics, "Setup: code-authored base build reported diagnostics");

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        PrefabFacadeManifestGenerator.Regenerate();
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        if (AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath) != null)
        {
            AssetDatabase.DeleteAsset(VariantPath);
        }

        AssetDatabase.DeleteAsset(HumanBasePath);
        AssetDatabase.DeleteAsset(CodeBasePath);
        if (AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.DeleteAsset(FixturesDir);
        }

        if (_manifestExistedBefore) File.WriteAllBytes(_manifestPath, _manifestBytesBefore);
        else if (File.Exists(_manifestPath)) File.Delete(_manifestPath);
    }

    private static void ApplyPrefabModeEdit(string prefabPath, Action<GameObject> mutate)
    {
        var contents = PrefabUtility.LoadPrefabContents(prefabPath);
        mutate(contents);
        PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
        PrefabUtility.UnloadPrefabContents(contents);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceUpdate);
    }

    // Identical result for a code-defined and a human-made base: the built asset is a genuine
    // Variant (not a flattened copy, not a nested-instance-only shape) and the authored root
    // override on the FIRST/CREATE build persists as a variant property modification.
    [TestCase(false)]
    [TestCase(true)]
    public void Variant_Build_ProducesRealVariant_WithOverridePersisted(bool codeBase)
    {
        var basePropertyName = codeBase ? "VMCodeBase" : "VMHumanBase";
        File.WriteAllText(_variantBuilderPath, VariantSource(basePropertyName,
            "        root.Override(e => e.Set((UnityEngine.Light l) => l.intensity, 7f));"));

        var build = PrefabBuildSyncTarget.Build(_variantBuilderPath, VariantPath, _variantSidecarPath);
        Assert.IsEmpty(build.Diagnostics,
            "Variant build reported diagnostics: " + string.Join("; ", build.Diagnostics.Select(d => d.Message)));

        var variantAsset = AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath);
        Assert.IsNotNull(variantAsset, "No variant .prefab was written at " + VariantPath);
        Assert.AreEqual(PrefabAssetType.Variant, PrefabUtility.GetPrefabAssetType(variantAsset),
            "Built asset is not reported as a Variant");
        Assert.IsTrue(PrefabUtility.IsPartOfVariantPrefab(variantAsset),
            "Built asset's root is not IsPartOfVariantPrefab");

        var mods = PrefabUtility.GetPropertyModifications(variantAsset) ?? Array.Empty<PropertyModification>();
        var intensityMod = mods.FirstOrDefault(m => m.propertyPath == "m_Intensity");
        Assert.IsNotNull(intensityMod, "GetPropertyModifications carries no m_Intensity override on the variant");
        Assert.AreEqual("7", intensityMod.value, "The authored override's value was not materialized onto the variant");
    }

    [Test]
    public void Variant_Build_AddComponentOverride_MaterializesOnVariant()
    {
        File.WriteAllText(_variantBuilderPath, VariantSource("VMHumanBase",
            "        root.AddComponent<UnityEngine.AudioSource>();"));

        var build = PrefabBuildSyncTarget.Build(_variantBuilderPath, VariantPath, _variantSidecarPath);
        Assert.IsEmpty(build.Diagnostics,
            "Variant build reported diagnostics: " + string.Join("; ", build.Diagnostics.Select(d => d.Message)));

        var variantAsset = AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath);
        Assert.IsNotNull(variantAsset.GetComponent<AudioSource>(),
            "The authored AddComponent<AudioSource>() did not materialize onto the reloaded variant asset");

        var added = PrefabUtility.GetAddedComponents(variantAsset) ?? new List<UnityAddedComponent>();
        Assert.IsTrue(added.Any(a => a.instanceComponent is AudioSource),
            "AudioSource is not recorded as an added component on the variant instance");
    }

    [Test]
    public void Variant_Build_RemoveComponentOverride_MaterializesOnVariant()
    {
        File.WriteAllText(_variantBuilderPath, VariantSource("VMHumanBase",
            "        root.RemoveComponent<UnityEngine.BoxCollider>();"));

        var build = PrefabBuildSyncTarget.Build(_variantBuilderPath, VariantPath, _variantSidecarPath);
        Assert.IsEmpty(build.Diagnostics,
            "Variant build reported diagnostics: " + string.Join("; ", build.Diagnostics.Select(d => d.Message)));

        var variantAsset = AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath);
        Assert.IsNull(variantAsset.GetComponent<BoxCollider>(),
            "The authored RemoveComponent<BoxCollider>() did not remove the base's component from the reloaded variant asset");
    }

    [Test]
    public void Variant_BaseFieldChange_InheritsThroughToVariant()
    {
        File.WriteAllText(_variantBuilderPath, VariantSource("VMHumanBase", ""));
        var build = PrefabBuildSyncTarget.Build(_variantBuilderPath, VariantPath, _variantSidecarPath);
        Assert.IsEmpty(build.Diagnostics, "Setup variant build reported diagnostics");

        ApplyPrefabModeEdit(HumanBasePath, contents =>
        {
            contents.GetComponent<Light>().intensity = 9f;
            PrefabUtility.RecordPrefabInstancePropertyModifications(contents.GetComponent<Light>());
        });

        var variantAsset = AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath);
        Assert.AreEqual(9f, variantAsset.GetComponent<Light>().intensity, 0.0001f,
            "A base field the variant did not override did not inherit the base's new value");
    }

    [Test]
    public void Variant_ReconcileFieldEdit_AddsNewOverride_PatchesVariantCs()
    {
        File.WriteAllText(_variantBuilderPath, VariantSource("VMHumanBase", ""));
        var build = PrefabBuildSyncTarget.Build(_variantBuilderPath, VariantPath, _variantSidecarPath);
        Assert.IsEmpty(build.Diagnostics, "Setup variant build reported diagnostics");

        ApplyPrefabModeEdit(VariantPath, contents =>
        {
            var light = contents.GetComponent<Light>();
            light.intensity = 55f;
            PrefabUtility.RecordPrefabInstancePropertyModifications(light);
        });

        var sync = PrefabBuildSyncTarget.Sync(_variantBuilderPath, VariantPath, _variantSidecarPath);
        Assert.IsTrue(sync.Changed, "Sync reported no change despite a field edit on the variant root");

        var rewritten = File.ReadAllText(_variantBuilderPath);
        StringAssert.Contains(".Override(", rewritten,
            "Builder source did not gain an .Override(...) call for the variant field edit.\n" + rewritten);
        StringAssert.Contains("m_Intensity", rewritten,
            "Builder source's override does not target m_Intensity.\n" + rewritten);
    }

    [Test]
    public void Variant_ReconcileFieldEdit_ModifiesExistingOverride_PatchesVariantCs()
    {
        File.WriteAllText(_variantBuilderPath, VariantSource("VMHumanBase",
            "        root.Override(e => e.Set((UnityEngine.Light l) => l.intensity, 7f));"));
        var build = PrefabBuildSyncTarget.Build(_variantBuilderPath, VariantPath, _variantSidecarPath);
        Assert.IsEmpty(build.Diagnostics, "Setup variant build reported diagnostics");

        ApplyPrefabModeEdit(VariantPath, contents =>
        {
            var light = contents.GetComponent<Light>();
            light.intensity = 42f;
            PrefabUtility.RecordPrefabInstancePropertyModifications(light);
        });

        var sync = PrefabBuildSyncTarget.Sync(_variantBuilderPath, VariantPath, _variantSidecarPath);
        Assert.IsTrue(sync.Changed, "Sync reported no change despite modifying an existing variant override");

        var rebuild = PrefabBuildSyncTarget.Build(_variantBuilderPath, VariantPath, _variantSidecarPath);
        Assert.IsEmpty(rebuild.Diagnostics, "Rebuild from the reconciled source reported diagnostics");

        var variantAsset = AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath);
        Assert.AreEqual(42f, variantAsset.GetComponent<Light>().intensity, 0.0001f,
            "Rebuilding from the reconciled source did not materialize the MODIFIED override value");
    }

    [Test]
    public void Variant_ReconcileAddComponent_PatchesVariantCs()
    {
        File.WriteAllText(_variantBuilderPath, VariantSource("VMHumanBase", ""));
        var build = PrefabBuildSyncTarget.Build(_variantBuilderPath, VariantPath, _variantSidecarPath);
        Assert.IsEmpty(build.Diagnostics, "Setup variant build reported diagnostics");

        ApplyPrefabModeEdit(VariantPath, contents => contents.AddComponent<AudioSource>());

        var sync = PrefabBuildSyncTarget.Sync(_variantBuilderPath, VariantPath, _variantSidecarPath);
        Assert.IsTrue(sync.Changed, "Sync reported no change despite a component added on the variant root");

        var rewritten = File.ReadAllText(_variantBuilderPath);
        StringAssert.Contains(".AddComponent<UnityEngine.AudioSource>(", rewritten,
            "Builder source did not gain an .AddComponent<AudioSource>() call for the variant root.\n" + rewritten);
    }

    [Test]
    public void Variant_ReconcileRemoveComponent_PatchesVariantCs()
    {
        File.WriteAllText(_variantBuilderPath, VariantSource("VMHumanBase", ""));
        var build = PrefabBuildSyncTarget.Build(_variantBuilderPath, VariantPath, _variantSidecarPath);
        Assert.IsEmpty(build.Diagnostics, "Setup variant build reported diagnostics");

        ApplyPrefabModeEdit(VariantPath, contents =>
            UnityEngine.Object.DestroyImmediate(contents.GetComponent<BoxCollider>()));

        var sync = PrefabBuildSyncTarget.Sync(_variantBuilderPath, VariantPath, _variantSidecarPath);
        Assert.IsTrue(sync.Changed, "Sync reported no change despite a component removed from the variant root");

        var rewritten = File.ReadAllText(_variantBuilderPath);
        StringAssert.Contains(".RemoveComponent<UnityEngine.BoxCollider>(", rewritten,
            "Builder source did not gain a .RemoveComponent<BoxCollider>() call for the variant root.\n" + rewritten);
    }

    // Guards FIX A's no-churn property (the persisted root name must match the variant class name, so
    // no SetName op is ever generated) AND that a base-authored field the variant never overrides is
    // not spuriously reconciled onto the variant's own source.
    [Test]
    public void Variant_CleanSync_NoSpuriousChanges()
    {
        File.WriteAllText(_variantBuilderPath, VariantSource("VMHumanBase",
            "        root.Override(e => e.Set((UnityEngine.Light l) => l.intensity, 7f));"));
        var build = PrefabBuildSyncTarget.Build(_variantBuilderPath, VariantPath, _variantSidecarPath);
        Assert.IsEmpty(build.Diagnostics, "Setup variant build reported diagnostics");

        var beforeSync = File.ReadAllText(_variantBuilderPath);

        var sync = PrefabBuildSyncTarget.Sync(_variantBuilderPath, VariantPath, _variantSidecarPath);

        Assert.IsFalse(sync.Changed, "A clean sync with no live edits reported a change");
        Assert.IsEmpty(sync.Conflicts, "A clean sync with no live edits raised a conflict: "
            + string.Join("; ", sync.Conflicts.Select(c => c.Kind + ": " + c.Reason)));
        Assert.AreEqual(beforeSync, File.ReadAllText(_variantBuilderPath),
            "A clean sync with no live edits rewrote the builder source");
    }
}
