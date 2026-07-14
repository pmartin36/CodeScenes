namespace SceneBuilder.Core.Parsing
{
    public readonly record struct FlagPresence(bool HasTag, bool HasLayer, bool HasActive, bool HasStatic);
}
