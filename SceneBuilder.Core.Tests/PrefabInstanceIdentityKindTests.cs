using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // A scene NODE is an IdentityMap entry whose Kind is "GameObject" OR "PrefabInstance" — every
    // node-identity index must treat both the same way, and a "Component" entry must never appear
    // in either direction.
    public class PrefabInstanceIdentityKindTests
    {
        [Fact]
        public void GlobalObjectIdToLogicalId_IncludesPrefabInstanceEntries()
        {
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "tank-1", GlobalObjectId = "goid-tank", Kind = "PrefabInstance" },
                    new IdentityMapEntry { LogicalId = "root-1/Comp#0", GlobalObjectId = "goid-comp", Kind = "Component", ParentLogicalId = "root-1" },
                },
            };

            var index = IdentityNodeIndex.GlobalObjectIdToLogicalId(map);

            Assert.Equal("root-1", index["goid-root"]);
            Assert.Equal("tank-1", index["goid-tank"]);
            Assert.DoesNotContain("goid-comp", index.Keys);
        }

        [Fact]
        public void LogicalIdToGlobalObjectId_IncludesPrefabInstanceEntries()
        {
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "tank-1", GlobalObjectId = "goid-tank", Kind = "PrefabInstance" },
                    new IdentityMapEntry { LogicalId = "root-1/Comp#0", GlobalObjectId = "goid-comp", Kind = "Component", ParentLogicalId = "root-1" },
                },
            };

            var index = IdentityNodeIndex.LogicalIdToGlobalObjectId(map);

            Assert.Equal("goid-root", index["root-1"]);
            Assert.Equal("goid-tank", index["tank-1"]);
            Assert.DoesNotContain("root-1/Comp#0", index.Keys);
        }

        // Pins the GroupBy/First shape the shared builder must inherit — routing
        // ObjectReferenceResolver through it must not start throwing on a duplicate goid.
        [Fact]
        public void GlobalObjectIdToLogicalId_DuplicateGoid_FirstWins()
        {
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "first-1", GlobalObjectId = "goid-dup", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "second-1", GlobalObjectId = "goid-dup", Kind = "PrefabInstance" },
                },
            };

            var index = IdentityNodeIndex.GlobalObjectIdToLogicalId(map);

            Assert.Equal("first-1", index["goid-dup"]);
        }

        // A component field newly pointing at a live, mapped PrefabInstance root — one that is NOT
        // itself authored anywhere in source and carries no handle, so only the liveness set can
        // resolve it — must classify as resolvable, never DanglingReference. The target is
        // unauthored on purpose: an authored root or a handle would resolve it through a DIFFERENT
        // set (modelByLogicalId / handles) and mask a liveness-set bug limited to one node kind.
        [Fact]
        public void Reconcile_ObjectRefToLiveMappedPrefabInstance_IsNotDangling()
        {
            const string ownerLogicalId = "root-1";
            const string componentTypeFullName = "Game.DoorOpener";
            const string fieldKey = "target";
            const string instanceLogicalId = "tank-1";
            var componentLogicalId = $"{ownerLogicalId}/{componentTypeFullName}#0";

            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new GameObjectNode
                    {
                        LogicalId = ownerLogicalId,
                        Name = "Root",
                        Components = new[]
                        {
                            new ComponentData
                            {
                                LogicalId = componentLogicalId,
                                Type = new TypeRef(componentTypeFullName),
                                Fields = new FieldMap(new[]
                                {
                                    new KeyValuePair<string, ValueNode>(fieldKey, new ValueNode.ObjectRef(null)),
                                }),
                            },
                        },
                    },
                },
            };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = ownerLogicalId, GlobalObjectId = "goid-root", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = instanceLogicalId, GlobalObjectId = "goid-tank", Kind = "PrefabInstance" },
                },
            };

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-root",
                        Name = "Root",
                        Components = new[]
                        {
                            new ComponentData
                            {
                                LogicalId = "unused",
                                Type = new TypeRef(componentTypeFullName),
                                Fields = new FieldMap(new[]
                                {
                                    new KeyValuePair<string, ValueNode>(fieldKey, new ValueNode.ObjectRef(instanceLogicalId)),
                                }),
                            },
                        },
                    },
                    new SnapshotNode { GlobalObjectId = "goid-tank", Name = "Tank" },
                },
            };

            var fieldSpans = new Dictionary<string, IReadOnlyDictionary<string, SourceSpan>>
            {
                [componentLogicalId] = new Dictionary<string, SourceSpan> { [fieldKey] = new SourceSpan(0, 10) },
            };

            var result = Reconciler.Reconcile(
                model, snapshot, map, null, null, null, null, fieldSpans);

            Assert.DoesNotContain(result.Conflicts, c => c.Kind == ConflictKind.DanglingReference);
        }
    }
}
