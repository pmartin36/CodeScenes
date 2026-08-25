using System.Collections.Generic;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Validation
{
    // Non-throwing resolution seam: implementations (UnityResolutionProvider,
    // DiskResolutionProvider, or a test stub) return a result variant for every
    // outcome — including unresolved/ambiguous — and never throw. See spec
    // §"Collect ALL diagnostics".
    public interface IResolutionProvider
    {
        TypeResolution ResolveComponentType(TypeRef type, IReadOnlyList<string> usings);

        // field carries the owning component field's context for a DIRECT (top-level) authored
        // AssetRef value, so a sub-asset name scan can narrow to the field's expected
        // UnityEngine.Object type before declaring an ambiguity. Null (a nested value, or a caller
        // with no field context) resolves exactly as before the type filter existed.
        AssetResolution ResolveAssetPath(string displayPath, string? subAsset, AssetFieldContext? field = null);

        AssetResolution ResolveBuiltin(string name, string? typeHint);
    }
}
