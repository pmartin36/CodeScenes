using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using SceneBuilder.Authoring;
using SceneBuilder.Editor;

// C10: a component field targeting a component on the SAME GameObject (Button.m_TargetGraphic on
// its own Image is the default shape of every Unity Button) must emit a self-selector that compiles,
// converge to a fixed point, and round-trip back into the live scene identically — proved against a
// real Canvas/Image/Button/Text scene, both when the Image+Button arrive via authored source and
// when they arrive as a live scene edit on an already-mapped node. A cross-object reference is
// unaffected.
public class RoundTripSelfReferenceTests
{
    private const string ScenePath = "Assets/GateTests/__RoundTripSelfReferenceTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private const string CanvasImageButtonTextBody =
        "        var canvas = scene.Add(\"Canvas\");\n" +
        "        canvas.Component<Canvas>();\n" +
        "        var btn = canvas.Add(\"Button\");\n" +
        "        btn.Component<Image>();\n" +
        "        btn.Component<Button>();\n" +
        "        btn.Add(\"Text\").Component<Text>();";

    private static string Source(string body) => $@"
using UnityEngine;
using UnityEngine.UI;
using SceneBuilder.Authoring;
public class RoundTripSelfReferenceScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    private static GameObject Find(Scene scene, string path)
    {
        var parts = path.Split('/');
        var root = scene.GetRootGameObjects().FirstOrDefault(go => go.name == parts[0]);
        var t = root == null ? null : root.transform;
        for (var i = 1; t != null && i < parts.Length; i++)
        {
            t = t.Find(parts[i]);
        }
        return t == null ? null : t.gameObject;
    }

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_rtsr_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "RoundTripSelfReferenceScene.cs");
        _sidecarPath = Path.Combine(_dir, "RoundTripSelfReferenceScene.sbmap.json");
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

    [Test]
    public void SceneToCode_CanvasImageButtonTextScene_EmitsSelfSelector_AndCompiles()
    {
        File.WriteAllText(_builderPath, Source(CanvasImageButtonTextBody));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var buttonGo = Find(EditorSceneManager.GetActiveScene(), "Canvas/Button");
        Assert.IsNotNull(buttonGo, "Canvas/Button was not created by SceneBuilderBuild.Run");
        var button = buttonGo.GetComponent<Button>();
        var image = buttonGo.GetComponent<Image>();
        Assert.IsNotNull(button.targetGraphic, "Precondition failed: Button.targetGraphic was not auto-assigned by Unity.");
        Assert.AreSame(image, button.targetGraphic,
            "Precondition failed: Button.targetGraphic is not the Image on its own GameObject.");

        EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var written = File.ReadAllText(_builderPath);
        StringAssert.Contains("c.Set(\"m_TargetGraphic\", NodeHandle.Self)", written,
            "Emitted source does not carry the self-selector form.\n" + written);

        var diagnostics = BuilderCompileCheck.Check(written);
        Assert.IsFalse(diagnostics.Any(d => d.Id == "CS0841"),
            "Emitted source reports CS0841: " + string.Join(" | ", diagnostics.Select(d => d.ToString())));
    }

    [Test]
    public void SceneToCode_CanvasImageButtonTextScene_SecondSyncIsAByteLevelFixedPoint()
    {
        File.WriteAllText(_builderPath, Source(CanvasImageButtonTextBody));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        var beforeBytes = File.ReadAllBytes(_builderPath);

        var second = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        var afterBytes = File.ReadAllBytes(_builderPath);

        Assert.AreEqual(0, second.PatchEdits,
            "The sync following the self-selector write produced " + second.PatchEdits + " patch edit(s); it must be a no-op.");
        CollectionAssert.AreEqual(beforeBytes, afterBytes,
            "The builder source was not byte-identical after a sync that should have been a no-op.");
    }

    [Test]
    public void SceneToCode_MappedNodeGainsImageAndButtonInScene_EmitsSelfSelector_AndConverges()
    {
        File.WriteAllText(_builderPath, Source(
            "        var canvas = scene.Add(\"Canvas\");\n" +
            "        canvas.Component<Canvas>();\n" +
            "        canvas.Add(\"Button\");"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var buttonGo = Find(EditorSceneManager.GetActiveScene(), "Canvas/Button");
        Assert.IsNotNull(buttonGo, "Canvas/Button was not created by SceneBuilderBuild.Run");
        buttonGo.AddComponent<Image>();
        var liveButton = buttonGo.AddComponent<Button>();
        Assert.AreSame(buttonGo.GetComponent<Image>(), liveButton.targetGraphic,
            "Precondition failed: adding Button after Image did not auto-assign targetGraphic to the co-located Image.");

        EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var written = File.ReadAllText(_builderPath);
        StringAssert.Contains("c.Set(\"m_TargetGraphic\", NodeHandle.Self)", written,
            "Emitted source for the mapped-node append path does not carry the self-selector form.\n" + written);

        var second = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.AreEqual(0, second.PatchEdits,
            "The mapped-node append case did not converge to a fixed point on the next sync.");
    }

    [Test]
    public void SceneToCode_CrossObjectTargetGraphic_StillEmitsNamedHandle_AndCompiles()
    {
        File.WriteAllText(_builderPath, Source(
            "        var panel = scene.Add(\"Panel\");\n" +
            "        panel.Component<Image>();\n" +
            "        var btn = scene.Add(\"Btn\");\n" +
            "        btn.Component<Button>(c => c.Set(\"m_TargetGraphic\", panel));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        var second = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.AreEqual(0, second.PatchEdits, "The cross-object reference source did not converge to a fixed point.");

        var written = File.ReadAllText(_builderPath);
        StringAssert.Contains(", panel)", written, "Cross-object reference must still emit the named handle inline.\n" + written);
        StringAssert.DoesNotContain("NodeHandle.Self", written,
            "A cross-object reference must never emit the self-selector.\n" + written);

        var diagnostics = BuilderCompileCheck.Check(written);
        Assert.IsFalse(diagnostics.Any(d => d.Id == "CS0841"),
            "Cross-object reference source reports CS0841: " + string.Join(" | ", diagnostics.Select(d => d.ToString())));
    }

    [Test]
    public void CodeToScene_ConvergedSelfReference_RebuildsToTheOwnGameObjectGraphic()
    {
        File.WriteAllText(_builderPath, Source(CanvasImageButtonTextBody));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        var convergenceCheck = EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        Assert.AreEqual(0, convergenceCheck.PatchEdits, "Source did not converge to a stable baseline before the rebuild.");

        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene());

        var buttonGo = Find(EditorSceneManager.GetActiveScene(), "Canvas/Button");
        Assert.IsNotNull(buttonGo, "Canvas/Button was not found in the scene after the rebuild.");
        var button = buttonGo.GetComponent<Button>();
        var image = buttonGo.GetComponent<Image>();
        Assert.AreSame(image, button.targetGraphic,
            "Rebuilt scene's Button.targetGraphic is not the Image on its own GameObject.");
    }
}
