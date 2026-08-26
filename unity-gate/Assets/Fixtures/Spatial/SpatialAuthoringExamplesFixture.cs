using SceneBuilder.Authoring;
using UnityEngine;
using static SceneBuilder.Authoring.AssetRefs;

// Compiles the spec's spatial authoring examples (specs/19-spatial-authoring-components.md
// §"Authoring API") so the real .FitSize/.AlignTo call-sites and overload resolution — the
// aspect-locked overload, the explicit tuple `size:` overload, the target-override /
// depth-axis AlignTo forms, and a prefab-instance AlignTo target — are exercised by the
// Unity compile, not just NodeHandle.cs alone.
// Lives in its own asmdef referencing SceneBuilder.Authoring (GateFixtures.asmdef also references
// SceneBuilder.Authoring now, so it is no longer the Authoring-reference negative case — see
// BuilderProjectInjectorTests.ReferencesAuthoring_ReadsTheRealEditorAssemblyGraph).
public class SpatialAuthoringExamplesFixture : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        // Floor: explicit per-axis world size via the tuple overload — the load-bearing call.
        var floor = scene.Add("Floor")
             .Component<MeshFilter>(c => c.Set("m_Mesh", Builtin("Plane")))
             .FitSize(size: (20, 1, 20));

        // Crate: aspect-locked height, then aligned down onto the floor.
        scene.Add("Crate")
             .Component<MeshFilter>(c => c.Set("m_Mesh", Builtin("Cube")))
             .FitSize(height: 1.2f)
             .AlignTo(floor, y: AxisAlign.AbutMax);

        // Lamp aligned up onto an explicit target (no raycast needed).
        var ceiling = scene.Add("Ceiling").Component<MeshFilter>(c => c.Set("m_Mesh", Builtin("Plane")));
        scene.Add("Lamp")
             .Component<MeshFilter>(c => c.Set("m_Mesh", Builtin("Cylinder")))
             .FitSize(height: 0.3f)
             .AlignTo(ceiling, y: AxisAlign.AbutMin);

        // Poster aligned back-flush against a wall (depth axis).
        var wall = scene.Add("Wall").Component<MeshFilter>(c => c.Set("m_Mesh", Builtin("Plane")));
        scene.Add("Poster")
             .Component<MeshFilter>(c => c.Set("m_Mesh", Builtin("Quad")))
             .FitSize(width: 0.8f)
             .AlignTo(wall, z: AxisAlign.AbutMax);

        // Sign aligned up onto a prefab instance root — AlignTo's target accepts either kind
        // of scene object handle.
        var shelf = scene.Instance("Assets/Prefabs/Shelf.prefab");
        scene.Add("Sign")
             .Component<MeshFilter>(c => c.Set("m_Mesh", Builtin("Quad")))
             .FitSize(height: 0.2f)
             .AlignTo(shelf, y: AxisAlign.AbutMin);
    }
}
