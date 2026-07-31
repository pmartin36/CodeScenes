using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;

// The scene->code half of the C2 contract, against a real UnityEngine.UI.Image. With the
// reader no longer pruning default-valued fields, a live value that returns to its type's
// constructed default must REMOVE the `.Set(...)` call from source, never patch it to the default
// literal (patching it would leave the scene and code silently diverged). Mirrors
// RoundTripObjectRefTests/ComponentDefaultTemplateTests' harness (temp builder .cs + sidecar in a
// system temp dir, EmptyScene per test, EmittedCodeCompiles.SyncAndAssertCompiles as the only sync
// seam).
public class DefaultResetSyncTests
{
    private const string ScenePath = "Assets/GateTests/__DefaultResetSyncTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class DefaultResetSyncScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    private static GameObject FindRoot(Scene scene, string name) =>
        scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_drs_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "DefaultResetSyncScene.cs");
        _sidecarPath = Path.Combine(_dir, "DefaultResetSyncScene.sbmap.json");
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

    // 1. A sprite cleared to null: the `.Set("m_Sprite", ...)` setter is GONE (never patched to
    // a null/None literal); the rewritten source compiles; a second sync is a fixed point.
    [Test]
    public void SceneToCode_SpriteClearedToNull_RemovesSetterFromSource()
    {
        File.WriteAllText(_builderPath, Source(
            "        var icon = scene.Add(\"Icon\");\n" +
            "        icon.Component<UnityEngine.UI.Image>(c => c.Set(\"m_Sprite\", Builtin(\"UISprite\", \"Sprite\")));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var icon = FindRoot(EditorSceneManager.GetActiveScene(), "Icon");
        Assert.IsNotNull(icon, "Icon was not created by SceneBuilderBuild.Run");
        var image = icon.GetComponent<Image>();
        Assert.IsNotNull(image, "Authored Image was not materialized on Icon");
        Assert.IsNotNull(image.sprite, "Phase-1 build did not assign the sprite — test premise invalid");

        image.sprite = null;

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite a sprite reset to null");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.DoesNotContain("m_Sprite", rewritten,
            "Builder source still carries a `.Set(\"m_Sprite\", ...)` call after the sprite reset to its default (null).\n" + rewritten);
        StringAssert.Contains(".Component<UnityEngine.UI.Image>()", rewritten,
            "Builder source did not collapse to a bare `.Component<UnityEngine.UI.Image>()` after removing its sole setter.\n" + rewritten);

        var second = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(second.Changed, "NOT CONVERGED: a Sync with no further scene change reported Changed=true.");
        Assert.AreEqual(0, second.PatchEdits, "NOT CONVERGED: the no-op re-sync produced " + second.PatchEdits + " patch edit(s).");
    }

    // 2. A material cleared to null (the C2 x C5 crossover case, spec line 28's fourth value):
    // same shape as the sprite case, on Graphic.m_Material.
    [Test]
    public void SceneToCode_MaterialClearedToNull_RemovesSetterFromSource()
    {
        File.WriteAllText(_builderPath, Source(
            "        var icon = scene.Add(\"Icon\");\n" +
            "        icon.Component<UnityEngine.UI.Image>(c => c.Set(\"m_Material\", Builtin(\"Default-Material\")));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var icon = FindRoot(EditorSceneManager.GetActiveScene(), "Icon");
        Assert.IsNotNull(icon, "Icon was not created by SceneBuilderBuild.Run");
        var image = icon.GetComponent<Image>();
        Assert.IsNotNull(image, "Authored Image was not materialized on Icon");
        Assert.IsNotNull(image.material, "Phase-1 build did not assign the material — test premise invalid");

        image.material = null;

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite a material reset to null");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.DoesNotContain("m_Material", rewritten,
            "Builder source still carries a `.Set(\"m_Material\", ...)` call after the material reset to its default (null).\n" + rewritten);
        StringAssert.Contains(".Component<UnityEngine.UI.Image>()", rewritten,
            "Builder source did not collapse to a bare `.Component<UnityEngine.UI.Image>()` after removing its sole setter.\n" + rewritten);

        var second = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(second.Changed, "NOT CONVERGED: a Sync with no further scene change reported Changed=true.");
        Assert.AreEqual(0, second.PatchEdits, "NOT CONVERGED: the no-op re-sync produced " + second.PatchEdits + " patch edit(s).");
    }

    // 3. A colour reset to white (the type default): the `.Set("m_Color", ...)` setter is gone.
    [Test]
    public void SceneToCode_ColorResetToWhite_RemovesSetterFromSource()
    {
        File.WriteAllText(_builderPath, Source(
            "        var icon = scene.Add(\"Icon\");\n" +
            "        icon.Component<UnityEngine.UI.Image>(c => c.Set(\"m_Color\", new UnityEngine.Color(1f, 0f, 0f, 1f)));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var icon = FindRoot(EditorSceneManager.GetActiveScene(), "Icon");
        Assert.IsNotNull(icon, "Icon was not created by SceneBuilderBuild.Run");
        var image = icon.GetComponent<Image>();
        Assert.IsNotNull(image, "Authored Image was not materialized on Icon");
        Assert.AreEqual(Color.red, image.color, "Phase-1 build did not assign the red colour — test premise invalid");

        image.color = Color.white;

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite a colour reset to white");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.DoesNotContain("m_Color", rewritten,
            "Builder source still carries a `.Set(\"m_Color\", ...)` call after the colour reset to its default (white).\n" + rewritten);
        StringAssert.Contains(".Component<UnityEngine.UI.Image>()", rewritten,
            "Builder source did not collapse to a bare `.Component<UnityEngine.UI.Image>()` after removing its sole setter.\n" + rewritten);

        var second = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(second.Changed, "NOT CONVERGED: a Sync with no further scene change reported Changed=true.");
        Assert.AreEqual(0, second.PatchEdits, "NOT CONVERGED: the no-op re-sync produced " + second.PatchEdits + " patch edit(s).");
    }

    // 4. Image.type reset to Simple (the type default): the `.Set("m_Type", ...)` setter is gone.
    // TypeFullName spelling (CLR "+" on read vs C# "." on parse) is normalized elsewhere
    // (ValueNode.Enum equality — see SceneBuilder.Core.Tests/ValueNodeTests.cs
    // Enum_ClrNestedSpellingAndSourceSpelling_AreTheSameValue) and is not this test's concern.
    [Test]
    public void SceneToCode_ImageTypeResetToSimple_RemovesSetterFromSource()
    {
        File.WriteAllText(_builderPath, Source(
            "        var icon = scene.Add(\"Icon\");\n" +
            "        icon.Component<UnityEngine.UI.Image>(c => c.Set(\"m_Type\", UnityEngine.UI.Image.Type.Tiled));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var icon = FindRoot(EditorSceneManager.GetActiveScene(), "Icon");
        Assert.IsNotNull(icon, "Icon was not created by SceneBuilderBuild.Run");
        var image = icon.GetComponent<Image>();
        Assert.IsNotNull(image, "Authored Image was not materialized on Icon");
        Assert.AreEqual(Image.Type.Tiled, image.type, "Phase-1 build did not assign Tiled — test premise invalid");

        image.type = Image.Type.Simple;

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite Image.type reset to Simple");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.DoesNotContain("m_Type", rewritten,
            "Builder source still carries a `.Set(\"m_Type\", ...)` call after Image.type reset to its default (Simple).\n" + rewritten);
        StringAssert.Contains(".Component<UnityEngine.UI.Image>()", rewritten,
            "Builder source did not collapse to a bare `.Component<UnityEngine.UI.Image>()` after removing its sole setter.\n" + rewritten);

        var second = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(second.Changed, "NOT CONVERGED: a Sync with no further scene change reported Changed=true.");
        Assert.AreEqual(0, second.PatchEdits, "NOT CONVERGED: the no-op re-sync produced " + second.PatchEdits + " patch edit(s).");
    }

    // 5. Explicit collapse assertion: with the sprite setter as the closure's SOLE setter, removal
    // leaves a bare `.Component<UnityEngine.UI.Image>()` -- no `(c =>` remaining -- and it compiles.
    [Test]
    public void SceneToCode_LastSetterRemoved_CollapsesToBareComponentCall()
    {
        File.WriteAllText(_builderPath, Source(
            "        var icon = scene.Add(\"Icon\");\n" +
            "        icon.Component<UnityEngine.UI.Image>(c => c.Set(\"m_Sprite\", Builtin(\"UISprite\", \"Sprite\")));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var icon = FindRoot(EditorSceneManager.GetActiveScene(), "Icon");
        var image = icon.GetComponent<Image>();
        image.sprite = null;

        EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.DoesNotContain("(c =>", rewritten,
            "Builder source still carries a configure closure after its sole setter was removed.\n" + rewritten);
        StringAssert.Contains(".Component<UnityEngine.UI.Image>()", rewritten,
            "Builder source did not collapse to a bare `.Component<UnityEngine.UI.Image>()`.\n" + rewritten);
    }

    // 6. One of two setters reset to default: sprite + colour authored, only colour reset -> the
    // sprite setter survives, source compiles, second sync is a fixed point.
    [Test]
    public void SceneToCode_OneOfTwoSettersResetToDefault_KeepsTheOther()
    {
        File.WriteAllText(_builderPath, Source(
            "        var icon = scene.Add(\"Icon\");\n" +
            "        icon.Component<UnityEngine.UI.Image>(c =>\n" +
            "        {\n" +
            "            c.Set(\"m_Sprite\", Builtin(\"UISprite\", \"Sprite\"));\n" +
            "            c.Set(\"m_Color\", new UnityEngine.Color(1f, 0f, 0f, 1f));\n" +
            "        });"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var icon = FindRoot(EditorSceneManager.GetActiveScene(), "Icon");
        Assert.IsNotNull(icon, "Icon was not created by SceneBuilderBuild.Run");
        var image = icon.GetComponent<Image>();
        Assert.IsNotNull(image, "Authored Image was not materialized on Icon");
        Assert.IsNotNull(image.sprite, "Phase-1 build did not assign the sprite — test premise invalid");
        Assert.AreEqual(Color.red, image.color, "Phase-1 build did not assign the red colour — test premise invalid");

        // Reset ONLY the colour; the sprite stays authored.
        image.color = Color.white;

        var result = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsTrue(result.Changed, "Sync reported no change despite a colour reset to white");

        var rewritten = File.ReadAllText(_builderPath);
        StringAssert.DoesNotContain("m_Color", rewritten,
            "Builder source still carries a `.Set(\"m_Color\", ...)` call after the colour reset to its default (white).\n" + rewritten);
        StringAssert.Contains("m_Sprite", rewritten,
            "Builder source lost the sprite setter, which was never reset -- only the colour setter should have been removed.\n" + rewritten);

        var second = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.IsFalse(second.Changed, "NOT CONVERGED: a Sync with no further scene change reported Changed=true.");
        Assert.AreEqual(0, second.PatchEdits, "NOT CONVERGED: the no-op re-sync produced " + second.PatchEdits + " patch edit(s).");
    }
}
