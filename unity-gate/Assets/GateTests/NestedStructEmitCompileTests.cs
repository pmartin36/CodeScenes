using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;
using SceneBuilder.Core.Reconcile;

// Spec 32 C4, against real ugui components: ColorBlock/FontData serialize by PRIVATE field name
// (m_NormalColor, m_FontSize, ...), so the naive nested emitter that keys by serialized name wrote
// non-compiling source (7x CS0122 for ColorBlock, 12x CS0117 for FontData). Every scene->code
// assertion here goes through EmittedCodeCompiles.SyncAndAssertCompiles: a real Button/Text edit
// must produce a COMPILING initializer that names only the CHANGED public member.
public class NestedStructEmitCompileTests
{
    private const string ScenePath = "Assets/GateTests/__NestedStructEmitCompileTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class NestedStructEmitScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    private static GameObject FindRoot(Scene scene, string name) =>
        scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);

    // Counts top-level `= ` assignments inside the FIRST `{ ... }` initializer body found in `text`
    // starting at `openBrace` -- i.e. how many members the initializer names.
    private static int CountInitializerMembers(string text, int openBrace)
    {
        var depth = 0;
        var count = 0;
        for (var i = openBrace; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return count;
                }
            }
            else if (depth == 1 && text[i] == '=' && (i == 0 || text[i - 1] != '!') && text[i + 1] != '=')
            {
                count++;
            }
        }

        return count;
    }

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_nsec_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "NestedStructEmitScene.cs");
        _sidecarPath = Path.Combine(_dir, "NestedStructEmitScene.sbmap.json");
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }
    }

    // Deliverable 1: one ColorBlock member changed emits a COMPILING initializer naming only that
    // member, converges on a second sync, and the live colour survives a rebuild.
    [Test]
    public void SceneToCode_ButtonColorBlockOneColourChanged_EmitsSingleMemberCompilingInitializer()
    {
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Btn\").Component<UnityEngine.UI.Button>();"));
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var btn = FindRoot(EditorSceneManager.GetActiveScene(), "Btn").GetComponent<Button>();
        var colors = btn.colors;
        colors.normalColor = new Color(1f, 0f, 0f, 1f);
        btn.colors = colors;

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite a live ColorBlock edit.");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("new UnityEngine.UI.ColorBlock { normalColor = ", rewritten,
            "Emitted source did not carry the public `normalColor` spelling.\n" + rewritten);
        StringAssert.DoesNotContain("m_NormalColor", rewritten,
            "Emitted source leaked the private serialized field name.\n" + rewritten);

        var literalStart = rewritten.IndexOf("new UnityEngine.UI.ColorBlock {");
        var openBrace = rewritten.IndexOf('{', literalStart);
        Assert.AreEqual(1, CountInitializerMembers(rewritten, openBrace),
            "Only the changed ColorBlock member (normalColor) should appear in the initializer.\n" + rewritten);

        var second = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(second.Changed, "A second sync over an unchanged scene must be a fixed point.");
        Assert.AreEqual(0, second.PatchEdits, "A second sync must produce zero patch edits (churn guard).");

        var build = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.AreEqual(0, build.PlanOpCount, "Rebuilding from a converged source must apply zero ops.");

        var afterRebuild = FindRoot(EditorSceneManager.GetActiveScene(), "Btn").GetComponent<Button>();
        Assert.AreEqual(new Color(1f, 0f, 0f, 1f), afterRebuild.colors.normalColor,
            "The authored colour must survive the rebuild.");
    }

    // Deliverable 2: same contract for FontData -- one member (fontSize), not all twelve.
    [Test]
    public void SceneToCode_TextFontDataFontSizeChanged_EmitsSingleMemberCompilingInitializer()
    {
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Txt\").Component<UnityEngine.UI.Text>();"));
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var text = FindRoot(EditorSceneManager.GetActiveScene(), "Txt").GetComponent<Text>();
        text.fontSize = 20; // FontData.defaultFontData.fontSize == 14

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite a live FontData edit.");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("new UnityEngine.UI.FontData { fontSize = 20 }", rewritten,
            "Emitted source did not carry the single-member public `fontSize` spelling.\n" + rewritten);
        StringAssert.DoesNotContain("m_FontSize", rewritten,
            "Emitted source leaked the private serialized field name.\n" + rewritten);

        var second = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(second.Changed, "A second sync over an unchanged scene must be a fixed point.");
        Assert.AreEqual(0, second.PatchEdits, "A second sync must produce zero patch edits (churn guard).");
    }

    // Deliverable 3: a session touching a Button AND a Text ends with ZERO compile errors.
    [Test]
    public void SceneToCode_ButtonAndTextTouchedInOneSession_BuilderCompileCheckReportsZeroErrors()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Btn\").Component<UnityEngine.UI.Button>();\n" +
            "        scene.Add(\"Txt\").Component<UnityEngine.UI.Text>();"));
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var btn = FindRoot(EditorSceneManager.GetActiveScene(), "Btn").GetComponent<Button>();
        var colors = btn.colors;
        colors.pressedColor = new Color(0f, 1f, 0f, 1f);
        btn.colors = colors;

        var text = FindRoot(EditorSceneManager.GetActiveScene(), "Txt").GetComponent<Text>();
        text.lineSpacing = 2f; // FontData.defaultFontData.lineSpacing == 1

        EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var errors = BuilderCompileCheck.Check(File.ReadAllText(_builderPath));
        Assert.IsEmpty(errors, BuilderCompileCheck.Format(errors, "Button + Text touched in one session", File.ReadAllText(_builderPath)));
    }

    // Deliverable 6 (Complete, code->scene): rebuilding from a source that now authors only ONE
    // member resets every other REPRESENTABLE member to the type default on the live component --
    // a stale live value the source no longer mentions does not survive a rebuild.
    [Test]
    public void CodeToScene_RebuildFromNarrowedInitializer_ResetsOmittedMemberToDefault()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Btn\").Component<UnityEngine.UI.Button>(b => b.Set(\"m_Colors\", " +
            "new UnityEngine.UI.ColorBlock { normalColor = new UnityEngine.Color(1f, 0f, 0f, 1f), " +
            "pressedColor = new UnityEngine.Color(0f, 0f, 1f, 1f) }));"));
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var btn = FindRoot(EditorSceneManager.GetActiveScene(), "Btn").GetComponent<Button>();
        Assert.AreEqual(new Color(1f, 0f, 0f, 1f), btn.colors.normalColor);
        Assert.AreEqual(new Color(0f, 0f, 1f, 1f), btn.colors.pressedColor);

        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Btn\").Component<UnityEngine.UI.Button>(b => b.Set(\"m_Colors\", " +
            "new UnityEngine.UI.ColorBlock { normalColor = new UnityEngine.Color(1f, 0f, 0f, 1f) }));"));
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var afterRebuild = FindRoot(EditorSceneManager.GetActiveScene(), "Btn").GetComponent<Button>();
        Assert.AreEqual(new Color(1f, 0f, 0f, 1f), afterRebuild.colors.normalColor,
            "The still-authored member must remain materialized.");
        Assert.AreEqual(ColorBlock.defaultColorBlock.pressedColor, afterRebuild.colors.pressedColor,
            "The member the narrower source no longer authors must reset to the type default, not stay stale.");
    }

    // Deliverable 4: a nested member with NO public spelling is never emitted with its private name;
    // the file still compiles, and the sync reports a located conflict naming it -- even though the
    // struct also carries a representable member that IS emitted.
    [Test]
    public void SceneToCode_MixedMemberWithNoPublicSpelling_NeverLeaksPrivateNameAndReportsConflict()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Fixture\").Component<GateFixtures.NestedUnrepresentableFixtureBehaviour>();"));
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var fixtureGo = FindRoot(EditorSceneManager.GetActiveScene(), "Fixture");
        var behaviour = fixtureGo.GetComponent<GateFixtures.NestedUnrepresentableFixtureBehaviour>();
        var so = new SerializedObject(behaviour);
        so.FindProperty("Mixed.Visible").floatValue = 5f;
        so.FindProperty("Mixed.m_Hidden").floatValue = 7f;
        so.ApplyModifiedProperties();

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("Visible = 5", rewritten,
            "Emitted source must carry the changed public representable member.\n" + rewritten);
        StringAssert.DoesNotContain("m_Hidden", rewritten,
            "Emitted source must never carry the private field spelling.\n" + rewritten);
        Assert.IsTrue(result.Conflicts.Any(c => c.Kind == ConflictKind.UnrepresentableValue),
            "A member with no compiling spelling must surface a located UnrepresentableValue conflict.");
    }

    // A struct whose ONLY member has no compiling spelling projects to nothing: a sync must apply no
    // edit for that field, must never report it as a per-pass Conflict (or every untouched user
    // struct of this shape logs on every sync with no action taken), and must surface it as a
    // standing NOTE exactly once -- a capability fact of the type, not an event repeated forever.
    [Test]
    public void SceneToCode_AllUnrepresentableNestedMember_ReportsOnceThenIsSilentAndStillCompiles()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Fixture\").Component<GateFixtures.NestedUnrepresentableFixtureBehaviour>();"));
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var fixtureGo = FindRoot(EditorSceneManager.GetActiveScene(), "Fixture");
        var behaviour = fixtureGo.GetComponent<GateFixtures.NestedUnrepresentableFixtureBehaviour>();
        var so = new SerializedObject(behaviour);
        so.FindProperty("Nested.m_Hidden").floatValue = 5f;
        so.ApplyModifiedProperties();

        var first = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        Assert.IsEmpty(first.Conflicts.Where(c => c.Kind == ConflictKind.UnrepresentableValue).ToArray(),
            "A struct whose only member has no compiling spelling must never be reported as a per-pass conflict.");
        var note = first.Notes.SingleOrDefault(n => n.Kind == ConflictKind.UnrepresentableValue && n.Reason.Contains("'Nested'"));
        Assert.IsNotNull(note, "The first sync must surface exactly one note naming the Nested field that could not be represented.\n" +
            string.Join("\n", first.Notes.Select(n => n.LogicalId + ": " + n.Reason)));
        StringAssert.Contains("Fixture", note.LogicalId, "The note must name the affected component.");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.DoesNotContain("m_Hidden", rewritten, "Emitted source must never carry the private field spelling.\n" + rewritten);

        var second = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(second.Changed, "An unchanged scene must be a fixed point.");
        Assert.AreEqual(0, second.PatchEdits, "A second sync of an unchanged scene must produce zero patch edits.");
        Assert.IsEmpty(second.Notes, "The second sync of an unchanged scene must surface no notes (reported once per session).");
    }

    // A settled scene must stay settled even when the touched struct also carries a member with no
    // compiling emission form (Button.navigation.selectOnUp, an object reference; Button.onClick, a
    // UnityEvent) -- the comparison that decides "did anything change" must use the same projection
    // an emission would use, or the field re-patches and re-reports on every sync forever.
    [Test]
    public void SceneToCode_TwoButtonsWithExplicitNavigation_SecondSyncIsFixedPointWithNoConflicts()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"BtnA\").Component<UnityEngine.UI.Button>();\n" +
            "        scene.Add(\"BtnB\").Component<UnityEngine.UI.Button>();"));
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var btnA = FindRoot(EditorSceneManager.GetActiveScene(), "BtnA").GetComponent<Button>();
        var btnB = FindRoot(EditorSceneManager.GetActiveScene(), "BtnB").GetComponent<Button>();
        var navigation = btnA.navigation;
        navigation.mode = Navigation.Mode.Explicit;
        navigation.selectOnUp = btnB;
        btnA.navigation = navigation;

        var first = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(first.Changed, "Sync reported no change despite a live Navigation edit.");

        var onClickNotes = first.Notes.Where(n => n.Kind == ConflictKind.UnrepresentableValue && n.Reason.Contains("m_OnClick")).ToArray();
        Assert.AreEqual(1, onClickNotes.Length,
            "Two Button components sharing the same unrepresentable field (m_OnClick) must surface it as ONE note, not one per instance.\n" +
            string.Join("\n", first.Notes.Select(n => n.LogicalId + ": " + n.Reason)));

        var second = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(second.Changed, "A settled scene must be a fixed point even with an unrepresentable residual member.");
        Assert.AreEqual(0, second.PatchEdits, "A settled scene must produce zero patch edits.");
        Assert.IsEmpty(second.Conflicts.Where(c => c.Kind == ConflictKind.UnrepresentableValue).ToArray(),
            "A settled scene must not re-report the same unrepresentable member on every sync.");

        var build = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.AreEqual(0, build.PlanOpCount, "Rebuilding from a converged source must apply zero ops.");
    }

    // Unity reports a struct's members in ITS serialized order; the author writes them in whatever
    // order reads best. A positional comparison of the two calls identical values different: the
    // settled scene re-patches on every sync and the first sync silently rewrites the author's
    // member order. Nothing is patched here, and the authored order survives.
    [Test]
    public void SceneToCode_ColorBlockMembersAuthoredOutOfSerializedOrder_IsFixedPointAndKeepsAuthoredOrder()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Btn\").Component<UnityEngine.UI.Button>(b => b.Set(\"m_Colors\", " +
            "new UnityEngine.UI.ColorBlock { fadeDuration = 0.25f, normalColor = new UnityEngine.Color(1f, 0f, 0f, 1f) }));"));
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var btn = FindRoot(EditorSceneManager.GetActiveScene(), "Btn").GetComponent<Button>();
        Assert.AreEqual(0.25f, btn.colors.fadeDuration, "Authored fadeDuration did not materialize.");
        Assert.AreEqual(new Color(1f, 0f, 0f, 1f), btn.colors.normalColor, "Authored normalColor did not materialize.");

        var first = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.AreEqual(0, first.PatchEdits,
            "A struct whose members are authored in a different order from Unity's serialized order is the " +
            "SAME value: syncing it must patch nothing.");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("new UnityEngine.UI.ColorBlock { fadeDuration = 0.25f, normalColor = ", rewritten,
            "Sync reordered the author's struct members.\n" + rewritten);

        var second = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(second.Changed, "A settled out-of-order struct must be a fixed point.");
        Assert.AreEqual(0, second.PatchEdits, "A second sync must produce zero patch edits.");

        var build = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.AreEqual(0, build.PlanOpCount,
            "Rebuilding from a settled out-of-order struct must apply zero ops (member order alone is not a change).");
    }

    // An ENUM member sitting INSIDE a real struct (Navigation.mode) must round-trip through the same
    // canonical spelling a top-level enum field does. When a value pass descends into a component's
    // top-level fields but not into a struct's members, the inner enum never reaches the normalizer,
    // compares unequal to the live read forever, and re-patches on every sync.
    [Test]
    public void SceneToCode_NavigationModeEnumInsideAStruct_EmitsCanonicalSpellingAndSecondSyncIsFixedPoint()
    {
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Btn\").Component<UnityEngine.UI.Button>();"));
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var btn = FindRoot(EditorSceneManager.GetActiveScene(), "Btn").GetComponent<Button>();
        var navigation = btn.navigation;
        navigation.mode = Navigation.Mode.Horizontal; // default is Navigation.Mode.Automatic
        btn.navigation = navigation;

        var first = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(first.Changed, "Sync reported no change despite a live Navigation.mode edit.");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.Contains("new UnityEngine.UI.Navigation { mode = UnityEngine.UI.Navigation.Mode.Horizontal }", rewritten,
            "Emitted source did not carry the canonical enum spelling for the struct's inner member.\n" + rewritten);

        var second = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(second.Changed, "A settled enum-inside-a-struct must be a fixed point.");
        Assert.AreEqual(0, second.PatchEdits, "A second sync must produce zero patch edits (churn guard).");

        var build = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.AreEqual(0, build.PlanOpCount, "Rebuilding from a converged source must apply zero ops.");
        Assert.AreEqual(Navigation.Mode.Horizontal,
            FindRoot(EditorSceneManager.GetActiveScene(), "Btn").GetComponent<Button>().navigation.mode,
            "The authored enum member must survive the rebuild.");
    }

    // A stock, never-touched Button/Text carries UnityEvent fields (onClick, onCullStateChanged)
    // with no compiling spelling. A field that produces no edit must never produce a conflict
    // either, or every user's untouched UI logs on every single sync. Rebuilding the same converged
    // source twice in a row must not apply MORE ops the second time than the first: unrelated
    // RequireComponent-harvested fields (RectTransform, CanvasRenderer) may keep a residual op count
    // above zero, but that count must not grow sync over sync.
    [Test]
    public void SceneToCode_StockTextWithNoUserEdit_ReportsNoConflictsAndRebuildOpCountDoesNotGrow()
    {
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Txt\").Component<UnityEngine.UI.Text>();"));
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(result.Conflicts.Where(c => c.Kind == ConflictKind.UnrepresentableValue).ToArray(),
            "A stock Text with no user edit must not report an unrepresentable-member conflict.");

        var second = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsEmpty(second.Notes, "A second sync of an unchanged stock Text must surface no notes (reported once per session, if at all).");

        var firstBuild = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        var secondBuild = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.LessOrEqual(secondBuild.PlanOpCount, firstBuild.PlanOpCount,
            "Rebuilding the same converged source twice must not apply more ops the second time.");
    }
}
