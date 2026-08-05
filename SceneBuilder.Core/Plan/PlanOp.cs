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
    [JsonDerivedType(typeof(AddComponent), "AddComponent")]
    [JsonDerivedType(typeof(RemoveComponent), "RemoveComponent")]
    [JsonDerivedType(typeof(ReorderComponent), "ReorderComponent")]
    [JsonDerivedType(typeof(SetAssetRef), "SetAssetRef")]
    [JsonDerivedType(typeof(SetReference), "SetReference")]
    [JsonDerivedType(typeof(SetArraySize), "SetArraySize")]
    [JsonDerivedType(typeof(InstantiatePrefab), "InstantiatePrefab")]
    [JsonDerivedType(typeof(SetInstanceOverride), "SetInstanceOverride")]
    [JsonDerivedType(typeof(AddInstanceComponent), "AddInstanceComponent")]
    [JsonDerivedType(typeof(RemoveInstanceComponent), "RemoveInstanceComponent")]
    [JsonDerivedType(typeof(RevertInstanceOverride), "RevertInstanceOverride")]
    [JsonDerivedType(typeof(RevertAddedComponent), "RevertAddedComponent")]
    [JsonDerivedType(typeof(RevertRemovedComponent), "RevertRemovedComponent")]
    [JsonDerivedType(typeof(AddInstanceChild), "AddInstanceChild")]
    [JsonDerivedType(typeof(RemoveInstanceChild), "RemoveInstanceChild")]
    [JsonDerivedType(typeof(RevertAddedChild), "RevertAddedChild")]
    [JsonDerivedType(typeof(RevertRemovedChild), "RevertRemovedChild")]
    public abstract record PlanOp
    {
        [JsonPropertyOrder(0)]
        public string LogicalId { get; init; } = "";
    }
}
