using System.Runtime.CompilerServices;

// Makes SceneBuilder.Licensing.Editor's `internal` test seams (EntitlementCheck.Transport,
// EntitlementCheck.RunCheckAsync) reachable from the unity-gate EditMode suite, mirroring
// com.codescenes/Editor/AssemblyInfo.cs's precedent for SceneBuilder.Editor.
[assembly: InternalsVisibleTo("GateTests")]
