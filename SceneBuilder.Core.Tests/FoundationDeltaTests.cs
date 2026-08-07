using System.Collections.Generic;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;
using SceneBuilder.Core.Serialization;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Foundation contracts UnityEvent syncing depends on: a ValueNode.Primitive that survives a
    // JSON round trip, MemberSpellingIndex's lookup rule, the one guarded factory for a listener
    // that cannot be synced, and the component-LogicalId ordinal-within-type rule.
    public class FoundationDeltaTests
    {
        // A listener argument deserialized from plan JSON must project without throwing -- the
        // shape a write-then-apply round trip produces.
        [Theory]
        [InlineData(PrimitiveKind.Int)]
        [InlineData(PrimitiveKind.Float)]
        [InlineData(PrimitiveKind.String)]
        [InlineData(PrimitiveKind.Bool)]
        public void UnityEventProjection_ToPersistentCall_OnADeserializedListener_DoesNotThrow(PrimitiveKind kind)
        {
            var argValue = kind switch
            {
                PrimitiveKind.Int => ValueNode.Primitive.Int(7),
                PrimitiveKind.Float => ValueNode.Primitive.Float(1.5f),
                PrimitiveKind.String => ValueNode.Primitive.String("hi"),
                PrimitiveKind.Bool => ValueNode.Primitive.Bool(true),
                _ => throw new System.ArgumentOutOfRangeException(nameof(kind)),
            };
            var argMode = kind switch
            {
                PrimitiveKind.Int => ListenerArgMode.Int,
                PrimitiveKind.Float => ListenerArgMode.Float,
                PrimitiveKind.String => ListenerArgMode.String,
                PrimitiveKind.Bool => ListenerArgMode.Bool,
                _ => throw new System.ArgumentOutOfRangeException(nameof(kind)),
            };

            var listeners = new ValueNode.UnityEventListeners(new[]
            {
                new UnityEventListener(
                    target: new ValueNode.ObjectRef("door"),
                    methodName: "Handle",
                    argMode: argMode,
                    argValue: argValue),
            });

            var json = CanonicalJson.Serialize<ValueNode>(listeners);
            var roundTripped = (ValueNode.UnityEventListeners)CanonicalJson.Deserialize<ValueNode>(json)!;

            var call = UnityEventProjection.ToPersistentCall(roundTripped.Listeners[0]);

            switch (kind)
            {
                case PrimitiveKind.Int: Assert.Equal(7, call.IntArgument); break;
                case PrimitiveKind.Float: Assert.Equal(1.5f, call.FloatArgument); break;
                case PrimitiveKind.String: Assert.Equal("hi", call.StringArgument); break;
                case PrimitiveKind.Bool: Assert.True(call.BoolArgument); break;
            }
        }

        // The shipped equality guarantee must survive normalizing Value at construction rather
        // than only inside Equals/GetHashCode.
        [Fact]
        public void Primitive_Equality_StillHoldsAcrossAJsonBoundary()
        {
            var original = ValueNode.Primitive.Int(42);
            var json = CanonicalJson.Serialize<ValueNode>(original);
            var roundTripped = CanonicalJson.Deserialize<ValueNode>(json);

            Assert.Equal(original, roundTripped);
        }

        // ---- SceneSnapshot / ComponentData canonical-JSON contract -------------------------

        // A component GlobalObjectId and a snapshot-level MemberSpelling both survive the
        // canonical-JSON round trip byte-for-byte, and neither collapses into the other value on
        // the object graph.
        [Fact]
        public void SceneSnapshot_CarryingAComponentGoidAndAMemberSpelling_RoundTripsThroughCanonicalJsonByteEqual()
        {
            const string nodeGoid = "GlobalObjectId_V1-2-node000000000000000000000000000000-1-0";
            const string componentGoid = "GlobalObjectId_V1-2-comp111111111111111111111111111-1-0";

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        Name = "Root",
                        GlobalObjectId = nodeGoid,
                        Components = new[]
                        {
                            new ComponentData
                            {
                                LogicalId = "Root/UnityEngine.UI.Button#0",
                                Type = new TypeRef("UnityEngine.UI.Button"),
                                Fields = new FieldMap(new[]
                                {
                                    new KeyValuePair<string, ValueNode>("m_Interactable", ValueNode.Primitive.Bool(true)),
                                }),
                                GlobalObjectId = componentGoid,
                            },
                        },
                    },
                },
                MemberSpellings = new[]
                {
                    new MemberSpelling
                    {
                        Type = new TypeRef("UnityEngine.UI.Slider"),
                        SerializedPath = "m_OnValueChanged",
                        PublicName = "onValueChanged",
                    },
                },
            };

            var json = CanonicalJson.Serialize(snapshot);
            var back = CanonicalJson.Deserialize<SceneSnapshot>(json);
            var reserialized = CanonicalJson.Serialize(back);

            Assert.Equal(json, reserialized);
            Assert.Equal(componentGoid, back.Roots[0].Components[0].GlobalObjectId);
            Assert.NotEqual(back.Roots[0].GlobalObjectId, back.Roots[0].Components[0].GlobalObjectId);
            Assert.Equal("onValueChanged", back.MemberSpellings[0].PublicName);
            Assert.Equal("UnityEngine.UI.Slider", back.MemberSpellings[0].Type.FullName);
            Assert.Equal("m_OnValueChanged", back.MemberSpellings[0].SerializedPath);
        }

        // ---- MemberSpellingIndex ------------------------------------------------------------

        [Fact]
        public void MemberSpellingIndex_TryGet_HitAndMiss()
        {
            var index = MemberSpellingIndex.Build(new[]
            {
                new MemberSpelling
                {
                    Type = new TypeRef("UnityEngine.UI.Slider"),
                    SerializedPath = "m_OnValueChanged",
                    PublicName = "onValueChanged",
                },
            });

            Assert.True(index.TryGet("UnityEngine.UI.Slider", "m_OnValueChanged", out var hit));
            Assert.Equal("onValueChanged", hit);

            Assert.False(index.TryGet("UnityEngine.UI.Slider", "m_OnClick", out _));
            Assert.False(index.TryGet("UnityEngine.UI.Button", "m_OnValueChanged", out _));
        }

        [Fact]
        public void MemberSpellingIndex_Build_FirstWinsOnADuplicateTypeAndPath()
        {
            var index = MemberSpellingIndex.Build(new[]
            {
                new MemberSpelling { Type = new TypeRef("T"), SerializedPath = "m_X", PublicName = "first" },
                new MemberSpelling { Type = new TypeRef("T"), SerializedPath = "m_X", PublicName = "second" },
            });

            Assert.True(index.TryGet("T", "m_X", out var name));
            Assert.Equal("first", name);
        }

        [Fact]
        public void MemberSpellingIndex_Empty_AnswersMissForEverything()
        {
            Assert.False(MemberSpellingIndex.Empty.TryGet("T", "m_X", out _));
        }

        // ---- Conflict.UnsyncableListener -----------------------------------------------------

        [Fact]
        public void Conflict_DirectConstructionOfUnsyncableListener_Throws()
        {
            Assert.Throws<System.ArgumentException>(
                () => new Conflict { Kind = ConflictKind.UnsyncableListener });
        }

        [Fact]
        public void Conflict_UnsyncableListener_CarriesLocatedDataAndANonNullRecurrenceKey()
        {
            var conflict = Conflict.UnsyncableListener(
                "door/UnityEngine.UI.Button#0", "UnityEngine.UI.Button", "m_OnClick", 0,
                ListenerReportReason.EmptyMethodName);

            Assert.Equal(ConflictKind.UnsyncableListener, conflict.Kind);
            Assert.NotNull(conflict.Located);
            Assert.Equal("door/UnityEngine.UI.Button#0", conflict.Located!.LogicalId);
            Assert.Equal("UnityEngine.UI.Button", conflict.Located.ComponentTypeFullName);
            Assert.Equal("m_OnClick", conflict.Located.MemberKey);
            Assert.NotNull(conflict.RecurrenceKey);
        }

        [Fact]
        public void Conflict_UnsyncableListener_SameInputsFromEitherDirection_ProduceAnIdenticalRecurrenceKey()
        {
            var fromWrite = Conflict.UnsyncableListener(
                "door/UnityEngine.UI.Button#0", "UnityEngine.UI.Button", "m_OnClick", 0,
                ListenerReportReason.UnresolvedTarget);
            var fromRead = Conflict.UnsyncableListener(
                "door/UnityEngine.UI.Button#0", "UnityEngine.UI.Button", "m_OnClick", 0,
                ListenerReportReason.UnresolvedTarget);

            Assert.Equal(fromWrite.RecurrenceKey, fromRead.RecurrenceKey);
        }

        [Fact]
        public void Conflict_UnsyncableListener_DifferentReasons_ProduceDifferentRecurrenceKeys()
        {
            var emptyMethod = Conflict.UnsyncableListener(
                "door/UnityEngine.UI.Button#0", "UnityEngine.UI.Button", "m_OnClick", 0,
                ListenerReportReason.EmptyMethodName);
            var unresolvedTarget = Conflict.UnsyncableListener(
                "door/UnityEngine.UI.Button#0", "UnityEngine.UI.Button", "m_OnClick", 0,
                ListenerReportReason.UnresolvedTarget);

            Assert.NotEqual(emptyMethod.RecurrenceKey, unresolvedTarget.RecurrenceKey);
        }

        // ---- component-LogicalId ordinal-within-type -----------------------------------------

        // A snapshot-only AddedGameObjects child's component anchors count occurrences of the
        // TYPE within the node's component array, not the array index: the second component
        // (index 1, UnityEngine.Rigidbody) does not consume an ordinal for BoxCollider, so the
        // first BoxCollider (index 2) anchors on #0 and the second (index 3) on #1.
        [Fact]
        public void AddedChildComponents_OrdinalIsWithinType_NotTheArrayIndex()
        {
            var unrepresentable = new ValueNode.Nested(
                "UnityEngine.PhysicMaterial",
                new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>(
                        "m_Ref",
                        new ValueNode.AssetRef(new AssetRef { Guid = "guid-mat", DisplayPath = "Assets/M.physicMaterial" })),
                }));

            ComponentData BoxCollider() => new ComponentData
            {
                Type = new TypeRef("UnityEngine.BoxCollider"),
                Fields = new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>("m_Material", unrepresentable),
                }),
            };

            var added = new AddedGameObject
            {
                Parent = new OverrideTarget { ChildPath = "Turret" },
                Node = new GameObjectNode
                {
                    Name = "MuzzleFlash",
                    Components = new[]
                    {
                        new ComponentData { Type = new TypeRef("UnityEngine.Rigidbody") },
                        BoxCollider(),
                        BoxCollider(),
                    },
                },
            };

            var instance = new PrefabInstanceNode
            {
                LogicalId = "enemy",
                Name = "Enemy",
                SourcePrefab = new AssetRef { Guid = "guid-enemy-prefab", DisplayPath = "Assets/Prefabs/Enemy.prefab" },
            };
            var snapshotInstance = new SnapshotNode
            {
                GlobalObjectId = "goid-enemy",
                Name = "Enemy",
                SourcePrefabGuid = "guid-enemy-prefab",
                AddedGameObjects = new[] { added },
            };
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "enemy", GlobalObjectId = "goid-enemy", Kind = "PrefabInstance", SourcePrefabGuid = "guid-enemy-prefab" },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            Assert.Empty(result.Conflicts);
            var anchors = new List<string>();
            foreach (var note in result.Notes)
            {
                anchors.Add(note.Located!.LogicalId);
            }
            Assert.Equal(new[] { "enemy/UnityEngine.BoxCollider#0", "enemy/UnityEngine.BoxCollider#1" }, anchors);
        }
    }
}
