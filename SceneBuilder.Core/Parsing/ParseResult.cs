using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Parsing
{
    public sealed class ParseResult
    {
        public SceneModel Model { get; init; } = new();

        public IdentityMap IdentityMap { get; init; } = new();
    }
}
