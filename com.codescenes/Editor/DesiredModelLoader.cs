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
                IReadOnlyList<AssetEntry> harvestedAssets,
                IReadOnlyList<Conflict> bootstrapConflicts,
                IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> authoredSelectorNames,
                IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> overrideAuthoredSelectorNames)
            {
                Desired = desired;
                Parse = parse;
                FieldArgumentSpans = fieldArgumentSpans;
                HarvestedAssets = harvestedAssets;
                BootstrapConflicts = bootstrapConflicts;
                AuthoredSelectorNames = authoredSelectorNames;
                OverrideAuthoredSelectorNames = overrideAuthoredSelectorNames;
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

            /// <summary>
            /// m-nested-props b7-t2: located conflicts from <see cref="NestedOverrideBootstrap.Resolve"/>
            /// — a nested override/added/removed target whose <c>ChildPath</c> resolved to no live
            /// sub-object under a LIVE prefab instance root. Empty when nothing was located (including
            /// when <c>existingMap</c> was null — no sidecar means no instance is live yet).
            /// </summary>
            public IReadOnlyList<Conflict> BootstrapConflicts { get; }

            /// <summary>
            /// spec 54: componentLogicalId -&gt; (resolved field key -&gt; the AUTHORED selector
            /// identifier text), from <see cref="AuthoredPathResolver"/>'s member:&lt;name&gt; rewrite
            /// — the one channel that still tells Reconcile's diff-independent self-heal a field was
            /// authored as a typed selector at all, since <see cref="Desired"/> only ever carries the
            /// resolved serialized path.
            /// </summary>
            public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> AuthoredSelectorNames { get; }

            /// <summary>
            /// spec 54: instanceLogicalId -&gt; (<see cref="SceneBuilder.Core.Reconcile.OverrideSelectorKey.For"/>
            /// -&gt; the AUTHORED override selector identifier text), the override-path analogue of
            /// <see cref="AuthoredSelectorNames"/> — feeds Reconcile's override converged-skip self-heal.
            /// </summary>
            public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> OverrideAuthoredSelectorNames { get; }
        }

        /// <summary>
        /// Parses <paramref name="source"/> and returns a fully-prepared desired model. Every stage runs,
        /// always. <paramref name="existingMap"/> supplies both the prior identity entries (carried into
        /// the parse) and the <c>Assets[]</c> cache that lets a path stale from a move/rename recover its
        /// GUID. <paramref name="facadeCatalog"/> (b5-t5) resolves typed prefab façade forms
        /// (<c>Instance(Prefabs.X)</c>/<c>.On(sel =&gt; ...)</c>) during the parse; null (the default)
        /// keeps every existing 2-arg caller compiling unchanged and falls back to plain string forms.
        /// </summary>
        public static Loaded Load(string source, IdentityMap? existingMap, FacadeCatalog? facadeCatalog = null, AssetCatalog? assetCatalog = null)
        {
            var parse = ComponentTypeNormalizer.ParseAndNormalize(source, existingMap, facadeCatalog, assetCatalog);

            // §M3: resolve transient member:<name> field keys to serialized paths BEFORE any diff,
            // remapping the field-argument spans in lockstep so span-local field patches still match.
            var pathResolution = AuthoredPathResolver.Resolve(parse.Model, parse.FieldArgumentSpans, parse.Usings);
            var resolved = pathResolution.Model;
            var spans = pathResolution.Spans;

            // Normalize every enum-backed field (raw int OR an already-typed Enum node) to the SAME
            // canonical shape SerializedFieldBridge's read produces, via the SAME SerializedMemberMap
            // resolver — must run AFTER path resolution (it needs serialized paths, e.g. 'm_RenderMode',
            // not 'member:renderMode') and BEFORE lowering/diffing, so both directions inherit it and a
            // raw `.Set("m_RenderMode", 0)` never churns against a typed scene-side read.
            resolved = SerializedEnumNormalizer.Normalize(resolved);

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
            // ResetCatalogStampedAssetRefs runs first: a typed `Assets.<Group>...<Leaf>` catalog
            // chain (ValueNodeParser.TryParseAssetChain) stamps a Guid straight from the catalog at
            // PARSE time, paired with the catalog's own main-asset LOOKUP FileId placeholder — never
            // the live AssetDatabase-native identity Lower's resolver computes. Trusting that stamp
            // here would skip real resolution and leave the desired model's FileId permanently
            // disagreeing with a materialized snapshot's freshly-read one — a non-converging sync
            // (same failure mode this type's own remarks document above for an unlowered ref).
            var assetResolver = new AssetReferenceResolver.LoweringResolver(existingMap?.Assets);
            var desired = AssetRefLowering.Lower(
                AssetRefLowering.ResetCatalogStampedAssetRefs(resolved),
                (path, subName) => assetResolver.Resolve(path, subName),
                assetResolver.ResolveBuiltin,
                (path, subName, field) => assetResolver.Resolve(path, subName, field));

            // b5-t3: lower PrefabInstanceNode.SourcePrefab the same way — a display path resolved to
            // its (guid, fileId, typeHint) via the SAME harvesting resolver, so the sidecar Assets[]
            // gains a TypeHint="Prefab" row through the existing AssetCacheMerge at the caller.
            var prefabLowered = PrefabRefLowering.Lower(desired, assetResolver.ResolvePrefabSource);
            desired = prefabLowered.Model;

            // m-nested-props b7-t2: stamp every nested (below-root) Target's real SubKey from its live
            // sub-object BEFORE Rehydrate (so BaseValue disambiguation, spec #13, sees real keys) and
            // BEFORE any diff/materialize/reconcile (so a converged nested override neither erases on
            // rebuild nor churns on sync — see NestedOverrideBootstrap). Wired ONCE here so BOTH
            // directions inherit it by default. Guarded on existingMap != null: no sidecar means no
            // instance is live yet, so there is nothing to resolve against.
            IReadOnlyList<Conflict> bootstrapConflicts = System.Array.Empty<Conflict>();
            if (existingMap is not null)
            {
                desired = NestedOverrideBootstrap.Resolve(desired, existingMap, out bootstrapConflicts);
            }

            // M10 (b6-t2 rehydrate seam): thread each persisted InstanceOverrideRecord.BaseValue back
            // onto the matching desired PropertyOverride.BaseValue, so DetectStaleOverrides sees a
            // non-null desired BaseValue through the real adapter, not just hand-built Core POCOs.
            // Guarded on existingMap != null: the first Build has no sidecar yet, nothing to rehydrate.
            if (existingMap is not null)
            {
                desired = InstanceOverrideRehydrator.Rehydrate(desired, existingMap);
            }

            // spec 37: promote a node hosting a [RequireComponent(typeof(RectTransform))] component
            // to Kind=="RectTransform" (RectTransformFields defaults) so the differ's matched
            // omit-at-default arm runs for it instead of the D6 promotion arm. Runs last: it touches
            // only TransformData.Kind and the five rect fields, independent of the asset/enum/override
            // stages above. Wired ONCE here so both directions inherit it by default.
            desired = RectTransformPromotion.Promote(desired, RequireComponentPredicate.RequiresRectTransform);

            return new Loaded(
                desired, parse, spans, assetResolver.Harvested, bootstrapConflicts,
                pathResolution.AuthoredSelectorNames, pathResolution.OverrideAuthoredSelectorNames);
        }
    }
}
