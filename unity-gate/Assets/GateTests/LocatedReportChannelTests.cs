using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using SceneBuilder.Editor;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Reconcile;

// spec 33 C-located-report: an UnrepresentableValue report reaching the console must name the live
// scene hierarchy path alongside the LogicalId, the component type full name and the field/member
// key, through BOTH the keyed standing-note channel and the keyless per-pass conflict channel, and
// the location must be readable as DATA (Conflict.Located.ScenePath), not only as console text. An
// anchor with no resolvable live object degrades to the LogicalId triple with no path segment. The
// adapter's own note about a live object it is holding keeps its existing message text and gains
// the same live path.
public class LocatedReportChannelTests
{
    private const string ScenePath = "Assets/GateTests/__LocatedReportChannelTemp.unity";
    private const string ComponentTypeFullName = "GateFixtures.LocatedReportFixtureBehaviour";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class LocatedReportChannelScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    private static List<string> CaptureLogs(Action action)
    {
        var messages = new List<string>();
        void Handler(string condition, string stackTrace, LogType type) => messages.Add(condition);
        Application.logMessageReceived += Handler;
        try { action(); }
        finally { Application.logMessageReceived -= Handler; }
        return messages;
    }

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_lrc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "LocatedReportChannelScene.cs");
        _sidecarPath = Path.Combine(_dir, "LocatedReportChannelScene.sbmap.json");
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

    private GateFixtures.LocatedReportFixtureBehaviour BuildNestedFixture()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Panel\", panel => panel.Add(\"Fixture\").Component<GateFixtures.LocatedReportFixtureBehaviour>());"));
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var fixtureGo = GameObject.Find("Panel/Fixture");
        Assert.IsNotNull(fixtureGo, "Build did not materialize the nested Panel/Fixture object.");
        return fixtureGo.GetComponent<GateFixtures.LocatedReportFixtureBehaviour>();
    }

    // A struct whose only member has no compiling spelling (Anchored) surfaces as a keyed standing
    // NOTE (Reconciler routes RecurrenceKey != null reports into ReconcileResult.Notes).
    [Test]
    public void Sync_UnrepresentableStandingNote_NamesTheAnchorTypeMemberAndLiveScenePath()
    {
        var behaviour = BuildNestedFixture();
        var so = new SerializedObject(behaviour);
        so.FindProperty("Anchored.m_Hidden").floatValue = 5f;
        so.ApplyModifiedProperties();

        SceneBuilderSync.SyncResult result = null;
        var logs = CaptureLogs(() => result = EmittedCodeCompiles.SyncAndAssertCompiles(
            _builderPath, _sidecarPath, EditorSceneManager.GetActiveScene()));

        // The untouched Mixed field also carries an unrepresentable member sitting at its type
        // default, so it surfaces its own TYPE-scoped note alongside Anchored's; filter to the one
        // under test the same way the field-level suite disambiguates two co-resident reports.
        var note = result.Notes.SingleOrDefault(n =>
            n.Kind == ConflictKind.UnrepresentableValue && n.Located != null && n.Located.MemberKey == "Anchored");
        Assert.IsNotNull(note,
            "The first sync must surface a standing note for the Anchored field.\n" +
            string.Join("\n", result.Notes.Select(n => n.LogicalId + ": " + n.Reason)));
        Assert.IsNotNull(note.Located, "An UnrepresentableValue report must carry Located data.");
        Assert.AreEqual(ComponentTypeFullName, note.Located.ComponentTypeFullName,
            "The report must name the component's type full name.");
        Assert.AreEqual("Anchored", note.Located.MemberKey,
            "The report must name the field that could not be represented.");
        Assert.AreEqual("Panel/Fixture", note.Located.ScenePath,
            "The report must carry the live scene hierarchy path as data.");

        var expected = $"[CodeScenes] WARNING on '{note.Located.LogicalId}' (Panel/Fixture) > " +
            $"'{ComponentTypeFullName}' > 'Anchored': {note.Located.Detail}";
        Assert.AreEqual(expected, logs.Single(m => m.Contains("'Anchored'")),
            "The composed console NOTE line does not match segment for segment.\nLogs:\n" + string.Join("\n", logs));
    }

    // A struct with one representable member (Visible) and one that is not (m_Hidden) surfaces as a
    // keyless PER-PASS conflict (RecurrenceKey null), landing in ReconcileResult.Conflicts rather
    // than Notes.
    [Test]
    public void Sync_UnrepresentablePerPassConflict_NamesTheAnchorTypeMemberAndLiveScenePath()
    {
        var behaviour = BuildNestedFixture();
        var so = new SerializedObject(behaviour);
        so.FindProperty("Mixed.Visible").floatValue = 5f;
        so.FindProperty("Mixed.m_Hidden").floatValue = 7f;
        so.ApplyModifiedProperties();

        SceneBuilderSync.SyncResult result = null;
        var logs = CaptureLogs(() => result = EmittedCodeCompiles.SyncAndAssertCompiles(
            _builderPath, _sidecarPath, EditorSceneManager.GetActiveScene()));

        var conflict = result.Conflicts.SingleOrDefault(c => c.Kind == ConflictKind.UnrepresentableValue);
        Assert.IsNotNull(conflict,
            "A mixed struct with one unrepresentable member must surface a per-pass conflict.\n" +
            string.Join("\n", result.Conflicts.Select(c => c.LogicalId + ": " + c.Reason)));
        Assert.IsNotNull(conflict.Located, "An UnrepresentableValue report must carry Located data.");
        Assert.AreEqual(ComponentTypeFullName, conflict.Located.ComponentTypeFullName,
            "The report must name the component's type full name.");
        Assert.AreEqual("Mixed.m_Hidden", conflict.Located.MemberKey,
            "The report must name the excluded struct member.");
        Assert.AreEqual("Panel/Fixture", conflict.Located.ScenePath,
            "The report must carry the live scene hierarchy path as data.");

        var expected = $"[SceneBuilder] Conflict (UnrepresentableValue) on '{conflict.Located.LogicalId}' (Panel/Fixture) > " +
            $"'{ComponentTypeFullName}' > 'Mixed.m_Hidden': {conflict.Located.Detail}";
        Assert.AreEqual(expected, logs.Single(m => m.Contains("'Mixed.m_Hidden'")),
            "The composed console Conflict line does not match segment for segment.\nLogs:\n" + string.Join("\n", logs));
    }

    // A report anchored on a LogicalId this sync itself introduces (a handle-less owner gains a
    // handle so an appended component can be written) still names the live scene hierarchy path,
    // not just an anchor already present in the pre-sync sidecar.
    [Test]
    public void Sync_ReportAnchoredOnAHandleThisSyncIntroduces_StillNamesTheLiveScenePath()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Panel\", panel => panel.Add(\"Fixture\"));"));
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var fixtureGo = GameObject.Find("Panel/Fixture");
        Assert.IsNotNull(fixtureGo, "Build did not materialize the nested Panel/Fixture object.");
        fixtureGo.AddComponent<GateFixtures.LocatedReportFixtureBehaviour>();

        SceneBuilderSync.SyncResult result = null;
        CaptureLogs(() => result = EmittedCodeCompiles.SyncAndAssertCompiles(
            _builderPath, _sidecarPath, EditorSceneManager.GetActiveScene()));

        var note = result.Notes.SingleOrDefault(n =>
            n.Kind == ConflictKind.UnrepresentableValue && n.Located != null && n.Located.MemberKey == "Anchored");
        Assert.IsNotNull(note,
            "The append of a component onto a handle-less owner must still surface the Anchored note.\n" +
            string.Join("\n", result.Notes.Select(n => n.LogicalId + ": " + n.Reason)));
        Assert.IsFalse(note.Located.LogicalId.StartsWith("Panel/0/Fixture/0"),
            "The report's anchor must be the POST-batch id the append introduced, not the pre-sync positional id: " +
            note.Located.LogicalId);
        Assert.AreEqual("Panel/Fixture", note.Located.ScenePath,
            "A report anchored on a LogicalId this sync introduces must still carry the live scene hierarchy path.");
    }

    // An anchor absent from the IdentityMap has no live object to resolve: the report still logs the
    // LogicalId triple, with no path segment and no crash — never an empty "()".
    [Test]
    public void SurfaceNotes_ReportAnchoredOnUnmappedLogicalId_LogsWithoutAPathSegment()
    {
        var map = new IdentityMap { Entries = Array.Empty<IdentityMapEntry>() };
        var scenePath = new SceneHierarchyPath(map, Array.Empty<IdentityMapEntry>());
        var note = Conflict.Unrepresentable(
            "ghost", ComponentTypeFullName, "Anchored", "value could not be represented",
            "unrepresentable-type-field:ghost:Anchored", null);

        IReadOnlyList<Conflict> surfaced = null;
        var logs = CaptureLogs(() => surfaced = ConflictSurfacing.SurfaceNotes(new[] { note }, "builder.cs", scenePath));

        Assert.AreEqual(1, surfaced.Count, "An unmapped anchor must still surface, not be silently dropped.");
        Assert.IsNull(surfaced[0].Located.ScenePath, "An unresolvable anchor must not fabricate a path.");
        Assert.IsTrue(logs.Any(m => m.Contains("'ghost'")), "The console line must still name the LogicalId.");
        Assert.IsFalse(logs.Any(m => m.Contains("()")), "An unresolvable anchor must never render an empty path segment.");
    }

    // The adapter's own note about a live object it holds (e.g. PlanExecutor's RectTransform
    // promotion) keeps its message argument verbatim and gains the object's live path.
    [Test]
    public void LogNote_LiveGameObject_LogsExistingTextWithAppendedPath()
    {
        var anchor = new GameObject("Anchor");

        var logs = CaptureLogs(() => ConflictSurfacing.LogNote(anchor, "someId", "still says this"));

        Assert.AreEqual(1, logs.Count, "LogNote must log exactly one line.");
        Assert.AreEqual("[CodeScenes] NOTE on 'someId' (Anchor): still says this", logs[0],
            "The message argument must be preserved verbatim, with the live path appended after the LogicalId.");
    }
}
