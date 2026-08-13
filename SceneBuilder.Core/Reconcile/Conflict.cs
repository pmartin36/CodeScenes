using System;
using System.Collections.Generic;
using System.Linq;

namespace SceneBuilder.Core.Reconcile
{
    public enum ConflictKind
    {
        AmbiguousAnchor,
        MissingSourceAnchor,
        ReferencedHandle,
        DuplicateLogicalId,
        // A source handle's target vanished from the scene (Detection 1), or a snapshot target
        // resolves to nothing live (Detection 2) — see ComponentReconciler's FIELD-VALUE DIFF pass.
        DanglingReference,

        // b3-t3: the source prefab's default for an overridden property drifted (the override's
        // recorded BaseValue no longer matches the snapshot's current BaseValue) AND the live
        // instance value now equals the NEW default — ambiguous whether the user wants to keep the
        // override or adopt the new default. Neither silently kept nor dropped (spec #9).
        StaleOverride,

        // b4-t1: Instance(Prefabs.X) where X is absent from the FacadeCatalog (or the catalog
        // is null). Located over the `Prefabs.X` argument span. Never a throw, never a silent
        // drop — the instance node is still produced.
        UnknownFacadeReference,

        // b7-t2: a below-root override/added-component/removed-component/removed-child Target (or
        // AddedGameObject.Parent) whose ChildPath resolves to NO live sub-object under a LIVE prefab
        // instance root (see NestedOverrideBootstrap). Never a silent drop.
        UnresolvedNestedSelector,

        // m-ui-recttransform b4-t1: a matched node's RectTransform layout differs but the node's
        // authoring construct cannot host a `.RectTransform(...)` call (a PrefabInstanceNode root --
        // InstanceHandle has no such member, see InstanceHandle.cs). Emitting the edit would produce
        // source that does not compile, so it is surfaced as a located Conflict instead and NO edit
        // is emitted.
        UnlocalizableRectTransform,

        // A member of a nested struct/class field has no compiling emission form (an asset or
        // scene-object reference held inside the struct, or a serialized member with no public
        // spelling) and is at a NON-default value. Emitting it would produce source that does not
        // compile, so the member is excluded from the initializer and reported here instead. Never
        // raised for a member AT its type default (nothing was excluded that a user would notice).
        UnrepresentableValue,

        // A component field's declared reference type name matched two or more loaded types, so it
        // resolved to none -- never guessed. Constructed only through Conflict.AmbiguousTypeName,
        // which requires the object anchor, the component type full name and the field/member key.
        AmbiguousTypeName,

        // A component field the adapter never treats as author intent was AUTHORED in source. The
        // build emits no op for it and reports it instead. Constructed only through
        // Conflict.UnauthorableField, which requires the object anchor, the component type full
        // name and the field key.
        UnauthorableField,

        // m8: one persistent listener could not be synced in either direction (empty method name,
        // an unresolved target/object argument, Unity's legal "Missing" target, or no public C#
        // spelling for the event field). Constructed only through Conflict.UnsyncableListener,
        // which requires the component anchor, the component type full name, the event field key,
        // the listener's index and a ListenerReportReason.
        UnsyncableListener,

        // A nested prefab-instance node's live name diverged from its source. A `PrefabInstanceNode`
        // has no independent authoring representation for "name" -- it is always emitted as
        // `Instance(<path>)` / `Instance(Prefabs.X)`, and that statement's first argument IS the
        // path, not a name. Rewriting it as though it were a name would corrupt the reference. Never
        // silently applied; the builder statement is left untouched.
        UnrepresentableInstanceRename,
    }

    // Why one persistent listener is left alone in BOTH directions. Required (no default) at
    // Conflict.UnsyncableListener, so a new condition must add an inhabitant here rather than
    // spell its own report.
    public enum ListenerReportReason
    {
        EmptyMethodName,
        UnresolvedTarget,
        UnresolvedObjectArgument,
        MissingTarget,
        UnresolvedEventMemberName,
    }

    public sealed record Conflict
    {
        private readonly ConflictKind _kind;
        private readonly string _reason = "";

        // Records lose the implicit parameterless constructor once any constructor is declared --
        // kept explicitly so a plain object initializer (a non-guarded kind) still compiles, e.g.
        // com.codescenes/Editor/NestedOverrideBootstrap.cs's adapter-side construction.
        public Conflict() { }

        // The ONE door that can set a guarded kind, used only by FromReport. Bypasses the Kind
        // init guard by writing the backing field directly.
        private Conflict(ConflictKind kind) { _kind = kind; }

        public string? LogicalId { get; init; }
        public string? GlobalObjectId { get; init; }

        // The kinds whose located data is REQUIRED: constructible only through Unrepresentable,
        // AmbiguousTypeName, UnauthorableField or UnsyncableListener, none of which can be called
        // without a LocatedReport. Adding a kind here makes every other construction of it throw
        // -- deterministically, on first execution, not data-dependently. Internal (not private)
        // so the located-kind sweep in SceneBuilder.Core.Tests derives its set from this rule
        // rather than re-listing it.
        internal static bool RequiresLocatedReport(ConflictKind kind) =>
            kind == ConflictKind.UnrepresentableValue
            || kind == ConflictKind.AmbiguousTypeName
            || kind == ConflictKind.UnauthorableField
            || kind == ConflictKind.UnsyncableListener;

        public ConflictKind Kind
        {
            get => _kind;
            init => _kind = !RequiresLocatedReport(value)
                ? value
                : throw new System.ArgumentException(
                    "An UnrepresentableValue report is constructed through Conflict.Unrepresentable(...), " +
                    "an AmbiguousTypeName report through Conflict.AmbiguousTypeName(...), an " +
                    "UnauthorableField report through Conflict.UnauthorableField(...) and an " +
                    "UnsyncableListener report through Conflict.UnsyncableListener(...), all of which " +
                    "require the object anchor, the component type full name and the field/member key. " +
                    "Set Located, not Kind.");
        }

        public string Reason
        {
            get => Located?.Compose() ?? _reason;
            init => _reason = value;
        }

        public SourceSpan? Location { get; init; }

        // Identity of the STANDING condition this report describes -- a fact of the scene+source
        // that recurs on every reconcile until the user changes one of them, not an event of this
        // pass. Two reports with the same key are the same report: Reconciler routes them to
        // ReconcileResult.Notes and a surfacing channel shows one of them, once per editor session.
        // Null = an event, surfaced every time it happens.
        public string? RecurrenceKey { get; init; }

        // The located-report data (anchor, component type full name, field/member key) a kind
        // satisfying RequiresLocatedReport must carry; every other kind leaves this null.
        public LocatedReport? Located { get; init; }

        private static Conflict FromReport(
            ConflictKind kind, LocatedReport report, string? recurrenceKey, SourceSpan? location) =>
            new Conflict(kind)
            {
                LogicalId = report.LogicalId,
                Located = report,
                RecurrenceKey = recurrenceKey,
                Location = location,
            };

        // The ONE sentence stating what happened to a value the model cannot represent. Every
        // UnrepresentableValue report carries it, because Unrepresentable is the only construction
        // of the kind and appends it here.
        public const string ValueStaysInScene =
            "The live value stays in the scene and is NOT synced to code.";

        // THE construction of an unrepresentable-value report: the object anchor, the component
        // type full name and the field/member key are required positionally, and the fixed sentence
        // stating what happened to the value is appended to detail so no caller spells it.
        public static Conflict Unrepresentable(
            string logicalId, string componentTypeFullName, string memberKey, string detail,
            string? recurrenceKey, SourceSpan? location) =>
            FromReport(
                ConflictKind.UnrepresentableValue,
                new LocatedReport(logicalId, componentTypeFullName, memberKey, $"{detail} {ValueStaysInScene}"),
                recurrenceKey, location);

        // THE construction of an ambiguous-type-name report: candidate order, quoting and copy
        // live here so no caller spells any of them. Callers report only when 2+ candidates
        // matched; the recurrence key is NAME-scoped, so two reports sharing the ambiguous name
        // surface once per session regardless of which object/field triggered them.
        public static Conflict AmbiguousTypeName(
            string logicalId, string componentTypeFullName, string memberKey,
            string ambiguousName, IReadOnlyList<string> candidateFullNames)
        {
            var quoted = string.Join(
                ", ",
                candidateFullNames.OrderBy(n => n, StringComparer.Ordinal).Select(n => $"'{n}'"));

            return FromReport(
                ConflictKind.AmbiguousTypeName,
                new LocatedReport(logicalId, componentTypeFullName, memberKey,
                    $"Its declared type name '{ambiguousName}' is AMBIGUOUS — it matches {quoted}. The type " +
                    "was NOT guessed (§7), so this field's reference could not be resolved. Rename one of the " +
                    "colliding types so the name is unique among the editor's loaded types."),
                recurrenceKey: $"ambiguous-type-name:{ambiguousName}",
                location: null);
        }

        // THE one sentence stating that an authored line has no effect (excluded-field report).
        public const string LineHasNoEffect =
            "This line has NO effect: this field cannot be authored from code, so the build emits " +
            "nothing for it and it is never read back into code. Remove the line.";

        // THE construction of an unauthorable-field report: the object anchor, the component type
        // full name and the field key are required positionally.
        public static Conflict UnauthorableField(
            string componentLogicalId, string componentTypeFullName, string fieldKey) =>
            FromReport(
                ConflictKind.UnauthorableField,
                new LocatedReport(componentLogicalId, componentTypeFullName, fieldKey, LineHasNoEffect),
                recurrenceKey: $"unauthorable-field:{componentLogicalId}:{fieldKey}",
                location: null);

        // THE one sentence stating what happened to a listener that could not be synced.
        public const string ListenerLeftAsIs =
            "This listener is left exactly as it is, in the scene and in code, until this is fixed.";

        // THE construction of an unsyncable-listener report: the component anchor, the component
        // type full name, the event field key, the listener's index within that event and the
        // reason are all required positionally, and the recurrence key is composed here -- so the
        // write direction and the read direction cannot spell the same condition differently.
        public static Conflict UnsyncableListener(
            string componentLogicalId, string componentTypeFullName, string eventFieldKey,
            int listenerIndex, ListenerReportReason reason) =>
            FromReport(
                ConflictKind.UnsyncableListener,
                new LocatedReport(componentLogicalId, componentTypeFullName, eventFieldKey,
                    $"Listener at index {listenerIndex}: {ReasonSentence(reason)} {ListenerLeftAsIs}"),
                recurrenceKey: $"unsyncable-listener:{componentLogicalId}:{eventFieldKey}:{listenerIndex}:{reason}",
                location: null);

        // The one sentence naming WHY the listener could not be synced, chosen by reason so both
        // the write direction and the read direction produce the same sentence for the same
        // condition.
        private static string ReasonSentence(ListenerReportReason reason) => reason switch
        {
            ListenerReportReason.EmptyMethodName =>
                "This listener has no method name.",
            ListenerReportReason.UnresolvedTarget =>
                "This listener's target could not be resolved.",
            ListenerReportReason.UnresolvedObjectArgument =>
                "This listener's object-mode argument could not be resolved.",
            ListenerReportReason.MissingTarget =>
                "This listener's target is Unity's \"Missing\" reference.",
            ListenerReportReason.UnresolvedEventMemberName =>
                "This event field has no public C# spelling.",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
        };
    }
}
