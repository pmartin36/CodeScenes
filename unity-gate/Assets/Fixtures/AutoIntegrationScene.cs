using SceneBuilder.Authoring;

// ISceneDefinition fixture for AutoIntegrationTests (unity-gate/Assets/GateTests/
// AutoIntegrationTests.cs). SceneBuilderRouter.Discover() is a plain
// Directory.GetFiles(BuildersDirectory, "*.cs") scan — it routes the on-disk builder .cs the test
// writes at SceneBuilderPaths.Builder(name), never a compiled type. Build body is trivial; the
// test's on-disk source, not this compiled body, is what SceneBuilderBuild.Run parses.
public class AutoIntegrationScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
    }
}
