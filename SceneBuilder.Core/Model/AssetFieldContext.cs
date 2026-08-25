namespace SceneBuilder.Core.Model
{
    // Identifies the component field a direct (non-nested) authored AssetRef value was assigned
    // to, so a sub-asset name scan can narrow its candidates to the field's expected
    // UnityEngine.Object type before declaring an ambiguity. Model, not Validation/Lowering,
    // because both layers reference it.
    public readonly record struct AssetFieldContext(TypeRef Component, string FieldPath);
}
