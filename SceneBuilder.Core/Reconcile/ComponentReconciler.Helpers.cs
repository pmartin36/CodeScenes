using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Reconcile
{
    // The ref-render / component-append / asset-harvest / component-key tail helpers shared across
    // ComponentReconciler's other partials. Split out of ComponentReconciler.cs for file-size
    // discipline, mirroring the existing `.AlignAxis`/`.Defaults`/`.ManagedRef`/`.UnityEvents`
    // partial split.
    internal static partial class ComponentReconciler
    {
        // The one place an ObjectRef resolves to its rendered bare token (a NodeHandle variable
        // name, `NodeHandle.None`, or the self-target expression) — shared by the generic
        // substitution above and ManagedReferenceEmission's per-member render, so a handle
        // introduction is registered exactly once per ref regardless of caller.
        internal static string RenderObjectRefToken(
            string? targetLogicalId,
            System.Func<string?, (string? Handle, bool Introduce)> resolveOwnerHandle,
            List<SourceEdit> edits,
            string ownerNodeLogicalId)
        {
            if (targetLogicalId == null)
            {
                return "NodeHandle.None";
            }

            // Checked BEFORE resolveOwnerHandle is consulted: resolveOwnerHandle is side-effecting
            // (it can register a handle introduction), and a self-target needs no handle at all —
            // consulting it first would introduce one that is never used.
            if (SelfReferenceEmission.IsSelf(targetLogicalId, ownerNodeLogicalId))
            {
                return SelfReferenceEmission.AuthoredExpression;
            }

            var (handle, introduce) = resolveOwnerHandle(targetLogicalId);
            if (introduce && handle != null)
            {
                edits.Add(new IntroduceHandle { Anchor = targetLogicalId, Handle = handle });
            }

            return handle ?? targetLogicalId;
        }

        // §13 one-pass attach reuses this for the just-appended owner: same
        // `{owner}/{Type}#{ordinal}` id scheme, same AppendComponentStatement + Component
        // AddedEntry shape as the mapped-owner ADD path above — kept in exactly one place.
        internal static void EmitComponentAppend(
            // The owner's id in the CURRENT source — what the applier resolves its statement by.
            string ownerAnchor,
            // The owner's id AFTER this batch. These diverge exactly when a handle is introduced for
            // a handle-less owner: the rewrite makes the `var` name its LogicalId, so BuilderParser
            // will key the new component "{handle}/{Type}#{ordinal}". Keying it off the ANCHOR instead
            // strands the component on an id the re-parsed source no longer contains.
            string ownerEffectiveId,
            string typeFullName,
            int ordinal,
            // Index among the owner's representable components — where the statement must be PLACED.
            // Distinct from `ordinal`, which counts only same-TYPED components and keys the id.
            int siblingIndex,
            FieldMap fields,
            // Reused verbatim from RenderFieldValue below — resolves/introduces a handle for
            // an ObjectRef field's target. Side-effecting; called only for a field actually emitted.
            System.Func<string?, (string? Handle, bool Introduce)> resolveOwnerHandle,
            ISet<string> resolvableTargets,
            // Same-batch create-candidate identities — a field targeting one of these is
            // PENDING, not dangling (converges on the guaranteed second Sync). Same set
            // ReconcileComponents/ClassifySnapshotRef consult; not a second liveness notion.
            ISet<string> pendingTargets,
            // Only appended to when `reportUnresolvable` — see below.
            List<Conflict> conflicts,
            // True only when this append has NO overlapping FIELD-VALUE DIFF pass to report a
            // dangling field for it (a genuinely-new object, DetectAppends). False for the
            // mapped-owner ADD-path call when the component is ALSO in source, because that overlap's
            // dangling fields are already reported by Detection 2 / the introduce branch — reporting
            // here too would double-report the SAME field.
            bool reportUnresolvable,
            string? ownerHandle,
            bool introduceOwnerHandle,
            List<SourceEdit> edits,
            List<IdentityMapEntry> addedEntries,
            List<AssetEntry>? addedAssets,
            // Catalogued AssetRef fields pre-render into FieldExpressions the same way
            // ObjectRef fields already do — extends the existing pre-render gate below.
            AssetCatalog? assetCatalog = null,
            // Filter `fields` to the subset off the type's constructed default BEFORE the
            // ObjectRef pre-render loop below, so filteredFields/FieldExpressions are built from the
            // emitted set — a component all of whose fields are at default appends as a bare
            // `.Component<T>()`. `null` = Index.Empty = keep every
            // field (every hand-built test call stays green unchanged).
            ComponentDefaultOmission.Index? defaults = null)
        {
            var componentLogicalId = ComponentTargetResolution.ComposeLogicalId(ownerEffectiveId, typeFullName, ordinal);

            // A UnityEvent listener list has no `.Set(key, literal)` form (SourceExpr.ValueNodeLiteral
            // has no listener-literal arm) — it is authored as .OnClick(...)/.OnEvent(...) instead, on
            // the owner's own component-reference expression, never inline in an append's field set.
            // Strip it here so the append renders the rest of the component and converges the
            // listener itself on the mapped-owner listener-delta pass the following Sync (the owner is
            // mapped in-memory by this append's own addedEntries entry below).
            if (fields.Any(f => f.Value is ValueNode.UnityEventListeners))
            {
                fields = new FieldMap(fields.Where(f => f.Value is not ValueNode.UnityEventListeners));
            }

            fields = ComponentDefaultOmission.OmitDefaults(typeFullName, fields, defaults, componentLogicalId, conflicts);

            // The four-arm Unemittable/Dangling/Pending/Resolvable dispatch, its one owner shared
            // with the prefab-instance added-component append site.
            var (emittedFields, fieldExpressions) = SnapshotFieldEmission.EmitFieldSet(
                fields, typeFullName, componentLogicalId, ownerAnchor,
                resolvableTargets, pendingTargets, resolveOwnerHandle,
                reportUnresolvable, assetCatalog, edits, conflicts);

            edits.Add(new AppendComponentStatement
            {
                Anchor = ownerAnchor,
                ComponentLogicalId = componentLogicalId,
                TypeFullName = typeFullName,
                NewSiblingIndex = siblingIndex,
                Fields = emittedFields,
                FieldExpressions = fieldExpressions,
                OwnerHandle = ownerHandle,
                IntroduceOwnerHandle = introduceOwnerHandle,
            });

            addedEntries.Add(new IdentityMapEntry
            {
                LogicalId = componentLogicalId,
                GlobalObjectId = "",
                Kind = "Component",
                ComponentType = typeFullName,
                ParentLogicalId = ownerEffectiveId,
            });

            if (addedAssets != null)
            {
                foreach (var (_, value) in emittedFields)
                {
                    CollectAssetEntries(value, addedAssets);
                }
            }
        }

        // Asked ONLY of two values that already compare EQUAL: does the source's authored TEXT still
        // reflect the snapshot, or is it stale and in need of re-emission?
        //
        // It exists for exactly one case, by design. AssetRef identity is (Guid, FileId, IsBuiltin) —
        // DisplayPath and TypeHint are deliberately non-authoritative — so a MOVED/RENAMED asset (or a
        // built-in whose live-derived name/qualifier changed) is identity-EQUAL to its source ref while
        // the authored text in the source no longer reflects it. Equality alone would skip it and the
        // stale text would never be rewritten. Every other ValueNode kind determines its own emission,
        // so equality there already implies identical text and this returns true.
        //
        // The emitted text is a function of (DisplayPath, IsBuiltin, TypeHint, SubAsset): a built-in
        // renders as `Builtin("name")` / `Builtin("name", "Qualifier")`, a project asset ref as
        // `Asset("path")` / `Asset("path", "subName")` (SourceExpr.ValueNodeLiteral).
        //
        // The inverse case — text equal but identity different (same path, different sub-object fileId)
        // — is why this is an ADDITIONAL condition on top of Equals and never a replacement for it.
        private static bool AuthoredTextIsCurrent(ValueNode source, ValueNode snapshot)
        {
            switch (source, snapshot)
            {
                case (ValueNode.AssetRef(var a), ValueNode.AssetRef(var b)):
                    if (a is null || b is null)
                    {
                        return (a is null) == (b is null);
                    }

                    return string.Equals(a.DisplayPath, b.DisplayPath, System.StringComparison.Ordinal)
                        && a.IsBuiltin == b.IsBuiltin
                        && string.Equals(a.TypeHint, b.TypeHint, System.StringComparison.Ordinal)
                        && string.Equals(a.SubAsset, b.SubAsset, System.StringComparison.Ordinal);

                case (ValueNode.List la, ValueNode.List lb):
                    if (la.Items.Count != lb.Items.Count)
                    {
                        return false;
                    }

                    for (var i = 0; i < la.Items.Count; i++)
                    {
                        if (!AuthoredTextIsCurrent(la.Items[i], lb.Items[i]))
                        {
                            return false;
                        }
                    }

                    return true;

                case (ValueNode.Nested na, ValueNode.Nested nb):
                    foreach (var (key, value) in na.Fields)
                    {
                        if (!nb.Fields.TryGetValue(key, out var other) || !AuthoredTextIsCurrent(value, other))
                        {
                            return false;
                        }
                    }

                    return true;

                default:
                    return true;
            }
        }

        // Single choke-point harvest of every populated AssetRef reachable from a
        // snapshot ValueNode flowing into an emitted source edit. Cleared (AssetRef(null)),
        // empty-Guid, and built-in refs contribute nothing — a built-in's DisplayPath is a
        // live-derived object name, never a project path, and has no place in the asset
        // sidecar cache. Recurses into List/Nested so no caller has to special-case container
        // shapes.
        internal static void CollectAssetEntries(ValueNode node, List<AssetEntry> sink)
        {
            foreach (var (_, value) in ValueWalk.Enumerate(node))
            {
                if (value is ValueNode.AssetRef(var r) && r != null && !r.IsBuiltin && !string.IsNullOrEmpty(r.Guid))
                {
                    sink.Add(new AssetEntry { Guid = r.Guid, LastKnownPath = r.DisplayPath, TypeHint = r.TypeHint });
                }
            }
        }

        internal static bool IsTransform(ComponentData component) => component.Type.FullName == "UnityEngine.Transform";

        internal static ComponentData[] ExcludeTransform(ComponentData[] components) =>
            components.Where(c => !IsTransform(c)).ToArray();

        // The ordinal-within-type rule: each component's key pairs its type with the count of
        // same-type components before it in the array, not its array index. Mirrors
        // Differ.ComputeComponentKeys (Diff/Differ.cs:380-393), a separate private copy for that
        // module. This copy is the shared rule for the reconcile module: consumed here and by
        // ReconcilerInstances.Nested.cs:177.
        internal static (string TypeFullName, int Ordinal)[] ComputeComponentKeys(ComponentData[] components)
        {
            var keys = new (string TypeFullName, int Ordinal)[components.Length];
            var ordinalByType = new Dictionary<string, int>();
            for (var i = 0; i < components.Length; i++)
            {
                var typeFullName = components[i].Type.FullName;
                var ordinal = ordinalByType.TryGetValue(typeFullName, out var count) ? count : 0;
                ordinalByType[typeFullName] = ordinal + 1;
                keys[i] = (typeFullName, ordinal);
            }

            return keys;
        }
    }
}
