using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using SceneBuilder.Editor;

// spec 33 C7: a field the value model cannot represent surfaces as a located WARNING (not a NOTE)
// naming the object anchor, the component type, the field, and stating the value stays in the
// scene and is not synced, proved against a non-UnityEvent fixture field. Console-only: the
// builder source is byte-unchanged across the warning path, and the standing report surfaces at
// most once per editor session.
public class UnrepresentableFieldWarningTests
{
    private const string ScenePath = "Assets/GateTests/__UnrepresentableFieldWarningTemp.unity";
    private const string FixtureComponentTypeFullName = "GateFixtures.LocatedReportFixtureBehaviour";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using UnityEngine;
using UnityEngine.UI;
using SceneBuilder.Authoring;
public class UnrepresentableFieldWarningScene : ISceneDefinition
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
        _dir = Path.Combine(Path.GetTempPath(), "sb_ufw_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "UnrepresentableFieldWarningScene.cs");
        _sidecarPath = Path.Combine(_dir, "UnrepresentableFieldWarningScene.sbmap.json");
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

    // The path is general -- it fires for any unrepresentable field, not only a UnityEvent. Uses a
    // dedicated non-UnityEvent fixture field under a parent, so the LogicalId, the live scene
    // hierarchy path and the object's own name are three distinct strings.
    [Test]
    public void Sync_UnrepresentableNonUnityEventField_WarnsWithAllFourPartsAndLeavesTheSourceByteUnchanged()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Panel\", panel => panel.Add(\"Fixture\").Component<GateFixtures.LocatedReportFixtureBehaviour>());"));
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var fixtureGo = GameObject.Find("Panel/Fixture");
        Assert.IsNotNull(fixtureGo, "Build did not materialize the nested Panel/Fixture object.");
        var behaviour = fixtureGo.GetComponent<GateFixtures.LocatedReportFixtureBehaviour>();
        var so = new SerializedObject(behaviour);
        so.FindProperty("Anchored.m_Hidden").floatValue = 5f;
        so.ApplyModifiedProperties();

        ConflictSurfacing.ResetNotes();
        var preSyncBytes = File.ReadAllBytes(_builderPath);

        SceneBuilderSync.SyncResult result = null;
        var logs = CaptureLogs(() => result = EmittedCodeCompiles.SyncAndAssertCompiles(
            _builderPath, _sidecarPath, EditorSceneManager.GetActiveScene()));

        var note = result.Notes.SingleOrDefault(n => n.Located != null && n.Located.MemberKey == "Anchored");
        Assert.IsNotNull(note,
            "A non-UnityEvent field the value model cannot represent must also surface a located warning.\n" +
            string.Join("\n", result.Notes.Select(n => n.LogicalId + ": " + n.Reason)));
        Assert.IsFalse(string.IsNullOrEmpty(note.Located.LogicalId), "The report must name the object anchor.");
        Assert.AreEqual("Panel/Fixture", note.Located.ScenePath,
            "The report must carry the live scene hierarchy path.");
        Assert.AreEqual(FixtureComponentTypeFullName, note.Located.ComponentTypeFullName,
            "The report must name the component type.");
        Assert.AreEqual("Anchored", note.Located.MemberKey, "The report must name the field.");

        var matches = logs.Where(l => l.Message.Contains("'Anchored'")).ToArray();
        Assert.AreEqual(1, matches.Length,
            "Exactly one console line must name Anchored.\nLogs:\n" + string.Join("\n", logs.Select(l => l.Message)));
        var line = matches[0];
        StringAssert.Contains("The live value stays in the scene and is NOT synced to code.", line.Message,
            "The warning must state that the value stays in the scene and is not synced.");
        Assert.AreEqual(LogType.Warning, line.Type, "The promoted report must log at Warning severity.");
        StringAssert.StartsWith("[CodeScenes] WARNING on", line.Message,
            "The promoted report must be labeled WARNING, not NOTE.");

        Assert.AreEqual(0, result.PatchEdits, "The warning path must write no patch edit.");
        Assert.IsFalse(result.Changed, "The warning path must not change the builder source.");
        CollectionAssert.AreEqual(preSyncBytes, File.ReadAllBytes(_builderPath),
            "The builder source must be byte-unchanged across the warning path.");
    }

    // A standing report is a fact of the scene+source, deduped once per editor session per
    // (builder path, recurrence key) -- a fix that regressed into logging on every sync would make
    // auto-sync spam the console on every keystroke for any scene carrying the same unrepresentable
    // field shape.
    [Test]
    public void Sync_SecondSyncInTheSameSession_SurfacesTheWarningZeroTimes()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Panel\", panel => panel.Add(\"Fixture\").Component<GateFixtures.LocatedReportFixtureBehaviour>());"));
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var fixtureGo = GameObject.Find("Panel/Fixture");
        Assert.IsNotNull(fixtureGo, "Build did not materialize the nested Panel/Fixture object.");
        var behaviour = fixtureGo.GetComponent<GateFixtures.LocatedReportFixtureBehaviour>();
        var so = new SerializedObject(behaviour);
        so.FindProperty("Anchored.m_Hidden").floatValue = 5f;
        so.ApplyModifiedProperties();

        EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        SceneBuilderSync.SyncResult result = null;
        var logs = CaptureLogs(() => result = EmittedCodeCompiles.SyncAndAssertCompiles(
            _builderPath, _sidecarPath, EditorSceneManager.GetActiveScene()));

        Assert.IsEmpty(result.Notes,
            "A standing report must surface at most once per editor session.\n" +
            string.Join("\n", result.Notes.Select(n => n.LogicalId + ": " + n.Reason)));
        Assert.IsFalse(logs.Any(l => l.Message.Contains("WARNING on")),
            "A second sync in the same session must not re-log a standing warning.\nLogs:\n" +
            string.Join("\n", logs.Select(l => l.Message)));
    }
}
