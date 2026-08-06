using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using SceneBuilder.Editor;

// Live-editor confirmation that the migrated container-descent passes (AssetRefLowering, SourceExpr,
// PlanningValidator, SerializedFieldBridge.WriteProperty) still compose into working bidirectional
// sync. Test 1 covers code->scene materialization of builtin and project-asset references with a
// clean console and a zero-op second build; Test 2 covers an Inspector-driven scene->code edit
// reaching source with no new warnings. Derives from the pinned BuiltinRefGateHarness for scene setup
// and oracle helpers; drives the scene->code direction through RoundTripProofHarness, which already
// owns the fixed-point convergence discipline that case needs.
public class UniformValueDescentConfirmationTests : BuiltinRefGateHarness
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

    // Zero Warning/Error/Assert/Exception entries in `logs`, and at least one SceneBuilder info
    // entry -- proving the capture was actually live for the operation under test, so the absence
    // assertion above it is not vacuous.
    private static void AssertCleanConsole(List<(string Message, LogType Type)> logs, string context)
    {
        var bad = logs
            .Where(l => l.Type == LogType.Warning || l.Type == LogType.Error
                || l.Type == LogType.Assert || l.Type == LogType.Exception)
            .ToList();
        Assert.IsEmpty(
            bad,
            context + ": expected a clean console, captured:\n"
                + string.Join("\n", bad.Select(l => l.Type + ": " + l.Message)));
        Assert.IsTrue(
            logs.Any(l => l.Message.Contains("[SceneBuilder] ")),
            context + ": captured zero SceneBuilder log entries -- the capture was not live for this operation.");
    }

    // Checklist items 1 + 3: a scene using both builtin and project-asset references materializes
    // by reference against independent oracles with a clean console, and a second Materialize over
    // the unchanged source and scene applies zero plan ops.
    [Test]
    public void CodeToScene_BuiltinAndAssetReferences_ResolveWithACleanConsoleAndASecondBuildAppliesZeroOps()
    {
        var expectedMesh = PrimitiveMesh(PrimitiveType.Cube);
        var expectedBuiltinMaterial = PrimitiveMaterial(PrimitiveType.Cube);
        var expectedAssetMaterial = LoadMaterial(RedPath);
        Assert.IsNotNull(expectedAssetMaterial, "Fixture Red.mat did not load -- test premise invalid.");

        var body =
            "        var widget = scene.Add(\"Widget\");\n"
            + "        widget.Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"Cube\")));\n"
            + "        widget.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Asset(\""
            + RedPath + "\"), Builtin(\"Default-Material\") }));";
        File.WriteAllText(BuilderPath, Source(body));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        SceneBuilderBuild.BuildResult build = null;
        var firstLogs = CaptureLogs(() =>
        {
            build = SceneBuilderBuild.Run(BuilderPath, ScenePath, SidecarPath, EditorSceneManager.GetActiveScene());
        });

        Assert.IsEmpty(
            build.Diagnostics,
            "First build was refused.\n" + string.Join("\n", build.Diagnostics.Select(d => d.Message)));

        var widget = FindRoot(EditorSceneManager.GetActiveScene(), "Widget");
        Assert.IsNotNull(widget, "Widget was not created by SceneBuilderBuild.Run");

        var mf = widget.GetComponent<MeshFilter>();
        Assert.IsNotNull(mf, "Authored MeshFilter was not materialized on Widget");
        Assert.AreEqual(
            expectedMesh, mf.sharedMesh,
            "Assigned mesh is not the same object as a live CreatePrimitive(Cube)'s mesh.");

        var mr = widget.GetComponent<MeshRenderer>();
        Assert.IsNotNull(mr, "Authored MeshRenderer was not materialized on Widget");
        Assert.AreEqual(2, mr.sharedMaterials.Length, "Mixed material list did not materialize exactly 2 slots.");
        Assert.AreEqual(expectedAssetMaterial, mr.sharedMaterials[0], "Slot [0] is not the project asset (Red.mat).");
        Assert.AreEqual(expectedBuiltinMaterial, mr.sharedMaterials[1], "Slot [1] is not the built-in Default-Material.");
        Assert.AreNotEqual(
            mr.sharedMaterials[0], mr.sharedMaterials[1],
            "Both slots hold the same object -- the order assertion above would be vacuous.");

        AssertCleanConsole(firstLogs, "first build");

        SceneBuilderBuild.BuildResult rebuild = null;
        var secondLogs = CaptureLogs(() =>
        {
            rebuild = SceneBuilderBuild.Run(BuilderPath, ScenePath, SidecarPath, EditorSceneManager.GetActiveScene());
        });

        Assert.IsEmpty(
            rebuild.Diagnostics,
            "Second build over an unchanged source and scene was refused.\n"
                + string.Join("\n", rebuild.Diagnostics.Select(d => d.Message)));
        Assert.AreEqual(
            0, rebuild.PlanOpCount,
            "Second build over an unchanged source and scene applied " + rebuild.PlanOpCount
                + " plan op(s); a converged build applies none.");

        AssertCleanConsole(secondLogs, "second build");
    }

    // Checklist item 2: an Inspector-shaped edit to a material slot (via SerializedObject, the
    // Inspector's own write path) reaches the builder source byte-exactly, with no new warnings
    // across the whole converge-edit-sync-converge sequence.
    [Test]
    public void SceneToCode_InspectorEditedMaterialSlot_ReachesSourceWithNoWarnings()
    {
        var builtinMaterial = PrimitiveMaterial(PrimitiveType.Cube);

        var body =
            "        var widget = scene.Add(\"Widget\");\n"
            + "        widget.Component<UnityEngine.MeshFilter>(c => c.Set(\"m_Mesh\", Builtin(\"Cube\")));\n"
            + "        widget.Component<UnityEngine.MeshRenderer>(c => c.Set(\"m_Materials\", new[] { Asset(\""
            + RedPath + "\"), Builtin(\"Default-Material\") }));";

        RoundTripProofHarness.ProofResult result = null;
        var logs = CaptureLogs(() =>
        {
            result = RoundTripProofHarness.ProveSceneToCode(
                usings: "using static SceneBuilder.Authoring.AssetRefs;\n",
                body: body,
                mutateScene: ctx =>
                {
                    var mr = ctx.RequireRoot("Widget").GetComponent<MeshRenderer>();
                    var so = new SerializedObject(mr);
                    var mats = so.FindProperty("m_Materials");
                    Assert.IsNotNull(mats, "m_Materials serialized property was not found -- test premise invalid.");
                    Assert.AreEqual(2, mats.arraySize, "m_Materials did not have 2 elements -- test premise invalid.");
                    mats.GetArrayElementAtIndex(0).objectReferenceValue = builtinMaterial;
                    so.ApplyModifiedProperties();
                },
                assertSource: ctx =>
                {
                    StringAssert.Contains(
                        "new[] { Builtin(\"Default-Material\"), Builtin(\"Default-Material\") }",
                        ctx.Source,
                        "Edited source does not contain the expected byte-exact material list.");
                    StringAssert.DoesNotContain(
                        "Asset(\"" + RedPath,
                        ctx.Source,
                        "Edited source still references the replaced asset -- the edit did not reach source.");
                    Assert.AreEqual(1, ctx.Sync.PatchEdits, "Drive sync applied an unexpected number of patch edits.");
                    Assert.IsTrue(
                        ctx.Sync.Changed,
                        "Drive sync did not report Changed even though the Inspector edit should reach source.");
                });
        });

        Assert.IsNotNull(result, "ProveSceneToCode did not return a result.");
        AssertCleanConsole(logs, "scene->code round trip");
    }
}
