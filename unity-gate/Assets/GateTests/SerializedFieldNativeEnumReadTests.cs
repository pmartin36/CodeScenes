using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using SceneBuilder.Editor;
using SceneBuilder.Core.Model;

// SerializedFieldBridge's focused adapter-read unit for ENUM serialized properties, mirroring
// AssetReferenceResolverObjectRefReadTests.cs's role for the object-ref read. A native serialized
// enum (no managed FieldInfo backs the path, e.g. Canvas.m_RenderMode) reads as a typed
// ValueNode.Enum carrying the member NAME, resolved via SerializedMemberMap — the same as a managed
// enum field. Read and write therefore agree on both native and managed enums.
// ReadComponent no longer prunes ANY default-valued field (unconditional, unfiltered read —
// the decision to omit a default value moved to the emit side, ComponentDefaultOmission). The
// per-type default template (SceneSnapshot.ComponentDefaults) is built by ComponentDefaultTemplate,
// which constructs through Create — the same primitive PlanExecutor creates components with — so a
// native enum's "constructed default" reflects Create's EditorCreationDefaults overlay, not a raw
// AddComponent.
public class SerializedFieldNativeEnumReadTests
{
    private const string ScenePath = "Assets/GateTests/__NativeEnumReadTemp.unity";

    [SetUp]
    public void SetUp()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    [TearDown]
    public void TearDown()
    {
        if (System.IO.File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }
    }

    private static void SaveActiveScene()
    {
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);
    }

    [Test]
    public void ReadComponent_NativeEnumAtNonDefaultValue_ReadsAsTypedEnumMember()
    {
        var go = new GameObject("Canvas");
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace; // Create's overlay makes ScreenSpaceOverlay
                                                    // the template default, so WorldSpace is the
                                                    // "away from default" value this test needs.
        SaveActiveScene();

        var data = SerializedFieldBridge.ReadComponent(canvas);

        Assert.IsTrue(data.Fields.TryGetValue("m_RenderMode", out var node),
            "A native enum authored away from its type default must survive CollectFields, not be " +
            "dropped as Unsupported.");
        Assert.IsNotInstanceOf<ValueNode.Unsupported>(node,
            "A native enum property must not read back as Unsupported (it has a working write path).");
        Assert.AreEqual(new ValueNode.Enum("UnityEngine.RenderMode", new[] { "WorldSpace" }, IsFlags: false), node,
            "A native enum must read as a typed member NAME, resolved via SerializedMemberMap — never " +
            "the raw SerializedProperty.intValue.");
    }

    [Test]
    public void ReadComponent_ManagedEnumField_StillReadsAsEnumNode()
    {
        var go = new GameObject("Fitter");
        var fitter = go.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize; // default is Unconstrained
        SaveActiveScene();

        var data = SerializedFieldBridge.ReadComponent(fitter);

        Assert.IsTrue(data.Fields.TryGetValue("m_HorizontalFit", out var node),
            "A managed enum field authored away from its default must still be present.");
        Assert.IsInstanceOf<ValueNode.Enum>(node,
            "A managed enum field must keep reading as ValueNode.Enum, not be widened into the " +
            "native-enum int fallback.");
        CollectionAssert.Contains(((ValueNode.Enum)node).Members, "PreferredSize",
            "The Enum node must carry the authored member name.");
    }

    [Test]
    public void Read_LiveScene_ComponentDefaultsCarriesCanvasTemplateIncludingNativeEnum()
    {
        var go = new GameObject("Canvas");
        go.AddComponent<Canvas>(); // left at its constructed default: m_PixelPerfect=false. The template's
                                    // m_RenderMode default is ScreenSpaceOverlay, from Create's overlay —
                                    // NOT this GameObject's own raw AddComponent value (WorldSpace).
        SaveActiveScene();

        var snapshot = SceneSnapshotReader.Read(EditorSceneManager.GetActiveScene());

        var canvasDefaults = System.Array.Find(snapshot.ComponentDefaults,
            d => d.Type.FullName == "UnityEngine.Canvas");
        Assert.IsNotNull(canvasDefaults,
            "SceneSnapshot.ComponentDefaults must carry a UnityEngine.Canvas template — this is the " +
            "live proof that FromRoots ever populates a non-empty default template.");

        Assert.IsTrue(canvasDefaults.Fields.TryGetValue("m_PixelPerfect", out var pixelPerfect),
            "The Canvas template must carry m_PixelPerfect — a managed bool field at its " +
            "constructed default value.");
        Assert.AreEqual(ValueNode.Primitive.Bool(false), pixelPerfect);

        Assert.IsTrue(canvasDefaults.Fields.TryGetValue("m_RenderMode", out var renderMode),
            "The Canvas template must carry m_RenderMode at its constructed default — a native enum " +
            "reads as a typed member name, and the template is built from " +
            "ComponentDefaultTemplate.Create, which applies the ScreenSpaceOverlay overlay.");
        Assert.AreEqual(
            new ValueNode.Enum("UnityEngine.RenderMode", new[] { "ScreenSpaceOverlay" }, IsFlags: false),
            renderMode,
            "The template's m_RenderMode default is ScreenSpaceOverlay via Create's overlay, not " +
            "the raw AddComponent default (WorldSpace).");
    }
}
