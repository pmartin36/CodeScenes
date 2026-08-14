using System.Collections.Generic;
using SceneBuilder.Core.Model;
using Plan = SceneBuilder.Core.Plan;

namespace SceneBuilder.Core.Diff
{
    // Bypass check (spec 35:105-107): every FINAL plan op carrying a field path excluded for the
    // component it targets, as "{componentLogicalId} > {typeFullName} > {path}". Empty result means
    // no site bypassed the exclusion gate. A component's type is resolved from an index built off
    // the DESIRED model (the same model that owns every componentLogicalId a build op can carry).
    // AddInstanceComponent/AddInstanceChild carry a ComponentData inline and are checked against it
    // directly, since their fields never lower into separate SetField ops. SetInstanceOverride's
    // Target names a nested prefab member, not a model component; its ComponentType full name is
    // composed into a TypeRef and checked directly against that, the same bridge the emit-side gate
    // uses (ExcludedFieldGate.AdmitOverride).
    public static class ExcludedFieldAudit
    {
        public static IReadOnlyList<string> EmittedExclusions(
            SceneBuilder.Core.Plan.Plan plan, SceneModel desired, IFieldExclusionPolicy policy)
        {
            var typeByComponentId = new Dictionary<string, TypeRef>();
            IndexComponents(desired.Roots, typeByComponentId);

            var offenders = new List<string>();
            foreach (var op in plan.Ops)
            {
                switch (op)
                {
                    case Plan.SetField setField:
                        CheckPath(setField.LogicalId, setField.Path, typeByComponentId, policy, offenders);
                        break;
                    case Plan.SetAssetRef setAssetRef:
                        CheckPath(setAssetRef.LogicalId, setAssetRef.Path, typeByComponentId, policy, offenders);
                        break;
                    case Plan.SetReference setReference:
                        CheckPath(setReference.LogicalId, setReference.Path, typeByComponentId, policy, offenders);
                        break;
                    case Plan.SetArraySize setArraySize:
                        CheckPath(setArraySize.LogicalId, setArraySize.Path, typeByComponentId, policy, offenders);
                        break;
                    case Plan.AddInstanceComponent addInstanceComponent:
                        CheckComponentFields(addInstanceComponent.Component, policy, offenders);
                        break;
                    case Plan.AddInstanceChild addInstanceChild:
                        CheckNode(addInstanceChild.Node, policy, offenders);
                        break;
                    case Plan.SetInstanceOverride setInstanceOverride:
                        CheckOverride(setInstanceOverride.LogicalId, setInstanceOverride.Target, setInstanceOverride.PropertyPath, policy, offenders);
                        break;
                }
            }

            return offenders;
        }

        private static void IndexComponents(GameObjectNode[] nodes, Dictionary<string, TypeRef> typeByComponentId)
        {
            foreach (var node in nodes)
            {
                foreach (var component in node.Components)
                {
                    if (!string.IsNullOrEmpty(component.LogicalId))
                    {
                        typeByComponentId[component.LogicalId] = component.Type;
                    }
                }

                IndexComponents(node.Children, typeByComponentId);
            }
        }

        private static void CheckPath(
            string componentLogicalId, string path, Dictionary<string, TypeRef> typeByComponentId,
            IFieldExclusionPolicy policy, List<string> offenders)
        {
            if (!typeByComponentId.TryGetValue(componentLogicalId, out var type))
            {
                return;
            }

            if (policy.IsExcluded(type, ExcludedFieldGate.Root(path)))
            {
                offenders.Add($"{componentLogicalId} > {type.FullName} > {path}");
            }
        }

        private static void CheckComponentFields(ComponentData component, IFieldExclusionPolicy policy, List<string> offenders)
        {
            foreach (var field in component.Fields)
            {
                if (policy.IsExcluded(component.Type, ExcludedFieldGate.Root(field.Key)))
                {
                    offenders.Add($"{component.LogicalId} > {component.Type.FullName} > {field.Key}");
                }
            }
        }

        private static void CheckOverride(
            string instanceLogicalId, OverrideTarget target, string propertyPath,
            IFieldExclusionPolicy policy, List<string> offenders)
        {
            if (policy.IsExcluded(new TypeRef(target.ComponentType), ExcludedFieldGate.Root(propertyPath)))
            {
                offenders.Add($"{instanceLogicalId} > {target.ComponentType} > {propertyPath}");
            }
        }

        private static void CheckNode(GameObjectNode node, IFieldExclusionPolicy policy, List<string> offenders)
        {
            foreach (var component in node.Components)
            {
                CheckComponentFields(component, policy, offenders);
            }

            foreach (var child in node.Children)
            {
                CheckNode(child, policy, offenders);
            }
        }
    }
}
