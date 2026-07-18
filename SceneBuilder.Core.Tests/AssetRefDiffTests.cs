using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Diff;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b2-t3: Diff compares asset-ref fields on (Guid,FileId,IsBuiltin) only, incl. None
    // semantics and built-in refs. No new production code is expected — this rides on
    // AssetRef.Equals (b1-t1) flowing through Differ.EmitComponentEdits' `actualValue !=
    // field.Value` comparison.
    public class AssetRefDiffTests
    {
        private const string FieldKey = "sharedMaterial";

        private static (SceneModel Model, SceneSnapshot Snapshot, IdentityMap Map) BuildScene(ValueNode desiredValue, ValueNode actualValue)
        {
            var desiredComponent = new ComponentData
            {
                LogicalId = "root-1/UnityEngine.MeshRenderer#0",
                Type = new TypeRef("UnityEngine.MeshRenderer"),
                Fields = new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>(FieldKey, desiredValue),
                }),
            };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Components = new[] { desiredComponent } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var actualComponent = new ComponentData
            {
                Type = new TypeRef("UnityEngine.MeshRenderer"),
                Fields = new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>(FieldKey, actualValue),
                }),
            };
            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root", Components = new[] { actualComponent } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                },
            };

            return (model, snapshot, map);
        }

        [Fact]
        public void Diff_AssetRefSameGuidDifferentDisplayPath_NoChange()
        {
            var desired = new ValueNode.AssetRef(new AssetRef { Guid = "abc123", FileId = 0, DisplayPath = "Assets/Materials/Red.mat", TypeHint = "Material" });
            var actual = new ValueNode.AssetRef(new AssetRef { Guid = "abc123", FileId = 0, DisplayPath = "Assets/Materials/Renamed.mat", TypeHint = "Material" });
            var (model, snapshot, map) = BuildScene(desired, actual);

            var changeSet = Differ.Diff(model, snapshot, map);

            Assert.Empty(changeSet.Ops.OfType<SetField>());
        }

        [Fact]
        public void Diff_AssetRefDifferentGuid_ReportsChange()
        {
            var desired = new ValueNode.AssetRef(new AssetRef { Guid = "abc123", FileId = 0, DisplayPath = "Assets/Materials/Red.mat" });
            var actual = new ValueNode.AssetRef(new AssetRef { Guid = "zzz999", FileId = 0, DisplayPath = "Assets/Materials/Red.mat" });
            var (model, snapshot, map) = BuildScene(desired, actual);

            var changeSet = Differ.Diff(model, snapshot, map);

            var setField = Assert.Single(changeSet.Ops.OfType<SetField>());
            Assert.Equal("root-1", setField.LogicalId);
            Assert.Equal(FieldKey, setField.Path);
            Assert.Equal(desired, setField.Value);
        }

        [Fact]
        public void Diff_NullAssetRef_ChangeSemanticsAndCanonicalNoneToken()
        {
            var populated = new ValueNode.AssetRef(new AssetRef { Guid = "abc123", FileId = 0, DisplayPath = "Assets/Materials/Red.mat" });
            var none = new ValueNode.AssetRef(null);

            // desired = None, actual = populated -> change (clears the field).
            var (clearedModel, clearedSnapshot, clearedMap) = BuildScene(none, populated);
            var clearedChangeSet = Differ.Diff(clearedModel, clearedSnapshot, clearedMap);
            var clearedSetField = Assert.Single(clearedChangeSet.Ops.OfType<SetField>());
            Assert.Equal(none, clearedSetField.Value);

            // desired = populated, actual = None -> change (assigns the field).
            var (assignedModel, assignedSnapshot, assignedMap) = BuildScene(populated, none);
            var assignedChangeSet = Differ.Diff(assignedModel, assignedSnapshot, assignedMap);
            var assignedSetField = Assert.Single(assignedChangeSet.Ops.OfType<SetField>());
            Assert.Equal(populated, assignedSetField.Value);

            // desired = None, actual = None -> no change.
            var (noneModel, noneSnapshot, noneMap) = BuildScene(none, none);
            var noneChangeSet = Differ.Diff(noneModel, noneSnapshot, noneMap);
            Assert.Empty(noneChangeSet.Ops.OfType<SetField>());

            // Canonical None token is distinct from any populated AssetRef and round-trips
            // (identity/equality level, not JSON — see AssetRefValueTests for the JSON round-trip).
            Assert.NotEqual(populated, none);
            var noneAgain = new ValueNode.AssetRef(null);
            Assert.Equal(none, noneAgain);
        }

        // b2-t3: Diff needs no built-in-specific awareness — it rides on AssetRef.Equals
        // (b1-t1's three-part key) exactly as the non-built-in cases above already prove.
        // These three pin that from the built-in angle specifically. No production change
        // is expected; if any of these fail, Diff DOES need built-in awareness — do not
        // patch Differ.cs to force it green, surface it as a spec deviation instead.
        private const string DefaultResourcesGuid = "0000000000000000e000000000000000";
        private const long CubeFileId = 10202;
        private const long SphereFileId = 10207;

        [Fact]
        public void Diff_BuiltinAssetRefSameGuidFileIdDifferentTypeHint_NoChange()
        {
            var desired = new ValueNode.AssetRef(new AssetRef { Guid = DefaultResourcesGuid, FileId = CubeFileId, IsBuiltin = true, DisplayPath = "Cube", TypeHint = "" });
            var actual = new ValueNode.AssetRef(new AssetRef { Guid = DefaultResourcesGuid, FileId = CubeFileId, IsBuiltin = true, DisplayPath = "Cube", TypeHint = "Mesh" });
            var (model, snapshot, map) = BuildScene(desired, actual);

            var changeSet = Differ.Diff(model, snapshot, map);

            Assert.Empty(changeSet.Ops.OfType<SetField>());
        }

        [Fact]
        public void Diff_BuiltinAssetRefDifferentFileId_ReportsChange()
        {
            var desired = new ValueNode.AssetRef(new AssetRef { Guid = DefaultResourcesGuid, FileId = CubeFileId, IsBuiltin = true, DisplayPath = "Cube" });
            var actual = new ValueNode.AssetRef(new AssetRef { Guid = DefaultResourcesGuid, FileId = SphereFileId, IsBuiltin = true, DisplayPath = "Sphere" });
            var (model, snapshot, map) = BuildScene(desired, actual);

            var changeSet = Differ.Diff(model, snapshot, map);

            var setField = Assert.Single(changeSet.Ops.OfType<SetField>());
            Assert.Equal("root-1", setField.LogicalId);
            Assert.Equal(FieldKey, setField.Path);
            Assert.Equal(desired, setField.Value);
        }

        [Fact]
        public void Diff_BuiltinAssetRefVsAssetAssetRef_DifferentGuid_ReportsChange()
        {
            var desired = new ValueNode.AssetRef(new AssetRef { Guid = DefaultResourcesGuid, FileId = CubeFileId, IsBuiltin = true, DisplayPath = "Cube" });
            var actual = new ValueNode.AssetRef(new AssetRef { Guid = "proj-guid-abc", FileId = 0, IsBuiltin = false, DisplayPath = "Assets/M/Cube.fbx" });
            var (model, snapshot, map) = BuildScene(desired, actual);

            var changeSet = Differ.Diff(model, snapshot, map);

            var setField = Assert.Single(changeSet.Ops.OfType<SetField>());
            Assert.Equal("root-1", setField.LogicalId);
            Assert.Equal(FieldKey, setField.Path);
            Assert.Equal(desired, setField.Value);
        }

        // b1-t1: SubAsset is non-authoritative (like DisplayPath/TypeHint) — Diff keys on
        // (Guid, FileId, IsBuiltin) only, never on SubAsset name.
        [Fact]
        public void Diff_TwoSubAssetsSameFileMainVsSub_ReportsChange()
        {
            const long mainFileId = 100;
            const long subFileId = 200;
            var desired = new ValueNode.AssetRef(new AssetRef { Guid = "abc123", FileId = subFileId, DisplayPath = "Assets/Models/Barrel.fbx", SubAsset = "BarrelMesh" });
            var actual = new ValueNode.AssetRef(new AssetRef { Guid = "abc123", FileId = mainFileId, DisplayPath = "Assets/Models/Barrel.fbx", SubAsset = "" });
            var (model, snapshot, map) = BuildScene(desired, actual);

            var changeSet = Differ.Diff(model, snapshot, map);

            var setField = Assert.Single(changeSet.Ops.OfType<SetField>());
            Assert.Equal("root-1", setField.LogicalId);
            Assert.Equal(FieldKey, setField.Path);
            Assert.Equal(desired, setField.Value);
        }

        [Fact]
        public void Diff_SubAssetSameGuidFileIdDifferentName_NoChange()
        {
            const long subFileId = 200;
            var desired = new ValueNode.AssetRef(new AssetRef { Guid = "abc123", FileId = subFileId, DisplayPath = "Assets/Models/Barrel.fbx", SubAsset = "BarrelMesh" });
            var actual = new ValueNode.AssetRef(new AssetRef { Guid = "abc123", FileId = subFileId, DisplayPath = "Assets/Models/Barrel.fbx", SubAsset = "barrelmesh" });
            var (model, snapshot, map) = BuildScene(desired, actual);

            var changeSet = Differ.Diff(model, snapshot, map);

            Assert.Empty(changeSet.Ops.OfType<SetField>());
        }

        [Fact]
        public void Diff_SubAssetVsBuiltin_ReportsChange()
        {
            var desired = new ValueNode.AssetRef(new AssetRef { Guid = "proj-guid", FileId = 200, IsBuiltin = false, DisplayPath = "Assets/Models/Barrel.fbx", SubAsset = "X" });
            var actual = new ValueNode.AssetRef(new AssetRef { Guid = DefaultResourcesGuid, FileId = CubeFileId, IsBuiltin = true, DisplayPath = "Cube" });
            var (model, snapshot, map) = BuildScene(desired, actual);

            var changeSet = Differ.Diff(model, snapshot, map);

            var setField = Assert.Single(changeSet.Ops.OfType<SetField>());
            Assert.Equal("root-1", setField.LogicalId);
            Assert.Equal(FieldKey, setField.Path);
            Assert.Equal(desired, setField.Value);
        }
    }
}
