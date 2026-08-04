using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Materialize;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Plan;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // An authored ObjectRef(null) (NodeHandle.None) lowers to exactly one clearing SetReference
    // against a live component whose read could not represent the assigned reference it is
    // replacing, so the field is ABSENT from live Fields rather than holding a resolvable value.
    // Absence must never be read as "at the type default" for a reference field.
    public class ObjectRefClearTests
    {
        private const string DoorOpenerKey = "target";
        private const string ButtonKey = "m_TargetGraphic";

        private static IdentityMap SingleGameObjectMap() =>
            new()
            {
                Scene = "Assets/Scenes/Demo.unity",
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-a", GlobalObjectId = "goid-a", Kind = "GameObject" },
                },
            };

        private static Plan.Plan MaterializeClear(
            TypeRef componentType,
            string fieldKey,
            FieldMap liveFields,
            ComponentData[]? componentDefaults = null)
        {
            var desiredFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(fieldKey, new ValueNode.ObjectRef(null)),
            });
            var desiredComponent = new ComponentData { LogicalId = "comp-1", Type = componentType, Fields = desiredFields };
            var root = new GameObjectNode { LogicalId = "root-a", Name = "Root", Components = new[] { desiredComponent } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var actualComponent = new ComponentData { Type = componentType, Fields = liveFields };
            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-a", Name = "Root", Components = new[] { actualComponent } };
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { snapshotRoot },
                ComponentDefaults = componentDefaults ?? Array.Empty<ComponentData>(),
            };

            return Materializer.Materialize(model, snapshot, SingleGameObjectMap());
        }

        private static void AssertExactlyOneClearingSetReference(Plan.Plan plan, string fieldKey)
        {
            var op = Assert.Single(plan.Ops.OfType<SetReference>(), o => o.LogicalId == "comp-1" && o.Path == fieldKey);
            Assert.True(string.IsNullOrEmpty(op.TargetLogicalId));
            Assert.DoesNotContain(plan.Ops.OfType<SetField>(), sf => sf.LogicalId == "comp-1" && sf.Path == fieldKey);
            Assert.Single(plan.Ops);
        }

        [Fact]
        public void Clear_NativePPtrField_FieldPrunedFromLiveFields_EmitsOneClearingSetReference()
        {
            var buttonType = new TypeRef("UnityEngine.UI.Button");
            var componentDefaults = new[]
            {
                new ComponentData
                {
                    Type = buttonType,
                    Fields = new FieldMap(new[]
                    {
                        new KeyValuePair<string, ValueNode>(ButtonKey, new ValueNode.ObjectRef(null)),
                    }),
                },
            };

            var plan = MaterializeClear(buttonType, ButtonKey, FieldMap.Empty, componentDefaults);

            AssertExactlyOneClearingSetReference(plan, ButtonKey);
        }

        [Fact]
        public void Clear_MonoBehaviourGameObjectField_FieldPrunedFromLiveFields_EmitsOneClearingSetReference()
        {
            var doorOpenerType = new TypeRef("Game.DoorOpener");
            var componentDefaults = new[]
            {
                new ComponentData
                {
                    Type = doorOpenerType,
                    Fields = new FieldMap(new[]
                    {
                        new KeyValuePair<string, ValueNode>(DoorOpenerKey, new ValueNode.ObjectRef(null)),
                    }),
                },
            };

            var plan = MaterializeClear(doorOpenerType, DoorOpenerKey, FieldMap.Empty, componentDefaults);

            AssertExactlyOneClearingSetReference(plan, DoorOpenerKey);
        }

        [Fact]
        public void Clear_LiveValueAssignedObjectRef_Button_EmitsOneClearingSetReference()
        {
            var buttonType = new TypeRef("UnityEngine.UI.Button");
            var liveFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(ButtonKey, new ValueNode.ObjectRef("lid-image")),
            });

            var plan = MaterializeClear(buttonType, ButtonKey, liveFields);

            AssertExactlyOneClearingSetReference(plan, ButtonKey);
        }

        [Fact]
        public void Clear_LiveValueUnsupportedObjectReference_EmitsOneClearingSetReference()
        {
            var doorOpenerType = new TypeRef("Game.DoorOpener");
            var liveFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(DoorOpenerKey, new ValueNode.Unsupported("ObjectReference")),
            });

            var plan = MaterializeClear(doorOpenerType, DoorOpenerKey, liveFields);

            AssertExactlyOneClearingSetReference(plan, DoorOpenerKey);
        }

        [Fact]
        public void Clear_NoComponentDefaultsEntry_EmitsOneClearingSetReference()
        {
            var doorOpenerType = new TypeRef("Game.DoorOpener");

            var plan = MaterializeClear(doorOpenerType, DoorOpenerKey, FieldMap.Empty);

            AssertExactlyOneClearingSetReference(plan, DoorOpenerKey);
        }

        [Fact]
        public void Clear_LiveValueAlreadyNull_EmitsZeroOps()
        {
            var doorOpenerType = new TypeRef("Game.DoorOpener");
            var liveFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(DoorOpenerKey, new ValueNode.ObjectRef(null)),
            });
            var componentDefaults = new[]
            {
                new ComponentData
                {
                    Type = doorOpenerType,
                    Fields = new FieldMap(new[]
                    {
                        new KeyValuePair<string, ValueNode>(DoorOpenerKey, new ValueNode.ObjectRef(null)),
                    }),
                },
            };

            var plan = MaterializeClear(doorOpenerType, DoorOpenerKey, liveFields, componentDefaults);

            Assert.Empty(plan.Ops);
        }

        [Fact]
        public void Parse_BothSpellings_LowerToTheSameObjectRefNull()
        {
            const string source = @"
using UnityEngine;
using SceneBuilder.Authoring;

public class DoorOpener : MonoBehaviour { public GameObject target; }

public class FooScene : ISceneDefinition {
    public void Build(SceneRoot scene) {
        scene.Add(""StringSpelling"")
             .Component<DoorOpener>(c => c.Set(""m_TargetGraphic"", NodeHandle.None));

        scene.Add(""TypedSpelling"")
             .Component<DoorOpener>(c => c.Set(x => x.target, NodeHandle.None));
    }
}
";
            var result = BuilderParser.Parse(source);

            var stringSpelled = Assert.Single(result.Model.Roots, r => r.Name == "StringSpelling");
            var typedSpelled = Assert.Single(result.Model.Roots, r => r.Name == "TypedSpelling");

            var stringField = Assert.IsType<ValueNode.ObjectRef>(
                Assert.Single(stringSpelled.Components).Fields["m_TargetGraphic"]);
            var typedField = Assert.IsType<ValueNode.ObjectRef>(
                Assert.Single(typedSpelled.Components).Fields["member:target"]);

            Assert.Null(stringField.TargetLogicalId);
            Assert.Null(typedField.TargetLogicalId);
            Assert.Equal(stringField, typedField);
        }
    }
}
