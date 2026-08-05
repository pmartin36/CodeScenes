using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using SceneBuilder.Editor;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;

// specs/32-serialization-fidelity.md, C5: ObjectReferenceResolver.ExpectedRefType's PPtr
// parse accepts BOTH spellings SerializedProperty.type actually uses — "PPtr<$X>" for a MANAGED C#
// field and "PPtr<X>" for a NATIVE one — and resolves the bare token X through a 3-rung ladder:
// managed FieldInfo (exact), the GameObject special case, then a short-name scan across every loaded
// UnityEngine.Object subtype with an unambiguity check. A token matching two or more distinct types
// never guesses; it resolves to null and reports every candidate.
public class PPtrShortNameResolutionTests
{
    private const string ScenePath = "Assets/GateTests/__PPtrShortNameResolutionTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using UnityEngine;
using UnityEngine.UI;
using SceneBuilder.Authoring;
public class PPtrShortNameResolutionScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_pptr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "PPtrShortNameResolutionScene.cs");
        _sidecarPath = Path.Combine(_dir, "PPtrShortNameResolutionScene.sbmap.json");
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        ConflictSurfacing.ResetNotes();
    }

    private static GameObject FindRoot(Scene scene, string name)
    {
        foreach (var go in scene.GetRootGameObjects())
        {
            if (go.name == name)
            {
                return go;
            }
        }
        return null;
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

    private static List<string> CaptureLogs(Action action)
    {
        var messages = new List<string>();
        void Handler(string condition, string stackTrace, LogType type) => messages.Add(condition);
        Application.logMessageReceived += Handler;
        try
        {
            action();
        }
        finally
        {
            Application.logMessageReceived -= Handler;
        }
        return messages;
    }

    // 1. Every measured "PPtr<$X>" (managed) field resolves to its real declared type: Image.m_Sprite,
    // Image.m_Material, Button.m_TargetGraphic, Button.m_Navigation.m_SelectOnUp.
    [Test]
    public void ExpectedRefType_ManagedReferenceFields_ResolveToTheirRealTypes()
    {
        var go = new GameObject("UI");
        var image = go.AddComponent<Image>();
        var button = go.AddComponent<Button>();

        var spriteProp = new SerializedObject(image).FindProperty("m_Sprite");
        var materialProp = new SerializedObject(image).FindProperty("m_Material");
        var targetGraphicProp = new SerializedObject(button).FindProperty("m_TargetGraphic");
        var selectOnUpProp = new SerializedObject(button).FindProperty("m_Navigation.m_SelectOnUp");

        Assert.IsNotNull(spriteProp, "Image.m_Sprite property not found");
        Assert.IsNotNull(materialProp, "Image.m_Material property not found");
        Assert.IsNotNull(targetGraphicProp, "Button.m_TargetGraphic property not found");
        Assert.IsNotNull(selectOnUpProp, "Button.m_Navigation.m_SelectOnUp property not found");

        Assert.AreEqual(typeof(Sprite), ObjectReferenceResolver.ExpectedRefType(spriteProp, out _),
            "Image.m_Sprite did not resolve to UnityEngine.Sprite");
        Assert.AreEqual(typeof(Material), ObjectReferenceResolver.ExpectedRefType(materialProp, out _),
            "Image.m_Material did not resolve to UnityEngine.Material");
        Assert.AreEqual(typeof(Graphic), ObjectReferenceResolver.ExpectedRefType(targetGraphicProp, out _),
            "Button.m_TargetGraphic did not resolve to UnityEngine.UI.Graphic");
        Assert.AreEqual(typeof(Selectable), ObjectReferenceResolver.ExpectedRefType(selectOnUpProp, out _),
            "Button.m_Navigation.m_SelectOnUp did not resolve to UnityEngine.UI.Selectable");
    }

    // 2. A native "PPtr<X>" field (no managed FieldInfo) resolves by short name: Canvas.m_Camera and
    // SpriteRenderer.m_ProbeAnchor.
    [Test]
    public void ExpectedRefType_NativeReferenceField_ResolvesByShortName()
    {
        var canvas = new GameObject("Canvas").AddComponent<Canvas>();
        var cameraProp = new SerializedObject(canvas).FindProperty("m_Camera");
        Assert.IsNotNull(cameraProp, "Canvas.m_Camera property not found");
        Assert.AreEqual(typeof(Camera), ObjectReferenceResolver.ExpectedRefType(cameraProp, out _),
            "Canvas.m_Camera (native PPtr<Camera>) did not resolve by short name");

        var spriteRenderer = new GameObject("SpriteRenderer").AddComponent<SpriteRenderer>();
        var probeAnchorProp = new SerializedObject(spriteRenderer).FindProperty("m_ProbeAnchor");
        Assert.IsNotNull(probeAnchorProp, "SpriteRenderer.m_ProbeAnchor property not found");
        Assert.AreEqual(typeof(Transform), ObjectReferenceResolver.ExpectedRefType(probeAnchorProp, out _),
            "SpriteRenderer.m_ProbeAnchor (native PPtr<Transform>) did not resolve by short name");
    }

    // 3. A real build+sync over a Canvas/Image/Button hierarchy logs no
    // "Could not resolve component type" console warning while classifying their null reference
    // fields.
    [Test]
    public void Sync_OverCanvasImageAndButton_LogsNoUnresolvedComponentTypeWarning()
    {
        File.WriteAllText(_builderPath, Source(
            "        var ui = scene.Add(\"UI\");\n" +
            "        ui.Component<Canvas>();\n" +
            "        ui.Component<Image>();\n" +
            "        ui.Component<Button>();"));

        var scene = EditorSceneManager.GetActiveScene();

        var logs = CaptureLogs(() =>
        {
            var buildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
            Assert.IsEmpty(buildResult.Diagnostics,
                "Setup build reported diagnostics: "
                + string.Join("; ", buildResult.Diagnostics.Select(d => d.Message)));

            EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
        });

        Assert.IsFalse(logs.Any(m => m.Contains("Could not resolve component type")),
            "Build+Sync over a Canvas/Image/Button hierarchy logged an unresolved component type "
            + "warning.\nLogs:\n" + string.Join("\n", logs));
    }

    // 4. D2's named concrete consequence: a null Button.m_TargetGraphic (managed, scene-object typed)
    // classifies as ObjectRef(null), not AssetRef(null).
    [Test]
    public void ReadObjectReference_NullButtonTargetGraphic_IsObjectRefNull()
    {
        var button = new GameObject("Button").AddComponent<Button>();
        button.targetGraphic = null;

        var resolver = ObjectReferenceResolver.BuildSceneRefResolver(new IdentityMap());
        var prop = new SerializedObject(button).FindProperty("m_TargetGraphic");
        Assert.IsNotNull(prop, "Button.m_TargetGraphic property not found");

        var node = AssetReferenceResolver.ReadObjectReference(prop, resolver);

        Assert.IsInstanceOf<ValueNode.ObjectRef>(node,
            "A null Button.m_TargetGraphic must classify as a SCENE-OBJECT reference "
            + "(got " + node.GetType().Name + ")");
        Assert.IsNull(((ValueNode.ObjectRef)node).TargetLogicalId,
            "A cleared m_TargetGraphic must read as the None form ObjectRef(null)");
    }

    // 5. An ambiguous short name (two distinct loaded types both named "Rigidbody") resolves to null
    // and reports every candidate — never an arbitrary winner.
    [Test]
    public void ResolveObjectTypeByShortName_AmbiguousName_IsNullAndReportsEveryCandidate()
    {
        var resolved = ComponentTypeResolver.ResolveObjectTypeByShortName("Rigidbody", out var candidates);

        Assert.IsNull(resolved, "An ambiguous short name must resolve to null, not a guessed winner");
        Assert.Contains(typeof(Rigidbody), candidates.ToList(),
            "Candidates did not include UnityEngine.Rigidbody");
        Assert.Contains(typeof(MyGame.Physics.Rigidbody), candidates.ToList(),
            "Candidates did not include MyGame.Physics.Rigidbody");
    }

    // 6. The live-property path agrees with #5: a native field whose type name is ambiguous across
    // loaded assemblies (HingeJoint.m_ConnectedBody -> "Rigidbody") resolves to null, never to either
    // colliding candidate.
    [Test]
    public void ExpectedRefType_AmbiguousNativeField_IsNull()
    {
        var joint = new GameObject("Joint").AddComponent<HingeJoint>();
        var prop = new SerializedObject(joint).FindProperty("m_ConnectedBody");
        Assert.IsNotNull(prop, "HingeJoint.m_ConnectedBody property not found");

        var resolved = ObjectReferenceResolver.ExpectedRefType(prop, out _);

        Assert.IsNull(resolved, "An ambiguous native PPtr token must resolve to null");
        Assert.AreNotEqual(typeof(MyGame.Physics.Rigidbody), resolved,
            "An ambiguous native PPtr token must never silently pick the wrong candidate");
        Assert.AreNotEqual(typeof(Rigidbody), resolved,
            "An ambiguous native PPtr token must never silently pick a candidate, even the intended one");
    }

    // 7. The GameObject special case still wins for a managed field, ahead of the short-name rung.
    [Test]
    public void ExpectedRefType_GameObjectTypedManagedField_StillResolves()
    {
        var doorOpener = new GameObject("Opener").AddComponent<DoorOpener>();
        var prop = new SerializedObject(doorOpener).FindProperty("target");
        Assert.IsNotNull(prop, "DoorOpener.target property not found");

        Assert.AreEqual(typeof(GameObject), ObjectReferenceResolver.ExpectedRefType(prop, out _),
            "DoorOpener.target (managed GameObject-typed field) did not resolve to typeof(GameObject)");
    }

    // 8. A namespaced user MonoBehaviour with no simple-name collision resolves by short name — the
    // M5 user-script shape that only ever worked when the type had no namespace.
    [Test]
    public void ResolveObjectTypeByShortName_NamespacedUserMonoBehaviour_Resolves()
    {
        var resolved = ComponentTypeResolver.ResolveObjectTypeByShortName("Enemy", out var candidates);

        Assert.AreEqual(typeof(MyGame.Enemies.Enemy), resolved,
            "Short name 'Enemy' did not resolve to MyGame.Enemies.Enemy");
        Assert.IsEmpty(candidates, "An unambiguous resolve must report no candidates");
    }

    // 9. The PPtr string literal is parsed in exactly one place in the package — a mechanical pin, so
    // a future caller cannot re-invent a second, possibly divergent, parse.
    [Test]
    public void Package_PPtrIsParsedInExactlyOnePlace()
    {
        var root = SceneBuilderPaths.PackageRootPath;
        Assert.IsNotNull(root, "SceneBuilderPaths.PackageRootPath resolved to null - cannot scan the "
            + "package for PPtr parse sites (this must FAIL, never vacuously pass).");

        var hits = new List<string>();
        foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (File.ReadAllText(file).Contains("PPtr<"))
            {
                hits.Add(Path.GetFileName(file));
            }
        }

        CollectionAssert.AreEquivalent(new[] { "ObjectReferenceResolver.cs" }, hits,
            "The PPtr string literal must be parsed in exactly ObjectReferenceResolver.cs. Found in: "
            + string.Join(", ", hits));
    }

    // 10. The blast radius of parsing the NATIVE PPtr form is bounded: a null native ASSET-typed
    // field (MeshFilter.m_Mesh -> Mesh, not a Component/GameObject) must stay AssetRef(null), never
    // be reclassified as a scene reference.
    [Test]
    public void ReadObjectReference_NullNativeAssetTypedField_StaysAssetRefNull()
    {
        var meshFilter = new GameObject("Filter").AddComponent<MeshFilter>();
        meshFilter.sharedMesh = null;

        var resolver = ObjectReferenceResolver.BuildSceneRefResolver(new IdentityMap());
        var prop = new SerializedObject(meshFilter).FindProperty("m_Mesh");
        Assert.IsNotNull(prop, "MeshFilter.m_Mesh property not found");

        var node = AssetReferenceResolver.ReadObjectReference(prop, resolver);

        Assert.IsInstanceOf<ValueNode.AssetRef>(node,
            "A null native asset-typed field (m_Mesh) must NOT be reclassified as a scene reference "
            + "by short-name resolution (got " + node.GetType().Name + ")");
        Assert.IsNull(((ValueNode.AssetRef)node).Ref, "A cleared mesh field must read as AssetRef(null)");
    }

    // 11. Building an authored reference on a field whose declared type name collides across loaded
    // assemblies, targeting an object that lacks the component, logs exactly one located console
    // error naming the object anchor, the component type, the field, the ambiguous short name and
    // every distinct colliding candidate full name -- and the live property stays unassigned: the
    // drop is reported, not repaired.
    [Test]
    public void Build_AmbiguousFieldWhoseTargetLacksTheComponent_LogsTheLocatedCandidateMessage()
    {
        File.WriteAllText(_builderPath, Source(
            "        var plain = scene.Add(\"Plain\");\n" +
            "        var joint = scene.Add(\"Joint\");\n" +
            "        joint.Component<UnityEngine.HingeJoint>(c => c.Set(\"m_ConnectedBody\", plain));"));

        var scene = EditorSceneManager.GetActiveScene();

        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("AMBIGUOUS"));

        SceneBuilderBuild.BuildResult buildResult = null;
        var logs = CaptureLogs(() =>
        {
            buildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        });

        Assert.IsEmpty(buildResult.Diagnostics,
            "Build reported diagnostics: " + string.Join("; ", buildResult.Diagnostics.Select(d => d.Message)));

        var ambiguousMessages = logs.Where(m => m.Contains("AMBIGUOUS")).ToList();
        Assert.AreEqual(1, ambiguousMessages.Count,
            "Expected exactly one located AMBIGUOUS error.\nLogs:\n" + string.Join("\n", logs));

        var message = ambiguousMessages[0];
        StringAssert.Contains("Joint", message, "Message does not name the object anchor.\n" + message);
        StringAssert.Contains("UnityEngine.HingeJoint", message, "Message does not name the component type.\n" + message);
        StringAssert.Contains("m_ConnectedBody", message, "Message does not name the field.\n" + message);
        StringAssert.Contains("'Rigidbody'", message, "Message does not name the ambiguous short name.\n" + message);
        StringAssert.Contains("'MyGame.Physics.Rigidbody'", message, "Message does not list the MyGame.Physics.Rigidbody candidate.\n" + message);
        StringAssert.Contains("'UnityEngine.Rigidbody'", message, "Message does not list the UnityEngine.Rigidbody candidate.\n" + message);

        var joint = FindRoot(scene, "Joint");
        Assert.IsNotNull(joint, "Joint was not created.");
        var hinge = joint.GetComponent<HingeJoint>();
        Assert.IsNotNull(hinge, "HingeJoint was not added.");
        Assert.IsNull(hinge.connectedBody,
            "The dropped reference must stay null -- reporting the drop does not repair it.");
    }

    // 12. The same ambiguous field on a target that DOES carry the component is silent and assigns
    // correctly -- the report fires only on a write Unity's own setter measurably drops, never on
    // every ambiguous resolution.
    [Test]
    public void Build_AmbiguousFieldWhoseTargetCarriesTheComponent_IsSilentAndAssigns()
    {
        File.WriteAllText(_builderPath, Source(
            "        var body = scene.Add(\"Body\");\n" +
            "        body.Component<UnityEngine.Rigidbody>();\n" +
            "        var joint = scene.Add(\"Joint\");\n" +
            "        joint.Component<UnityEngine.HingeJoint>(c => c.Set(\"m_ConnectedBody\", body));"));

        var scene = EditorSceneManager.GetActiveScene();

        SceneBuilderBuild.BuildResult buildResult = null;
        var logs = CaptureLogs(() =>
        {
            buildResult = SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
        });

        Assert.IsEmpty(buildResult.Diagnostics,
            "Build reported diagnostics: " + string.Join("; ", buildResult.Diagnostics.Select(d => d.Message)));
        Assert.IsFalse(logs.Any(m => m.Contains("AMBIGUOUS")),
            "An unambiguous WRITE must not log the ambiguity report.\nLogs:\n" + string.Join("\n", logs));

        var joint = FindRoot(scene, "Joint");
        var body = FindRoot(scene, "Body");
        Assert.IsNotNull(joint, "Joint was not created.");
        Assert.IsNotNull(body, "Body was not created.");
        var hinge = joint.GetComponent<HingeJoint>();
        Assert.AreEqual(body.GetComponent<UnityEngine.Rigidbody>(), hinge.connectedBody,
            "The reference must resolve when the target genuinely carries the declared component.");
    }

    // 13. A second build in the same session reports the same ambiguous name nothing further -- the
    // session-scoped recurrence key claims it once, not once per build.
    [Test]
    public void Build_TheSameAmbiguousName_IsReportedOncePerSession()
    {
        File.WriteAllText(_builderPath, Source(
            "        var plain = scene.Add(\"Plain\");\n" +
            "        var joint = scene.Add(\"Joint\");\n" +
            "        joint.Component<UnityEngine.HingeJoint>(c => c.Set(\"m_ConnectedBody\", plain));"));

        var scene = EditorSceneManager.GetActiveScene();

        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("AMBIGUOUS"));
        var firstLogs = CaptureLogs(() => SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene));
        Assert.AreEqual(1, firstLogs.Count(m => m.Contains("AMBIGUOUS")),
            "First build did not log the located AMBIGUOUS error exactly once.\nLogs:\n" + string.Join("\n", firstLogs));

        var secondLogs = CaptureLogs(() => SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene));
        Assert.IsFalse(secondLogs.Any(m => m.Contains("AMBIGUOUS")),
            "A second build in the same session must not repeat the ambiguity report.\nLogs:\n" + string.Join("\n", secondLogs));
    }

    // 14. An unknown short name resolves to null with no candidates -- there is nothing ambiguous
    // about a name matching zero loaded types.
    [Test]
    public void ResolveObjectTypeByShortName_UnknownName_IsNullWithNoCandidates()
    {
        var resolved = ComponentTypeResolver.ResolveObjectTypeByShortName(
            "ThisShortNameMatchesNoLoadedTypeAnywhereInTheDomain", out var candidates);

        Assert.IsNull(resolved, "An unknown short name must resolve to null");
        Assert.IsEmpty(candidates, "An unknown short name must report no candidates");
    }

    // 15. A repeated resolve of the same ambiguous name returns the SAME candidate set from the
    // cache, not a freshly recomputed one.
    [Test]
    public void ResolveObjectTypeByShortName_RepeatedCall_ReturnsTheSameCandidatesFromCache()
    {
        var first = ComponentTypeResolver.ResolveObjectTypeByShortName("Rigidbody", out var firstCandidates);
        var second = ComponentTypeResolver.ResolveObjectTypeByShortName("Rigidbody", out var secondCandidates);

        Assert.IsNull(first);
        Assert.IsNull(second);
        CollectionAssert.AreEquivalent(firstCandidates.ToList(), secondCandidates.ToList(),
            "A repeated resolve of the same ambiguous name must return the same candidate set");
    }

    // 16. The candidate list for an ambiguous name holds no duplicate type entries.
    [Test]
    public void ResolveObjectTypeByShortName_Candidates_AreDistinctTypes()
    {
        ComponentTypeResolver.ResolveObjectTypeByShortName("Rigidbody", out var candidates);

        CollectionAssert.AllItemsAreUnique(candidates.ToList(),
            "The candidate list for an ambiguous name must hold distinct types");
    }

    // 17. Resolving a short name, ambiguous or not, never logs to the console -- callers own
    // whether and how a resulting ambiguity is reported.
    [Test]
    public void ResolveObjectTypeByShortName_NeverLogs()
    {
        var logs = CaptureLogs(() => ComponentTypeResolver.ResolveObjectTypeByShortName("Rigidbody", out _));

        Assert.IsEmpty(logs, "ResolveObjectTypeByShortName must never log.\nLogs:\n" + string.Join("\n", logs));
    }
}
