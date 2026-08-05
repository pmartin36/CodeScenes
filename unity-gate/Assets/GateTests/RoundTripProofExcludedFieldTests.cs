using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using SceneBuilder.Editor;
using SceneBuilder.Core.Reconcile;

// A builder source authoring a serialized-field root the adapter never treats as author intent
// (SpriteRenderer.m_SortingLayer, a derived sorting-layer index) is a code->scene build fixed point
// whose author's file survives every build and sync in the sequence, and a scene->code sync neither
// reports it nor touches it while an unrelated sibling field on the same component still round-trips
// normally.
public class RoundTripProofExcludedFieldTests
{
    private static List<(string Message, LogType Type)> CaptureLogs(Action action)
    {
        var messages = new List<(string, LogType)>();
        void Handler(string condition, string stackTrace, LogType type) => messages.Add((condition, type));
        Application.logMessageReceived += Handler;
        try { action(); }
        finally { Application.logMessageReceived -= Handler; }
        return messages;
    }

    private static string FixtureBody(int foregroundId) =>
        "        var panel = scene.Add(\"Panel\");\n" +
        "        var sprite = panel.Add(\"Sprite\");\n" +
        "        sprite.Component<UnityEngine.SpriteRenderer>(c => {\n" +
        "            c.Set(\"m_SortingLayerID\", " + foregroundId + ");\n" +
        "            c.Set(\"m_SortingOrder\", 5);\n" +
        "            c.Set(\"m_SortingLayer\", 1);\n" +
        "        });";

    private static string ControlBody(int foregroundId) =>
        "        var panel = scene.Add(\"Panel\");\n" +
        "        var sprite = panel.Add(\"Sprite\");\n" +
        "        sprite.Component<UnityEngine.SpriteRenderer>(c => {\n" +
        "            c.Set(\"m_SortingLayerID\", " + foregroundId + ");\n" +
        "            c.Set(\"m_SortingOrder\", 5);\n" +
        "        });";

    [SetUp]
    public void SetUp() => ConflictSurfacing.ResetNotes();

    [TearDown]
    public void TearDown() => ConflictSurfacing.ResetNotes();

    [Test]
    public void CodeToScene_SourceAuthoringAnExcludedField_ConvergesAndItsSourceSurvives()
    {
        Assert.IsTrue(SortingLayer.layers.Any(l => l.name == "Foreground"),
            "ProjectSettings/TagManager.asset must define a 'Foreground' sorting layer to exercise this proof - none found.");
        var foregroundId = SortingLayer.NameToID("Foreground");

        RoundTripProofHarness.ProofResult fixtureResult = null;
        var logs = CaptureLogs(() => fixtureResult = RoundTripProofHarness.ProveCodeToSceneFixedPoint(
            usings: "",
            body: FixtureBody(foregroundId),
            assertScene: ctx =>
            {
                var sprite = GameObject.Find("Panel/Sprite");
                Assert.IsNotNull(sprite, "Build did not materialize the nested Panel/Sprite object.");
                var renderer = sprite.GetComponent<SpriteRenderer>();
                Assert.IsNotNull(renderer, "Build did not attach the SpriteRenderer that authors m_SortingLayer.");
                Assert.AreEqual(5, renderer.sortingOrder,
                    "The sibling authored field on the same component did not reach the live scene.");
                Assert.AreEqual(foregroundId, renderer.sortingLayerID, "The authored sortingLayerID did not materialize.");
                Assert.AreEqual("Foreground", renderer.sortingLayerName, "The live sorting layer name is wrong.");
            },
            assertConverged: ctx =>
            {
                StringAssert.Contains("\"m_SortingLayer\", 1", ctx.Source,
                    "The excluded field's authored line must survive convergence -- nothing may be pruned "
                        + "from the author's source.\n" + ctx.Source);
                var renderer = GameObject.Find("Panel/Sprite").GetComponent<SpriteRenderer>();
                Assert.AreEqual(foregroundId, renderer.sortingLayerID, "The live sorting layer must be unchanged by convergence.");
                Assert.AreEqual("Foreground", renderer.sortingLayerName, "The live sorting layer name must be unchanged by convergence.");
            }));

        var matches = logs.Where(l => l.Message.Contains("'m_SortingLayer'")).ToArray();
        Assert.AreEqual(1, matches.Length,
            "Exactly one console line across the seed build, both rebuilds and all three syncs must name "
                + "m_SortingLayer.\nLogs:\n" + string.Join("\n", logs.Select(l => l.Message)));
        Assert.AreEqual(LogType.Warning, matches[0].Type, "The report must log at Warning severity.");
        StringAssert.Contains(Conflict.LineHasNoEffect, matches[0].Message, "The report must state the authored line has no effect.");

        Assert.IsNotNull(fixtureResult, "ProveCodeToSceneFixedPoint must return a ProofResult.");
        var note = fixtureResult.Build.Notes.SingleOrDefault(n => n.Kind == ConflictKind.UnauthorableField);
        Assert.IsNotNull(note, "The seed build must surface exactly one UnauthorableField report.");
        Assert.AreEqual("m_SortingLayer", note.Located.MemberKey);

        var controlResult = RoundTripProofHarness.ProveCodeToSceneFixedPoint(
            usings: "",
            body: ControlBody(foregroundId),
            assertScene: ctx => { },
            assertConverged: ctx => { });

        Assert.IsEmpty(controlResult.Build.Notes,
            "The control fixture (no excluded field authored) must raise no report.");
        Assert.AreEqual(controlResult.Build.PlanOpCount, fixtureResult.Build.PlanOpCount,
            "Authoring the excluded field must add zero plan ops relative to the report-free control build.");
    }

    [Test]
    public void SceneToCode_EditedSiblingField_ReachesSourceAndLeavesTheExcludedLineIntact()
    {
        Assert.IsTrue(SortingLayer.layers.Any(l => l.name == "Foreground"),
            "ProjectSettings/TagManager.asset must define a 'Foreground' sorting layer to exercise this proof - none found.");
        var foregroundId = SortingLayer.NameToID("Foreground");

        // Logs are not captured here: the harness's seed build legitimately logs the excluded-field
        // warning once (the build-side report), so a whole-sequence log capture cannot distinguish
        // that from a sync-side log without re-implementing the harness's internals. The sync-side
        // direction guard is the Conflicts assertion below instead.
        RoundTripProofHarness.ProveSceneToCode(
            usings: "",
            body: FixtureBody(foregroundId),
            mutateScene: ctx =>
            {
                GameObject.Find("Panel/Sprite").GetComponent<SpriteRenderer>().sortingOrder = 7;
            },
            assertSource: ctx =>
            {
                StringAssert.Contains("\"m_SortingOrder\", 7", ctx.Source,
                    "The edited sibling field did not reach builder source.\n" + ctx.Source);
                StringAssert.Contains("\"m_SortingLayer\", 1", ctx.Source,
                    "The excluded field's authored line must not be pruned by a sync.\n" + ctx.Source);
                Assert.IsFalse(ctx.Sync.Conflicts.Any(c => c.Kind == ConflictKind.UnauthorableField),
                    "A scene->code sync must never report the excluded field -- the report is build-only.");

                var renderer = GameObject.Find("Panel/Sprite").GetComponent<SpriteRenderer>();
                Assert.AreEqual(foregroundId, renderer.sortingLayerID, "The live sorting layer must be unchanged by the sync.");
                Assert.AreEqual("Foreground", renderer.sortingLayerName, "The live sorting layer name must be unchanged by the sync.");
            });
    }
}
