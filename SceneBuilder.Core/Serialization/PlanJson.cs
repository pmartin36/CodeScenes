using SceneBuilder.Core.Plan;

namespace SceneBuilder.Core.Serialization
{
    public static class PlanJson
    {
        public static string Serialize(Plan.Plan plan) => CanonicalJson.Serialize(plan);

        public static Plan.Plan Deserialize(string json) => CanonicalJson.Deserialize<Plan.Plan>(json);
    }
}
