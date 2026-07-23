using SceneBuilder.Core.Lowering;
using SceneBuilder.Core.Model;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b1-t3: OverrideMapper.ToOverrides/ToRecords are inverse, per-entry lossless (incl.
    // null vs "" Value), pass ObjectReference through symmetrically (AssetRef/ObjectRef),
    // and never collapse entries that share SubKey but differ in ComponentType (full pair-key).
    public class OverrideMapperTests
    {
        [Fact]
        public void RoundTrip_ValueMod_ByteStable()
        {
            var record = new ModificationRecord
            {
                Target = new OverrideTarget { ComponentType = "prefab-a", SubKey = new PrefabInstanceKey { TargetObjectId = 100 } },
                PropertyPath = "health",
                Value = "50",
                ObjectReference = null,
            };

            var overrides = OverrideMapper.ToOverrides(new[] { record });
            var single = Assert.Single(overrides);
            Assert.Equal(record.Target, single.Target);
            Assert.Equal(record.PropertyPath, single.PropertyPath);
            Assert.Equal(new ValueNode.Primitive(PrimitiveKind.String, "50"), single.Value);
            Assert.Null(single.ObjectReference);

            var roundTripped = OverrideMapper.ToRecords(overrides);
            Assert.Equal(new[] { record }, roundTripped);
        }

        [Fact]
        public void RoundTrip_NullAndEmptyValue_PreservedDistinctly()
        {
            var nullRecord = new ModificationRecord
            {
                Target = new OverrideTarget { ComponentType = "prefab-a", SubKey = new PrefabInstanceKey { TargetObjectId = 1 } },
                PropertyPath = "field",
                Value = null,
            };
            var emptyRecord = nullRecord with { Value = "" };

            var nullBack = Assert.Single(OverrideMapper.ToRecords(OverrideMapper.ToOverrides(new[] { nullRecord })));
            var emptyBack = Assert.Single(OverrideMapper.ToRecords(OverrideMapper.ToOverrides(new[] { emptyRecord })));

            Assert.Null(nullBack.Value);
            Assert.Equal("", emptyBack.Value);
            Assert.NotEqual(nullBack.Value, emptyBack.Value);
        }

        [Fact]
        public void ObjectReference_AssetRef_PassesThroughSymmetrically()
        {
            var assetRef = new ValueNode.AssetRef(new AssetRef { Guid = "guid-1", FileId = 0, IsBuiltin = false });
            var record = new ModificationRecord
            {
                Target = new OverrideTarget { ComponentType = "prefab-a", SubKey = new PrefabInstanceKey { TargetObjectId = 5 } },
                PropertyPath = "sharedMaterial",
                Value = "",
                ObjectReference = assetRef,
            };

            var overrides = OverrideMapper.ToOverrides(new[] { record });
            var single = Assert.Single(overrides);
            Assert.Equal(assetRef, single.ObjectReference);

            var roundTripped = Assert.Single(OverrideMapper.ToRecords(overrides));
            Assert.Equal(record, roundTripped);
        }

        [Fact]
        public void ObjectReference_ObjectRef_PassesThroughSymmetrically()
        {
            var objectRef = new ValueNode.ObjectRef("some-logical-id");
            var record = new ModificationRecord
            {
                Target = new OverrideTarget { ComponentType = "prefab-a", SubKey = new PrefabInstanceKey { TargetObjectId = 6 } },
                PropertyPath = "target",
                Value = "",
                ObjectReference = objectRef,
            };

            var overrides = OverrideMapper.ToOverrides(new[] { record });
            var single = Assert.Single(overrides);
            Assert.Equal(objectRef, single.ObjectReference);

            var roundTripped = Assert.Single(OverrideMapper.ToRecords(overrides));
            Assert.Equal(record, roundTripped);
        }

        [Fact]
        public void PairKey_SameSubKey_DifferentComponentType_DistinctEntries()
        {
            var recordA = new ModificationRecord
            {
                Target = new OverrideTarget { ComponentType = "prefab-a", SubKey = new PrefabInstanceKey { TargetObjectId = 42 } },
                PropertyPath = "health",
                Value = "10",
            };
            var recordB = new ModificationRecord
            {
                Target = new OverrideTarget { ComponentType = "prefab-b", SubKey = new PrefabInstanceKey { TargetObjectId = 42 } },
                PropertyPath = "health",
                Value = "20",
            };

            var overrides = OverrideMapper.ToOverrides(new[] { recordA, recordB });

            Assert.Equal(2, overrides.Length);
            Assert.NotEqual(overrides[0].Target, overrides[1].Target);
            Assert.NotEqual(overrides[0], overrides[1]);

            // A SubKey-only key would collapse both entries; ComponentType participates too.
            Assert.NotEqual(overrides[0].Target.ComponentType, overrides[1].Target.ComponentType);
            Assert.Equal(overrides[0].Target.SubKey, overrides[1].Target.SubKey);
        }

        [Fact]
        public void Equality_SameSubKeyAndComponentType_DifferentChildPath_AreEqual_ShareHashCode()
        {
            var subKey = new PrefabInstanceKey { TargetPrefabId = 111, TargetObjectId = 222 };
            var a = new OverrideTarget { SubKey = subKey, ComponentType = "UnityEngine.BoxCollider", ChildPath = "Turret" };
            var b = new OverrideTarget { SubKey = subKey, ComponentType = "UnityEngine.BoxCollider", ChildPath = "Turret/Barrel" };

            Assert.Equal(a, b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());

            var differentType = a with { ComponentType = "UnityEngine.Light" };
            Assert.NotEqual(a, differentType);
        }
    }
}
