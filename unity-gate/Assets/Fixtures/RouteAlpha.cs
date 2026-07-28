using SceneBuilder.Authoring;

// Compiled ISceneDefinition fixture for the multi-scene-builders router tests
// (unity-gate/Assets/GateTests/MultiSceneRoutingTests.cs): TypeCache discovery needs a REAL
// compiled type named "RouteAlpha", decoupled from the authored SOURCE the harness writes to
// <ProjectRoot>/SceneBuilders/RouteAlpha.cs at runtime (the only link is the file-stem ==
// type.Name convention SceneBuilderRouter.Discover() uses). Build body is trivial — the harness's
// on-disk source, not this compiled body, is what SceneBuilderBuild.Run parses.
public class RouteAlpha : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
    }
}
