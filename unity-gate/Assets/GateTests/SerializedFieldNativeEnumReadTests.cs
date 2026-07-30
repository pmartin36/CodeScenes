using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using SceneBuilder.Editor;
using SceneBuilder.Core.Model;

// SerializedFieldBridge's focused adapter-read unit for ENUM serialized properties, mirroring
// AssetReferenceResolverObjectRefReadTests.cs's role for the object-ref read. A serialized enum whose
// managed FieldInfo cannot be resolved (a native class field, e.g. Canvas.m_RenderMode) reads as
// ValueNode.Primitive.Int(intValue) — the same shape WriteField consumes — while a managed enum field
// keeps reading as ValueNode.Enum. Read and write therefore agree on native enums, and the per-type
// default template carries them at their constructed value.
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
    public void ReadComponent_NativeEnumAtNonDefaultValue_ReadsAsIntPrimitive()
    {
        var go = new GameObject("Canvas");
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay; // 0 — NOT a freshly-constructed default (WorldSpace, 2)
        SaveActiveScene();

        var data = SerializedFieldBridge.ReadComponent(canvas);

        Assert.IsTrue(data.Fields.TryGetValue("m_RenderMode", out var node),
            "A native enum authored away from its type default must survive CollectFields, not be " +
            "dropped as Unsupported.");
        Assert.IsNotInstanceOf<ValueNode.Unsupported>(node,
            "A native enum property must not read back as Unsupported (it has a working write path).");
        Assert.AreEqual(ValueNode.Primitive.Int(0), node,
            "A native enum must read as the raw SerializedProperty.intValue, the same shape " +
            "WriteField already consumes for it.");
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
        go.AddComponent<Canvas>(); // left at its constructed default: m_RenderMode=WorldSpace(2), m_PixelPerfect=false
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
            "reads as an int, and the template is built from the same CollectFields walk that builds " +
            "ReadComponent's prune index.");
        Assert.AreEqual(ValueNode.Primitive.Int(2), renderMode);
    }
}
