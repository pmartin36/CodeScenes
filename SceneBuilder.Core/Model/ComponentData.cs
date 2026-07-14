namespace SceneBuilder.Core.Model
{
    public record ComponentData
    {
        public string LogicalId { get; init; } = "";
        public TypeRef Type { get; init; } = new TypeRef("");
        public FieldMap Fields { get; init; } = FieldMap.Empty;
    }
}
