using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Plan
{
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
    [JsonDerivedType(typeof(CreateObject), "CreateObject")]
    [JsonDerivedType(typeof(DestroyObject), "DestroyObject")]
    [JsonDerivedType(typeof(SetParent), "SetParent")]
    [JsonDerivedType(typeof(ReorderChild), "ReorderChild")]
    [JsonDerivedType(typeof(SetName), "SetName")]
    [JsonDerivedType(typeof(SetTag), "SetTag")]
    [JsonDerivedType(typeof(SetLayer), "SetLayer")]
    [JsonDerivedType(typeof(SetActive), "SetActive")]
    [JsonDerivedType(typeof(SetStatic), "SetStatic")]
    [JsonDerivedType(typeof(SetField), "SetField")]
    public abstract record PlanOp
    {
        [JsonPropertyOrder(0)]
        public string LogicalId { get; init; } = "";
    }
}
