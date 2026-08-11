using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;

// The single default-template owner. Pins:
//  - ReadComponent no longer prunes default-valued fields (spec C2 read contract).
//  - ComponentDefaultTemplate.Create is the ONE creation primitive, and it produces a Canvas at
//    ScreenSpaceOverlay (spec C3), matching what Register's template captures.
//  - A Canvas authored from code with no explicit render mode lands ScreenSpaceOverlay in the
//    live scene (code->scene half of C3).
//  - Package_NoRawAddComponentOutsideCreationPrimitive: no production `.cs` file other than
//    ComponentDefaultTemplate.cs itself may call `.AddComponent(...)` — every current and future
//    creation call site inherits ComponentDefaultTemplate's overlay by construction, never by
//    per-caller opt-in (CLAUDE.md).
public class ComponentDefaultTemplateTests
{
    private const string ScenePath = "Assets/GateTests/__ComponentDefaultTemplateTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using SceneBuilder.Authoring;
public class ComponentDefaultTemplateScene : ISceneDefinition
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
        _dir = Path.Combine(Path.GetTempPath(), "sb_cdt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "ComponentDefaultTemplateScene.cs");
        _sidecarPath = Path.Combine(_dir, "ComponentDefaultTemplateScene.sbmap.json");
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

    // Deliverable 3: ReadComponent must report fields at their type default, not prune them.
    [Test]
    public void ReadComponent_DefaultValuedFields_AreReported()
    {
        var canvasGo = new GameObject("Canvas");
        var canvas = canvasGo.AddComponent<Canvas>();
        var canvasData = SerializedFieldBridge.ReadComponent(canvas, resolveSceneRef: null);
        Assert.IsTrue(canvasData.Fields.ContainsKey("m_RenderMode"),
            "A Canvas at its default render mode must still report m_RenderMode.");

        var imageGo = new GameObject("Image");
        var image = imageGo.AddComponent<Image>();
        var imageData = SerializedFieldBridge.ReadComponent(image, resolveSceneRef: null);
        Assert.IsTrue(imageData.Fields.ContainsKey("m_Sprite"),
            "An Image with a null (default) m_Sprite must still report m_Sprite.");
        Assert.IsTrue(imageData.Fields.ContainsKey("m_Color"),
            "An Image at its default white m_Color must still report m_Color.");
        Assert.IsTrue(imageData.Fields.ContainsKey("m_Type"),
            "An Image at its default m_Type (Simple) must still report m_Type.");
    }

    // Deliverable 1: ComponentDefaultTemplate.Create is the ONE creation primitive, and it applies
    // the measured ScreenSpaceOverlay overlay (a raw AddComponent<Canvas> gives
    // WorldSpace; this overlay is what fixes spec C3).
    [Test]
    public void Create_Canvas_IsScreenSpaceOverlay()
    {
        var go = new GameObject("Canvas");

        var created = ComponentDefaultTemplate.Create(go, typeof(Canvas));

        Assert.IsNotNull(created, "ComponentDefaultTemplate.Create must return the created Canvas.");
        Assert.AreEqual(RenderMode.ScreenSpaceOverlay, ((Canvas)created).renderMode,
            "A Canvas created through ComponentDefaultTemplate.Create must be ScreenSpaceOverlay.");
    }

    // Deliverable 2 (code->scene): a Canvas authored with no explicit render mode must land
    // ScreenSpaceOverlay in the live scene once creation routes through ComponentDefaultTemplate.
    [Test]
    public void CodeToScene_CanvasAuthoredWithNoRenderMode_IsScreenSpaceOverlay()
    {
        File.WriteAllText(_builderPath, Source("        scene.Add(\"Canvas\").Component<UnityEngine.Canvas>();"));

        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);

        var canvasGo = FindRoot(EditorSceneManager.GetActiveScene(), "Canvas");
        Assert.IsNotNull(canvasGo, "Canvas was not created by SceneBuilderBuild.Run");
        var canvas = canvasGo.GetComponent<Canvas>();
        Assert.IsNotNull(canvas, "Canvas component was not attached");

        Assert.AreEqual(RenderMode.ScreenSpaceOverlay, canvas.renderMode,
            "A Canvas authored with no explicit render mode must be created ScreenSpaceOverlay, " +
            "not Unity's raw AddComponent default (WorldSpace).");
    }

    // Regression (bug class): the default-template harvest AddComponents the probed type onto a
    // throwaway bare GameObject, firing its OnValidate with no siblings. Any user MonoBehaviour whose
    // OnValidate logs (e.g. SurfaceSnap warning it has no Renderer to snap against) must NOT leak that
    // to the console during the harvest. Suppression lives at the probe in ComponentDefaultTemplate,
    // so every current and future logging OnValidate inherits it -- not fixed per-component. A
    // purpose-built probe proves the CLASS, not one hard-coded component.
    [Test]
    public void Register_ComponentWithLoggingOnValidate_DoesNotLeakToConsole()
    {
        ComponentDefaultTemplate.Register(typeof(LoggingOnValidateProbe));
        LogAssert.NoUnexpectedReceived();
    }

    // Deliverable 1 (gate-enforced, not just documented): ComponentDefaultTemplate.Create must be
    // the ONE component-creation primitive. A raw `.AddComponent(...)` call anywhere else in the
    // package is a silent bypass a future EditorCreationDefaults entry would never reach.
    private static readonly Regex StringLiteral = new Regex("\"(?:\\\\.|[^\"\\\\])*\"", RegexOptions.Compiled);
    private static readonly Regex RawAddComponentCall = new Regex(@"\.\s*AddComponent\s*[<(]", RegexOptions.Compiled);

    [Test]
    public void Package_NoRawAddComponentOutsideCreationPrimitive()
    {
        var root = SceneBuilderPaths.PackageRootPath;
        Assert.IsNotNull(root, "SceneBuilderPaths.PackageRootPath resolved to null - cannot scan the " +
            "package for raw AddComponent calls (this must FAIL, never vacuously pass).");

        var violations = new List<string>();
        foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "ComponentDefaultTemplate.cs")
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = StringLiteral.Replace(lines[i], "\"\"");
                var commentIndex = line.IndexOf("//", StringComparison.Ordinal);
                if (commentIndex >= 0)
                {
                    line = line.Substring(0, commentIndex);
                }

                if (RawAddComponentCall.IsMatch(line))
                {
                    var relative = file.Substring(root.Length).TrimStart('/', '\\');
                    violations.Add($"{relative}:{i + 1}");
                }
            }
        }

        Assert.IsEmpty(violations,
            "Every component creation in com.codescenes/ must go through " +
            "ComponentDefaultTemplate.Create(owner, type), not a raw .AddComponent(...) call, so a " +
            "future EditorCreationDefaults entry applies at every creation site with no per-caller " +
            "opt-in. Violations:\n" + string.Join("\n", violations));
    }
}

// A MonoBehaviour that logs an error from OnValidate on a bare object -- the exact shape (e.g.
// SurfaceSnap with no Renderer) that leaks to the console during ComponentDefaultTemplate's harvest.
// Only ever added to a throwaway HideAndDontSave GameObject by Register, never saved to a scene.
public class LoggingOnValidateProbe : MonoBehaviour
{
    private void OnValidate()
    {
        Debug.LogError("[CodeScenesTest] LoggingOnValidateProbe.OnValidate leaked during the default-template harvest.");
    }
}
