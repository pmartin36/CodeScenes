using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Plan
{
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
    [JsonDerivedType(typeof(CreateObject), "CreateObject")]
    public abstract record PlanOp;
}
