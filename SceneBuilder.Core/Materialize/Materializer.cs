using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Plan;
using Change = SceneBuilder.Core.Diff;

namespace SceneBuilder.Core.Materialize
{
    public static class Materializer
    {
        public static Plan.Plan Materialize(SceneModel desired, SceneSnapshot actual, IdentityMap identityMap)
        {
            var changeSet = Change.Differ.Diff(desired, actual, identityMap);

            var passA = new List<PlanOp>();
            var passB = new List<PlanOp>();

            foreach (var op in changeSet.Ops)
            {
                if (op is Change.AddNode addNode)
                {
                    passA.Add(new CreateObject { LogicalId = addNode.LogicalId, Name = addNode.Name });
                }
            }

            foreach (var op in changeSet.Ops)
            {
                switch (op)
                {
                    case Change.AddNode addNode:
                        if (addNode.ParentLogicalId != null)
                        {
                            passB.Add(new SetParent { LogicalId = addNode.LogicalId, ParentLogicalId = addNode.ParentLogicalId });
                        }

                        break;
                    case Change.RemoveNode removeNode:
                        passB.Add(new DestroyObject { LogicalId = removeNode.LogicalId });
                        break;
                    case Change.Reparent reparent:
                        passB.Add(new SetParent { LogicalId = reparent.LogicalId, ParentLogicalId = reparent.NewParentLogicalId });
                        break;
                    case Change.Reorder reorder:
                        passB.Add(new ReorderChild { LogicalId = reorder.LogicalId, SiblingIndex = reorder.SiblingIndex });
                        break;
                    case Change.SetName setName:
                        passB.Add(new SetName { LogicalId = setName.LogicalId, Name = setName.Name });
                        break;
                    case Change.SetTag setTag:
                        passB.Add(new SetTag { LogicalId = setTag.LogicalId, Tag = setTag.Tag });
                        break;
                    case Change.SetLayer setLayer:
                        passB.Add(new SetLayer { LogicalId = setLayer.LogicalId, Layer = setLayer.Layer });
                        break;
                    case Change.SetActive setActive:
                        passB.Add(new SetActive { LogicalId = setActive.LogicalId, Active = setActive.Active });
                        break;
                    case Change.SetStatic setStatic:
                        passB.Add(new SetStatic { LogicalId = setStatic.LogicalId, IsStatic = setStatic.IsStatic });
                        break;
                    case Change.SetTransform setTransform:
                        passB.Add(new SetField
                        {
                            LogicalId = setTransform.LogicalId,
                            Path = "m_LocalPosition",
                            Value = new ValueNode.Vec3(setTransform.Transform.Position),
                        });
                        passB.Add(new SetField
                        {
                            LogicalId = setTransform.LogicalId,
                            Path = "m_LocalRotation",
                            Value = new ValueNode.Quat(setTransform.Transform.Rotation),
                        });
                        passB.Add(new SetField
                        {
                            LogicalId = setTransform.LogicalId,
                            Path = "m_LocalScale",
                            Value = new ValueNode.Vec3(setTransform.Transform.Scale),
                        });
                        break;
                }
            }

            return new Plan.Plan
            {
                SchemaVersion = desired.SchemaVersion,
                ScenePath = identityMap.Scene,
                Ops = passA.Concat(passB).ToArray(),
            };
        }
    }
}
