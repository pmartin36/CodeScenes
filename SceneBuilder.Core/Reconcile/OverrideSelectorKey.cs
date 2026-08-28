using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Reconcile
{
    // spec 54: the ONE composite-key formatter for an override authored-selector carrier
    // (instanceLogicalId -> OverrideSelectorKey.For(...) -> authored member name), shared by the
    // adapter producer and the Core self-heal consumer so a same-resolved-path override on two
    // different sub-objects/types under one instance can never collide or drift between the two
    // sites.
    public static class OverrideSelectorKey
    {
        public static string For(OverrideTarget target, string resolvedPropertyPath) =>
            target.ChildPath + " | " + target.ComponentType + " | " + resolvedPropertyPath;
    }
}
