using System.IO;
using System.Linq;
using NUnit.Framework;
using SceneBuilder.Editor;

// Gate for b6-t1 (spec 30, Bug 6): a NEW SceneBuilders/*.cs must be discoverable the moment the
// watcher observes it, with NO domain reload and WITHOUT the test calling
// SceneBuilderRouter.ResetForTests() itself (that is exactly what masked the bug — the existing
// router tests reset between cases). Drives the real production choke point:
// SceneBuilderAutoSync.EnqueueWatcherPath(...) -> PumpOnce() -> DrainWatcherPaths() (main thread).
public class RouterCacheInvalidationTests
{
    private string _builderPath;
    private string _sidecarPath;

    [SetUp]
    public void SetUp()
    {
        SceneBuilderRouter.ResetForTests();
        SceneBuilderAutoSync.ResetForTests();
    }

    [TearDown]
    public void TearDown()
    {
        if (_builderPath != null && File.Exists(_builderPath)) File.Delete(_builderPath);
        if (_sidecarPath != null && File.Exists(_sidecarPath)) File.Delete(_sidecarPath);

        SceneBuilderRouter.ResetForTests();
        SceneBuilderAutoSync.ResetForTests();
    }

    // Checklist (research.md PUBLIC_SURFACE): a Created watcher event, drained on the main thread via
    // PumpOnce(), must invalidate the router's cache so the very next Discover() sees the new builder
    // and TryRouteBuilderFile resolves it — no ResetForTests() call in between.
    [Test]
    public void NewBuilder_DiscoveredAfterWatcherCreate_NoReload()
    {
        var name = "RouterCacheInvalidation_" + System.Guid.NewGuid().ToString("N");
        SceneBuilderPaths.EnsureBuildersDirectory();
        _builderPath = SceneBuilderPaths.Builder(name);
        _sidecarPath = SceneBuilderPaths.Sidecar(name);

        // Populate the cache BEFORE the new builder exists.
        var before = SceneBuilderRouter.Discover();
        Assert.IsFalse(before.Any(r => r.BuilderName == name),
            "Precondition: the new builder must not exist yet when the cache is first populated.");

        File.WriteAllText(_builderPath, $@"
using SceneBuilder.Authoring;
public class {name} : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
        scene.Add(""Root"");
    }}
}}");

        // Pin the current bug: writing the file alone does not invalidate the memoized cache.
        var stillCached = SceneBuilderRouter.Discover();
        Assert.IsFalse(stillCached.Any(r => r.BuilderName == name),
            "Cache must still be stale immediately after the file write, before any watcher event is drained.");

        // Drive the real production seam: background handler enqueue + main-thread pump drain.
        SceneBuilderAutoSync.EnqueueWatcherPath(_builderPath, System.IO.WatcherChangeTypes.Created);
        SceneBuilderAutoSync.PumpOnce();

        var after = SceneBuilderRouter.Discover();
        Assert.IsTrue(after.Any(r => r.BuilderName == name),
            "Discover() must include the new builder once the watcher's Created event has been drained " +
            "by PumpOnce() — no domain reload and no test-only ResetForTests() call.");

        Assert.IsTrue(SceneBuilderRouter.TryRouteBuilderFile(_builderPath, out _),
            "TryRouteBuilderFile must resolve the new builder's own path once discovered.");
    }
}
