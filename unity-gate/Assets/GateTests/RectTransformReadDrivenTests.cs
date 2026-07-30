using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using SceneBuilder.Editor;
using SceneBuilder.Core.Model;

// m-ui-recttransform b3-t1 (specs/13-recttransform.md): the adapter READ side of RectTransform
// support. SceneSnapshotReader must stamp Kind="RectTransform" + the five Vec2 fields off a live
// RectTransform (plain Transforms unchanged), and DeriveDrivenChannels must OR in the per-axis rect
// channels Unity itself reports as driven, PLUS the existing base Position/Scale bits per D2 —
// against REAL driving components (Canvas/CanvasScaler, HorizontalLayoutGroup, ContentSizeFitter),
// never a hand-populated fixture (research.md's "risky half").
public class RectTransformReadDrivenTests
{
    private const string Dir = "Assets/GateTests/Fixtures_RectTransformReadDriven";
    private const string ScenePath = "Assets/GateTests/__RectTransformReadDrivenTemp.unity";

    private PrefabFacadeFixture.Handles _handles;

    [SetUp]
    public void SetUp()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        _handles = PrefabFacadeFixture.Create(Dir);
    }

    [TearDown]
    public void TearDown()
    {
        PrefabFacadeFixture.Delete(Dir);
        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }
    }

    private static SnapshotNode FindNode(SnapshotNode[] roots, string name)
    {
        foreach (var n in roots)
        {
            if (n.Name == name) return n;
            var found = FindNode(n.Children, name);
            if (found != null) return found;
        }

        return null;
    }

    [Test]
    public void Read_CanvasRoot_StampsRectKindAndCanvasDrivenChannelsIncludingBaseTransform()
    {
        var go = new GameObject("HudCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        try
        {
            go.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            Canvas.ForceUpdateCanvases();

            var snapshot = SceneSnapshotReader.Read(go.scene);
            var node = FindNode(snapshot.Roots, "HudCanvas");

            Assert.IsNotNull(node, "HudCanvas not found in snapshot.");
            Assert.AreEqual(RectTransformFields.Kind, node.Transform.Kind,
                "A ScreenSpaceOverlay Canvas's RectTransform must read Kind==RectTransform.");

            const ChannelMask expected = ChannelMask.AllRectFields
                | ChannelMask.PositionX | ChannelMask.PositionY | ChannelMask.PositionZ
                | ChannelMask.ScaleX | ChannelMask.ScaleY | ChannelMask.ScaleZ;
            Assert.AreEqual(expected, node.Transform.DrivenChannels,
                "A Canvas-driven RectTransform (Unity reports DrivenTransformProperties.All) must ALSO OR " +
                "in the base PositionX/Y/Z + ScaleX/Y/Z bits (D2) — without them a ScreenSpaceOverlay " +
                "Canvas would emit a pos:/scale: patch on every sync.");
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void Read_LayoutGroupChild_StampsDrivenAnchoredPositionAnchorsAndSizeDelta()
    {
        var canvasGo = new GameObject("HudCanvas", typeof(Canvas));
        try
        {
            var groupGo = new GameObject("Group", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            groupGo.transform.SetParent(canvasGo.transform, false);
            var group = groupGo.GetComponent<HorizontalLayoutGroup>();
            group.childControlWidth = true;
            group.childControlHeight = true;

            var itemGo = new GameObject("Item", typeof(RectTransform), typeof(Image));
            itemGo.transform.SetParent(groupGo.transform, false);

            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)groupGo.transform);

            var snapshot = SceneSnapshotReader.Read(canvasGo.scene);
            var node = FindNode(snapshot.Roots, "Item");

            Assert.IsNotNull(node, "Item not found in snapshot.");
            const ChannelMask expected = ChannelMask.AnchoredPosition | ChannelMask.AnchorMin
                | ChannelMask.AnchorMax | ChannelMask.SizeDelta | ChannelMask.PositionX | ChannelMask.PositionY;
            Assert.AreEqual(expected, node.Transform.DrivenChannels,
                "A HorizontalLayoutGroup-controlled child must stamp AnchoredPosition/Anchors/SizeDelta " +
                "plus the base PositionX/Y bits (D2).");
        }
        finally
        {
            Object.DestroyImmediate(canvasGo);
        }
    }

    [Test]
    public void Read_ContentSizeFitterChild_StampsOnlyDrivenSizeDeltaX()
    {
        var canvasGo = new GameObject("HudCanvas", typeof(Canvas));
        try
        {
            var itemGo = new GameObject("Fitted", typeof(RectTransform), typeof(Image), typeof(ContentSizeFitter));
            itemGo.transform.SetParent(canvasGo.transform, false);
            var fitter = itemGo.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;

            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)itemGo.transform);

            var snapshot = SceneSnapshotReader.Read(canvasGo.scene);
            var node = FindNode(snapshot.Roots, "Fitted");

            Assert.IsNotNull(node, "Fitted not found in snapshot.");
            Assert.AreEqual(ChannelMask.SizeDeltaX, node.Transform.DrivenChannels,
                "A horizontal-only ContentSizeFitter must stamp ONLY SizeDeltaX driven — a whole-field " +
                "SizeDelta here is a per-axis masking regression (D5), not a rounding-up.");
        }
        finally
        {
            Object.DestroyImmediate(canvasGo);
        }
    }

    [Test]
    public void Read_AnchoredPanel_ReadsFiveFieldsVerbatim_NoDrivenChannels()
    {
        var canvasGo = new GameObject("HudCanvas", typeof(Canvas));
        try
        {
            var panelGo = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panelGo.transform.SetParent(canvasGo.transform, false);
            var rt = (RectTransform)panelGo.transform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(24f, -24f);
            rt.sizeDelta = new Vector2(320f, 120f);

            var snapshot = SceneSnapshotReader.Read(canvasGo.scene);
            var node = FindNode(snapshot.Roots, "Panel");

            Assert.IsNotNull(node, "Panel not found in snapshot.");
            Assert.AreEqual(RectTransformFields.Kind, node.Transform.Kind);
            Assert.AreEqual(new Vec2(24f, -24f), node.Transform.AnchoredPosition);
            Assert.AreEqual(new Vec2(320f, 120f), node.Transform.SizeDelta);
            Assert.AreEqual(new Vec2(0f, 1f), node.Transform.AnchorMin);
            Assert.AreEqual(new Vec2(0f, 1f), node.Transform.AnchorMax);
            Assert.AreEqual(new Vec2(0f, 1f), node.Transform.Pivot);
            Assert.AreEqual(ChannelMask.None, node.Transform.DrivenChannels,
                "An ordinary anchored Panel with no driving component must report no driven channels.");
        }
        finally
        {
            Object.DestroyImmediate(canvasGo);
        }
    }

    [Test]
    public void Read_PlainTransformNode_ReadsTransformKind_NoUiFields()
    {
        var go = new GameObject("Plain");
        try
        {
            var snapshot = SceneSnapshotReader.Read(go.scene);
            var node = FindNode(snapshot.Roots, "Plain");

            Assert.IsNotNull(node, "Plain not found in snapshot.");
            Assert.AreEqual("Transform", node.Transform.Kind);
            Assert.IsNull(node.Transform.AnchoredPosition);
            Assert.IsNull(node.Transform.SizeDelta);
            Assert.IsNull(node.Transform.AnchorMin);
            Assert.IsNull(node.Transform.AnchorMax);
            Assert.IsNull(node.Transform.Pivot);
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void Read_RectTransformAndTransform_AreNeverReadIntoComponents()
    {
        var canvasGo = new GameObject("HudCanvas", typeof(Canvas));
        var plainGo = new GameObject("PlainNode");
        try
        {
            var panelGo = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panelGo.transform.SetParent(canvasGo.transform, false);

            var snapshot = SceneSnapshotReader.Read(canvasGo.scene);
            var panelNode = FindNode(snapshot.Roots, "Panel");
            var plainNode = FindNode(snapshot.Roots, "PlainNode");

            Assert.IsNotNull(panelNode, "Panel not found in snapshot.");
            Assert.IsNotNull(plainNode, "PlainNode not found in snapshot.");
            Assert.IsFalse(
                panelNode.Components.Any(c => c.Type.FullName == "UnityEngine.RectTransform" || c.Type.FullName == "UnityEngine.Transform"),
                "A RectTransform must never be read into Components[].");
            Assert.IsTrue(panelNode.Components.Any(c => c.Type.FullName == "UnityEngine.UI.Image"),
                "Sanity check: the Panel's Image must still be read, so the RectTransform exclusion above isn't vacuous.");
            Assert.IsFalse(plainNode.Components.Any(c => c.Type.FullName == "UnityEngine.Transform"),
                "A plain Transform must never be read into Components[].");
        }
        finally
        {
            Object.DestroyImmediate(canvasGo);
            Object.DestroyImmediate(plainGo);
        }
    }

    [Test]
    public void Read_PrefabInstanceNestedRectChild_ReadsRectKindAndFields()
    {
        var tank = (GameObject)PrefabUtility.InstantiatePrefab(
            AssetDatabase.LoadAssetAtPath<GameObject>(_handles.TankPath));
        tank.name = "Tank";
        try
        {
            var barrel = tank.transform.Find("LeftTurret/Barrel").gameObject;
            var hud = new GameObject("Hud", typeof(RectTransform));
            hud.transform.SetParent(barrel.transform, false);
            var rt = (RectTransform)hud.transform;
            rt.sizeDelta = new Vector2(200f, 80f);
            rt.pivot = new Vector2(0f, 1f);

            var scene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.SaveScene(scene, ScenePath);

            var addedPremise = PrefabUtility.GetAddedGameObjects(tank);
            Assert.IsTrue(addedPremise != null && addedPremise.Any(a => a.instanceGameObject == hud),
                "Premise failed: Hud did not register as a PrefabUtility AddedGameObjects override.");

            var node = SceneSnapshotReader.Read(scene).Roots.First(r => r.Name == "Tank");
            var added = node.AddedGameObjects.FirstOrDefault(a => a.Node.Name == "Hud");

            Assert.IsNotNull(added, "Hud did not read into Tank's AddedGameObjects[].");
            Assert.AreEqual(RectTransformFields.Kind, added!.Node.Transform.Kind,
                "A RectTransform added under a nested prefab-instance sub-object must read Kind==RectTransform.");
            Assert.AreEqual(new Vec2(200f, 80f), added.Node.Transform.SizeDelta);
            Assert.AreEqual(new Vec2(0f, 1f), added.Node.Transform.Pivot);
        }
        finally
        {
            Object.DestroyImmediate(tank);
        }
    }

    // m-ui-recttransform b3-t1 (iteration 2, research.md scope/bucket-b3.md finding): the nested
    // probe path (PrefabInstanceProbe.Nested.cs) hardcodes ChannelMask.None for an added child's
    // driven mask "deliberately... preserves current record equality" — but a UI child added under
    // a prefab instance can carry a REAL driving component just like a root-scene child, and its
    // driven axis must not be authored back into source as if it were a manual edit.
    [Test]
    public void Read_PrefabInstanceNestedRectChild_ReportsRealDrivenChannels()
    {
        var tank = (GameObject)PrefabUtility.InstantiatePrefab(
            AssetDatabase.LoadAssetAtPath<GameObject>(_handles.TankPath));
        tank.name = "Tank";
        try
        {
            var barrel = tank.transform.Find("LeftTurret/Barrel").gameObject;
            var hud = new GameObject("Hud", typeof(RectTransform), typeof(Image), typeof(ContentSizeFitter));
            hud.transform.SetParent(barrel.transform, false);
            var fitter = hud.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;

            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)hud.transform);

            var scene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.SaveScene(scene, ScenePath);

            var addedPremise = PrefabUtility.GetAddedGameObjects(tank);
            Assert.IsTrue(addedPremise != null && addedPremise.Any(a => a.instanceGameObject == hud),
                "Premise failed: Hud did not register as a PrefabUtility AddedGameObjects override.");

            var node = SceneSnapshotReader.Read(scene).Roots.First(r => r.Name == "Tank");
            var added = node.AddedGameObjects.FirstOrDefault(a => a.Node.Name == "Hud");

            Assert.IsNotNull(added, "Hud did not read into Tank's AddedGameObjects[].");
            Assert.AreEqual(ChannelMask.SizeDeltaX, added!.Node.Transform.DrivenChannels,
                "A horizontal-only ContentSizeFitter on a nested-prefab-instance added child must stamp " +
                "SizeDeltaX driven exactly as it does for a root-scene child — a hardcoded ChannelMask.None " +
                "on this path means a driven layout gets authored into source as if it were a manual edit.");
        }
        finally
        {
            Object.DestroyImmediate(tank);
        }
    }
}
