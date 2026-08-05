using NUnit.Framework;
using UnityEngine;

// Proves both RoundTripProofHarness entry points against a real component, deliberately none of
// the four types the component proof suite covers.
public class RoundTripProofSmokeTests
{
    [Test]
    public void CodeToScene_AuthoredRigidbodyMass_MaterializesOnTheLiveComponent()
    {
        RoundTripProofHarness.ProveCodeToScene(
            usings: "",
            body:
                "        var body = scene.Add(\"Body\");\n" +
                "        body.Component<Rigidbody>(c => c.Set(x => x.mass, 5f));\n",
            assertScene: ctx =>
            {
                var rigidbody = ctx.RequireRoot("Body").GetComponent<Rigidbody>();
                Assert.AreEqual(5f, rigidbody.mass);
            });
    }

    [Test]
    public void SceneToCode_EditedRigidbodyMass_ReachesTheBuilderSource()
    {
        RoundTripProofHarness.ProveSceneToCode(
            usings: "",
            body:
                "        var body = scene.Add(\"Body\");\n" +
                "        body.Component<Rigidbody>(c => c.Set(x => x.mass, 5f));\n",
            mutateScene: ctx =>
            {
                ctx.RequireRoot("Body").GetComponent<Rigidbody>().mass = 9f;
            },
            assertSource: ctx =>
            {
                StringAssert.Contains("mass", ctx.Source);
                StringAssert.Contains("9", ctx.Source);
                StringAssert.DoesNotContain("5f", ctx.Source);
            });
    }
}
