using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Lowering;
using SceneBuilder.Core.Materialize;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b4-t1: asset-ref field change on a MAPPED owner -> PatchComponentField rewrite (already
    // falls out of the existing field-diff + b1 rendering) AND the NEW sidecar
    // ReconcileResult.AddedAssets channel (harvests the new populated GUID; a cleared field
    // contributes no entry).
    public class AssetRefReconcileTests
    {
        private const string ComponentTypeFullName = "UnityEngine.MeshRenderer";
        private const string FieldKey = "sharedMaterial";

        private static (SceneModel Model, IdentityMap Map, string ComponentLogicalId) MappedRootWithAssetField(ValueNode sourceValue)
        {
            const string ownerLogicalId = "root-1";
            var componentLogicalId = $"{ownerLogicalId}/{ComponentTypeFullName}#0";

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
                                Type = new TypeRef(ComponentTypeFullName),
                                Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>(FieldKey, sourceValue) }),
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
                },
            };

            return (model, map, componentLogicalId);
        }

        private static SceneSnapshot SnapshotWithAssetField(ValueNode snapshotValue) => new SceneSnapshot
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
                            Type = new TypeRef(ComponentTypeFullName),
                            Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>(FieldKey, snapshotValue) }),
                        },
                    },
                },
            },
        };

        private static Dictionary<string, IReadOnlyDictionary<string, SourceSpan>> FieldSpans(string componentLogicalId) =>
            new()
            {
                [componentLogicalId] = new Dictionary<string, SourceSpan> { [FieldKey] = new SourceSpan(0, 10) },
            };

        // spec test 6: source Guid "" (parsed Asset("...") literal never carries a resolved
        // Guid) differs from the snapshot's resolved Guid -> one PatchComponentField rewriting
        // the argument to the snapshot's re-derived DisplayPath, AND AddedAssets gains exactly
        // one entry for the new GUID.
        [Fact]
        public void Reconcile_AssetRefGuidChanged_PatchesArgumentToNewPathAndAddsAssetEntry()
        {
            var sourceValue = new ValueNode.AssetRef(new Model.AssetRef { Guid = "", FileId = 0, DisplayPath = "Assets/Old.png" });
            var (model, map, componentLogicalId) = MappedRootWithAssetField(sourceValue);

            var snapshotValue = new ValueNode.AssetRef(new Model.AssetRef
            {
                Guid = "abc123",
                FileId = 0,
                DisplayPath = "Assets/New.png",
                TypeHint = "Material",
            });
            var snapshot = SnapshotWithAssetField(snapshotValue);

            var result = Reconciler.Reconcile(
                model, snapshot, map, null, null, null, null, FieldSpans(componentLogicalId));

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Equal(componentLogicalId, patch.Anchor);
            Assert.Equal(SourceExpr.ValueNodeLiteral(snapshotValue), patch.NewExpr);
            Assert.Equal("Asset(\"Assets/New.png\")", patch.NewExpr);

            var addedAsset = Assert.Single(result.AddedAssets);
            Assert.Equal("abc123", addedAsset.Guid);
            Assert.Equal("Assets/New.png", addedAsset.LastKnownPath);
            Assert.Equal("Material", addedAsset.TypeHint);
        }

        // spec test 13: snapshot AssetRef(null) (field cleared in the scene) -> patch rewrites
        // the argument to the None form; no Assets[] entry is required for a cleared field.
        [Fact]
        public void Reconcile_AssetRefClearedToNone_PatchesSourceToNullAndAddsNoAssetEntry()
        {
            var sourceValue = new ValueNode.AssetRef(new Model.AssetRef { Guid = "", FileId = 0, DisplayPath = "Assets/Old.png" });
            var (model, map, componentLogicalId) = MappedRootWithAssetField(sourceValue);

            var snapshotValue = new ValueNode.AssetRef(null);
            var snapshot = SnapshotWithAssetField(snapshotValue);

            var result = Reconciler.Reconcile(
                model, snapshot, map, null, null, null, null, FieldSpans(componentLogicalId));

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Equal(componentLogicalId, patch.Anchor);
            Assert.Equal("Asset(null)", patch.NewExpr);

            Assert.Empty(result.AddedAssets);
        }

        // Regression guard: identity-only equality (Guid,FileId) must keep flowing through
        // reconcile once the sidecar harvest is wired in — a display-path-only drift with the
        // SAME identity is a no-op (no patch, no AddedAssets entry).
        [Fact]
        public void Reconcile_AssetRefSameGuidDifferentDisplayPath_NoPatchNoAssetEntry()
        {
            var sourceValue = new ValueNode.AssetRef(new Model.AssetRef { Guid = "abc123", FileId = 0, DisplayPath = "Assets/Old.png" });
            var (model, map, componentLogicalId) = MappedRootWithAssetField(sourceValue);

            var snapshotValue = new ValueNode.AssetRef(new Model.AssetRef { Guid = "abc123", FileId = 0, DisplayPath = "Assets/Renamed.png" });
            var snapshot = SnapshotWithAssetField(snapshotValue);

            var result = Reconciler.Reconcile(
                model, snapshot, map, null, null, null, null, FieldSpans(componentLogicalId));

            Assert.Empty(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Empty(result.AddedAssets);
        }

        // b4-t2 / spec test 14a: an asset-ref field on a GameObject that is itself newly created
        // in the SAME snapshot converges in one Reconcile pass (§13 attach) - the append carries
        // the Asset(...) field onto the just-created component statement rather than dropping it
        // or deferring to a 2nd Sync. A second Sync of the applied (parsed-back + path->guid
        // lowered) source against the unchanged snapshot must then be a no-op.
        private const string NewObjectUnderMappedRootScene = @"
public class AssetRefNewObjectScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""Root"");
    }
}
";

        [Fact]
        public void Reconcile_AssetRefOnNewObject_Converges()
        {
            var parsed = BuilderParser.Parse(NewObjectUnderMappedRootScene);
            var rootLogicalId = Assert.Single(parsed.Model.Roots).LogicalId;

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = rootLogicalId, GlobalObjectId = "goid-root", Kind = "GameObject" },
                },
            };

            var populatedRef = new ValueNode.AssetRef(new Model.AssetRef
            {
                Guid = "abc123",
                FileId = 0,
                DisplayPath = "Assets/New.png",
                TypeHint = "Material",
            });

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-root",
                        Name = "Root",
                        Children = new[]
                        {
                            new SnapshotNode
                            {
                                GlobalObjectId = "goid-sprite",
                                Name = "Sprite",
                                Components = new[]
                                {
                                    new ComponentData
                                    {
                                        LogicalId = "unused",
                                        Type = new TypeRef(ComponentTypeFullName),
                                        Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>(FieldKey, populatedRef) }),
                                    },
                                },
                            },
                        },
                    },
                },
            };

            // ---- pass 1: append the new object + component in ONE Reconcile call ----
            var recon = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors, handles: parsed.Handles);

            Assert.Empty(recon.Conflicts);

            var append = Assert.Single(recon.Patch.Edits.OfType<AppendStatement>());
            var componentAppend = Assert.Single(recon.Patch.Edits.OfType<AppendComponentStatement>());
            Assert.NotNull(append.Handle);
            Assert.Equal(append.Handle, componentAppend.OwnerHandle);

            var addedAsset = Assert.Single(recon.AddedAssets);
            Assert.Equal("abc123", addedAsset.Guid);

            var patched = SourcePatchApplier.Apply(NewObjectUnderMappedRootScene, recon.Patch, parsed.Anchors);

            Assert.Contains($".Set(\"{FieldKey}\", Asset(\"Assets/New.png\"))", patched);

            // ---- pass 2: reparse + lower (path->guid, the real adapter pipeline) + converge ----
            var reparsed = BuilderParser.Parse(patched);
            var spriteLogicalId = append.Handle!;

            var reparsedMap = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = rootLogicalId, GlobalObjectId = "goid-root", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = spriteLogicalId, GlobalObjectId = "goid-sprite", Kind = "GameObject", ParentLogicalId = rootLogicalId },
                    new IdentityMapEntry
                    {
                        LogicalId = $"{spriteLogicalId}/{ComponentTypeFullName}#0",
                        GlobalObjectId = "",
                        Kind = "Component",
                        ComponentType = ComponentTypeFullName,
                        ParentLogicalId = spriteLogicalId,
                    },
                },
            };

            var lowered = AssetRefLowering.Lower(
                reparsed.Model,
                path => path == "Assets/New.png" ? ("abc123", 0L, "Material") : null);

            var plan = Materializer.Materialize(lowered, snapshot, reparsedMap);

            Assert.Empty(plan.Ops);
        }
    }
}
