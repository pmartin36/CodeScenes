using SceneBuilder.Core.Identity;

namespace SceneBuilder.Core.Serialization
{
    public static class IdentityMapJson
    {
        public static string Serialize(IdentityMap map) => CanonicalJson.Serialize(map);

        public static IdentityMap Deserialize(string json) => CanonicalJson.Deserialize<IdentityMap>(json);
    }
}
