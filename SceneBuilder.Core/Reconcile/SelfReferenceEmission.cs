namespace SceneBuilder.Core.Reconcile
{
    // THE one place the "an ObjectRef target IS the node the value is being written onto" rule
    // lives — every RenderFieldValue call site consults it before falling back to the owner's own
    // handle, which would otherwise emit a local variable inside the statement that declares it
    // (CS0841).
    internal static class SelfReferenceEmission
    {
        // The authored, variable-free spelling emitted for a self-reference.
        internal const string AuthoredExpression = "NodeHandle.Self";

        // The reserved intermediate ObjectRef target the parser produces for `NodeHandle.Self`,
        // resolved to the owning node's LogicalId by ObjectRefLowering. Not a legal C# identifier,
        // so it can never collide with an authored handle name.
        internal const string Marker = "<self>";

        internal static bool IsMarker(string? targetLogicalId) => targetLogicalId == Marker;

        // True when this ObjectRef target IS the node the value is being written onto.
        internal static bool IsSelf(string? targetLogicalId, string ownerNodeLogicalId) =>
            targetLogicalId != null && targetLogicalId == ownerNodeLogicalId;
    }
}
