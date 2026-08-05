namespace SceneBuilder.Core.Model
{
    // The adapter/Core crossing: which serialized-field roots the adapter treats as not-author-intent
    // for a given component type. A FUNCTION, not a per-type table: the create path asks about
    // component types that are not in the live snapshot at all, which a table populated from the
    // snapshot tree cannot answer.
    public interface IFieldExclusionPolicy
    {
        // fieldRoot is already reduced to its root segment (up to the first '.' or '[') by the
        // caller. An implementation that re-reduces must be idempotent on an already-root path.
        bool IsExcluded(TypeRef componentType, string fieldRoot);
    }

    // The default policy: nothing is excluded. A SceneSnapshot that does not set FieldExclusions
    // diffs and materializes with no field suppressed.
    public sealed class NoFieldExclusions : IFieldExclusionPolicy
    {
        public static readonly NoFieldExclusions Instance = new();

        private NoFieldExclusions()
        {
        }

        public bool IsExcluded(TypeRef componentType, string fieldRoot) => false;
    }
}
