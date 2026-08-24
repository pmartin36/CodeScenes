using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;

// Spec 47: AuthoredPathResolver.ResolvePath must match a typed member selector against the live
// probe's REAL serialized names instead of a fixed camelCase->m_PascalCase guess. Each positive
// case authors `.Set(x => x.<member>, value)` against a member whose real serialized name the old
// mangling could never produce (a space, a lowercase letter right after "m_", or a name that
// diverges from the property spelling entirely), runs the real build, and checks the value
// actually materialized on the live component with zero Console warnings/errors. The two throw
// cases pin the located-error contract: a member with no serialized backing still refuses loudly,
// and a member Unity splits across two serialized fields names both real fields in the error.
public class TypedSelectorSerializedNameResolveTests
{
    private const string ScenePath = "Assets/GateTests/__TypedSelectorSerializedNameResolveTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using UnityEngine;
using SceneBuilder.Authoring;
public class TypedSelectorSerializedNameResolveScene : ISceneDefinition
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

    private static void AssertNoWarningsOrErrors(List<(string Message, LogType Type)> logs) =>
        Assert.IsFalse(logs.Any(l => l.Type == LogType.Warning || l.Type == LogType.Error),
            "Build logged a warning/error:\n" + string.Join("\n", logs.Select(l => l.Type + ": " + l.Message)));

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_tssn_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "TypedSelectorSerializedNameResolveScene.cs");
        _sidecarPath = Path.Combine(_dir, "TypedSelectorSerializedNameResolveScene.sbmap.json");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        if (File.Exists(ScenePath)) AssetDatabase.DeleteAsset(ScenePath);
    }

    [Test]
    public void CodeToScene_CameraNearClipPlane_ResolvesSpacedNameAndApplies()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Cam\").Component<UnityEngine.Camera>(c => c.Set(x => x.nearClipPlane, 0.5f));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var logs = CaptureLogs(() =>
            SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene()));

        var cam = FindRoot(EditorSceneManager.GetActiveScene(), "Cam");
        Assert.IsNotNull(cam, "Cam was not created by SceneBuilderBuild.Run");
        Assert.AreEqual(0.5f, cam.GetComponent<Camera>().nearClipPlane, 0.001f,
            "Typed nearClipPlane selector did not resolve to the real 'near clip plane' serialized name.");
        AssertNoWarningsOrErrors(logs);
    }

    [Test]
    public void CodeToScene_CameraFieldOfView_ResolvesSpacedNameAndApplies()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Cam\").Component<UnityEngine.Camera>(c => c.Set(x => x.fieldOfView, 42f));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var logs = CaptureLogs(() =>
            SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene()));

        var cam = FindRoot(EditorSceneManager.GetActiveScene(), "Cam");
        Assert.IsNotNull(cam, "Cam was not created by SceneBuilderBuild.Run");
        Assert.AreEqual(42f, cam.GetComponent<Camera>().fieldOfView, 0.001f,
            "Typed fieldOfView selector did not resolve to the real 'field of view' serialized name.");
        AssertNoWarningsOrErrors(logs);
    }

    [Test]
    public void CodeToScene_TextMeshProUGUIFontSize_ResolvesLowercaseFAndApplies()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Label\").Component<TMPro.TextMeshProUGUI>(c => c.Set(x => x.fontSize, 24f));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var logs = CaptureLogs(() =>
            SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene()));

        var label = FindRoot(EditorSceneManager.GetActiveScene(), "Label");
        Assert.IsNotNull(label, "Label was not created by SceneBuilderBuild.Run");
        Assert.AreEqual(24f, label.GetComponent<TMPro.TextMeshProUGUI>().fontSize, 0.001f,
            "Typed fontSize selector did not resolve to the real lowercase-f 'm_fontSize' serialized name.");
        AssertNoWarningsOrErrors(logs);
    }

    [Test]
    public void CodeToScene_LightType_EnumResolvesAndWritesWithoutRawCast()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Lamp\").Component<UnityEngine.Light>(c => c.Set(x => x.type, LightType.Spot));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var logs = CaptureLogs(() =>
            SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene()));

        var lamp = FindRoot(EditorSceneManager.GetActiveScene(), "Lamp");
        Assert.IsNotNull(lamp, "Lamp was not created by SceneBuilderBuild.Run");
        Assert.AreEqual(LightType.Spot, lamp.GetComponent<Light>().type,
            "Typed enum selector for Light.type did not resolve and write.");
        AssertNoWarningsOrErrors(logs);
    }

    [Test]
    public void CodeToScene_ImageFillMethod_EnumResolvesAndWrites()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Icon\").Component<UnityEngine.UI.Image>(c => c.Set(x => x.fillMethod, UnityEngine.UI.Image.FillMethod.Horizontal));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var logs = CaptureLogs(() =>
            SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene()));

        var icon = FindRoot(EditorSceneManager.GetActiveScene(), "Icon");
        Assert.IsNotNull(icon, "Icon was not created by SceneBuilderBuild.Run");
        Assert.AreEqual(UnityEngine.UI.Image.FillMethod.Horizontal, icon.GetComponent<UnityEngine.UI.Image>().fillMethod,
            "Typed enum selector for Image.fillMethod did not resolve and write.");
        AssertNoWarningsOrErrors(logs);
    }

    [Test]
    public void CodeToScene_RigidbodyCollisionDetectionMode_ResolvesToMCollisionDetectionAndWrites()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Box\").Component<UnityEngine.Rigidbody>(c => c.Set(x => x.collisionDetectionMode, UnityEngine.CollisionDetectionMode.Continuous));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var logs = CaptureLogs(() =>
            SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene()));

        var box = FindRoot(EditorSceneManager.GetActiveScene(), "Box");
        Assert.IsNotNull(box, "Box was not created by SceneBuilderBuild.Run");
        Assert.AreEqual(CollisionDetectionMode.Continuous, box.GetComponent<Rigidbody>().collisionDetectionMode,
            "Typed collisionDetectionMode selector did not resolve to the real 'm_CollisionDetection' serialized name.");
        AssertNoWarningsOrErrors(logs);
    }

    [Test]
    public void CodeToScene_RigidbodyInterpolation_ResolvesToMInterpolateAndWrites()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Box\").Component<UnityEngine.Rigidbody>(c => c.Set(x => x.interpolation, RigidbodyInterpolation.Interpolate));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var logs = CaptureLogs(() =>
            SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene()));

        var box = FindRoot(EditorSceneManager.GetActiveScene(), "Box");
        Assert.IsNotNull(box, "Box was not created by SceneBuilderBuild.Run");
        Assert.AreEqual(RigidbodyInterpolation.Interpolate, box.GetComponent<Rigidbody>().interpolation,
            "Typed interpolation selector did not resolve to the real 'm_Interpolate' serialized name.");
        AssertNoWarningsOrErrors(logs);
    }

    // Camera.pixelWidth is a real, compilable C# property with NO serialized backing field (it is
    // computed from the target rect/texture at runtime) -- the resolver must still refuse loudly
    // rather than silently guessing or materializing garbage.
    [Test]
    public void CodeToScene_MemberWithNoSerializedBacking_ThrowsLocatedError()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Cam\").Component<UnityEngine.Camera>(c => c.Set(x => x.pixelWidth, 100));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene()));

        StringAssert.Contains("Cannot resolve authored member", ex.Message);
        StringAssert.Contains("pixelWidth", ex.Message);
    }

    // TextMeshProUGUI.alignment is backed by TWO separate serialized fields (m_HorizontalAlignment /
    // m_VerticalAlignment), neither of which shares its enum type -- a single typed selector cannot
    // address both, so the located error must name both real fields instead of a generic message.
    [Test]
    public void CodeToScene_TmpAlignmentSplitAcrossTwoFields_ThrowsLocatedErrorNamingBothFields()
    {
        File.WriteAllText(_builderPath, Source(
            "        scene.Add(\"Label\").Component<TMPro.TextMeshProUGUI>(c => c.Set(x => x.alignment, TMPro.TextAlignmentOptions.Center));"));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, EditorSceneManager.GetActiveScene()));

        StringAssert.Contains("m_HorizontalAlignment", ex.Message);
        StringAssert.Contains("m_VerticalAlignment", ex.Message);
    }
}
