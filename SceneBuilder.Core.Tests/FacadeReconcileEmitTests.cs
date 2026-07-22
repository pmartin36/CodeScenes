using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b4-t4: scene->code Reconcile EMITS the typed `.Instance(Prefabs.X)` form when the
    // snapshot's SourcePrefabGuid resolves through the FacadeCatalog (Guid->PropertyName, b1-t2);
    // falls back to the string `.Instance("<path>")` form ONLY when the prefab is absent from the
    // catalog. Typed is the DEFAULT emitted output (spec 25, Behavior 10).
    public class FacadeReconcileEmitTests
    {
        private const string PrefabGuid = "guid-tank-prefab";
        private const string PrefabPath = "Assets/Prefabs/Tank.prefab";

        private const string EmptyBuildFixture = @"
public class FacadeEmitScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
    }
}
";

        private static (SceneModel Model, SceneSnapshot Snapshot, IdentityMap Map) SnapshotOnlyTankInstance()
        {
            var model = new SceneModel { SchemaVersion = 1, Roots = System.Array.Empty<GameObjectNode>() };

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-tank-1",
                        Name = "Tank",
                        SourcePrefabGuid = PrefabGuid,
                    },
                },
            };

            var map = new IdentityMap
            {
                Entries = System.Array.Empty<IdentityMapEntry>(),
                Assets = new[] { new AssetEntry { Guid = PrefabGuid, LastKnownPath = PrefabPath, TypeHint = "GameObject" } },
            };

            return (model, snapshot, map);
        }

        [Fact]
        public void Reconcile_EmitsTypedInstance_WhenPrefabHasFacade()
        {
            var (model, snapshot, map) = SnapshotOnlyTankInstance();

            var facadeCatalog = new FacadeCatalog
            {
                Entries = new[]
                {
                    new FacadeEntry { PropertyName = "Tank", Guid = PrefabGuid, Root = new FacadeNode { TypeName = "GameObject" } },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map, facadeCatalog: facadeCatalog);

            var append = Assert.IsType<AppendStatement>(Assert.Single(result.Patch.Edits));

            var anchors = BuilderParser.Parse(EmptyBuildFixture).Anchors;
            var rendered = SourcePatchApplier.Apply(EmptyBuildFixture, result.Patch, anchors);

            Assert.Contains("scene.Instance(Prefabs.Tank);", rendered);
            Assert.DoesNotContain("Instance(\"" + PrefabPath + "\")", rendered);
        }

        [Fact]
        public void Reconcile_FallsBackToString_WhenNoFacade()
        {
            var (model, snapshot, map) = SnapshotOnlyTankInstance();

            // Catalog exists but has no entry for PrefabGuid — absent from the manifest.
            var facadeCatalog = new FacadeCatalog { Entries = System.Array.Empty<FacadeEntry>() };

            var result = Reconciler.Reconcile(model, snapshot, map, facadeCatalog: facadeCatalog);

            var append = Assert.IsType<AppendStatement>(Assert.Single(result.Patch.Edits));
            Assert.Equal(PrefabPath, append.SourcePrefabPath);
            Assert.Null(append.SourcePropertyName);

            var anchors = BuilderParser.Parse(EmptyBuildFixture).Anchors;
            var rendered = SourcePatchApplier.Apply(EmptyBuildFixture, result.Patch, anchors);

            Assert.Contains("scene.Instance(\"" + PrefabPath + "\");", rendered);
        }
    }
}
