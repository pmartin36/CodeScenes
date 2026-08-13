using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using SceneBuilder.Editor;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Plan;

// The adapter WRITE path for a [SerializeReference] polymorphic field (specs/10-m9-serializereference.md):
// PlanExecutor applies a SetManagedReference op to a live component's field, observed back through a
// fresh SerializedObject. Instantiate, whole-instance type-change replace (no stale prior-type fields),
// clear-to-null, nested recursion, idempotent re-apply, and a no-construction-path type left unchanged
// with a located error surfaced.
public class ManagedReferenceWriteTests
{
    [SetUp]
    public void SetUp()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    private static AiBrain BuildBrain(out string logicalId)
    {
        logicalId = "Host/AiBrain#0";
        var go = new GameObject("Host");
        return go.AddComponent<AiBrain>();
    }

    private static Plan BrainPlan(string logicalId, PlanOp op) => new()
    {
        Ops = new[]
        {
            new CreateObject { LogicalId = "Host", Name = "Host" },
            new AddComponent { LogicalId = logicalId, Type = new TypeRef("AiBrain") },
            op,
        },
    };

    private static FieldMap Fields(params (string key, ValueNode value)[] entries) =>
        new(System.Array.ConvertAll(entries,
            e => new System.Collections.Generic.KeyValuePair<string, ValueNode>(e.key, e.value)));

    [Test]
    public void SetManagedReference_ConcreteInstance_WritesTypeAndFields()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var plan = BrainPlan("Host/AiBrain#0", new SetManagedReference
        {
            LogicalId = "Host/AiBrain#0",
            Path = "strategy",
            ConcreteType = new TypeRef("Aggressive"),
            Fields = Fields(("range", ValueNode.Primitive.Float(5f))),
        });

        PlanExecutor.Execute(plan, new IdentityMap(), scene);

        var brain = GameObject.Find("Host").GetComponent<AiBrain>();
        var strategy = new SerializedObject(brain).FindProperty("strategy");
        Assert.IsNotNull(strategy.managedReferenceValue, "strategy must not be None after applying a concrete instance");
        Assert.IsInstanceOf<Aggressive>(strategy.managedReferenceValue);
        Assert.AreEqual(5f, ((Aggressive)strategy.managedReferenceValue).range);
    }

    [Test]
    public void SetManagedReference_TypeChangeOverExistingInstance_ReplacesWholeInstanceNoStaleFields()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var brain = BuildBrain(out var logicalId);
        var so = new SerializedObject(brain);
        so.FindProperty("strategy").managedReferenceValue = new Aggressive { range = 5f };
        so.ApplyModifiedPropertiesWithoutUndo();

        var goid = GlobalObjectId.GetGlobalObjectIdSlow(brain).ToString();
        var map = new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry
                {
                    LogicalId = logicalId, GlobalObjectId = goid, Kind = "Component",
                    ComponentType = "AiBrain", ParentLogicalId = "Host",
                },
            },
        };
        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new SetManagedReference
                {
                    LogicalId = logicalId,
                    Path = "strategy",
                    ConcreteType = new TypeRef("Flee"),
                    Fields = Fields(("speed", ValueNode.Primitive.Float(3f))),
                },
            },
        };

        PlanExecutor.Execute(plan, map, scene);

        var strategy = new SerializedObject(brain).FindProperty("strategy");
        Assert.IsInstanceOf<Flee>(strategy.managedReferenceValue,
            $"expected a whole-instance replace to Flee, got {strategy.managedReferenceValue?.GetType().Name}");
        Assert.AreEqual(3f, ((Flee)strategy.managedReferenceValue).speed);
    }

    [Test]
    public void SetManagedReference_NullConcreteType_ClearsFieldToNone()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var brain = BuildBrain(out var logicalId);
        var so = new SerializedObject(brain);
        so.FindProperty("strategy").managedReferenceValue = new Aggressive { range = 5f };
        so.ApplyModifiedPropertiesWithoutUndo();

        var goid = GlobalObjectId.GetGlobalObjectIdSlow(brain).ToString();
        var map = new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry
                {
                    LogicalId = logicalId, GlobalObjectId = goid, Kind = "Component",
                    ComponentType = "AiBrain", ParentLogicalId = "Host",
                },
            },
        };
        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new SetManagedReference
                {
                    LogicalId = logicalId,
                    Path = "strategy",
                    ConcreteType = null,
                    Fields = FieldMap.Empty,
                },
            },
        };

        PlanExecutor.Execute(plan, map, scene);

        var strategy = new SerializedObject(brain).FindProperty("strategy");
        Assert.IsNull(strategy.managedReferenceValue, "a null ConcreteType must clear the field to None");
    }

    [Test]
    public void SetManagedReference_NestedComposite_RecursesIntoPrimaryAndFallback()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var plan = BrainPlan("Host/AiBrain#0", new SetManagedReference
        {
            LogicalId = "Host/AiBrain#0",
            Path = "strategy",
            ConcreteType = new TypeRef("Composite"),
            Fields = Fields(
                ("primary", new ValueNode.ManagedReference(new TypeRef("Aggressive"),
                    Fields(("range", ValueNode.Primitive.Float(5f))))),
                ("fallback", new ValueNode.ManagedReference(new TypeRef("Flee"),
                    Fields(("speed", ValueNode.Primitive.Float(3f)))))),
        });

        PlanExecutor.Execute(plan, new IdentityMap(), scene);

        var brain = GameObject.Find("Host").GetComponent<AiBrain>();
        var strategyValue = new SerializedObject(brain).FindProperty("strategy").managedReferenceValue;
        Assert.IsInstanceOf<Composite>(strategyValue,
            $"outer strategy must be a Composite, got {strategyValue?.GetType().Name ?? "null"}");
        var composite = (Composite)strategyValue;
        Assert.IsInstanceOf<Aggressive>(composite.primary);
        Assert.AreEqual(5f, ((Aggressive)composite.primary).range);
        Assert.IsInstanceOf<Flee>(composite.fallback);
        Assert.AreEqual(3f, ((Flee)composite.fallback).speed);
    }

    [Test]
    public void SetManagedReference_ReapplyEqualPlan_IsIdempotent()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var brain = BuildBrain(out var logicalId);
        var goid = GlobalObjectId.GetGlobalObjectIdSlow(brain).ToString();
        var map = new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry
                {
                    LogicalId = logicalId, GlobalObjectId = goid, Kind = "Component",
                    ComponentType = "AiBrain", ParentLogicalId = "Host",
                },
            },
        };
        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                new SetManagedReference
                {
                    LogicalId = logicalId,
                    Path = "strategy",
                    ConcreteType = new TypeRef("Aggressive"),
                    Fields = Fields(("range", ValueNode.Primitive.Float(5f))),
                },
            },
        };

        PlanExecutor.Execute(plan, map, scene);
        PlanExecutor.Execute(plan, map, scene);

        var strategy = new SerializedObject(brain).FindProperty("strategy");
        Assert.IsInstanceOf<Aggressive>(strategy.managedReferenceValue);
        Assert.AreEqual(5f, ((Aggressive)strategy.managedReferenceValue).range,
            "re-applying an equal plan must leave the field in the same state, not throw or corrupt it");
    }

    [Test]
    public void SetManagedReference_NoConstructionPathType_LeavesFieldUnchangedAndLogsLocatedError()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var brain = BuildBrain(out var logicalId);
        var goid = GlobalObjectId.GetGlobalObjectIdSlow(brain).ToString();
        var map = new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry
                {
                    LogicalId = logicalId, GlobalObjectId = goid, Kind = "Component",
                    ComponentType = "AiBrain", ParentLogicalId = "Host",
                },
            },
        };
        var plan = new Plan
        {
            Ops = new PlanOp[]
            {
                // IStrategy is an interface: no viable construction path.
                new SetManagedReference
                {
                    LogicalId = logicalId,
                    Path = "strategy",
                    ConcreteType = new TypeRef("IStrategy"),
                    Fields = FieldMap.Empty,
                },
            },
        };

        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("IStrategy"));
        PlanExecutor.Execute(plan, map, scene);

        var strategy = new SerializedObject(brain).FindProperty("strategy");
        Assert.IsNull(strategy.managedReferenceValue,
            "a type with no viable construction path must leave the field unchanged (still None), not half-built");
    }
}
