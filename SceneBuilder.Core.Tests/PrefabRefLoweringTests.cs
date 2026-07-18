using System.Collections.Generic;
using SceneBuilder.Core.Lowering;
using SceneBuilder.Core.Model;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b2-t3: PrefabRefLowering.Lower(SceneModel, resolver) resolves a PrefabInstanceNode's
    // SourcePrefab.DisplayPath -> Guid. Never drops a node on a miss; instead collects a
    // located AssetRefError. Already-resolved / empty-path refs pass through untouched and
    // never consult the resolver. Recurses into Children (an instance nested under a plain
    // root). Plain GameObjectNodes are unaffected. See research.md (b2-t3).
    public class PrefabRefLoweringTests
    {
        [Fact]
        public void Lower_ResolvableSourcePrefab_StampsGuid()
        {
            var instance = new PrefabInstanceNode
            {
                LogicalId = "enemy1",
                Name = "Enemy1",
                SourcePrefab = new AssetRef { DisplayPath = "Assets/Prefabs/Enemy.prefab" },
            };
            var model = new SceneModel { Roots = new GameObjectNode[] { instance } };
            var resolver = StubResolver(new Dictionary<string, (string, long, string)>
            {
                ["Assets/Prefabs/Enemy.prefab"] = ("enemy-guid", 0, "GameObject"),
            });

            var result = PrefabRefLowering.Lower(model, resolver);

            Assert.Empty(result.Errors);
            var lowered = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            Assert.Equal("enemy-guid", lowered.SourcePrefab.Guid);
            Assert.Equal("Assets/Prefabs/Enemy.prefab", lowered.SourcePrefab.DisplayPath);
        }

        [Fact]
        public void Lower_UnresolvablePath_YieldsLocatedErrorAndPreservesNode()
        {
            var instance = new PrefabInstanceNode
            {
                LogicalId = "enemy1",
                Name = "Enemy1",
                SourcePrefab = new AssetRef { DisplayPath = "Assets/Prefabs/Missing.prefab" },
            };
            var model = new SceneModel { Roots = new GameObjectNode[] { instance } };

            var result = PrefabRefLowering.Lower(model, (_, _) => null);

            var lowered = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            Assert.Equal("", lowered.SourcePrefab.Guid);
            var error = Assert.Single(result.Errors);
            Assert.Equal("Assets/Prefabs/Missing.prefab", error.LastKnownPath);
            Assert.Equal("Enemy1", error.ObjectName);
        }

        [Fact]
        public void Lower_NestedInstanceUnderPlainRoot_IsLowered()
        {
            var instance = new PrefabInstanceNode
            {
                LogicalId = "coin1",
                Name = "Coin1",
                SourcePrefab = new AssetRef { DisplayPath = "Assets/Prefabs/Coin.prefab" },
            };
            var plainRoot = new GameObjectNode
            {
                LogicalId = "pickups",
                Name = "Pickups",
                Children = new GameObjectNode[] { instance },
            };
            var model = new SceneModel { Roots = new[] { plainRoot } };
            var resolver = StubResolver(new Dictionary<string, (string, long, string)>
            {
                ["Assets/Prefabs/Coin.prefab"] = ("coin-guid", 0, "GameObject"),
            });

            var result = PrefabRefLowering.Lower(model, resolver);

            Assert.Empty(result.Errors);
            var loweredRoot = Assert.Single(result.Model.Roots);
            var loweredChild = Assert.IsType<PrefabInstanceNode>(Assert.Single(loweredRoot.Children));
            Assert.Equal("coin-guid", loweredChild.SourcePrefab.Guid);
        }

        [Fact]
        public void Lower_AlreadyResolvedSourcePrefab_Unchanged_NoResolverCall()
        {
            var instance = new PrefabInstanceNode
            {
                LogicalId = "enemy1",
                Name = "Enemy1",
                SourcePrefab = new AssetRef { Guid = "already-resolved", DisplayPath = "Assets/Prefabs/Enemy.prefab" },
            };
            var model = new SceneModel { Roots = new GameObjectNode[] { instance } };
            var spyResolver = SpyResolver();

            var result = PrefabRefLowering.Lower(model, spyResolver);

            Assert.Empty(result.Errors);
            var lowered = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            Assert.Equal("already-resolved", lowered.SourcePrefab.Guid);
        }

        [Fact]
        public void Lower_PlainGameObjectOnly_NoErrors_NoResolverCall()
        {
            var plain = new GameObjectNode { LogicalId = "g1", Name = "Player" };
            var model = new SceneModel { Roots = new[] { plain } };
            var spyResolver = SpyResolver();

            var result = PrefabRefLowering.Lower(model, spyResolver);

            Assert.Empty(result.Errors);
            Assert.Same(plain, Assert.Single(result.Model.Roots));
        }

        private static System.Func<string, string?, (string guid, long fileId, string typeHint)?> StubResolver(
            IDictionary<string, (string, long, string)> map)
        {
            return (path, _) => map.TryGetValue(path, out var hit) ? hit : ((string, long, string)?)null;
        }

        private static System.Func<string, string?, (string guid, long fileId, string typeHint)?> SpyResolver()
        {
            return (path, _) => throw new Xunit.Sdk.XunitException("resolver should not be called: " + path);
        }
    }
}
