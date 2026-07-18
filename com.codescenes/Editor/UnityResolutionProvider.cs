#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Validation;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// The editor-side, non-throwing <see cref="IResolutionProvider"/>: maps the SAME decision atoms
    /// the Build backstop uses (<see cref="ComponentTypeResolver.Resolve(TypeRef, IReadOnlyList{string}, out IReadOnlyList{Type})"/>,
    /// <see cref="AssetReferenceResolver.LoweringResolver.TryResolve"/>, <see cref="BuiltinCatalog"/>)
    /// into discriminated <see cref="TypeResolution"/>/<see cref="AssetResolution"/> results — it owns
    /// NO resolution logic of its own and never throws. Used by <see cref="PlanningValidator"/> so the
    /// editor Build and the headless validator drive the same collect-all walk over one decision surface.
    /// </summary>
    public sealed class UnityResolutionProvider : IResolutionProvider
    {
        private readonly AssetReferenceResolver.LoweringResolver _lowering;

        public UnityResolutionProvider(IEnumerable<AssetEntry>? cachedAssets = null)
        {
            _lowering = new AssetReferenceResolver.LoweringResolver(cachedAssets);
        }

        public TypeResolution ResolveComponentType(TypeRef type, IReadOnlyList<string> usings)
        {
            var t = ComponentTypeResolver.Resolve(type, usings, out var ambiguous);
            if (t != null)
            {
                return new TypeResolution.Resolved(t.FullName!);
            }

            if (ambiguous.Count >= 2)
            {
                var candidates = ambiguous.Select(x => x.FullName!).OrderBy(n => n, StringComparer.Ordinal).ToList();
                return new TypeResolution.Ambiguous(candidates);
            }

            return new TypeResolution.Unresolved(ComponentTypeNormalizer.SuggestQualified(type.FullName));
        }

        public AssetResolution ResolveAssetPath(string displayPath, string? subAsset)
        {
            var probe = _lowering.TryResolve(displayPath);

            if (string.IsNullOrEmpty(subAsset))
            {
                switch (probe.Kind)
                {
                    case AssetReferenceResolver.LoweringResolver.PathProbeKind.Empty:
                        return new AssetResolution.Deferred();

                    case AssetReferenceResolver.LoweringResolver.PathProbeKind.Resolved:
                        return new AssetResolution.Resolved(probe.Guid, probe.FileId, probe.TypeHint);

                    default:
                        // ContainerPath / MissingPath / DeletedGuid — all unresolved from the collect-all seam.
                        return new AssetResolution.Unresolved(Array.Empty<string>());
                }
            }

            // b3-t4: a sub-asset name was authored. Path problems take precedence over sub-object
            // scanning — do not scan when the main path itself doesn't resolve.
            switch (probe.Kind)
            {
                case AssetReferenceResolver.LoweringResolver.PathProbeKind.Empty:
                    return new AssetResolution.Deferred();

                case AssetReferenceResolver.LoweringResolver.PathProbeKind.Resolved:
                    var subProbe = AssetReferenceResolver.LoweringResolver.TryResolveSubObject(probe.CurrentPath, subAsset);
                    switch (subProbe.Kind)
                    {
                        case AssetReferenceResolver.LoweringResolver.SubObjectProbeKind.Resolved:
                            return new AssetResolution.Resolved(subProbe.Guid, subProbe.FileId, subProbe.TypeHint);

                        case AssetReferenceResolver.LoweringResolver.SubObjectProbeKind.Ambiguous:
                            return new AssetResolution.Ambiguous(subProbe.Names);

                        default:
                            // NotFound / Unidentifiable — no sub-object by that name resolves cleanly.
                            return new AssetResolution.SubAssetUnresolved(subAsset, subProbe.Names);
                    }

                default:
                    // ContainerPath / MissingPath / DeletedGuid — all unresolved from the collect-all seam.
                    return new AssetResolution.Unresolved(Array.Empty<string>());
            }
        }

        public AssetResolution ResolveBuiltin(string name, string? typeHint)
        {
            var hint = string.IsNullOrEmpty(typeHint) ? null : typeHint;
            var obj = BuiltinCatalog.Resolve(name, hint, out var ambiguous);
            if (obj != null)
            {
                if (UnityEditor.AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out var guid, out var fileId)
                    && !string.IsNullOrEmpty(guid))
                {
                    return new AssetResolution.Resolved(guid, fileId, obj.GetType().Name);
                }

                return new AssetResolution.Unresolved(Array.Empty<string>());
            }

            if (ambiguous)
            {
                return new AssetResolution.Ambiguous(BuiltinCatalog.CandidateTypeNames(name));
            }

            return new AssetResolution.Unresolved(BuiltinCatalog.Suggest(name, hint).ToList());
        }
    }
}
