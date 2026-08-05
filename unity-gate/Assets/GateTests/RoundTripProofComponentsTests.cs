using System.Linq;
using NUnit.Framework;
using UnityEngine;

// Round-trip proof for the four required component types: Camera, MeshRenderer, SpriteRenderer,
// Light. Each type gets one code->scene case and one scene->code case, driven exclusively through
// RoundTripProofHarness so convergence (a second sync is a zero-edit fixed point) is asserted by
// the harness itself rather than restated here.
//
// Camera has no existing coverage anywhere in this suite; both its cases are new. MeshRenderer and
// SpriteRenderer already have attach/asset-field and Inspector-visibility coverage elsewhere — the
// delta proved here is a plain serialized field driven in both directions with convergence, plus
// (for SpriteRenderer) that an existing typed selector spelling survives a scene edit while the
// sorting-layer id is added as a raw field and the derived index is not. Light is otherwise only
// exercised inside prefab-instance/nested-override contexts; a plain scene-node Light is new here.
public class RoundTripProofComponentsTests
{
    // Camera serializes fieldOfView as "field of view" -- no m_ prefix, spaces in the name -- so the
    // typed member selector (c.Set(x => x.fieldOfView, ...)) cannot resolve it and throws at build.
    // The raw serialized-path spelling is what actually converges, so that is what this proves.
    [Test]
    public void CodeToScene_AuthoredCameraFieldOfView_MaterializesOnTheLiveCamera()
    {
        RoundTripProofHarness.ProveCodeToScene(
            usings: "",
            body:
                "        var cam = scene.Add(\"Cam\");\n" +
                "        cam.Component<UnityEngine.Camera>(c => c.Set(\"field of view\", 42f));\n",
            assertScene: ctx =>
            {
                var camera = ctx.RequireRoot("Cam").GetComponent<Camera>();
                Assert.AreEqual(42f, camera.fieldOfView, 0.001f,
                    "Authored field of view did not materialize on the live Camera.");
            });
    }

    [Test]
    public void SceneToCode_EditedCameraFieldOfView_ReachesTheBuilderSource()
    {
        RoundTripProofHarness.ProveSceneToCode(
            usings: "",
            body:
                "        scene.Add(\"Cam\").Component<UnityEngine.Camera>();\n",
            mutateScene: ctx =>
            {
                ctx.RequireRoot("Cam").GetComponent<Camera>().fieldOfView = 33f;
            },
            assertSource: ctx =>
            {
                StringAssert.Contains("\"field of view\", 33f", ctx.Source,
                    "The edited field of view did not reach builder source.\n" + ctx.Source);
            });
    }

    [Test]
    public void SceneToCode_EditedCameraClearFlags_EmitsTheNamedEnumMember()
    {
        RoundTripProofHarness.ProveSceneToCode(
            usings: "",
            body:
                "        scene.Add(\"Cam\").Component<UnityEngine.Camera>();\n",
            mutateScene: ctx =>
            {
                ctx.RequireRoot("Cam").GetComponent<Camera>().clearFlags = CameraClearFlags.Depth;
            },
            assertSource: ctx =>
            {
                StringAssert.Contains("UnityEngine.CameraClearFlags.Depth", ctx.Source,
                    "The edited clear flags did not emit the named enum member.\n" + ctx.Source);
                StringAssert.DoesNotContain("\"m_ClearFlags\", 3", ctx.Source,
                    "Clear flags must emit the named enum member, not a bare int.\n" + ctx.Source);
            });
    }

    [Test]
    public void CodeToScene_AuthoredMeshRendererReceiveShadows_MaterializesOnTheLiveRenderer()
    {
        RoundTripProofHarness.ProveCodeToScene(
            usings: "",
            body:
                "        var surf = scene.Add(\"Surf\");\n" +
                "        surf.Component<UnityEngine.MeshRenderer>(c => c.Set(x => x.receiveShadows, false));\n",
            assertScene: ctx =>
            {
                var mr = ctx.RequireRoot("Surf").GetComponent<MeshRenderer>();
                Assert.IsFalse(mr.receiveShadows,
                    "Authored receiveShadows=false did not materialize on the live MeshRenderer.");
            });
    }

    [Test]
    public void SceneToCode_EditedMeshRendererSortingOrder_ReachesTheBuilderSourceWithoutTheDerivedIndex()
    {
        RoundTripProofHarness.ProveSceneToCode(
            usings: "",
            body:
                "        scene.Add(\"Surf\").Component<UnityEngine.MeshRenderer>();\n",
            mutateScene: ctx =>
            {
                ctx.RequireRoot("Surf").GetComponent<MeshRenderer>().sortingOrder = 3;
            },
            assertSource: ctx =>
            {
                StringAssert.Contains("\"m_SortingOrder\", 3", ctx.Source,
                    "The edited sorting order did not reach builder source.\n" + ctx.Source);
                StringAssert.DoesNotContain("\"m_SortingLayer\"", ctx.Source,
                    "The derived sorting-layer index must never reach builder source.\n" + ctx.Source);
            });
    }

    [Test]
    public void CodeToScene_AuthoredSpriteRendererSortingOrder_MaterializesOnTheLiveRenderer()
    {
        RoundTripProofHarness.ProveCodeToScene(
            usings: "",
            body:
                "        var sp = scene.Add(\"Sprite\");\n" +
                "        sp.Component<UnityEngine.SpriteRenderer>(c => c.Set(x => x.sortingOrder, 5));\n",
            assertScene: ctx =>
            {
                var sr = ctx.RequireRoot("Sprite").GetComponent<SpriteRenderer>();
                Assert.AreEqual(5, sr.sortingOrder,
                    "Authored sortingOrder did not materialize on the live SpriteRenderer.");
            });
    }

    [Test]
    public void SceneToCode_EditedSpriteRendererSortingLayer_EmitsOnlyTheIdAndKeepsTheTypedSpelling()
    {
        Assert.IsTrue(SortingLayer.layers.Any(l => l.name == "Foreground"),
            "ProjectSettings/TagManager.asset must define a 'Foreground' sorting layer to exercise this proof - none found.");
        var expectedId = SortingLayer.NameToID("Foreground");

        RoundTripProofHarness.ProveSceneToCode(
            usings: "",
            body:
                "        var sp = scene.Add(\"Sprite\");\n" +
                "        sp.Component<UnityEngine.SpriteRenderer>(c => c.Set(x => x.sortingOrder, 5));\n",
            mutateScene: ctx =>
            {
                var sr = ctx.RequireRoot("Sprite").GetComponent<SpriteRenderer>();
                sr.sortingOrder = 7;
                sr.sortingLayerName = "Foreground";
            },
            assertSource: ctx =>
            {
                StringAssert.Contains("c.Set(x => x.sortingOrder, 7)", ctx.Source,
                    "The existing typed sortingOrder selector must survive the edit.\n" + ctx.Source);
                StringAssert.Contains($"\"m_SortingLayerID\", {expectedId}", ctx.Source,
                    "The edited sorting layer must reach builder source as the raw id.\n" + ctx.Source);
                StringAssert.DoesNotContain("\"m_SortingLayer\"", ctx.Source,
                    "The derived sorting-layer index must never reach builder source.\n" + ctx.Source);
                StringAssert.DoesNotContain("\"m_Size\"", ctx.Source,
                    "m_Size must never reach builder source.\n" + ctx.Source);
                StringAssert.DoesNotContain("\"m_WasSpriteAssigned\"", ctx.Source,
                    "m_WasSpriteAssigned must never reach builder source.\n" + ctx.Source);
            });
    }

    [Test]
    public void CodeToScene_AuthoredLightIntensity_MaterializesOnTheLiveLight()
    {
        RoundTripProofHarness.ProveCodeToScene(
            usings: "",
            body:
                "        var lt = scene.Add(\"Lamp\");\n" +
                "        lt.Component<UnityEngine.Light>(c => c.Set(x => x.intensity, 4f));\n",
            assertScene: ctx =>
            {
                var light = ctx.RequireRoot("Lamp").GetComponent<Light>();
                Assert.AreEqual(4f, light.intensity, 0.001f,
                    "Authored intensity did not materialize on the live Light.");
            });
    }

    [Test]
    public void SceneToCode_EditedLightTypeRangeAndColor_ReachesTheBuilderSource()
    {
        RoundTripProofHarness.ProveSceneToCode(
            usings: "",
            body:
                "        scene.Add(\"Lamp\").Component<UnityEngine.Light>();\n",
            mutateScene: ctx =>
            {
                var light = ctx.RequireRoot("Lamp").GetComponent<Light>();
                light.type = LightType.Spot;
                light.range = 12f;
                light.color = Color.red;
            },
            assertSource: ctx =>
            {
                StringAssert.Contains("UnityEngine.LightType.Spot", ctx.Source,
                    "The edited light type did not emit the named enum member.\n" + ctx.Source);
                StringAssert.Contains("\"m_Range\", 12f", ctx.Source,
                    "The edited range did not reach builder source.\n" + ctx.Source);
                StringAssert.Contains("new UnityEngine.Color(1f, 0f, 0f, 1f)", ctx.Source,
                    "The edited color did not reach builder source.\n" + ctx.Source);
            });
    }

    // Proves the four types coexist through one build/sync pass, not just in isolation.
    [Test]
    public void CodeToScene_AllFourComponentsInOneScene_MaterializeAndConverge()
    {
        RoundTripProofHarness.ProveCodeToScene(
            usings: "",
            body:
                "        var cam = scene.Add(\"Cam\");\n" +
                "        cam.Component<UnityEngine.Camera>(c => c.Set(\"field of view\", 42f));\n" +
                "        var surf = scene.Add(\"Surf\");\n" +
                "        surf.Component<UnityEngine.MeshRenderer>(c => c.Set(x => x.receiveShadows, false));\n" +
                "        var sp = scene.Add(\"Sprite\");\n" +
                "        sp.Component<UnityEngine.SpriteRenderer>(c => c.Set(x => x.sortingOrder, 5));\n" +
                "        var lt = scene.Add(\"Lamp\");\n" +
                "        lt.Component<UnityEngine.Light>(c => c.Set(x => x.intensity, 4f));\n",
            assertScene: ctx =>
            {
                Assert.AreEqual(42f, ctx.RequireRoot("Cam").GetComponent<Camera>().fieldOfView, 0.001f,
                    "Camera field of view did not materialize when combined with the other three types.");
                Assert.IsFalse(ctx.RequireRoot("Surf").GetComponent<MeshRenderer>().receiveShadows,
                    "MeshRenderer receiveShadows did not materialize when combined with the other three types.");
                Assert.AreEqual(5, ctx.RequireRoot("Sprite").GetComponent<SpriteRenderer>().sortingOrder,
                    "SpriteRenderer sortingOrder did not materialize when combined with the other three types.");
                Assert.AreEqual(4f, ctx.RequireRoot("Lamp").GetComponent<Light>().intensity, 0.001f,
                    "Light intensity did not materialize when combined with the other three types.");
            });
    }
}
