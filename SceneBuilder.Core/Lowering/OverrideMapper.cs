using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Lowering
{
    // b1-t3: symmetric bridge between the adapter's ModificationRecord (mirrors Unity's
    // PropertyModification, 4 members) and the serialized PropertyOverride. 1:1, no
    // grouping/dedup — pair-key (PrefabId, ObjectId) non-collapse is structural.
    public static class OverrideMapper
    {
        public static PropertyOverride[] ToOverrides(IReadOnlyList<ModificationRecord> records) =>
            records.Select(rec => new PropertyOverride
            {
                Target = rec.Target,
                PropertyPath = rec.PropertyPath,
                Value = new ValueNode.Primitive(PrimitiveKind.String, rec.Value),
                ObjectReference = rec.ObjectReference,
            }).ToArray();

        public static ModificationRecord[] ToRecords(IReadOnlyList<PropertyOverride> overrides) =>
            overrides.Select(ov => new ModificationRecord
            {
                Target = ov.Target,
                PropertyPath = ov.PropertyPath,
                Value = (ov.Value as ValueNode.Primitive)?.Value as string,
                ObjectReference = ov.ObjectReference,
            }).ToArray();
    }
}
