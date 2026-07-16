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

        AssetResolution ResolveAssetPath(string displayPath, string? subAsset);

        AssetResolution ResolveBuiltin(string name, string? typeHint);
    }
}
