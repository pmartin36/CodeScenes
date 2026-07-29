using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SceneBuilder.Editor;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Plan;

// b2-t3: PlanExecutor RectTransform write path (specs/13-recttransform.md). Exercises
// PlanExecutor.Execute directly against a live editor scene: CreateObject with TransformKind
// creates a UI GameObject via typeof(RectTransform); the five m_* SetField paths write the typed
// RectTransform properties IN PLACE on the EXISTING mapped object (never destroy+recreate); a
// MAPPED plain-Transform node receiving rect fields is promoted in place with a located note; no
// "Unhandled SetField" warning fires for the five rect paths nor the three Transform paths on a
// UI node; and a freshly created RectTransform's live defaults match RectTransformFields' table
// (D9).
public class RectTransformExecutorTests
{
    private const string ScenePath = "Assets/GateTests/__RectTransformExecutorTemp.unity";

    [SetUp]
    public void SetUp()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }
    }

    private static GameObject Find(string name) => GameObject.Find(name);

    private static Vector2 ToVector2(Vec2 v) => new Vector2(v.X, v.Y);

    private static List<string> CaptureLogs(Action action)
    {
        var messages = new List<string>();
        void Handler(string condition, string stackTrace, LogType type) => messages.Add(condition);
        Application.logMessageReceived += Handler;
        try { action(); }
        finally { Application.logMessageReceived -= Handler; }
        return messages;
    }

    private static PlanOp[] FiveRectFields(string logicalId, Vec2 anchoredPos, Vec2 sizeDelta, Vec2 anchorMin, Vec2 anchorMax, Vec2 pivot) => new PlanOp[]
    {
        new SetField { LogicalId = logicalId, Path = RectTransformFields.AnchoredPositionPath, Value = new ValueNode.Vec2(anchoredPos) },
        new SetField { LogicalId = logicalId, Path = RectTransformFields.SizeDeltaPath,        Value = new ValueNode.Vec2(sizeDelta) },
        new SetField { LogicalId = logicalId, Path = RectTransformFields.AnchorMinPath,        Value = new ValueNode.Vec2(anchorMin) },
        new SetField { LogicalId = logicalId, Path = RectTransformFields.AnchorMaxPath,        Value = new ValueNode.Vec2(anchorMax) },
        new SetField { LogicalId = logicalId, Path = RectTransformFields.PivotPath,            Value = new ValueNode.Vec2(pivot) },
    };

    [Test]
    public void CreateObject_RectTransformKind_CreatesGameObjectWithRectTransform()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new CreateObject { LogicalId = "Panel", Name = "Panel", TransformKind = RectTransformFields.Kind },
            },
        };

        PlanExecutor.Execute(plan, new IdentityMap(), scene);

        var panel = Find("Panel");
        Assert.IsNotNull(panel, "CreateObject did not create the GameObject");
        Assert.IsInstanceOf<RectTransform>(panel.transform,
            "CreateObject with TransformKind=RectTransform must create a GameObject whose transform is a RectTransform");
    }

    [Test]
    public void CreateObject_NoTransformKind_CreatesPlainTransform()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new CreateObject { LogicalId = "Plain", Name = "Plain" },
            },
        };

        PlanExecutor.Execute(plan, new IdentityMap(), scene);

        var plain = Find("Plain");
        Assert.IsNotNull(plain, "CreateObject did not create the GameObject");
        Assert.IsNotInstanceOf<RectTransform>(plain.transform,
            "CreateObject with no TransformKind must create a plain Transform, not a RectTransform");
    }

    [Test]
    public void SetFields_OnExistingRectTransform_ApplyFiveValuesInPlace_SameEntityId()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var createPlan = new Plan
        {
            Ops = new PlanOp[]
            {
                new CreateObject { LogicalId = "Panel", Name = "Panel", TransformKind = RectTransformFields.Kind },
            },
        };
        PlanExecutor.Execute(createPlan, new IdentityMap(), scene);
        var panel = Find("Panel");
        Assert.IsNotNull(panel, "Setup: CreateObject did not create Panel");

        // A saved scene is required: an unsaved scene degenerates every GlobalObjectId to "Null".
        EditorSceneManager.SaveScene(scene, ScenePath);
        var goid = GlobalObjectId.GetGlobalObjectIdSlow(panel).ToString();
        var entityIdBefore = panel.GetEntityId();

        var map = new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry { LogicalId = "Panel", GlobalObjectId = goid, Kind = "GameObject" },
            },
        };

        var anchoredPos = new Vec2(10f, -20f);
        var sizeDelta = new Vec2(200f, 120f);
        var anchorMin = new Vec2(0.1f, 0.2f);
        var anchorMax = new Vec2(0.9f, 0.8f);
        var pivot = new Vec2(0.25f, 0.75f);

        var writePlan = new Plan
        {
            Ops = FiveRectFields("Panel", anchoredPos, sizeDelta, anchorMin, anchorMax, pivot),
        };

        PlanExecutor.Execute(writePlan, map, scene);

        var rt = panel.transform as RectTransform;
        Assert.IsNotNull(rt, "Panel's transform is no longer a RectTransform after Execute");
        Assert.AreEqual(ToVector2(anchoredPos), rt.anchoredPosition, "anchoredPosition was not applied");
        Assert.AreEqual(ToVector2(sizeDelta), rt.sizeDelta, "sizeDelta was not applied");
        Assert.AreEqual(ToVector2(anchorMin), rt.anchorMin, "anchorMin was not applied");
        Assert.AreEqual(ToVector2(anchorMax), rt.anchorMax, "anchorMax was not applied");
        Assert.AreEqual(ToVector2(pivot), rt.pivot, "pivot was not applied");
        Assert.AreEqual(entityIdBefore, panel.GetEntityId(),
            "Writing rect fields to an existing mapped RectTransform destroyed and recreated the object");
    }

    [Test]
    public void SetField_SingleRectPath_ChangesOnlyThatField()
    {
        var scene = EditorSceneManager.GetActiveScene();
        // CreateObject and its SetField must be in ONE plan/ONE Execute call: ExecutionResult's
        // GameObjectsByLogicalId is local to a single Execute call, so a second call with an empty
        // IdentityMap can never resolve "Panel" and the write silently no-ops. This mirrors what the
        // real Materializer emits for create-with-payload (b2-t2 DATA_FLOW #1).
        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new CreateObject { LogicalId = "Panel", Name = "Panel", TransformKind = RectTransformFields.Kind },
                new SetField
                {
                    LogicalId = "Panel",
                    Path = RectTransformFields.SizeDeltaPath,
                    Value = new ValueNode.Vec2(new Vec2(300f, 40f)),
                },
            },
        };
        PlanExecutor.Execute(plan, new IdentityMap(), scene);

        var panel = Find("Panel");
        Assert.IsNotNull(panel, "Setup: CreateObject did not create Panel");
        var rt = panel.transform as RectTransform;
        Assert.IsNotNull(rt, "Panel's transform is no longer a RectTransform after Execute");
        Assert.AreEqual(new Vector2(300f, 40f), rt.sizeDelta, "sizeDelta was not applied by its own SetField");
        Assert.AreEqual(ToVector2(RectTransformFields.DefaultAnchoredPosition), rt.anchoredPosition,
            "anchoredPosition changed even though no SetField named it");
    }

    [Test]
    public void MappedPlainTransform_ReceivingRectFields_IsPromotedInPlace_WithLocatedNote()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var createPlan = new Plan
        {
            Ops = new PlanOp[]
            {
                new CreateObject { LogicalId = "Plain", Name = "Plain" },
            },
        };
        PlanExecutor.Execute(createPlan, new IdentityMap(), scene);
        var plain = Find("Plain");
        Assert.IsNotNull(plain, "Setup: CreateObject did not create Plain");
        Assert.IsNotInstanceOf<RectTransform>(plain.transform, "Setup: object must start with a plain Transform");

        EditorSceneManager.SaveScene(scene, ScenePath);
        var goidBefore = GlobalObjectId.GetGlobalObjectIdSlow(plain).ToString();
        var entityIdBefore = plain.GetEntityId();

        var map = new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry { LogicalId = "Plain", GlobalObjectId = goidBefore, Kind = "GameObject" },
            },
        };

        // Non-default values (G1): a bare-default RectTransform() model never emits a SetField at
        // all, so this task's promotion contract needs at least one authored non-default field.
        var writePlan = new Plan
        {
            Ops = FiveRectFields("Plain",
                new Vec2(5f, 6f), new Vec2(150f, 60f), new Vec2(0f, 0f), new Vec2(1f, 1f), new Vec2(0.5f, 0.5f)),
        };

        List<string> logs = null;
        PlanExecutor.ExecutionResult result = null;
        logs = CaptureLogs(() => result = PlanExecutor.Execute(writePlan, map, scene));

        var promoted = Find("Plain");
        Assert.IsNotNull(promoted, "Object was destroyed rather than promoted in place");
        Assert.IsInstanceOf<RectTransform>(promoted.transform,
            "A mapped plain-Transform node receiving rect fields must be promoted to a RectTransform in place");
        Assert.AreEqual(entityIdBefore, promoted.GetEntityId(),
            "Promotion must keep the SAME object instance id (no destroy+recreate)");
        Assert.AreEqual(goidBefore, GlobalObjectId.GetGlobalObjectIdSlow(promoted).ToString(),
            "Promotion must keep the SAME GlobalObjectId (no destroy+recreate)");

        var rt = (RectTransform)promoted.transform;
        Assert.AreEqual(new Vector2(5f, 6f), rt.anchoredPosition, "Authored anchoredPosition did not land after promotion");
        Assert.AreEqual(new Vector2(150f, 60f), rt.sizeDelta, "Authored sizeDelta did not land after promotion");

        Assert.IsTrue(logs.Any(m => m.Contains("NOTE on 'Plain'")),
            "Promoting a mapped plain Transform to a RectTransform must surface a located note naming the object, "
            + "not do it silently.\nLogs:\n" + string.Join("\n", logs));
    }

    [Test]
    public void RectTransformWrite_LogsNoUnhandledSetFieldWarning()
    {
        var scene = EditorSceneManager.GetActiveScene();
        // CreateObject and its SetFields must be in ONE plan/ONE Execute call: a second Execute call
        // with an empty IdentityMap can never resolve "Panel" (GameObjectsByLogicalId is local to a
        // single call), which previously made every assertion below pass vacuously against a no-op.
        var anchoredPos = new Vec2(3f, 4f);
        var ops = new List<PlanOp>
        {
            new CreateObject { LogicalId = "Panel", Name = "Panel", TransformKind = RectTransformFields.Kind },
            new SetField { LogicalId = "Panel", Path = "m_LocalPosition", Value = new ValueNode.Vec3(new Vec3(1f, 2f, 3f)) },
            new SetField { LogicalId = "Panel", Path = "m_LocalScale",    Value = new ValueNode.Vec3(new Vec3(2f, 2f, 2f)) },
            new SetField { LogicalId = "Panel", Path = "m_LocalRotation", Value = new ValueNode.Quat(Quat.Identity) },
        };
        ops.AddRange(FiveRectFields("Panel", anchoredPos, new Vec2(80f, 30f), new Vec2(0f, 0f), new Vec2(1f, 1f), new Vec2(0.5f, 0.5f)));

        var plan = new Plan { Ops = ops.ToArray() };

        GameObject panel = null;
        var logs = CaptureLogs(() =>
        {
            PlanExecutor.Execute(plan, new IdentityMap(), scene);
            panel = Find("Panel");
        });

        Assert.IsNotNull(panel, "Setup: CreateObject did not create Panel");
        Assert.IsFalse(logs.Any(m => m.Contains("Unhandled SetField")),
            "A rect SetField or a Transform SetField applied to a UI node must not be reported as unhandled.\nLogs:\n"
            + string.Join("\n", logs));

        var rt = panel.transform as RectTransform;
        Assert.IsNotNull(rt, "Panel's transform is no longer a RectTransform after Execute");
        Assert.AreEqual(3f, rt.localPosition.z, 1e-5f, "localPosition.z did not land on the RectTransform");
        Assert.AreEqual(new Vector3(2f, 2f, 2f), rt.localScale, "localScale did not land on the RectTransform");
        Assert.AreEqual(ToVector2(anchoredPos), rt.anchoredPosition,
            "anchoredPosition must hold the authored rect value, not be re-derived from localPosition x/y");
    }

    [Test]
    public void FreshRectTransform_MatchesRectTransformFieldsDefaultTable()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new CreateObject { LogicalId = "Panel", Name = "Panel", TransformKind = RectTransformFields.Kind },
            },
        };
        PlanExecutor.Execute(plan, new IdentityMap(), scene);

        var panel = Find("Panel");
        Assert.IsNotNull(panel, "Setup: CreateObject did not create Panel");
        var rt = panel.transform as RectTransform;
        Assert.IsNotNull(rt, "Setup: Panel's transform is not a RectTransform");

        Assert.AreEqual(ToVector2(RectTransformFields.DefaultAnchoredPosition), rt.anchoredPosition,
            "Live default anchoredPosition does not match RectTransformFields.DefaultAnchoredPosition");
        Assert.AreEqual(ToVector2(RectTransformFields.DefaultSizeDelta), rt.sizeDelta,
            "Live default sizeDelta does not match RectTransformFields.DefaultSizeDelta");
        Assert.AreEqual(ToVector2(RectTransformFields.DefaultAnchorMin), rt.anchorMin,
            "Live default anchorMin does not match RectTransformFields.DefaultAnchorMin");
        Assert.AreEqual(ToVector2(RectTransformFields.DefaultAnchorMax), rt.anchorMax,
            "Live default anchorMax does not match RectTransformFields.DefaultAnchorMax");
        Assert.AreEqual(ToVector2(RectTransformFields.DefaultPivot), rt.pivot,
            "Live default pivot does not match RectTransformFields.DefaultPivot");
    }
}
