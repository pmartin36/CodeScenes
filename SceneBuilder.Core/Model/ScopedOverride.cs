using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Model
{
    // Records a `.On(selector, ...)` target resolved against a FacadeCatalog: the RealName-joined
    // path to the nested child (never the sanitized identifier) plus its durable LocalId. The
    // closure (nested-override values) is spec 24's materialize step — out of scope here (b4-t2).
    // See specs/25-typed-prefab-facades.md.
    public sealed record ScopedOverride
    {
        [JsonPropertyOrder(0)]
        public string ChildPath { get; init; } = "";

        [JsonPropertyOrder(1)]
        public long LocalId { get; init; }
    }
}
