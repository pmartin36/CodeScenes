using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using SceneBuilder.Editor;
using SceneBuilder.Core.Reconcile;

// Spec 35 D2: a source-authored field the adapter never treats as author intent (e.g.
// SpriteRenderer.m_SortingLayer, a Renderer-derived index) is suppressed on the code->scene build
// AND reported through the same located channel spec 33 C5/C7 use, at Warning severity, at most
// once per editor session per (builder, field). The report fires whether the component is newly
// created by the build or already matched in the scene, and the build never rewrites the author's
// source to achieve it.
public class UnauthorableFieldBuildReportTests
{
    private const string ScenePath = "Assets/GateTests/__UnauthorableFieldBuildReportTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using UnityEngine;
using SceneBuilder.Authoring;
public class UnauthorableFieldBuildReportScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    private static List<(string Message, LogType Type)> CaptureLogs(Action action)
    {
        var messages = new List<(string, LogType)>();
        void Handler(string condition, string stackTrace, LogType type) => messages.Add((condition, type));
        Application.logMessageReceived += Handler;
        try { action(); }
        finally { Application.logMessageReceived -= Handler; }
        return messages;
    }

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_ufb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "UnauthorableFieldBuildReportScene.cs");
        _sidecarPath = Path.Combine(_dir, "UnauthorableFieldBuildReportScene.sbmap.json");
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        ConflictSurfacing.ResetNotes();
    }

    [TearDown]
    public void TearDown()
    {
        ConflictSurfacing.ResetNotes();

        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }
    }

    private const string CreatePathBody =
        "        var panel = scene.Add(\"Panel\");\n" +
        "        var sprite = panel.Add(\"Sprite\");\n" +
        "        sprite.Component<UnityEngine.SpriteRenderer>(c => c.Set(\"m_SortingLayer\", 1));";

    [Test]
    public void Build_CreatePath_FirstBuildOfSession_WarnsWithAllFourPartsAndLeavesSourceByteUnchanged()
    {
        File.WriteAllText(_builderPath, Source(CreatePathBody));
        var preBuildBytes = File.ReadAllBytes(_builderPath);
        var scene = EditorSceneManager.GetActiveScene();

        SceneBuilderBuild.BuildResult result = null;
        var logs = CaptureLogs(() => result = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene));

        // Non-vacuity premise: the component was CREATED by this build, not matched.
        var spriteGo = GameObject.Find("Panel/Sprite");
        Assert.IsNotNull(spriteGo, "Build did not materialize the nested Panel/Sprite object.");
        Assert.IsNotNull(spriteGo.GetComponent<SpriteRenderer>(),
            "Build did not attach the SpriteRenderer this test authors m_SortingLayer on.");

        var matches = logs.Where(l => l.Message.Contains("'m_SortingLayer'")).ToArray();
        Assert.AreEqual(1, matches.Length,
            "Exactly one console line must name m_SortingLayer.\nLogs:\n" +
            string.Join("\n", logs.Select(l => l.Message)));
        var line = matches[0];
        Assert.AreEqual(LogType.Warning, line.Type, "The report must log at Warning severity, not a NOTE.");
        StringAssert.StartsWith("[CodeScenes] WARNING on", line.Message,
            "The report must be labeled WARNING, not NOTE.");
        StringAssert.Contains(Conflict.LineHasNoEffect, line.Message,
            "The report must state the authored line has no effect.");

        Assert.IsNotNull(result, "Run must return a BuildResult.");
        var note = result.Notes.SingleOrDefault(n => n.Kind == ConflictKind.UnauthorableField);
        Assert.IsNotNull(note,
            "BuildResult.Notes must carry the surfaced UnauthorableField report.\n" +
            string.Join("\n", result.Notes.Select(n => n.Kind + ": " + n.Reason)));
        Assert.IsFalse(string.IsNullOrEmpty(note.Located.LogicalId), "The report must name the object anchor.");
        Assert.AreEqual("Panel/Sprite", note.Located.ScenePath,
            "The report must carry the live scene hierarchy path.");
        Assert.AreEqual("UnityEngine.SpriteRenderer", note.Located.ComponentTypeFullName,
            "The report must name the component type.");
        Assert.AreEqual("m_SortingLayer", note.Located.MemberKey, "The report must name the field.");

        var expected = "[CodeScenes] WARNING on " + note.Located.Compose();
        Assert.AreEqual(expected, line.Message, "The console line must match the composed report exactly.");

        CollectionAssert.AreEqual(preBuildBytes, File.ReadAllBytes(_builderPath),
            "The builder source must be byte-unchanged by the report path.");
    }

    [Test]
    public void Build_MatchedPath_ComponentAlreadyInScene_WarnsWithAllFourParts()
    {
        // Phase 1: create the SpriteRenderer with no excluded field authored, so it is matched
        // (not created) by the second build.
        File.WriteAllText(_builderPath, Source(
            "        var panel = scene.Add(\"Panel\");\n" +
            "        var sprite = panel.Add(\"Sprite\");\n" +
            "        sprite.Component<UnityEngine.SpriteRenderer>(c => c.Set(\"m_SortingLayerID\", 0));"));
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        Assert.IsNotNull(GameObject.Find("Panel/Sprite"), "Phase 1 build did not materialize Panel/Sprite.");

        ConflictSurfacing.ResetNotes();

        // Phase 2: author the excluded field on the now-matched component (a single Component call
        // with both fields set in its block body — two separate calls would each try to add a new
        // SpriteRenderer).
        File.WriteAllText(_builderPath, Source(
            "        var panel = scene.Add(\"Panel\");\n" +
            "        var sprite = panel.Add(\"Sprite\");\n" +
            "        sprite.Component<UnityEngine.SpriteRenderer>(c => {\n" +
            "            c.Set(\"m_SortingLayerID\", 0);\n" +
            "            c.Set(\"m_SortingLayer\", 1);\n" +
            "        });"));

        SceneBuilderBuild.BuildResult result = null;
        var logs = CaptureLogs(() => result = SceneBuilderBuild.Run(
            _builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene()));

        var matches = logs.Where(l => l.Message.Contains("'m_SortingLayer'")).ToArray();
        Assert.AreEqual(1, matches.Length,
            "Exactly one console line must name m_SortingLayer on the matched path.\nLogs:\n" +
            string.Join("\n", logs.Select(l => l.Message)));
        var line = matches[0];
        Assert.AreEqual(LogType.Warning, line.Type, "The matched-path report must also log at Warning severity.");
        StringAssert.StartsWith("[CodeScenes] WARNING on", line.Message);
        StringAssert.Contains(Conflict.LineHasNoEffect, line.Message);

        var note = result.Notes.SingleOrDefault(n => n.Kind == ConflictKind.UnauthorableField);
        Assert.IsNotNull(note, "BuildResult.Notes must carry the matched-path report too.");
        Assert.AreEqual("UnityEngine.SpriteRenderer", note.Located.ComponentTypeFullName);
        Assert.AreEqual("m_SortingLayer", note.Located.MemberKey);
    }

    [Test]
    public void Build_SecondBuildInTheSameSession_SurfacesTheWarningZeroTimes()
    {
        File.WriteAllText(_builderPath, Source(CreatePathBody));
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        SceneBuilderBuild.BuildResult result = null;
        var logs = CaptureLogs(() => result = SceneBuilderBuild.Run(
            _builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene()));

        Assert.IsNotNull(result);
        Assert.IsEmpty(result.Notes,
            "A standing report must surface at most once per editor session.\n" +
            string.Join("\n", result.Notes.Select(n => n.Kind + ": " + n.Reason)));
        Assert.IsFalse(logs.Any(l => l.Message.Contains("WARNING on") || l.Message.Contains("m_SortingLayer")),
            "A second build in the same session must not re-log the standing warning.\nLogs:\n" +
            string.Join("\n", logs.Select(l => l.Message)));
    }

    // Structural: every production site that materializes a code->scene Plan must also surface its
    // reports, so a future plan producer cannot silently drop them.
    private static readonly Regex StringLiteral = new Regex("\"(?:\\\\.|[^\"\\\\])*\"", RegexOptions.Compiled);

    [Test]
    public void Package_EveryMaterializerCallerAlsoCallsSurfaceNotes()
    {
        var root = SceneBuilderPaths.PackageRootPath;
        Assert.IsNotNull(root, "SceneBuilderPaths.PackageRootPath resolved to null - cannot scan the " +
            "package for Materializer.Materialize callers (this must FAIL, never vacuously pass).");

        var materializes = new HashSet<string>();
        var surfaces = new HashSet<string>();
        foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            foreach (var raw in lines)
            {
                var line = StringLiteral.Replace(raw, "\"\"");
                var commentIndex = line.IndexOf("//", StringComparison.Ordinal);
                if (commentIndex >= 0)
                {
                    line = line.Substring(0, commentIndex);
                }

                if (line.Contains("Materializer.Materialize"))
                {
                    materializes.Add(file);
                }

                if (line.Contains("ConflictSurfacing.SurfaceNotes"))
                {
                    surfaces.Add(file);
                }
            }
        }

        Assert.IsNotEmpty(materializes, "Expected at least one production site calling Materializer.Materialize.");
        var missing = materializes.Where(f => !surfaces.Contains(f)).ToArray();
        Assert.IsEmpty(missing,
            "Every file that materializes a code->scene Plan must also surface its standing reports " +
            "through ConflictSurfacing.SurfaceNotes, so a future producer cannot drop them. Missing:\n" +
            string.Join("\n", missing));
    }
}
