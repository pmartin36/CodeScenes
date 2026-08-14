using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;
using SceneBuilder.Core.Reconcile;

// Spec 44 Invariant #2, build-path half: a declined prefab-instance override -- an authored
// override on an adapter-excluded serialized path, an added component carrying an excluded field,
// or a stale override (the source prefab's recorded default drifted and the live value now equals
// the new default) -- is surfaced on the code->scene build as a located WARNING naming the
// instance, the component type and the path, and is not re-logged on a second build in the same
// editor session.
public class DeclinedOverrideSurfacingTests
{
    private const string FixturesDir = "Assets/GateTests/Fixtures_DeclinedOverrideSurfacing";
    private const string PrefabPath = FixturesDir + "/DOS_BoxCollider.prefab";
    private const string SpritePrefabPath = FixturesDir + "/DOS_Sprite.prefab";
    private const string ScenePath = "Assets/GateTests/__DeclinedOverrideSurfacingTemp.unity";
    private const string InstanceName = "DOS_BoxCollider";
    private const string SpriteInstanceName = "DOS_Sprite";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class DeclinedOverrideSurfacingScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    private static GameObject FindRoot(Scene scene, string name) =>
        scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);

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
        ConflictSurfacing.ResetNotes();

        _dir = Path.Combine(Path.GetTempPath(), "sb_dos_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "DeclinedOverrideSurfacingScene.cs");
        _sidecarPath = Path.Combine(_dir, "DeclinedOverrideSurfacingScene.sbmap.json");

        if (!AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.CreateFolder("Assets/GateTests", "Fixtures_DeclinedOverrideSurfacing");
        }

        var source = new GameObject(InstanceName);
        source.AddComponent<BoxCollider>();
        PrefabUtility.SaveAsPrefabAsset(source, PrefabPath);
        UnityEngine.Object.DestroyImmediate(source);

        var spriteSource = new GameObject(SpriteInstanceName);
        spriteSource.AddComponent<SpriteRenderer>();
        PrefabUtility.SaveAsPrefabAsset(spriteSource, SpritePrefabPath);
        UnityEngine.Object.DestroyImmediate(spriteSource);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    [TearDown]
    public void TearDown()
    {
        ConflictSurfacing.ResetNotes();

        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        AssetDatabase.DeleteAsset(PrefabPath);
        AssetDatabase.DeleteAsset(SpritePrefabPath);
        if (AssetDatabase.IsValidFolder(FixturesDir))
        {
            AssetDatabase.DeleteAsset(FixturesDir);
        }

        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }
    }

    [Test]
    public void Build_StaleOverride_SurfacesWarningOnce_NotReLoggedOnSecondBuildInSameSession()
    {
        // Setup: bare instance.
        File.WriteAllText(_builderPath, Source($"        scene.Instance(\"{PrefabPath}\");"));
        var setup = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(setup.Diagnostics,
            "Setup build reported diagnostics: " + string.Join("; ", setup.Diagnostics.Select(d => d.Message)));

        var instanceRoot = FindRoot(EditorSceneManager.GetActiveScene(), InstanceName);
        Assert.IsNotNull(instanceRoot, "Setup: instance root was not created");
        var collider = instanceRoot.GetComponent<BoxCollider>();
        Assert.IsNotNull(collider, "Setup: fixture prefab has no root BoxCollider to override");
        collider.isTrigger = true;
        PrefabUtility.RecordPrefabInstancePropertyModifications(collider);

        // Record the override: recorded BaseValue == current prefab default (false) at this point,
        // so this build must NOT be stale yet.
        File.WriteAllText(_builderPath, Source(
            $"        scene.Instance(\"{PrefabPath}\").Override(e => e.Set<UnityEngine.BoxCollider>(\"m_IsTrigger\", true));"));
        var record = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(record.Diagnostics,
            "Override-recording build reported diagnostics: " + string.Join("; ", record.Diagnostics.Select(d => d.Message)));
        Assert.IsEmpty(record.Notes,
            "Setup precondition: the freshly-recorded override must not already be stale.\n"
            + string.Join("\n", record.Notes.Select(n => n.Kind + ": " + n.Reason)));

        // Drift: the prefab asset's own default now matches the recorded override's value, so the
        // recorded BaseValue (false) no longer matches the source prefab's current default (true),
        // and the live instance value (true) equals the new default.
        var contents = PrefabUtility.LoadPrefabContents(PrefabPath);
        contents.GetComponent<BoxCollider>().isTrigger = true;
        PrefabUtility.SaveAsPrefabAsset(contents, PrefabPath);
        PrefabUtility.UnloadPrefabContents(contents);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(PrefabPath, ImportAssetOptions.ForceUpdate);

        SceneBuilderBuild.BuildResult first = null;
        var firstLogs = CaptureLogs(() => first = SceneBuilderBuild.Run(
            _builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene()));
        Assert.IsEmpty(first.Diagnostics,
            "Stale build reported diagnostics: " + string.Join("; ", first.Diagnostics.Select(d => d.Message)));

        var note = first.Notes.SingleOrDefault(n => n.Kind == ConflictKind.StaleOverride);
        Assert.IsNotNull(note,
            "BuildResult.Notes must carry the surfaced StaleOverride report.\n"
            + string.Join("\n", first.Notes.Select(n => n.Kind + ": " + n.Reason)));
        StringAssert.Contains("BoxCollider", note.Located.Compose());
        StringAssert.Contains("m_IsTrigger", note.Located.Compose());

        var warningLines = firstLogs.Where(l => l.Message.Contains("m_IsTrigger")).ToArray();
        Assert.AreEqual(1, warningLines.Length,
            "Exactly one console line must name m_IsTrigger.\nLogs:\n" + string.Join("\n", firstLogs.Select(l => l.Message)));
        Assert.AreEqual(LogType.Warning, warningLines[0].Type, "The stale-override report must log at Warning severity.");

        SceneBuilderBuild.BuildResult second = null;
        var secondLogs = CaptureLogs(() => second = SceneBuilderBuild.Run(
            _builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene()));

        Assert.IsEmpty(second.Notes,
            "The stale-override WARNING must not be re-logged on a second build in the same session.\n"
            + string.Join("\n", second.Notes.Select(n => n.Kind + ": " + n.Reason)));
        Assert.IsFalse(secondLogs.Any(l => l.Message.Contains("m_IsTrigger")),
            "A second build in the same session must not re-log the standing warning.\nLogs:\n"
            + string.Join("\n", secondLogs.Select(l => l.Message)));
    }

    [Test]
    public void Build_ExcludedSpriteRendererSizeOverride_SurfacesWarningOnce_NotReLoggedSecondBuild()
    {
        File.WriteAllText(_builderPath, Source(
            $"        scene.Instance(\"{SpritePrefabPath}\").Override(e => e.Set<UnityEngine.SpriteRenderer>(\"m_Size\", new UnityEngine.Vector2(3f, 3f)));"));

        SceneBuilderBuild.BuildResult first = null;
        var firstLogs = CaptureLogs(() => first = SceneBuilderBuild.Run(
            _builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene()));
        Assert.IsEmpty(first.Diagnostics,
            "First build reported diagnostics: " + string.Join("; ", first.Diagnostics.Select(d => d.Message)));

        var spriteRoot = FindRoot(EditorSceneManager.GetActiveScene(), SpriteInstanceName);
        Assert.IsNotNull(spriteRoot, "First build did not materialize the sprite instance.");

        var note = first.Notes.SingleOrDefault(n => n.Kind == ConflictKind.UnauthorableField && n.Located.MemberKey == "m_Size");
        Assert.IsNotNull(note,
            "BuildResult.Notes must carry the surfaced excluded-override report for m_Size.\n"
            + string.Join("\n", first.Notes.Select(n => n.Kind + ": " + n.Reason)));
        Assert.AreEqual("UnityEngine.SpriteRenderer", note.Located.ComponentTypeFullName);
        Assert.IsFalse(string.IsNullOrEmpty(note.Located.LogicalId), "The report must name the instance anchor.");

        var warningLines = firstLogs.Where(l => l.Message.Contains("m_Size")).ToArray();
        Assert.AreEqual(1, warningLines.Length,
            "Exactly one console line must name m_Size.\nLogs:\n" + string.Join("\n", firstLogs.Select(l => l.Message)));
        Assert.AreEqual(LogType.Warning, warningLines[0].Type, "The excluded-override report must log at Warning severity.");
        StringAssert.StartsWith("[CodeScenes] WARNING on", warningLines[0].Message);
        StringAssert.Contains(Conflict.LineHasNoEffect, warningLines[0].Message);

        SceneBuilderBuild.BuildResult second = null;
        var secondLogs = CaptureLogs(() => second = SceneBuilderBuild.Run(
            _builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene()));

        Assert.IsFalse(second.Notes.Any(n => n.Kind == ConflictKind.UnauthorableField && n.Located.MemberKey == "m_Size"),
            "A second build in the same session must not re-log the excluded-override warning.\n"
            + string.Join("\n", second.Notes.Select(n => n.Kind + ": " + n.Reason)));
        Assert.IsFalse(secondLogs.Any(l => l.Message.Contains("m_Size")),
            "A second build in the same session must not re-log the excluded-override warning.\nLogs:\n"
            + string.Join("\n", secondLogs.Select(l => l.Message)));
    }

    [Test]
    public void Build_AddedComponentWithExcludedField_SurfacesWarningOnce_NotReLoggedSecondBuild()
    {
        File.WriteAllText(_builderPath, Source(
            $"        scene.Instance(\"{PrefabPath}\").AddComponent<UnityEngine.SpriteRenderer>(c => c.Set(\"m_SortingLayer\", 1));"));

        SceneBuilderBuild.BuildResult first = null;
        var firstLogs = CaptureLogs(() => first = SceneBuilderBuild.Run(
            _builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene()));
        Assert.IsEmpty(first.Diagnostics,
            "First build reported diagnostics: " + string.Join("; ", first.Diagnostics.Select(d => d.Message)));

        var instanceRoot = FindRoot(EditorSceneManager.GetActiveScene(), InstanceName);
        Assert.IsNotNull(instanceRoot, "First build did not materialize the box-collider instance.");
        Assert.IsNotNull(instanceRoot.GetComponent<SpriteRenderer>(),
            "First build did not attach the added SpriteRenderer this test authors m_SortingLayer on.");

        var note = first.Notes.SingleOrDefault(n => n.Kind == ConflictKind.UnauthorableField && n.Located.MemberKey == "m_SortingLayer");
        Assert.IsNotNull(note,
            "BuildResult.Notes must carry the surfaced added-component excluded-field report.\n"
            + string.Join("\n", first.Notes.Select(n => n.Kind + ": " + n.Reason)));
        Assert.AreEqual("UnityEngine.SpriteRenderer", note.Located.ComponentTypeFullName);

        var warningLines = firstLogs.Where(l => l.Message.Contains("m_SortingLayer")).ToArray();
        Assert.AreEqual(1, warningLines.Length,
            "Exactly one console line must name m_SortingLayer.\nLogs:\n" + string.Join("\n", firstLogs.Select(l => l.Message)));
        Assert.AreEqual(LogType.Warning, warningLines[0].Type, "The added-component excluded-field report must log at Warning severity.");
        StringAssert.StartsWith("[CodeScenes] WARNING on", warningLines[0].Message);
        StringAssert.Contains(Conflict.LineHasNoEffect, warningLines[0].Message);

        SceneBuilderBuild.BuildResult second = null;
        var secondLogs = CaptureLogs(() => second = SceneBuilderBuild.Run(
            _builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene()));

        Assert.IsFalse(second.Notes.Any(n => n.Kind == ConflictKind.UnauthorableField && n.Located.MemberKey == "m_SortingLayer"),
            "A second build in the same session must not re-log the added-component excluded-field warning.\n"
            + string.Join("\n", second.Notes.Select(n => n.Kind + ": " + n.Reason)));
        Assert.IsFalse(secondLogs.Any(l => l.Message.Contains("m_SortingLayer")),
            "A second build in the same session must not re-log the added-component excluded-field warning.\nLogs:\n"
            + string.Join("\n", secondLogs.Select(l => l.Message)));
    }
}
