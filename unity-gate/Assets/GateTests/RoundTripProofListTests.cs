using NUnit.Framework;

// Round-trip proof for a list of structs (GateFixtures.GateSampleBehaviour.Volley: Damage[]). The
// scene->code direction is already proved by
// RoundTripNestedValueTests.SceneToCode_ListOfNestedStructs_EmitsTypedArrayThatCompilesAndNoOpsOnResync,
// so this file adds only the missing CODE->SCENE direction, driven through RoundTripProofHarness so
// convergence (a second sync is a zero-edit fixed point) is asserted by the harness itself. It also
// pins the observable list/struct-member asymmetry: a list ITEM is never dropped, collapsed or
// reduced the way a struct MEMBER is.
public class RoundTripProofListTests
{
    [Test]
    public void CodeToScene_AuthoredListOfStructs_MaterializesEveryItemInOrder()
    {
        RoundTripProofHarness.ProveCodeToScene(
            usings: "",
            body:
                "        var enemy = scene.Add(\"Enemy\");\n" +
                "        enemy.Component<GateFixtures.GateSampleBehaviour>(c => c.Set(\"Volley\", new[] { " +
                "new GateFixtures.Damage { amount = 5f, kind = 1 }, " +
                "new GateFixtures.Damage { amount = 0f, kind = 0 }, " +
                "new GateFixtures.Damage { amount = 7.5f, kind = 2 } }));\n",
            assertScene: ctx =>
            {
                var behaviour = ctx.RequireRoot("Enemy").GetComponent<GateFixtures.GateSampleBehaviour>();
                Assert.IsNotNull(behaviour, "Authored GateSampleBehaviour was not materialized on Enemy");
                Assert.AreEqual(3, behaviour.Volley.Length, "Authored three-item Volley did not materialize with all items");
                Assert.AreEqual(5f, behaviour.Volley[0].amount, 0.0001f, "Volley[0].amount did not materialize");
                Assert.AreEqual(1, behaviour.Volley[0].kind, "Volley[0].kind did not materialize");
                Assert.AreEqual(0f, behaviour.Volley[1].amount, 0.0001f, "Volley[1].amount did not materialize");
                Assert.AreEqual(0, behaviour.Volley[1].kind, "Volley[1].kind did not materialize");
                Assert.AreEqual(7.5f, behaviour.Volley[2].amount, 0.0001f, "Volley[2].amount did not materialize");
                Assert.AreEqual(2, behaviour.Volley[2].kind, "Volley[2].kind did not materialize");
            });
    }

    // The middle item (amount = 0f, kind = 0) equals the type default on every member. A struct
    // MEMBER at its type default is excluded from the round trip, but a list ITEM at its type
    // default is not: it stays present, in position, and spelled explicitly in the converged source.
    [Test]
    public void CodeToScene_ListItemAtTheTypeDefault_IsNotFilteredOut()
    {
        var result = RoundTripProofHarness.ProveCodeToScene(
            usings: "",
            body:
                "        var enemy = scene.Add(\"Enemy\");\n" +
                "        enemy.Component<GateFixtures.GateSampleBehaviour>(c => c.Set(\"Volley\", new[] { " +
                "new GateFixtures.Damage { amount = 5f, kind = 1 }, " +
                "new GateFixtures.Damage { amount = 0f, kind = 0 }, " +
                "new GateFixtures.Damage { amount = 7.5f, kind = 2 } }));\n",
            assertScene: ctx =>
            {
                var behaviour = ctx.RequireRoot("Enemy").GetComponent<GateFixtures.GateSampleBehaviour>();
                Assert.IsNotNull(behaviour, "Authored GateSampleBehaviour was not materialized on Enemy");
                Assert.AreEqual(3, behaviour.Volley.Length, "The type-default-valued item was dropped from Volley");
                Assert.AreEqual(0f, behaviour.Volley[1].amount, 0.0001f, "Volley[1].amount did not materialize");
                Assert.AreEqual(0, behaviour.Volley[1].kind, "Volley[1].kind did not materialize");
            });

        var occurrences = result.ConvergedSource.Split(new[] { "new GateFixtures.Damage" }, System.StringSplitOptions.None).Length - 1;
        Assert.AreEqual(
            3, occurrences,
            "Converged source did not spell all three list items.\n" + result.ConvergedSource);
        StringAssert.Contains(
            "amount = 0f, kind = 0", result.ConvergedSource,
            "Converged source did not spell the type-default-valued list item explicitly.\n" + result.ConvergedSource);
    }

    // A struct member the author omits stays absent from the round-tripped source (it fills to the
    // type default on the live component only). Inside a list item, the same omission is completed
    // into source: the fill is written back explicitly, per item.
    [Test]
    public void CodeToScene_ListItemWithAnOmittedMember_IsCompletedInSourcePerItem()
    {
        var result = RoundTripProofHarness.ProveCodeToScene(
            usings: "",
            body:
                "        var enemy = scene.Add(\"Enemy\");\n" +
                "        enemy.Component<GateFixtures.GateSampleBehaviour>(c => c.Set(\"Volley\", new[] { " +
                "new GateFixtures.Damage { amount = 5f }, " +
                "new GateFixtures.Damage { kind = 2 } }));\n",
            assertScene: ctx =>
            {
                var behaviour = ctx.RequireRoot("Enemy").GetComponent<GateFixtures.GateSampleBehaviour>();
                Assert.IsNotNull(behaviour, "Authored GateSampleBehaviour was not materialized on Enemy");
                Assert.AreEqual(2, behaviour.Volley.Length, "Authored two-item Volley did not materialize");
                Assert.AreEqual(5f, behaviour.Volley[0].amount, 0.0001f, "Volley[0].amount did not materialize");
                Assert.AreEqual(0, behaviour.Volley[0].kind, "Volley[0].kind did not materialize to the type default");
                Assert.AreEqual(0f, behaviour.Volley[1].amount, 0.0001f, "Volley[1].amount did not materialize to the type default");
                Assert.AreEqual(2, behaviour.Volley[1].kind, "Volley[1].kind did not materialize");
            });

        StringAssert.Contains(
            "amount = 5f, kind = 0", result.ConvergedSource,
            "Converged source did not complete the omitted member of the first list item.\n" + result.ConvergedSource);
        StringAssert.Contains(
            "amount = 0f, kind = 2", result.ConvergedSource,
            "Converged source did not complete the omitted member of the second list item.\n" + result.ConvergedSource);
    }
}
