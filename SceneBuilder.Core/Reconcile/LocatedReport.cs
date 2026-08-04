namespace SceneBuilder.Core.Reconcile
{
    // The required data for a located report: the object anchor, the component type full name and
    // the field/member key — as positional (no-default) parameters, so a construction that omits
    // any of them does not compile. ScenePath is the one part Core cannot know: it stays absent
    // (null) everywhere Core builds a report, and is filled by the adapter with
    // `report with { ScenePath = ... }`.
    public sealed record LocatedReport(
        string LogicalId, string ComponentTypeFullName, string MemberKey, string Detail)
    {
        public string? ScenePath { get; init; }

        public string Where() => ScenePath is null
            ? $"'{LogicalId}' > '{ComponentTypeFullName}' > '{MemberKey}'"
            : $"'{LogicalId}' ({ScenePath}) > '{ComponentTypeFullName}' > '{MemberKey}'";

        public string Compose() => $"{Where()}: {Detail}";
    }
}
