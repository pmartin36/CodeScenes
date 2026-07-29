using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Plan
{
    public record CreateObject : PlanOp
    {
        [JsonPropertyOrder(1)]
        public string Name { get; init; } = "";

        // b2-t2: the created object's TransformData.Kind (RectTransformFields.Kind for a UI node,
        // null for a plain Transform). WhenWritingNull keeps a plain create at exactly
        // {op, logicalId, name} (spec 01:61-62, PlanJsonTests.cs CreateObject_..._AndNoExtraFields).
        [JsonPropertyOrder(2)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TransformKind { get; init; }
    }
}
