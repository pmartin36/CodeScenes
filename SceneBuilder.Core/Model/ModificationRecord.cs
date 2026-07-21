namespace SceneBuilder.Core.Model
{
    // Mirrors Unity's PropertyModification FOUR members in Core types (b1-t3). Target is the
    // (PrefabId, ObjectId) pair; ObjectReference is the already-lowered ValueNode form of
    // PropertyModification.objectReference. In-memory bridge between the adapter and
    // OverrideMapper — NOT the serialized form (PropertyOverride is). Default record
    // value-equality is correct: OverrideTarget/string/ValueNode are all value-equal.
    public sealed record ModificationRecord
    {
        public OverrideTarget Target { get; init; } = new();
        public string PropertyPath { get; init; } = "";
        public string? Value { get; init; }
        public ValueNode? ObjectReference { get; init; }
    }
}
