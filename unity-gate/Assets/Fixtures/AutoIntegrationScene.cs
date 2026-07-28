using SceneBuilder.Authoring;

// Compiled ISceneDefinition fixture for AutoIntegrationTests (unity-gate/Assets/GateTests/
// AutoIntegrationTests.cs): SceneBuilderRouter.Discover() only routes a REAL compiled type whose
// on-disk builder source exists at SceneBuilderPaths.Builder(name) — decoupled from the authored
// SOURCE the test writes there at runtime (the only link is the file-stem == type.Name convention).
// Build body is trivial; the test's on-disk source, not this compiled body, is what
// SceneBuilderBuild.Run parses.
public class AutoIntegrationScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
    }
}
