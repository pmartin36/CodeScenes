using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;

// End-to-end coverage of a typed native enum through the real product entry points
// (SceneBuilderBuild.Run / SceneBuilderSync.Run), never the SerializedFieldBridge unit boundary
// alone: authoring `c.Set(x => x.renderMode, RenderMode.X)` must land the value on a live Canvas
// for all three render modes, a scene-side render mode edit must harvest back as the typed dotted
// spelling and compile, a raw `c.Set("m_RenderMode", n)` form must stay byte-identical, and
// Rigidbody.constraints must harvest as a typed flags expression. Canvas.renderMode's C# GETTER
// coerces ScreenSpaceCamera to ScreenSpaceOverlay while worldCamera is null, so every assertion
// reads the SERIALIZED m_RenderMode value rather than the getter.
public class TypedEnumAuthoringRoundTripTests
{
    private const string ScenePath = "Assets/GateTests/__TypedEnumAuthoringTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body, string extraUsings = "") => $@"
using UnityEngine;
{extraUsings}
using SceneBuilder.Authoring;
public class TypedEnumAuthoringScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    private static GameObject FindRoot(Scene scene, string name) =>
        scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);

    private static List<(string Message, LogType Type)> CaptureLogs(System.Action action)
    {
        var messages = new List<(string, LogType)>();
        void Handler(string condition, string stackTrace, LogType type) => messages.Add((condition, type));
        Application.logMessageReceived += Handler;
        try { action(); }
        finally { Application.logMessageReceived -= Handler; }
        return messages;
    }

    private static int SerializedRenderMode(Canvas c) =>
        new SerializedObject(c).FindProperty("m_RenderMode").intValue;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_tea_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "TypedEnumAuthoringScene.cs");
        _sidecarPath = Path.Combine(_dir, "TypedEnumAuthoringScene.sbmap.json");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        if (File.Exists(ScenePath)) AssetDatabase.DeleteAsset(ScenePath);
    }

    [TestCase("ScreenSpaceOverlay", 0)]
    [TestCase("ScreenSpaceCamera", 1)]
    [TestCase("WorldSpace", 2)]
    public void CodeToScene_TypedRenderModeSetter_LandsSerializedValue(string member, int expected)
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"UI\").Component<Canvas>(c => c.Set(x => x.renderMode, RenderMode." + member + "));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var logs = CaptureLogs(() =>
            SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene()));

        var ui = FindRoot(EditorSceneManager.GetActiveScene(), "UI");
        Assert.IsNotNull(ui, "UI was not created by SceneBuilderBuild.Run");
        var canvas = ui.GetComponent<Canvas>();
        Assert.IsNotNull(canvas, "Canvas component was not attached");
        Assert.AreEqual(expected, SerializedRenderMode(canvas),
            "Typed authored render mode did not land as the serialized m_RenderMode value.");
        Assert.IsFalse(logs.Any(l => l.Type == LogType.Warning || l.Type == LogType.Error),
            "Build logged a warning/error:\n" + string.Join("\n", logs.Select(l => l.Type + ": " + l.Message)));
    }

    [Test]
    public void Sync_TypedRenderModeSetter_StaysTypedAcrossSettleAndIsFixedPoint()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"UI\").Component<Canvas>(c => c.Set(x => x.renderMode, RenderMode.ScreenSpaceCamera));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene()); // settle
        var settled = File.ReadAllText(_builderPath);

        var second = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.AreEqual(settled, File.ReadAllText(_builderPath), "Second sync was not byte-identical.");
        Assert.IsFalse(second.Changed, "Second sync reported a change with no scene edit.");
        StringAssert.Contains("c.Set(x => x.renderMode, RenderMode.ScreenSpaceCamera)", settled,
            "The typed authored spelling was rewritten.\n" + settled);
    }

    [Test]
    public void Sync_RawIntRenderModeSetter_StaysRawAcrossSettleAndIsFixedPoint()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"UI\").Component<Canvas>(c => c.Set(\"m_RenderMode\", 1));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var ui = FindRoot(EditorSceneManager.GetActiveScene(), "UI");
        Assert.AreEqual(1, SerializedRenderMode(ui.GetComponent<Canvas>()),
            "Raw int authored render mode did not land as the serialized m_RenderMode value.");

        SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene()); // settle
        var settled = File.ReadAllText(_builderPath);

        var second = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.AreEqual(settled, File.ReadAllText(_builderPath), "Second sync was not byte-identical.");
        Assert.IsFalse(second.Changed, "Second sync reported a change with no scene edit.");
        StringAssert.Contains("c.Set(\"m_RenderMode\", 1)", settled,
            "The raw int authored form was rewritten to the typed spelling.\n" + settled);
    }

    [Test]
    public void SceneToCode_RigidbodyConstraintsFlags_HarvestsTypedFlagsExpression_SecondSyncNoOp()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Body\").Component<Rigidbody>(c => c.Set(x => x.mass, 5f));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var body = FindRoot(EditorSceneManager.GetActiveScene(), "Body");
        body.GetComponent<Rigidbody>().constraints =
            RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezeRotationZ;

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite a constraints edit");
        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("RigidbodyConstraints.FreezePositionY | ", rewritten,
            "Harvest did not produce a typed flags expression.\n" + rewritten);

        var second = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.AreEqual(rewritten, File.ReadAllText(_builderPath), "Second sync was not a fixed point.\n" + File.ReadAllText(_builderPath));
        Assert.IsFalse(second.Changed, "Second sync reported a change.");
        Assert.AreEqual(RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezeRotationZ,
            FindRoot(EditorSceneManager.GetActiveScene(), "Body").GetComponent<Rigidbody>().constraints,
            "Live constraints value did not survive.");
    }

    [Test]
    public void SceneToCode_CanvasRenderModeChanged_HarvestsTypedSpelling_SecondSyncNoOp()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"UI\").Component<Canvas>();"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var ui = FindRoot(EditorSceneManager.GetActiveScene(), "UI");
        var so = new SerializedObject(ui.GetComponent<Canvas>());
        so.FindProperty("m_RenderMode").intValue = 1; // ScreenSpaceCamera, the coerced-getter case
        so.ApplyModifiedPropertiesWithoutUndo();

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite a render mode edit");
        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("RenderMode.ScreenSpaceCamera", rewritten,
            "Harvest did not produce the typed spelling for ScreenSpaceCamera.\n" + rewritten);

        var second = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.AreEqual(rewritten, File.ReadAllText(_builderPath), "Second sync was not a fixed point.");
        Assert.IsFalse(second.Changed, "Second sync reported a change.");
        Assert.AreEqual(1, SerializedRenderMode(FindRoot(EditorSceneManager.GetActiveScene(), "UI").GetComponent<Canvas>()),
            "Live serialized render mode did not survive the round trip.");
    }

    // A nested member's enum spelling authored SHORT (FontStyle.Bold, not UnityEngine.FontStyle.Bold)
    // one level inside a struct field must stay exactly as authored across a settle sync — the
    // normalization that keeps a top-level enum spelling stable must recurse into a Nested value too,
    // or the settle rewrites the user's spelling on the very first sync.
    [Test]
    public void Sync_NestedEnumMemberAuthoredShort_StaysShortAndIsFixedPoint()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Label\").Component<UnityEngine.UI.Text>(c => c.Set(\"m_FontData\", new UnityEngine.UI.FontData { fontStyle = FontStyle.Bold }));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene()); // settle
        var settled = File.ReadAllText(_builderPath);
        StringAssert.Contains("fontStyle = FontStyle.Bold", settled,
            "The nested enum member's short authored spelling was rewritten by the settle sync.\n" + settled);

        var second = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.AreEqual(settled, File.ReadAllText(_builderPath), "Second sync was not byte-identical.");
        Assert.IsFalse(second.Changed, "Second sync reported a change with no scene edit.");
    }

    // Same shape, but the STRUCT type's spelling is short (FontData, not UnityEngine.UI.FontData) and
    // the enum member is fully qualified — isolates which half of a Nested value's normalization
    // (the type-name arm or the member arm) would break if either were missing.
    [Test]
    public void Sync_NestedStructTypeAuthoredShort_StaysShortAndIsFixedPoint()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Label\").Component<UnityEngine.UI.Text>(c => c.Set(\"m_FontData\", new FontData { fontStyle = UnityEngine.FontStyle.Bold }));",
            "using UnityEngine.UI;"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene()); // settle
        var settled = File.ReadAllText(_builderPath);
        StringAssert.Contains("new FontData {", settled,
            "The nested struct's short type spelling was rewritten by the settle sync.\n" + settled);

        var second = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.AreEqual(settled, File.ReadAllText(_builderPath), "Second sync was not byte-identical.");
        Assert.IsFalse(second.Changed, "Second sync reported a change with no scene edit.");
    }

    // Depth-2 nesting (a struct field whose own field is itself a struct), both type names authored
    // short. A plain MonoBehaviour with no RequireComponent companions, so a rebuild can pin op-count
    // convergence to zero as well as source byte-stability.
    [Test]
    public void Sync_NestedInNestedAuthoredShort_StaysShortAndIsFixedPoint()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Enemy\").Component<GateFixtures.GateSampleBehaviour>(c => c.Set(\"Outer\", new Outer { inner = new Inner { x = 1f }, y = 2f }));",
            "using GateFixtures;"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene()); // settle
        var settled = File.ReadAllText(_builderPath);
        StringAssert.Contains("new Outer {", settled,
            "The outer struct's short type spelling was rewritten by the settle sync.\n" + settled);
        StringAssert.Contains("new Inner {", settled,
            "The depth-2 struct's short type spelling was rewritten by the settle sync.\n" + settled);

        var second = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.AreEqual(settled, File.ReadAllText(_builderPath), "Second sync was not byte-identical.");
        Assert.IsFalse(second.Changed, "Second sync reported a change with no scene edit.");

        var build = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.AreEqual(0, build.PlanOpCount, "Rebuilding from the settled source must apply zero ops.");
    }

    // A list element's own nested struct, type authored short — exercises the List arm's element-path
    // composition (the enum/nested normalizer must recurse through an array/list the same way the
    // reader does), not just a bare top-level or single-struct field.
    [Test]
    public void Sync_ListOfNestedAuthoredShort_StaysShortAndIsFixedPoint()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Enemy\").Component<GateFixtures.GateSampleBehaviour>(c => c.Set(\"Volley\", new[] { new Damage { amount = 1f, kind = 1 } }));",
            "using GateFixtures;"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene()); // settle
        var settled = File.ReadAllText(_builderPath);
        StringAssert.Contains("new[] { new Damage", settled,
            "The list element's short type spelling was rewritten by the settle sync.\n" + settled);

        var second = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.AreEqual(settled, File.ReadAllText(_builderPath), "Second sync was not byte-identical.");
        Assert.IsFalse(second.Changed, "Second sync reported a change with no scene edit.");
    }

    // A generic serializable struct field (Pair<int>) can never resolve to a concrete managed type, so
    // the normalizer must leave it exactly as authored rather than guess or throw — the same guard the
    // reader itself uses to avoid emitting a backtick-arity type name.
    [Test]
    public void Sync_GenericNestedValue_IsLeftExactlyAsAuthored()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Enemy\").Component<GateFixtures.GateSampleBehaviour>(c => c.Set(\"Pair\", new GateFixtures.Pair<int> { value = 7 }));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        var authored = File.ReadAllText(_builderPath);

        SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene()); // settle
        Assert.AreEqual(authored, File.ReadAllText(_builderPath),
            "The generic struct's authored value was rewritten by the settle sync.");

        var second = SceneBuilderSync.Run(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(second.Changed, "Second sync reported a change with no scene edit.");
    }
}
