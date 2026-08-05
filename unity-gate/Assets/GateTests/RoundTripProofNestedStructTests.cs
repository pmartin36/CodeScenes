using NUnit.Framework;

// Round-trip proof for a struct nested inside a struct, at least two levels deep
// (GateFixtures.DeepOuter { inner: DeepInner { a, b }, y }). The scene->code direction is already
// proved by RoundTripNestedValueTests.SceneToCode_NestedInNestedStruct_EmitsRecursiveTypedInitializerAndNoOpsOnResync,
// so this file adds only the missing CODE->SCENE direction, driven through RoundTripProofHarness so
// convergence (a second sync is a zero-edit fixed point) is asserted by the harness itself.
public class RoundTripProofNestedStructTests
{
    [Test]
    public void CodeToScene_AuthoredTwoLevelNestedStruct_MaterializesOnTheLiveComponent()
    {
        RoundTripProofHarness.ProveCodeToScene(
            usings: "",
            body:
                "        var enemy = scene.Add(\"Enemy\");\n" +
                "        enemy.Component<GateFixtures.NestedInNestedFixtureBehaviour>(c => c.Set(\"Deep\", " +
                "new GateFixtures.DeepOuter { y = 1f, inner = new GateFixtures.DeepInner { a = 2f, b = 3f } }));\n",
            assertScene: ctx =>
            {
                var behaviour = ctx.RequireRoot("Enemy").GetComponent<GateFixtures.NestedInNestedFixtureBehaviour>();
                Assert.IsNotNull(behaviour, "Authored NestedInNestedFixtureBehaviour was not materialized on Enemy");
                Assert.AreEqual(1f, behaviour.Deep.y, 0.0001f, "Authored outer.y did not materialize.");
                Assert.AreEqual(2f, behaviour.Deep.inner.a, 0.0001f, "Authored inner.a did not materialize.");
                Assert.AreEqual(3f, behaviour.Deep.inner.b, 0.0001f, "Authored inner.b did not materialize.");
            });
    }

    // A fresh build (never a rebuild-after-drift, which the scene->code fill test already covers)
    // that omits an inner-struct member must still fill it to the type default -- proving the fill
    // rule reaches depth 2, not just the outer level.
    [Test]
    public void CodeToScene_PartiallyAuthoredTwoLevelNestedStruct_FillsTheOmittedInnerMemberToDefault()
    {
        RoundTripProofHarness.ProveCodeToScene(
            usings: "",
            body:
                "        var enemy = scene.Add(\"Enemy\");\n" +
                "        enemy.Component<GateFixtures.NestedInNestedFixtureBehaviour>(c => c.Set(\"Deep\", " +
                "new GateFixtures.DeepOuter { y = 1f, inner = new GateFixtures.DeepInner { a = 2f } }));\n",
            assertScene: ctx =>
            {
                var behaviour = ctx.RequireRoot("Enemy").GetComponent<GateFixtures.NestedInNestedFixtureBehaviour>();
                Assert.IsNotNull(behaviour, "Authored NestedInNestedFixtureBehaviour was not materialized on Enemy");
                Assert.AreEqual(1f, behaviour.Deep.y, 0.0001f, "Authored outer.y did not materialize.");
                Assert.AreEqual(2f, behaviour.Deep.inner.a, 0.0001f, "Authored inner.a did not materialize.");
                Assert.AreEqual(0f, behaviour.Deep.inner.b, 0.0001f, "Omitted inner.b did not fill to the type default.");
            });
    }
}
