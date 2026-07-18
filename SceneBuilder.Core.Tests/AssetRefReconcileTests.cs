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
        private const string OtherFieldKey = "otherMaterial";

        // b2-t4 fixture constants — test-side literals only, per research.md CLEANLINESS
        // ("do not teach Core the container GUIDs"). Mirrors AssetRefDiffTests.cs:116-118.
        private const string DefaultResourcesGuid = "0000000000000000e000000000000000";
        private const long CubeFileId = 10202;
        private const long SphereFileId = 10207;
        private const string DefaultMaterialGuid = "0000000000000000f000000000000000";
        private const long DefaultMaterialFileId = 10303;

        private static (SceneModel Model, IdentityMap Map, string ComponentLogicalId) MappedRootWithAssetFields(
            IEnumerable<KeyValuePair<string, ValueNode>> sourceFields)
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
                                Fields = new FieldMap(sourceFields),
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

        private static (SceneModel Model, IdentityMap Map, string ComponentLogicalId) MappedRootWithAssetField(ValueNode sourceValue) =>
            MappedRootWithAssetFields(new[] { new KeyValuePair<string, ValueNode>(FieldKey, sourceValue) });

        private static SceneSnapshot SnapshotWithAssetFields(IEnumerable<KeyValuePair<string, ValueNode>> snapshotFields) => new SceneSnapshot
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
                            Fields = new FieldMap(snapshotFields),
                        },
                    },
                },
            },
        };

        private static SceneSnapshot SnapshotWithAssetField(ValueNode snapshotValue) =>
            SnapshotWithAssetFields(new[] { new KeyValuePair<string, ValueNode>(FieldKey, snapshotValue) });

        private static Dictionary<string, IReadOnlyDictionary<string, SourceSpan>> FieldSpans(string componentLogicalId, params string[] fieldKeys) =>
            new()
            {
                [componentLogicalId] = fieldKeys.ToDictionary(k => k, _ => new SourceSpan(0, 10)),
            };

        private static Dictionary<string, IReadOnlyDictionary<string, SourceSpan>> FieldSpans(string componentLogicalId) =>
            FieldSpans(componentLogicalId, FieldKey);

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

        // §"Move/rename stability": asset moved/renamed -> GUID unchanged -> the ref still resolves,
        // and "a Reconcile MAY update the literal in source for readability, but identity never
        // depended on it". So a display-path-only drift is NOT a no-op: the authored path in the source
        // text is now stale and must be re-derived to the asset's current path, and Assets[] must learn
        // the new LastKnownPath (it is the move-recovery cache — a stale entry rots it).
        //
        // Identity is still (Guid, FileId) ONLY and that is unchanged here: this is a TEXT refresh, not
        // a swap. It converges in exactly one pass — once the literal says the current path, the next
        // reconcile sees matching text and emits nothing.
        [Fact]
        public void Reconcile_AssetRefSameGuidDifferentDisplayPath_RefreshesPathLiteralAndCache()
        {
            var sourceValue = new ValueNode.AssetRef(new Model.AssetRef { Guid = "abc123", FileId = 0, DisplayPath = "Assets/Old.png" });
            var (model, map, componentLogicalId) = MappedRootWithAssetField(sourceValue);

            var snapshotValue = new ValueNode.AssetRef(new Model.AssetRef { Guid = "abc123", FileId = 0, DisplayPath = "Assets/Renamed.png" });
            var snapshot = SnapshotWithAssetField(snapshotValue);

            var result = Reconciler.Reconcile(
                model, snapshot, map, null, null, null, null, FieldSpans(componentLogicalId));

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Equal("Asset(\"Assets/Renamed.png\")", patch.NewExpr);

            var addedAsset = Assert.Single(result.AddedAssets);
            Assert.Equal("abc123", addedAsset.Guid);
            Assert.Equal("Assets/Renamed.png", addedAsset.LastKnownPath);
        }

        // The other half of the same rule, and the reason the drift check is an ADDITIONAL condition on
        // top of Equals rather than a replacement for it: identical authored TEXT does not imply
        // identical identity. Two sub-objects of one asset share a path and differ only by FileId — a
        // swap between them must still patch, even though the emitted literal is byte-identical.
        [Fact]
        public void Reconcile_AssetRefSameDisplayPathDifferentFileId_StillPatches()
        {
            var sourceValue = new ValueNode.AssetRef(new Model.AssetRef { Guid = "abc123", FileId = 111, DisplayPath = "Assets/Sheet.png" });
            var (model, map, componentLogicalId) = MappedRootWithAssetField(sourceValue);

            var snapshotValue = new ValueNode.AssetRef(new Model.AssetRef { Guid = "abc123", FileId = 222, DisplayPath = "Assets/Sheet.png" });
            var snapshot = SnapshotWithAssetField(snapshotValue);

            var result = Reconciler.Reconcile(
                model, snapshot, map, null, null, null, null, FieldSpans(componentLogicalId));

            Assert.Single(result.Patch.Edits.OfType<PatchComponentField>());
        }

        // CONVERGENCE (the defect this whole path had): an UNCHANGED, already-lowered asset ref must
        // produce NOTHING — no patch, no harvest. This is the Core-level statement of "a sync with no
        // scene change is a no-op"; the adapter's half is that Sync must LOWER the source ref at all,
        // since an unlowered ref carries Guid="" and can never be identity-equal to the snapshot's.
        [Fact]
        public void Reconcile_AssetRefIdenticalIdentityAndPath_ProducesNoEdit()
        {
            var sourceValue = new ValueNode.AssetRef(new Model.AssetRef { Guid = "abc123", FileId = 7, DisplayPath = "Assets/Red.mat", TypeHint = "Material" });
            var (model, map, componentLogicalId) = MappedRootWithAssetField(sourceValue);

            var snapshotValue = new ValueNode.AssetRef(new Model.AssetRef { Guid = "abc123", FileId = 7, DisplayPath = "Assets/Red.mat", TypeHint = "Material" });
            var snapshot = SnapshotWithAssetField(snapshotValue);

            var result = Reconciler.Reconcile(
                model, snapshot, map, null, null, null, null, FieldSpans(componentLogicalId));

            // Scoped to PatchComponentField like every sibling test here: the shared fixture maps the
            // OWNER but not the component, so an AppendComponentStatement for the unmapped component is
            // always present and is not what this asserts.
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
                (path, _) => path == "Assets/New.png" ? ("abc123", 0L, "Material") : null);

            var plan = Materializer.Materialize(lowered, snapshot, reparsedMap);

            Assert.Empty(plan.Ops);
        }

        // spec #24: a non-builtin source ref replaced by a built-in snapshot ref -> patches to
        // the Builtin(...) form. Passes via the !Equals path (different Guid) — a
        // characterization pin on b1-t3's emit reaching Reconcile, NOT a pin on change (1).
        [Fact]
        public void Reconcile_SnapshotBuiltinAgainstAssetSource_PatchesToBuiltinForm()
        {
            var sourceValue = new ValueNode.AssetRef(new Model.AssetRef { Guid = "", FileId = 0, DisplayPath = "Assets/Materials/Red.mat" });
            var (model, map, componentLogicalId) = MappedRootWithAssetField(sourceValue);

            var snapshotValue = new ValueNode.AssetRef(new Model.AssetRef
            {
                Guid = DefaultMaterialGuid,
                FileId = DefaultMaterialFileId,
                IsBuiltin = true,
                DisplayPath = "Default-Material",
                TypeHint = "",
            });
            var snapshot = SnapshotWithAssetField(snapshotValue);

            var result = Reconciler.Reconcile(
                model, snapshot, map, null, null, null, null, FieldSpans(componentLogicalId));

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Equal("Builtin(\"Default-Material\")", patch.NewExpr);
        }

        // spec #25: two built-in refs sharing a container Guid but differing FileId/name (a
        // primitive swap) -> patches to the new name. Also !Equals (different FileId) —
        // characterization.
        [Fact]
        public void Reconcile_SnapshotBuiltinChanged_PatchesName()
        {
            var sourceValue = new ValueNode.AssetRef(new Model.AssetRef { Guid = DefaultResourcesGuid, FileId = CubeFileId, IsBuiltin = true, DisplayPath = "Cube" });
            var (model, map, componentLogicalId) = MappedRootWithAssetField(sourceValue);

            var snapshotValue = new ValueNode.AssetRef(new Model.AssetRef { Guid = DefaultResourcesGuid, FileId = SphereFileId, IsBuiltin = true, DisplayPath = "Sphere" });
            var snapshot = SnapshotWithAssetField(snapshotValue);

            var result = Reconciler.Reconcile(
                model, snapshot, map, null, null, null, null, FieldSpans(componentLogicalId));

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Equal("Builtin(\"Sphere\")", patch.NewExpr);
        }

        // spec #26 — THE pin for change (1). Identity-equal (Guid, FileId, IsBuiltin) pair whose
        // TypeHint differs (an ambiguity that gained a qualifier): Equals is true and DisplayPath
        // is unchanged, so unpatched code's AuthoredTextIsCurrent (DisplayPath-only) reports
        // "current" and nothing is emitted. Must still patch, since the emitted text is a function
        // of (DisplayPath, IsBuiltin, TypeHint).
        [Fact]
        public void Reconcile_BuiltinTypeHintChanged_PatchesEvenWhenIdentityEqual()
        {
            var sourceValue = new ValueNode.AssetRef(new Model.AssetRef { Guid = DefaultResourcesGuid, FileId = CubeFileId, IsBuiltin = true, DisplayPath = "Cube", TypeHint = "" });
            var (model, map, componentLogicalId) = MappedRootWithAssetField(sourceValue);

            var snapshotValue = new ValueNode.AssetRef(new Model.AssetRef { Guid = DefaultResourcesGuid, FileId = CubeFileId, IsBuiltin = true, DisplayPath = "Cube", TypeHint = "Mesh" });
            var snapshot = SnapshotWithAssetField(snapshotValue);

            var result = Reconciler.Reconcile(
                model, snapshot, map, null, null, null, null, FieldSpans(componentLogicalId));

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Equal("Builtin(\"Cube\", \"Mesh\")", patch.NewExpr);
        }

        // spec #27: a component with TWO changed fields, one built-in and one non-builtin
        // Asset(...). AddedAssets must contain exactly the non-builtin's entry — the built-in
        // contributes nothing, but the sibling change proves the harvest still runs at all (an
        // implementation that skipped the whole harvest would also pass an Assert.Empty alone).
        [Fact]
        public void Reconcile_BuiltinRef_AddsNoAssetEntries()
        {
            var sourceFields = new[]
            {
                new KeyValuePair<string, ValueNode>(FieldKey, new ValueNode.AssetRef(new Model.AssetRef { Guid = DefaultResourcesGuid, FileId = CubeFileId, IsBuiltin = true, DisplayPath = "Cube" })),
                new KeyValuePair<string, ValueNode>(OtherFieldKey, new ValueNode.AssetRef(new Model.AssetRef { Guid = "", FileId = 0, DisplayPath = "Assets/Old.png" })),
            };
            var (model, map, componentLogicalId) = MappedRootWithAssetFields(sourceFields);

            var snapshotFields = new[]
            {
                new KeyValuePair<string, ValueNode>(FieldKey, new ValueNode.AssetRef(new Model.AssetRef { Guid = DefaultResourcesGuid, FileId = SphereFileId, IsBuiltin = true, DisplayPath = "Sphere" })),
                new KeyValuePair<string, ValueNode>(OtherFieldKey, new ValueNode.AssetRef(new Model.AssetRef { Guid = "abc123", FileId = 0, DisplayPath = "Assets/New.png", TypeHint = "Material" })),
            };
            var snapshot = SnapshotWithAssetFields(snapshotFields);

            var result = Reconciler.Reconcile(
                model, snapshot, map, null, null, null, null, FieldSpans(componentLogicalId, FieldKey, OtherFieldKey));

            Assert.Equal(2, result.Patch.Edits.OfType<PatchComponentField>().Count());

            var addedAsset = Assert.Single(result.AddedAssets);
            Assert.Equal("abc123", addedAsset.Guid);
            Assert.Equal("Assets/New.png", addedAsset.LastKnownPath);
        }

        // spec #28 / §13 create-with-payload: a newly editor-created object carrying a built-in
        // ref converges in one Reconcile pass (owner mapped, appended onto the just-created
        // component statement), and a second Sync of the applied + reparsed + lowered source
        // against the unchanged snapshot is a no-op. Mirrors Reconcile_AssetRefOnNewObject_Converges,
        // swapping the payload for a built-in and using AssetRefLowering's 3-arg builtinResolver
        // overload for pass 2.
        [Fact]
        public void Reconcile_BuiltinRefOnNewObject_Converges()
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

            var builtinRef = new ValueNode.AssetRef(new Model.AssetRef
            {
                Guid = DefaultResourcesGuid,
                FileId = CubeFileId,
                IsBuiltin = true,
                DisplayPath = "Cube",
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
                                        Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>(FieldKey, builtinRef) }),
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

            Assert.Empty(recon.AddedAssets);

            var patched = SourcePatchApplier.Apply(NewObjectUnderMappedRootScene, recon.Patch, parsed.Anchors);

            Assert.Contains($".Set(\"{FieldKey}\", Builtin(\"Cube\"))", patched);

            // ---- pass 2: reparse + lower (name->guid via builtinResolver, the real adapter
            // pipeline) + converge ----
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
                (path, _) => null,
                (name, hint) => name == "Cube" && hint is null ? (DefaultResourcesGuid, CubeFileId, "Mesh") : null);

            var plan = Materializer.Materialize(lowered, snapshot, reparsedMap);

            Assert.Empty(plan.Ops);
        }

        // spec #29: an unchanged built-in ref re-synced against an identical snapshot (including
        // TypeHint) is a no-op — no patch, no churn. This is what would catch change (1) being
        // over-applied.
        [Fact]
        public void Reconcile_ResyncUnchangedBuiltin_IsANoOp()
        {
            var sourceValue = new ValueNode.AssetRef(new Model.AssetRef { Guid = DefaultResourcesGuid, FileId = CubeFileId, IsBuiltin = true, DisplayPath = "Cube", TypeHint = "" });
            var (model, map, componentLogicalId) = MappedRootWithAssetField(sourceValue);

            var snapshotValue = new ValueNode.AssetRef(new Model.AssetRef { Guid = DefaultResourcesGuid, FileId = CubeFileId, IsBuiltin = true, DisplayPath = "Cube", TypeHint = "" });
            var snapshot = SnapshotWithAssetField(snapshotValue);

            var result = Reconciler.Reconcile(
                model, snapshot, map, null, null, null, null, FieldSpans(componentLogicalId));

            Assert.Empty(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Empty(result.AddedAssets);
        }

        // b2-t2 #21: a source main-asset ref (Guid="") against a resolved-sub-asset snapshot ->
        // !Equals (differing Guid), patches to the 2-arg form. Characterization: pins the 2-arg
        // emit (b2-t1) reaching Reconcile, not the AuthoredTextIsCurrent SubAsset clause itself.
        [Fact]
        public void Reconcile_SnapshotSubAssetAgainstMainAssetSource_PatchesToTwoArgForm()
        {
            var sourceValue = new ValueNode.AssetRef(new Model.AssetRef { Guid = "", FileId = 0, DisplayPath = "Assets/Barrel.fbx" });
            var (model, map, componentLogicalId) = MappedRootWithAssetField(sourceValue);

            var snapshotValue = new ValueNode.AssetRef(new Model.AssetRef
            {
                Guid = "fbxGuid",
                FileId = 2,
                DisplayPath = "Assets/Barrel.fbx",
                SubAsset = "BarrelMesh",
            });
            var snapshot = SnapshotWithAssetField(snapshotValue);

            var result = Reconciler.Reconcile(
                model, snapshot, map, null, null, null, null, FieldSpans(componentLogicalId));

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Equal("Asset(\"Assets/Barrel.fbx\", \"BarrelMesh\")", patch.NewExpr);
        }

        // b2-t2 #22 — THE make-or-break pin. Identity-equal (Guid, FileId, IsBuiltin) pair whose
        // SubAsset differs (renamed in the DCC tool, fileId unchanged): Equals is true, so without
        // the SubAsset clause in AuthoredTextIsCurrent nothing would be emitted. Must still patch,
        // since the emitted text is a function of (DisplayPath, IsBuiltin, TypeHint, SubAsset).
        [Fact]
        public void Reconcile_SubAssetNameChanged_PatchesEvenWhenIdentityEqual()
        {
            var sourceValue = new ValueNode.AssetRef(new Model.AssetRef { Guid = "g", FileId = 42, DisplayPath = "Assets/Barrel.fbx", SubAsset = "OldMesh" });
            var (model, map, componentLogicalId) = MappedRootWithAssetField(sourceValue);

            var snapshotValue = new ValueNode.AssetRef(new Model.AssetRef { Guid = "g", FileId = 42, DisplayPath = "Assets/Barrel.fbx", SubAsset = "NewMesh" });
            var snapshot = SnapshotWithAssetField(snapshotValue);

            var result = Reconciler.Reconcile(
                model, snapshot, map, null, null, null, null, FieldSpans(componentLogicalId));

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Equal("Asset(\"Assets/Barrel.fbx\", \"NewMesh\")", patch.NewExpr);
        }

        // b2-t2 #23: the anti-over-application twin of #22 — identical refs including SubAsset
        // resync as a no-op. Catches the SubAsset clause being over-applied (e.g. comparing
        // case-insensitively, or always returning false).
        [Fact]
        public void Reconcile_ResyncUnchangedSubAsset_IsANoOp()
        {
            var sourceValue = new ValueNode.AssetRef(new Model.AssetRef { Guid = "g", FileId = 42, DisplayPath = "Assets/Barrel.fbx", SubAsset = "BarrelMesh" });
            var (model, map, componentLogicalId) = MappedRootWithAssetField(sourceValue);

            var snapshotValue = new ValueNode.AssetRef(new Model.AssetRef { Guid = "g", FileId = 42, DisplayPath = "Assets/Barrel.fbx", SubAsset = "BarrelMesh" });
            var snapshot = SnapshotWithAssetField(snapshotValue);

            var result = Reconciler.Reconcile(
                model, snapshot, map, null, null, null, null, FieldSpans(componentLogicalId));

            Assert.Empty(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Empty(result.AddedAssets);
        }

        // b2-t2 #24: CollectAssetEntries harvest for a sub-asset ref carries LastKnownPath = the
        // asset PATH (DisplayPath), never the SubAsset name. Pure assertion — CollectAssetEntries
        // is unchanged by this task (research.md CONFIRMED).
        [Fact]
        public void Reconcile_SubAssetRef_HarvestsAssetEntryWithAssetPath()
        {
            var sourceValue = new ValueNode.AssetRef(new Model.AssetRef { Guid = "", FileId = 0, DisplayPath = "Assets/Barrel.fbx" });
            var (model, map, componentLogicalId) = MappedRootWithAssetField(sourceValue);

            var snapshotValue = new ValueNode.AssetRef(new Model.AssetRef
            {
                Guid = "fbxGuid",
                FileId = 2,
                DisplayPath = "Assets/Barrel.fbx",
                SubAsset = "BarrelMesh",
            });
            var snapshot = SnapshotWithAssetField(snapshotValue);

            var result = Reconciler.Reconcile(
                model, snapshot, map, null, null, null, null, FieldSpans(componentLogicalId));

            var addedAsset = Assert.Single(result.AddedAssets);
            Assert.Equal("fbxGuid", addedAsset.Guid);
            Assert.Equal("Assets/Barrel.fbx", addedAsset.LastKnownPath);
            Assert.NotEqual("BarrelMesh", addedAsset.LastKnownPath);
        }

        // b2-t2 #25: a sub-asset ref on a newly-created object converges identically to the
        // existing main-asset sibling (Reconcile_AssetRefOnNewObject_Converges above) — pass-1
        // append carries the 2-arg form + harvests the sidecar entry, pass-2 lower+materialize
        // (against the CURRENT 1-arg Lower resolver — b3-t2's 2-arg migration is out of scope
        // here) is a no-op.
        [Fact]
        public void Reconcile_SubAssetRefOnNewObject_Converges()
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
                Guid = "fbxGuid",
                FileId = 2,
                DisplayPath = "Assets/Barrel.fbx",
                SubAsset = "BarrelMesh",
                TypeHint = "Mesh",
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
            Assert.Equal("fbxGuid", addedAsset.Guid);
            Assert.Equal("Assets/Barrel.fbx", addedAsset.LastKnownPath);

            var patched = SourcePatchApplier.Apply(NewObjectUnderMappedRootScene, recon.Patch, parsed.Anchors);

            Assert.Contains($".Set(\"{FieldKey}\", Asset(\"Assets/Barrel.fbx\", \"BarrelMesh\"))", patched);

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
                (path, _) => path == "Assets/Barrel.fbx" ? ("fbxGuid", 2L, "Mesh") : null);

            var plan = Materializer.Materialize(lowered, snapshot, reparsedMap);

            Assert.Empty(plan.Ops);
        }
    }
}
