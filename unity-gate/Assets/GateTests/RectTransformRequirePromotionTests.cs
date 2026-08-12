using NUnit.Framework;
using UnityEngine;
using SceneBuilder.Core.Model;
using SceneBuilder.Editor;

// Spec 37's Unity confirmation checklist (specs/37-recttransform-require-promotion.md): a
// [RequireComponent(typeof(RectTransform))] host authored with no explicit .RectTransform(...) must
// not drift a phantom .RectTransform(...) on its first sync, and an in-editor rect edit on such a
// host must introduce a .RectTransform(...) carrying only the changed channel(s). Drives the real
// RequireComponentPredicate and the real Build/Sync path through RoundTripProofHarness against a
// live editor scene.
public class RectTransformRequirePromotionTests
{
    [Test]
    public void Predicate_DirectTransitiveNonRequiringUnresolvable_ClassifiedCorrectly()
    {
        Assert.IsTrue(
            RequireComponentPredicate.RequiresRectTransform(new TypeRef("UnityEngine.UI.Slider")),
            "Slider requires RectTransform directly via Selectable's RequireComponent attribute.");
        Assert.IsTrue(
            RequireComponentPredicate.RequiresRectTransform(new TypeRef("UnityEngine.UI.GraphicRaycaster")),
            "GraphicRaycaster requires RectTransform transitively: it requires Canvas, which itself "
                + "requires RectTransform.");
        Assert.IsFalse(
            RequireComponentPredicate.RequiresRectTransform(new TypeRef("UnityEngine.BoxCollider")),
            "BoxCollider resolves but requires no RectTransform.");
        Assert.IsFalse(
            RequireComponentPredicate.RequiresRectTransform(new TypeRef("UnityEngine.UI.ThisTypeDoesNotExist")),
            "An unresolvable TypeRef must answer false, not throw.");
    }

    [Test]
    public void CodeAuthoredSlider_FirstSyncIsFixedPoint_NoRectTransformInjected()
    {
        var result = RoundTripProofHarness.ProveCodeToSceneFixedPoint(
            usings: "",
            body: "        scene.Add(\"Slide\").Component<UnityEngine.UI.Slider>(_ => { });\n",
            assertScene: ctx =>
            {
                var root = ctx.RequireRoot("Slide");
                Assert.IsNotNull(root.GetComponent<RectTransform>(),
                    "A Slider host must carry a live RectTransform (RequireComponent honored).");
                Assert.IsNotNull(root.GetComponent<UnityEngine.UI.Slider>());
            },
            assertConverged: ctx =>
            {
                Assert.AreEqual(
                    0, ctx.Sync.PatchEdits,
                    "The first sync over a converged require-rect host must apply zero patch edits.");
            });

        StringAssert.DoesNotContain(
            ".RectTransform(", result.ConvergedSource,
            "A require-rect host authored with no explicit .RectTransform(...) must never gain one "
                + "from an unedited sync.\n" + result.ConvergedSource);
    }

    [Test]
    public void InEditorRectEdit_IntroducesOnlyChangedChannel_SecondSyncNoOp()
    {
        var result = RoundTripProofHarness.ProveSceneToCode(
            usings: "",
            body: "        scene.Add(\"Slide\").Component<UnityEngine.UI.Slider>(_ => { });\n",
            mutateScene: ctx =>
            {
                var rect = (RectTransform)ctx.RequireRoot("Slide").transform;
                rect.anchoredPosition = new UnityEngine.Vector2(-40f, -30f);
            },
            assertSource: ctx =>
            {
                StringAssert.Contains(
                    ".RectTransform(anchoredPos: (-40f, -30f)", ctx.Source,
                    "Moving the require-rect host's anchoredPosition must introduce a .RectTransform(...) "
                        + "carrying the changed channel with a real f-suffixed value.\n" + ctx.Source);
                StringAssert.DoesNotContain("anchorMin", ctx.Source,
                    "An unchanged channel (anchorMin) must be omitted, not written at its default.\n" + ctx.Source);
                StringAssert.DoesNotContain("pivot", ctx.Source,
                    "An unchanged channel (pivot) must be omitted, not written at its default.\n" + ctx.Source);
                StringAssert.DoesNotContain("sizeDelta", ctx.Source,
                    "An unchanged channel (sizeDelta) must be omitted, not written at its default.\n" + ctx.Source);
            });

        Assert.IsFalse(
            result.ConvergenceSync.Changed,
            "The sync following the introduced .RectTransform(...) edit must be a no-op.");
    }
}
