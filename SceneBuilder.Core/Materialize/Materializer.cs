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
            var skipped = new List<SkippedField>();

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
                        const ChannelMask positionMask = ChannelMask.PositionX | ChannelMask.PositionY | ChannelMask.PositionZ;
                        passB.Add(new SetField
                        {
                            LogicalId = setTransform.LogicalId,
                            Path = "m_LocalPosition",
                            Value = new ValueNode.Vec3(setTransform.Transform.Position),
                            DrivenChannels = setTransform.Transform.DrivenChannels & positionMask,
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
                            DrivenChannels = setTransform.Transform.DrivenChannels & ChannelMask.Scale,
                        });
                        break;
                    case Change.AddComponent addComponent:
                        passB.Add(new AddComponent
                        {
                            LogicalId = addComponent.Component.LogicalId,
                            Type = addComponent.Component.Type,
                        });
                        foreach (var (path, value) in addComponent.Component.Fields)
                        {
                            EmitFieldOp(addComponent.Component.LogicalId, path, value, passB, skipped);
                        }

                        break;
                    case Change.SetField setField:
                        EmitFieldOp(setField.ComponentLogicalId, setField.Path, setField.Value, passB, skipped);
                        break;
                    case Change.RemoveComponent removeComponent:
                        passB.Add(new RemoveComponent { LogicalId = removeComponent.ComponentLogicalId });
                        break;
                    case Change.ReorderComponent reorderComponent:
                        passB.Add(new ReorderComponent
                        {
                            LogicalId = reorderComponent.ComponentLogicalId,
                            GameObjectLogicalId = reorderComponent.LogicalId,
                            ComponentLogicalId = reorderComponent.ComponentLogicalId,
                            ToIndex = reorderComponent.ToIndex,
                        });
                        break;
                }
            }

            return new Plan.Plan
            {
                SchemaVersion = desired.SchemaVersion,
                ScenePath = identityMap.Scene,
                Ops = passA.Concat(passB).ToArray(),
                Skipped = skipped.ToArray(),
            };
        }

        private static void EmitFieldOp(
            string logicalId, string path, ValueNode value,
            List<PlanOp> passB, List<SkippedField> skipped)
        {
            if (value is ValueNode.AssetRef assetRef)
            {
                if (IsUnresolved(assetRef.Ref))
                {
                    skipped.Add(new SkippedField { LogicalId = logicalId, Path = path, Reason = "Unresolved" });
                }
                else
                {
                    passB.Add(new SetAssetRef
                    {
                        LogicalId = logicalId,
                        Path = path,
                        Guid = assetRef.Ref?.Guid,
                        FileId = assetRef.Ref?.FileId ?? 0,
                    });
                }
            }
            else if (value is ValueNode.ObjectRef objectRef)
            {
                passB.Add(new SetReference
                {
                    LogicalId = logicalId,
                    Path = path,
                    TargetLogicalId = objectRef.TargetLogicalId,
                });
            }
            else if (value is ValueNode.List list && list.Items.Count > 0 && list.Items.All(item => item is ValueNode.AssetRef))
            {
                for (var i = 0; i < list.Items.Count; i++)
                {
                    var element = (ValueNode.AssetRef)list.Items[i];
                    if (IsUnresolved(element.Ref))
                    {
                        skipped.Add(new SkippedField { LogicalId = logicalId, Path = $"{path}[{i}]", Reason = "Unresolved" });
                        continue;
                    }

                    passB.Add(new SetAssetRef
                    {
                        LogicalId = logicalId,
                        Path = $"{path}[{i}]",
                        Guid = element.Ref?.Guid,
                        FileId = element.Ref?.FileId ?? 0,
                    });
                }
            }
            else if (value is ValueNode.Unsupported)
            {
                skipped.Add(new SkippedField { LogicalId = logicalId, Path = path });
            }
            else
            {
                passB.Add(new SetField { LogicalId = logicalId, Path = path, Value = value });
            }
        }

        // A ref that EXISTS but carries no resolved GUID named something the resolver could not find.
        // Emitting it as SetAssetRef(guid: "") makes the adapter CLEAR the live field
        // (AssetReferenceResolver.cs:239-244) — silent data loss. Skip instead.
        // Ref == null is the genuine None/clear form and is NOT this — callers test it first.
        private static bool IsUnresolved(global::SceneBuilder.Core.Model.AssetRef? reference) =>
            reference is not null && string.IsNullOrEmpty(reference.Guid);
    }
}
