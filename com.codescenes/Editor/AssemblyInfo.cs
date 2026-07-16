using System.Runtime.CompilerServices;

// Makes SceneBuilder.Editor's `internal` surface (starting with BuiltinCatalog) reachable from the
// unity-gate EditMode suite, mirroring SceneBuilder.Core/Properties/AssemblyInfo.cs's precedent for
// SceneBuilder.Core.Tests.
[assembly: InternalsVisibleTo("GateTests")]
