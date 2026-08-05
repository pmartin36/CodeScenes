using NUnit.Framework;
using UnityEditor;

// Round-trip proof for a LIST of scene references (WaypointPath.waypoints / .targets), both
// serialization shapes Unity supports (GameObject[] and List<GameObject>). A bare ObjectRef proof
// already covers the single-reference case; this file covers the per-index lowering a list adds:
// SetArraySize plus one SetReference per element, in both directions, including a cleared element
// and a shortened list.
public class RoundTripProofObjectRefListTests
{
    [Test]
    public void CodeToScene_TwoElementSceneRefList_AssignsBothLiveElements()
    {
        RoundTripProofHarness.ProveCodeToScene(
            usings: "",
            body:
                "        var alpha = scene.Add(\"Alpha\");\n" +
                "        var beta = scene.Add(\"Beta\");\n" +
                "        var path = scene.Add(\"Path\");\n" +
                "        path.Component<WaypointPath>(c => c.Set(\"waypoints\", new[] { alpha, beta }));\n",
            assertScene: ctx =>
            {
                var waypointPath = ctx.RequireRoot("Path").GetComponent<WaypointPath>();
                Assert.IsNotNull(waypointPath, "Authored WaypointPath was not materialized on Path");
                Assert.AreEqual(2, waypointPath.waypoints.Length, "Authored two-element waypoints did not materialize");
                Assert.AreSame(ctx.RequireRoot("Alpha"), waypointPath.waypoints[0], "waypoints[0] was not Alpha");
                Assert.AreSame(ctx.RequireRoot("Beta"), waypointPath.waypoints[1], "waypoints[1] was not Beta");
            });
    }

    [Test]
    public void CodeToScene_GenericListOfSceneRefs_AssignsBothLiveElements()
    {
        RoundTripProofHarness.ProveCodeToScene(
            usings: "",
            body:
                "        var alpha = scene.Add(\"Alpha\");\n" +
                "        var beta = scene.Add(\"Beta\");\n" +
                "        var path = scene.Add(\"Path\");\n" +
                "        path.Component<WaypointPath>(c => c.Set(\"targets\", new[] { alpha, beta }));\n",
            assertScene: ctx =>
            {
                var waypointPath = ctx.RequireRoot("Path").GetComponent<WaypointPath>();
                Assert.IsNotNull(waypointPath, "Authored WaypointPath was not materialized on Path");
                Assert.AreEqual(2, waypointPath.targets.Count, "Authored two-element targets did not materialize");
                Assert.AreSame(ctx.RequireRoot("Alpha"), waypointPath.targets[0], "targets[0] was not Alpha");
                Assert.AreSame(ctx.RequireRoot("Beta"), waypointPath.targets[1], "targets[1] was not Beta");
            });
    }

    [Test]
    public void CodeToScene_ElementReauthoredAsNodeHandleNone_ClearsOnlyThatElement()
    {
        var result = RoundTripProofHarness.ProveReauthoredCodeToScene(
            usings: "",
            body:
                "        var alpha = scene.Add(\"Alpha\");\n" +
                "        var beta = scene.Add(\"Beta\");\n" +
                "        var path = scene.Add(\"Path\");\n" +
                "        path.Component<WaypointPath>(c => c.Set(\"waypoints\", new[] { alpha, beta }));\n",
            reauthor: source => source.Replace("new[] { alpha, beta }", "new[] { alpha, NodeHandle.None }"),
            assertScene: ctx =>
            {
                var waypointPath = ctx.RequireRoot("Path").GetComponent<WaypointPath>();
                Assert.IsNotNull(waypointPath, "WaypointPath was not found after the re-authored rebuild");
                Assert.AreEqual(2, waypointPath.waypoints.Length, "Clearing one element must not resize the array");
                Assert.AreSame(ctx.RequireRoot("Alpha"), waypointPath.waypoints[0], "waypoints[0] must stay Alpha");
                Assert.IsNull(waypointPath.waypoints[1], "waypoints[1] was not cleared to null");
            });

        StringAssert.Contains(
            "new[] { alpha, NodeHandle.None }", result.ConvergedSource,
            "Converged source did not keep the explicit NodeHandle.None at the cleared index.\n" + result.ConvergedSource);
    }

    [Test]
    public void CodeToScene_ShortenedSceneRefList_LeavesNoStaleTrailingElement()
    {
        RoundTripProofHarness.ProveReauthoredCodeToScene(
            usings: "",
            body:
                "        var alpha = scene.Add(\"Alpha\");\n" +
                "        var beta = scene.Add(\"Beta\");\n" +
                "        var gamma = scene.Add(\"Gamma\");\n" +
                "        var path = scene.Add(\"Path\");\n" +
                "        path.Component<WaypointPath>(c => c.Set(\"waypoints\", new[] { alpha, beta, gamma }));\n",
            reauthor: source => source.Replace("new[] { alpha, beta, gamma }", "new[] { alpha, beta }"),
            assertScene: ctx =>
            {
                var waypointPath = ctx.RequireRoot("Path").GetComponent<WaypointPath>();
                Assert.IsNotNull(waypointPath, "WaypointPath was not found after the re-authored rebuild");
                Assert.AreEqual(2, waypointPath.waypoints.Length, "Shortening the list did not shrink the live array");
                Assert.AreSame(ctx.RequireRoot("Beta"), waypointPath.waypoints[1], "waypoints[1] was not Beta after the shorten");
            });
    }

    [Test]
    public void SceneToCode_RewiredListElement_PatchesOnlyThatElement()
    {
        RoundTripProofHarness.ProveSceneToCode(
            usings: "",
            body:
                "        var alpha = scene.Add(\"Alpha\");\n" +
                "        var beta = scene.Add(\"Beta\");\n" +
                "        var gamma = scene.Add(\"Gamma\");\n" +
                "        var path = scene.Add(\"Path\");\n" +
                "        path.Component<WaypointPath>(c => c.Set(\"waypoints\", new[] { alpha, beta }));\n",
            mutateScene: ctx =>
            {
                var waypointPath = ctx.RequireRoot("Path").GetComponent<WaypointPath>();
                var so = new SerializedObject(waypointPath);
                so.FindProperty("waypoints").GetArrayElementAtIndex(1).objectReferenceValue = ctx.RequireRoot("Gamma");
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(waypointPath);
            },
            assertSource: ctx =>
            {
                StringAssert.Contains(
                    "c.Set(\"waypoints\", new SceneBuilder.Authoring.SceneObjectHandle[] { alpha, gamma })", ctx.Source,
                    "Rewiring waypoints[1] to Gamma did not patch the source to the rewired element.\n" + ctx.Source);
                StringAssert.DoesNotContain(
                    "new[] { alpha, beta }", ctx.Source,
                    "The stale pre-rewire list literal is still present in source.\n" + ctx.Source);
            });
    }

    [Test]
    public void SceneToCode_ListFieldAbsentFromSource_IntroducesTheStringSpelling()
    {
        RoundTripProofHarness.ProveSceneToCode(
            usings: "",
            body:
                "        var alpha = scene.Add(\"Alpha\");\n" +
                "        var beta = scene.Add(\"Beta\");\n" +
                "        var path = scene.Add(\"Path\");\n" +
                "        path.Component<WaypointPath>();\n",
            mutateScene: ctx =>
            {
                var waypointPath = ctx.RequireRoot("Path").GetComponent<WaypointPath>();
                waypointPath.waypoints = new[] { ctx.RequireRoot("Alpha"), ctx.RequireRoot("Beta") };
                EditorUtility.SetDirty(waypointPath);
            },
            assertSource: ctx =>
            {
                StringAssert.Contains(
                    "c.Set(\"waypoints\", new SceneBuilder.Authoring.SceneObjectHandle[] { alpha, beta })", ctx.Source,
                    "Introducing a scene-assigned list absent from source did not emit the string-path setter.\n" + ctx.Source);
                StringAssert.DoesNotContain(
                    "x => x.waypoints", ctx.Source,
                    "A reference list must never be introduced through the typed selector.\n" + ctx.Source);
            });
    }
}
