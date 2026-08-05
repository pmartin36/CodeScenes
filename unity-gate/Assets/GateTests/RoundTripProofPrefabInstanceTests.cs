using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

// Round-trip proof that a value read on a prefab-instance node goes through PrefabInstanceProbe
// rather than the plain reader. The discriminator: the live instance carries an OVERRIDE whose
// value differs from the source prefab asset. Code->scene proves the instance is read as one
// opaque unit (no child, no component, no override -- the plain reader would emit all three).
// Scene->code proves the emitted source carries the INSTANCE value, not the asset's.
public class RoundTripProofPrefabInstanceTests
{
    private const string FixturesDir = "Assets/GateTests/Fixtures_RoundTripProofPrefabInstance";
    private const string PrefabPath = FixturesDir + "/RoundTripProofPrefab.prefab";
    private const string RootName = "RoundTripProofPrefab"; // scene.Instance(path)'s node name derives from the path stem
    private const string ChildName = "RoundTripProofOwnedChild";

    private static string SeedBody() =>
        "        scene.Instance(\"" + PrefabPath + "\").Transform(pos: (3f, 0f, 5f));\n";

    [SetUp]
    public void SetUp()
    {
        if (!AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "Fixtures_RoundTripProofPrefabInstance");
        }

        var root = new GameObject(RootName);
        var light = root.AddComponent<Light>();
        light.intensity = 2.5f;
        var child = new GameObject(ChildName);
        child.transform.SetParent(root.transform);
        PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        UnityEngine.Object.DestroyImmediate(root);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    [TearDown]
    public void TearDown()
    {
        AssetDatabase.DeleteAsset(PrefabPath);
        if (AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.DeleteAsset(FixturesDir);
        }
    }

    [Test]
    public void CodeToScene_AuthoredInstance_MaterializesAConnectedInstanceAndStaysOneUnitInSource()
    {
        RoundTripProofHarness.ProveCodeToScene(
            usings: "",
            body: SeedBody(),
            assertScene: ctx =>
            {
                var root = ctx.RequireRoot(RootName);
                Assert.AreEqual(
                    PrefabInstanceStatus.Connected, PrefabUtility.GetPrefabInstanceStatus(root),
                    "The authored instance did not materialize as a connected prefab instance.");
                Assert.AreEqual(
                    new Vector3(3f, 0f, 5f), root.transform.localPosition,
                    "The authored transform did not materialize on the live instance root.");
                Assert.AreEqual(
                    2.5f, root.GetComponent<Light>().intensity, 0.001f,
                    "The unauthored root Light.intensity must read as the prefab asset's own value.");
                Assert.IsNotNull(
                    root.transform.Find(ChildName),
                    "The prefab's own child must be present on the live instance.");
            });

        var result = RoundTripProofHarness.ProveCodeToScene(
            usings: "",
            body: SeedBody(),
            assertScene: ctx => { });

        StringAssert.Contains(".Instance(", result.ConvergedSource,
            "The converged source must author the instance statement.\n" + result.ConvergedSource);
        StringAssert.DoesNotContain(".Component<", result.ConvergedSource,
            "A prefab-instance node read as one unit must never emit a .Component< call.\n" + result.ConvergedSource);
        StringAssert.DoesNotContain(ChildName, result.ConvergedSource,
            "A prefab-instance node read as one unit must never name its own child.\n" + result.ConvergedSource);
        StringAssert.DoesNotContain(".Override(", result.ConvergedSource,
            "No override was authored or live on the instance; none may appear in source.\n" + result.ConvergedSource);
        StringAssert.DoesNotContain("m_LocalPosition", result.ConvergedSource,
            "The root transform's own property modifications must not surface as a raw field.\n" + result.ConvergedSource);
        StringAssert.DoesNotContain("2.5", result.ConvergedSource,
            "The prefab asset's own Light.intensity must never surface in source.\n" + result.ConvergedSource);
    }

    [Test]
    public void SceneToCode_LiveRootOverride_EmitsTheInstanceValueNotThePrefabAssetValue()
    {
        var result = RoundTripProofHarness.ProveSceneToCode(
            usings: "",
            body: SeedBody(),
            mutateScene: ctx =>
            {
                var light = ctx.RequireRoot(RootName).GetComponent<Light>();
                light.intensity = 7.25f;
                PrefabUtility.RecordPrefabInstancePropertyModifications(light);
            },
            assertSource: ctx =>
            {
                StringAssert.Contains(
                    "\"m_Intensity\", \"7.25\"", ctx.Source,
                    "The live override's instance value did not reach builder source.\n" + ctx.Source);
                StringAssert.DoesNotContain("2.5", ctx.Source,
                    "The prefab asset's own value must never surface in source once an override is live.\n" + ctx.Source);
                StringAssert.DoesNotContain(".Component<", ctx.Source,
                    "A prefab-instance node read as one unit must never emit a .Component< call.\n" + ctx.Source);
                StringAssert.DoesNotContain(ChildName, ctx.Source,
                    "A prefab-instance node read as one unit must never name its own child.\n" + ctx.Source);
            });

        var assetLight = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath).GetComponent<Light>();
        Assert.AreEqual(
            2.5f, assetLight.intensity, 0.001f,
            "Recording the instance override must not touch the prefab asset on disk.\n" + result.ConvergedSource);
    }
}
