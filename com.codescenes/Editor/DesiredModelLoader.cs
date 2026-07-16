#nullable enable
using System.Collections.Generic;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Lowering;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// THE single seam that turns builder SOURCE into the DESIRED model, for BOTH directions
    /// (<see cref="SceneBuilderBuild"/> code-&gt;scene and <see cref="SceneBuilderSync"/> scene-&gt;code).
    /// It owns the whole pipeline — parse → <see cref="AuthoredPathResolver"/> → <see cref="AssetRefLowering"/> —
    /// so that no caller can obtain a desired model that skipped a stage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This type exists because the stages were previously duplicated per-caller and Sync silently
    /// omitted one: Build lowered authored <c>Asset("path")</c> refs to their <c>(guid, fileId)</c>,
    /// Sync did not. Since <c>AssetRef.Equals</c> keys on <c>(Guid, FileId)</c> ONLY, an unlowered
    /// source ref (<c>Guid=""</c>) could never equal a populated snapshot ref, so every sync re-patched
    /// and re-harvested every asset ref forever — a non-converging sync, and, with the file watcher
    /// driving code-&gt;scene, a feedback loop.
    /// </para>
    /// <para>
    /// The fix is structural, not a pasted-in call: lowering is not something a caller opts into and
    /// can forget, it is the only way <see cref="Load"/> ever returns. Both directions MUST go through
    /// here. Note <see cref="Loaded.Parse"/> exposes the RAW parse for structural concerns (identity
    /// remapping, anchors, spans) — its <c>Model</c> is deliberately NOT the desired model and must
    /// never be handed to a Diff/Reconcile; use <see cref="Loaded.Desired"/> for that.
    /// </para>
    /// </remarks>
    public static class DesiredModelLoader
    {
        /// <summary>The fully-prepared result of <see cref="Load"/>.</summary>
        public sealed class Loaded
        {
            internal Loaded(
                SceneModel desired,
                ParseResult parse,
                IReadOnlyDictionary<string, IReadOnlyDictionary<string, SourceSpan>> fieldArgumentSpans,
                IReadOnlyList<AssetEntry> harvestedAssets)
            {
                Desired = desired;
                Parse = parse;
                FieldArgumentSpans = fieldArgumentSpans;
                HarvestedAssets = harvestedAssets;
            }

            /// <summary>
            /// The DESIRED model: parsed, authored member paths resolved to serialized paths, and asset
            /// refs lowered to <c>(guid, fileId, typeHint)</c>. The ONLY model that may be fed to a
            /// Diff/Materialize/Reconcile.
            /// </summary>
            public SceneModel Desired { get; }

            /// <summary>
            /// The raw parse, for STRUCTURAL concerns only: <c>IdentityMap</c>, <c>Anchors</c>,
            /// <c>ComponentAnchors</c>, <c>FlagPresence</c>, <c>Handles</c>. Its <c>Model</c> is
            /// unresolved and unlowered — it is the right input for <c>IdentityRemapper</c> (which
            /// matches on structure) and the WRONG input for any value comparison.
            /// </summary>
            public ParseResult Parse { get; }

            /// <summary>
            /// The parse's field-argument spans, REMAPPED in lockstep with the member→serialized-path
            /// rewrite, so span-local field patching still matches post-resolution keys.
            /// </summary>
            public IReadOnlyDictionary<string, IReadOnlyDictionary<string, SourceSpan>> FieldArgumentSpans { get; }

            /// <summary>
            /// Every asset GUID resolved during lowering, paired with its CURRENT path — the caller
            /// merges these into the sidecar <c>Assets[]</c> so the cache stays a valid move-recovery
            /// source.
            /// </summary>
            public IReadOnlyList<AssetEntry> HarvestedAssets { get; }
        }

        /// <summary>
        /// Parses <paramref name="source"/> and returns a fully-prepared desired model. Every stage runs,
        /// always. <paramref name="existingMap"/> supplies both the prior identity entries (carried into
        /// the parse) and the <c>Assets[]</c> cache that lets a path stale from a move/rename recover its
        /// GUID.
        /// </summary>
        public static Loaded Load(string source, IdentityMap? existingMap)
        {
            var parse = ComponentTypeNormalizer.ParseAndNormalize(source, existingMap);

            // §M3: resolve transient member:<name> field keys to serialized paths BEFORE any diff,
            // remapping the field-argument spans in lockstep so span-local field patches still match.
            var (resolved, spans) = AuthoredPathResolver.Resolve(parse.Model, parse.FieldArgumentSpans);

            // A LOCATED pre-pass, over the desired-but-unlowered model (serialized paths already
            // resolved above, so the thrown message names 'm_Mesh', not 'member:mesh'): throws on the
            // first unresolvable Builtin(...) or authored built-in-container path, naming the object,
            // component and field. Must run BEFORE lowering — Core's AssetRefLowering never throws, and
            // the lowering-side builtinResolver below only ever receives (name, typeHint), so it cannot
            // locate an error itself.
            BuiltinRefValidator.Validate(resolved);

            // §M4: lower authored Asset("path") refs to their AssetDatabase (guid, fileId, typeHint)
            // BEFORE any diff, so Core compares on the authoritative GUID identity. GUID-authoritative:
            // a path stale from a move/rename recovers its GUID from the sidecar Assets[] cache; only a
            // GUID that maps to NOTHING (asset truly deleted) fails loud. Built-in refs route through
            // ResolveBuiltin — the always-on unlocated backstop the pre-pass above enriches.
            var assetResolver = new AssetReferenceResolver.LoweringResolver(existingMap?.Assets);
            var desired = AssetRefLowering.Lower(resolved, assetResolver.Resolve, assetResolver.ResolveBuiltin);

            return new Loaded(desired, parse, spans, assetResolver.Harvested);
        }
    }
}
