using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Diff;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;

namespace SceneBuilder.Core.Reconcile
{
    // Detects conflicts that cannot be resolved into a SourceEdit, and owns the ONE definition of
    // the hazard behind most of them: sibling statements distinguishable only by their POSITION.
    //
    // Its consumers deliberately apply DIFFERENT policies to the same detection, which is why the
    // detection lives here rather than in any one of them:
    //   * BuilderParser.ParseCore  -> reports it on every parse (ParseResult.Ambiguities). The one
    //                                 call both directions reach, so neither can skip the check.
    //   * SceneBuilderBuild        -> REFUSES. Code->scene has no way to guess correctly.
    //   * Reconciler               -> HEALS, by injecting `.Id(...)` (scene->code writes the file).
    //   * DetectAmbiguousReorders  -> surfaces the one case an id cannot rescue: the positional
    //                                 mapping is already scrambled, so there is nothing sound to pin.
    internal static class ConflictDetector
    {
        // THE definition of "this node is distinguishable only by POSITION": its LogicalId encodes
        // its own name + sibling index, i.e. it has neither an authored handle (`var x = ...`, which
        // makes the id the var name) nor an explicit `.Id("...")` (which makes the id that literal).
        // Both of those live IN the statement; a sibling index is only IMPLIED BY the statement's
        // position, which is exactly why a positional id does not survive a statement move.
        //
        // ONE definition, shared by every consumer — the parser's ambiguity report
        // (BuilderParser.ParseCore), the reorder conflict below, and the Reconciler's `.Id(...)`
        // injection — so all three agree on what is ambiguous BY CONSTRUCTION rather than by three
        // hand-kept-in-sync copies of the same shape check.
        internal static bool IsPositional(GameObjectNode node, string? parentLogicalId) =>
            LogicalIdResolver.TryParseSynthesized(node.LogicalId, parentLogicalId, out var parsedName, out _)
            && parsedName == node.Name;

        // Every sibling group that CANNOT be told apart: >= 2 siblings sharing a name whose ids are
        // all positional. Yielded per parent level, pre-order.
        //
        // NOTE the threshold is ">= 2 POSITIONAL members", not "all members are positional". Three
        // siblings named "Enemy" of which one carries an explicit `.Id(...)` still leave TWO that are
        // only distinguishable by position — an `All(...)` test scores that group unambiguous and
        // walks straight past a live instance of the very defect it exists to catch.
        internal static IEnumerable<(string? ParentLogicalId, string Name, GameObjectNode[] PositionalMembers)>
            AmbiguousGroups(SceneModel model)
        {
            var results = new List<(string?, string, GameObjectNode[])>();
            Walk(model.Roots, null);
            return results;

            void Walk(GameObjectNode[] siblings, string? parentLogicalId)
            {
                foreach (var group in siblings.GroupBy(n => n.Name))
                {
                    // Prefab instances are identity-keyed by their persisted
                    // (TargetPrefabId, TargetObjectId) pair-key (spec 07), not by sibling name/position,
                    // so they are exempt from the positional-sibling ambiguity rule (spec 16). This
                    // exemption lives HERE (the one shared chokepoint) so both DuplicateNameConflicts
                    // (build-refusal) and DetectAmbiguousReorders inherit it uniformly.
                    var positional = group.Where(n => n is not PrefabInstanceNode && IsPositional(n, parentLogicalId)).ToArray();
                    if (positional.Length >= 2)
                    {
                        results.Add((parentLogicalId, group.Key, positional));
                    }
                }

                foreach (var node in siblings)
                {
                    Walk(node.Children, node.LogicalId);
                }
            }
        }

        // The parse-time report (§7: fail loud, located). Computed on EVERY BuilderParser.Parse — the
        // one call both directions reach — so no caller can route around the detection. Parse does
        // NOT throw on these: Sync must be able to parse an ambiguous file in order to HEAL it by
        // injecting `.Id(...)`. The policy is the consumer's: Build refuses, Sync heals.
        internal static IReadOnlyList<Conflict> DuplicateNameConflicts(
            SceneModel model,
            IReadOnlyDictionary<string, SourceSpan> anchors)
        {
            var conflicts = new List<Conflict>();

            foreach (var (_, name, members) in AmbiguousGroups(model))
            {
                var idList = string.Join("', '", members.Select(n => n.LogicalId));
                conflicts.Add(new Conflict
                {
                    Kind = ConflictKind.AmbiguousAnchor,
                    LogicalId = members[0].LogicalId,
                    Reason =
                        $"Ambiguous duplicate sibling name: {members.Length} siblings named '{name}' ('{idList}') have " +
                        "neither a handle nor an explicit `.Id(\"...\")`, so they are distinguishable only by their " +
                        "position in the file. Any edit that moves a statement would silently re-point identity at a " +
                        "different object. Add `.Id(\"...\")` to each to disambiguate them.",
                    Location = anchors.TryGetValue(members[0].LogicalId, out var span) ? span : null,
                });
            }

            return conflicts;
        }

        // Colliding LogicalIds: two or more nodes whose authored `var` handle / explicit
        // `.Id(...)` resolve to the SAME id. Unlike DuplicateNameConflicts (positional-only ids,
        // always `Name/index`), a collision here is on a HAND-AUTHORED id, so it is disjoint by
        // construction from that detector — a positional id can never appear in this grouping.
        // Ids are GLOBAL identity (not scoped per-parent), so `nodeAnchors` — the un-collapsed,
        // whole-file, document-order list (unlike ParseResult.Anchors, a dict that silently
        // collapses collisions to one entry) — is grouped across the entire file.
        internal static IReadOnlyList<Conflict> DuplicateLogicalIdConflicts(IReadOnlyList<NodeAnchor> nodeAnchors)
        {
            var order = new List<string>();
            var groups = new Dictionary<string, List<NodeAnchor>>(StringComparer.Ordinal);

            foreach (var anchor in nodeAnchors)
            {
                if (!groups.TryGetValue(anchor.LogicalId, out var members))
                {
                    members = new List<NodeAnchor>();
                    groups[anchor.LogicalId] = members;
                    order.Add(anchor.LogicalId);
                }

                members.Add(anchor);
            }

            var conflicts = new List<Conflict>();

            foreach (var logicalId in order)
            {
                var members = groups[logicalId];
                if (members.Count < 2)
                {
                    continue;
                }

                conflicts.Add(new Conflict
                {
                    Kind = ConflictKind.DuplicateLogicalId,
                    LogicalId = logicalId,
                    Reason =
                        $"Duplicate LogicalId '{logicalId}': {members.Count} nodes resolve to the same id. " +
                        "Explicit `.Id(\"...\")` values and `var` handles are a GLOBAL identity, not scoped " +
                        "per-parent, so the same id must not be reused anywhere in the file.",
                    Location = members[1].Span,
                });
            }

            return conflicts;
        }

        public static (IReadOnlyList<Conflict> Conflicts, ISet<string> Suppressed) DetectAmbiguousReorders(
            SceneModel model,
            ChangeSet changeSet,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            IReadOnlyDictionary<string, string>? logicalIdToGlobalObjectId = null)
        {
            var reorderedIds = new HashSet<string>(changeSet.Ops.OfType<Reorder>().Select(op => op.LogicalId));
            var conflicts = new List<Conflict>();
            var suppressed = new HashSet<string>();

            foreach (var (_, name, members) in AmbiguousGroups(model))
            {
                if (!members.Any(n => reorderedIds.Contains(n.LogicalId)))
                {
                    continue;
                }

                var reorderedMember = members.First(n => reorderedIds.Contains(n.LogicalId));
                var idList = string.Join("', '", members.Select(n => n.LogicalId));

                conflicts.Add(new Conflict
                {
                    Kind = ConflictKind.AmbiguousAnchor,
                    LogicalId = reorderedMember.LogicalId,
                    GlobalObjectId = logicalIdToGlobalObjectId != null
                        && logicalIdToGlobalObjectId.TryGetValue(reorderedMember.LogicalId, out var goid)
                        ? goid
                        : null,
                    Reason = $"Ambiguous reorder: siblings '{idList}' share name '{name}' with synthesized ids; positional anchor cannot be localized.",
                    Location = anchors.TryGetValue(reorderedMember.LogicalId, out var span) ? span : null,
                });

                foreach (var member in members)
                {
                    suppressed.Add(member.LogicalId);
                }
            }

            return (conflicts, suppressed);
        }

        public static Conflict MissingAnchor(string logicalId, string? globalObjectId) =>
            new()
            {
                Kind = ConflictKind.MissingSourceAnchor,
                LogicalId = logicalId,
                GlobalObjectId = globalObjectId,
                Reason = $"No source anchor for LogicalId '{logicalId}' (object exists in scene but has no builder statement).",
                Location = null,
            };

        public static Conflict UnanchorableDelete(string logicalId, string? globalObjectId) =>
            new()
            {
                Kind = ConflictKind.MissingSourceAnchor,
                LogicalId = logicalId,
                GlobalObjectId = globalObjectId,
                Reason = $"Cannot remove '{logicalId}': no source anchor/statement to delete (structural change not anchorable to a builder construct).",
                Location = null,
            };

        public static Conflict DanglingReference(
            string sourceLogicalId, string fieldPath, string? missingTarget, SourceSpan? location) =>
            new()
            {
                Kind = ConflictKind.DanglingReference,
                LogicalId = sourceLogicalId,
                Reason = $"Dangling reference: field '{fieldPath}' on '{sourceLogicalId}' targets " +
                    $"'{missingTarget ?? "<unknown>"}', which no longer exists in the scene. The field was NOT " +
                    "silently cleared — restore the target, or set the reference to NodeHandle.None.",
                Location = location,
            };

        // The source prefab's default for an overridden property drifted since the override
        // was recorded, and the live instance value now sits at the NEW default. Named
        // "instance > target > propertyPath" per #9/checklist #6. The detection logic itself lives
        // in InstanceOverrideDiff.DetectStaleOverrides, which calls this factory.
        public static Conflict StaleOverride(
            string? instanceLogicalId, OverrideTarget target, string propertyPath,
            ValueNode recordedBase, ValueNode currentBase, SourceSpan? location = null) =>
            new()
            {
                Kind = ConflictKind.StaleOverride,
                LogicalId = instanceLogicalId,
                Reason = $"Stale override '{instanceLogicalId} > {target.ComponentType} > {propertyPath}': " +
                    $"the source prefab's default changed (recorded '{recordedBase}', now '{currentBase}') and the " +
                    "instance value now equals the new default. Not silently kept or dropped — confirm whether to " +
                    "keep the override or accept the new default.",
                Location = location,
            };

        // A prefab-instance root's RectTransform layout has
        // diverged from what its authoring construct can persist — either the prefab ASSET's own
        // layout (a known, diverged baseline) or, when that baseline is unresolvable, any live value
        // at all (§7: unknown never goes quiet). ONE conflict per NODE per sync (not per field) —
        // Reconciler.RectTransform.cs's EmitRectTransformEdits/SplitCreatedPayload call this once
        // with every unpersisted field's ArgName, never per-field. `fieldArgNames` is in canonical
        // RectTransformFields.All order.
        public static Conflict UnlocalizableRectTransform(
            string logicalId, string? globalObjectId, IEnumerable<string> fieldArgNames, SourceSpan? location) =>
            new()
            {
                Kind = ConflictKind.UnlocalizableRectTransform,
                LogicalId = logicalId,
                GlobalObjectId = globalObjectId,
                Reason = $"Cannot write RectTransform layout back to source for prefab instance '{logicalId}' " +
                    $"(fields: {string.Join(", ", fieldArgNames)}): the live layout is not the prefab asset's own, " +
                    "and a `.Instance(...)` statement has no `.RectTransform(...)` call to hold it (§7). It was NOT " +
                    "written. Move the layout into the prefab asset, or author the object with `scene.Add(...)`.",
                Location = location,
            };

        // A nested prefab-instance node's live name diverged from its source. A `PrefabInstanceNode`
        // has no independent authoring representation for "name" -- its statement is always
        // `Instance(<path>)` / `Instance(Prefabs.X)`, whose first argument is the source path, not a
        // name. Rewriting that argument as a name would repoint the reference at a nonexistent asset,
        // so the rename is surfaced here instead and NO edit is emitted (Reconciler.cs `case SetName`).
        public static Conflict UnrepresentableInstanceRename(string logicalId, string? globalObjectId, SourceSpan? location) =>
            new()
            {
                Kind = ConflictKind.UnrepresentableInstanceRename,
                LogicalId = logicalId,
                GlobalObjectId = globalObjectId,
                Reason = $"Cannot rename prefab instance '{logicalId}': its `.Instance(...)` statement's first " +
                    "argument is the source path, not a name, so a nested instance has no independent authoring " +
                    "representation for its name. The live rename was NOT written back to source.",
                Location = location,
            };

        // A live `[SerializeReference]` value's recorded `managedReferenceFullTypename` resolves to
        // no loadable C# type (the concrete type was renamed or removed). A STANDING condition of
        // the scene+source (recurs on every reconcile until the type is restored or the field is
        // re-authored), so it is recurrence-keyed and routes into ReconcileResult.Notes, not
        // Conflicts. The source is NOT patched and the reference is NOT nulled — either would
        // discard the live value with no way to recover it once the type is available again.
        public static Conflict ManagedReferenceMissingType(
            string componentLogicalId, string componentTypeFullName, string fieldKey, string fullTypename,
            SourceSpan? location) =>
            Conflict.Unrepresentable(
                componentLogicalId, componentTypeFullName, fieldKey,
                "The [SerializeReference] field's live type '" + fullTypename + "' resolves to no loadable " +
                "C# type (renamed or removed). The source was NOT patched and the reference was NOT nulled.",
                recurrenceKey: $"managed-ref-missing-type:{componentLogicalId}:{fieldKey}", location);

        public static Conflict UnanchorableComponentEdit(string componentLogicalId, string editKind) =>
            new()
            {
                Kind = ConflictKind.MissingSourceAnchor,
                LogicalId = componentLogicalId,
                GlobalObjectId = null,
                Reason = $"Cannot {editKind} for component '{componentLogicalId}': not localizable to a single source construct (§7).",
                Location = null,
            };

        // A member of a nested field's struct/class has no compiling emission form (see
        // NestedValueEmission.Project). The member is excluded from the emitted initializer; the
        // rest of the struct still round-trips. Never raised for a member at its type default, and
        // never raised for a pass that ends up emitting nothing for the field.
        public static Conflict UnrepresentableNestedMember(
            string componentLogicalId, string componentTypeFullName, string fieldKey, string memberPath,
            SourceSpan? location) =>
            Conflict.Unrepresentable(
                componentLogicalId, componentTypeFullName, $"{fieldKey}.{memberPath}",
                "No compiling initializer form exists for this member, so it was left out of the " +
                "emitted initializer; the rest of the value's members still round-trip.",
                recurrenceKey: null, location);

        // EVERY member of a nested field that differs from the type default has no compiling
        // emission form, so the field projects to nothing: no site emits an edit for it, and
        // without this the live value is discarded with no edit, no conflict and no trace. Distinct
        // from UnrepresentableNestedMember above, which reports members left OUT of a value that is
        // still being emitted. The recurrence key is INSTANCE-scoped: at least one excluded member is
        // provably non-default (an AssetRef/ObjectRef), which is a fact of this component, not of the
        // type, so a second component of the same type with its own drift gets its own report.
        public static Conflict UnrepresentableNestedField(
            string componentLogicalId, string componentTypeFullName, string fieldKey,
            IEnumerable<string> memberPaths, SourceSpan? location) =>
            Conflict.Unrepresentable(
                componentLogicalId, componentTypeFullName, fieldKey,
                $"Every member that differs from the type default ({string.Join(", ", memberPaths)}) has no " +
                "compiling initializer form, so there is nothing to write. Author it in the scene's prefab, " +
                "or move the value to a field the builder can express.",
                recurrenceKey: $"unrepresentable-field:{componentLogicalId}:{fieldKey}", location);

        // A nested field's ONLY excluded members are Unsupported placeholders (no public spelling,
        // e.g. Button.m_OnClick) -- a placeholder's marker text names the member but never its value,
        // so no INSTANCE can ever prove it changed. This is a capability fact of (component type,
        // field), never of the instance, so the recurrence key is TYPE-scoped: a stock scene with N
        // components of this type costs one surfaced report, not N.
        public static Conflict UnrepresentableNestedFieldMembers(
            string componentLogicalId, string componentTypeFullName, string fieldKey,
            IEnumerable<string> memberPaths, SourceSpan? location) =>
            Conflict.Unrepresentable(
                componentLogicalId, componentTypeFullName, fieldKey,
                $"Its serialized members ({string.Join(", ", memberPaths)}) have no compiling initializer " +
                "form, so no value for this field can be written to code.",
                recurrenceKey: $"unrepresentable-type-field:{componentTypeFullName}:{fieldKey}", location);

        // A List field reaches, at any depth, an item with no compiling emission form (see
        // ListValueEmission.HasUnemittableItem). Dropping just that item would shift every later
        // item and silently move the other references, so the WHOLE field is left unwritten
        // instead — never a partial array. Self-heals once the item's own condition resolves (the
        // usual cause is a scene object not yet in the IdentityMap, which the same pass creates).
        public static Conflict UnrepresentableListItem(
            string componentLogicalId, string componentTypeFullName, string fieldKey, SourceSpan? location) =>
            Conflict.Unrepresentable(
                componentLogicalId, componentTypeFullName, fieldKey,
                "A list item has no compiling emission form, and dropping it would shift every later " +
                "item, so no value for this field can be written to code.",
                recurrenceKey: $"unrepresentable-list-item:{componentLogicalId}:{fieldKey}", location);

        // A closed-grammar component (FitSize/SurfaceSnap) has a live field that returned to
        // its type default, but its dedicated fluent call throws on an empty argument list -- there
        // is no compiling form for "removed". Reuses MissingSourceAnchor's kind: same family as
        // UnanchorableComponentEdit ("no source construct can express this edit"), just with a
        // located span when one is available.
        public static Conflict UnremovableClosedGrammarField(
            string componentLogicalId, string fieldKey, SourceSpan? location) =>
            new()
            {
                Kind = ConflictKind.MissingSourceAnchor,
                LogicalId = componentLogicalId,
                GlobalObjectId = null,
                Reason = $"Cannot remove field '{fieldKey}' for component '{componentLogicalId}': its closed-grammar " +
                    "authoring call (FitSize/SurfaceSnap) has no compiling form for an empty argument list. The " +
                    "live value returned to its default; the setter was NOT removed and NOT patched (§7).",
                Location = location,
            };
    }
}
