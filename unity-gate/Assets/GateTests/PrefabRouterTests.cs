using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SceneBuilder.Editor;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Serialization;

// Pins the .cs <-> .prefab routing arm: SceneBuilderRouter.DiscoverPrefabs scans
// SceneBuilderPaths.PrefabBuildersDirectory (mirroring the scene arm's Discover over
// BuildersDirectory), resolving each route's PrefabPath from the builder's own sidecar Scene field
// (the generalized target-path slot) or a deterministic default when no sidecar exists yet.
public class PrefabRouterTests
{
    private string _builderPath;
    private string _sidecarPath;

    [SetUp]
    public void SetUp()
    {
        SceneBuilderRouter.ResetForTests();
        SceneBuilderPaths.EnsurePrefabBuildersDirectory();
    }

    [TearDown]
    public void TearDown()
    {
        if (_builderPath != null && File.Exists(_builderPath)) File.Delete(_builderPath);
        if (_sidecarPath != null && File.Exists(_sidecarPath)) File.Delete(_sidecarPath);
        SceneBuilderRouter.ResetForTests();
    }

    private static string Source(string name) => $@"
using SceneBuilder.Authoring;
public class {name} : IPrefabDefinition
{{
    public void Build(PrefabRoot root)
    {{
        root.Add(""{name}"");
    }}
}}";

    [Test]
    public void Discover_MapsBuilderToPrefabViaSidecarScene()
    {
        var name = "PrefabRouter_" + Guid.NewGuid().ToString("N");
        _builderPath = SceneBuilderPaths.PrefabBuilder(name);
        _sidecarPath = SceneBuilderPaths.PrefabSidecar(name);
        var prefabPath = "Assets/GateTests/Fixtures_PrefabRouter/" + name + ".prefab";

        File.WriteAllText(_builderPath, Source(name));
        File.WriteAllText(_sidecarPath, IdentityMapJson.Serialize(new IdentityMap { Scene = prefabPath }));

        var routes = SceneBuilderRouter.DiscoverPrefabs();
        var route = routes.FirstOrDefault(r => r.BuilderName == name);

        Assert.AreEqual(prefabPath, route.PrefabPath,
            "DiscoverPrefabs did not resolve the prefab path from the builder's sidecar Scene field.");
    }

    [Test]
    public void TryRoutePrefabBuilderFile_ResolvesCsToPrefab()
    {
        var name = "PrefabRouter_" + Guid.NewGuid().ToString("N");
        _builderPath = SceneBuilderPaths.PrefabBuilder(name);
        _sidecarPath = SceneBuilderPaths.PrefabSidecar(name);
        var prefabPath = "Assets/GateTests/Fixtures_PrefabRouter/" + name + ".prefab";

        File.WriteAllText(_builderPath, Source(name));
        File.WriteAllText(_sidecarPath, IdentityMapJson.Serialize(new IdentityMap { Scene = prefabPath }));
        SceneBuilderRouter.ResetForTests();

        Assert.IsTrue(SceneBuilderRouter.TryRoutePrefabBuilderFile(_builderPath, out var route),
            "TryRoutePrefabBuilderFile did not resolve a known prefab builder's own .cs path");
        Assert.AreEqual(prefabPath, route.PrefabPath, "Resolved route pointed at the wrong .prefab path");
    }

    [Test]
    public void TryRoutePrefabAsset_ResolvesPrefabToCs()
    {
        var name = "PrefabRouter_" + Guid.NewGuid().ToString("N");
        _builderPath = SceneBuilderPaths.PrefabBuilder(name);
        _sidecarPath = SceneBuilderPaths.PrefabSidecar(name);
        var prefabPath = "Assets/GateTests/Fixtures_PrefabRouter/" + name + ".prefab";

        File.WriteAllText(_builderPath, Source(name));
        File.WriteAllText(_sidecarPath, IdentityMapJson.Serialize(new IdentityMap { Scene = prefabPath }));
        SceneBuilderRouter.ResetForTests();

        Assert.IsTrue(SceneBuilderRouter.TryRoutePrefabAsset(prefabPath, out var route),
            "TryRoutePrefabAsset did not resolve a known .prefab path back to its builder");
        Assert.AreEqual(_builderPath, route.BuilderPath, "Resolved route pointed at the wrong builder .cs path");
    }

    [Test]
    public void Discover_DefaultsPrefabPath_WhenNoSidecar()
    {
        var name = "PrefabRouter_" + Guid.NewGuid().ToString("N");
        _builderPath = SceneBuilderPaths.PrefabBuilder(name);
        _sidecarPath = SceneBuilderPaths.PrefabSidecar(name);

        File.WriteAllText(_builderPath, Source(name));
        // No sidecar written — the route must fall back to a deterministic default.

        var routes = SceneBuilderRouter.DiscoverPrefabs();
        var route = routes.FirstOrDefault(r => r.BuilderName == name);

        Assert.AreEqual("Assets/SceneBuilder/" + name + ".prefab", route.PrefabPath,
            "DiscoverPrefabs did not fall back to the deterministic default prefab path when no sidecar exists.");
    }
}
