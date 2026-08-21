using System.Runtime.CompilerServices;

// Makes SceneBuilder.Authoring's `internal` surface (the ProjectedExtent kernel) reachable from the
// unity-gate EditMode suite, mirroring com.codescenes/Editor/AssemblyInfo.cs's precedent for
// SceneBuilder.Editor.
[assembly: InternalsVisibleTo("GateTests")]
