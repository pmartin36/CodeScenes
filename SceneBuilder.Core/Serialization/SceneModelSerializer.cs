using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Serialization
{
    public static class SceneModelSerializer
    {
        public static string Serialize(SceneModel model) => CanonicalJson.Serialize(model);

        public static SceneModel Deserialize(string json) => CanonicalJson.Deserialize<SceneModel>(json);
    }
}
