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

            // b2-t2: pre-scan the rect ops so pass A can stamp CreateObject.TransformKind without
            // touching AddNode or the Differ. RectTransformDiff.EmitCreate emits a SetRectTransform
            // for every CREATED rect model node, so the set is complete before pass A runs.
            // The IsRectTransform guard rejects the D6 promotion op, whose model side is a plain
            // Transform: a promoted node is already live and must never be stamped as a UI create.
            var rectLogicalIds = new HashSet<string>();
            foreach (var op in changeSet.Ops)
            {
                if (op is Change.SetRectTransform rect && rect.Transform.IsRectTransform)
                {
                    rectLogicalIds.Add(rect.LogicalId);
                }
            }

            foreach (var op in changeSet.Ops)
            {
                if (op is Change.AddNode addNode)
                {
                    passA.Add(new CreateObject
                    {
                        LogicalId = addNode.LogicalId,
                        Name = addNode.Name,
                        TransformKind = rectLogicalIds.Contains(addNode.LogicalId) ? RectTransformFields.Kind : null,
                    });
                }
                else if (op is Change.AddInstance addInstance)
                {
                    passA.Add(new InstantiatePrefab
                    {
                        LogicalId = addInstance.LogicalId,
                        Guid = addInstance.Guid,
                        ParentLogicalId = addInstance.ParentLogicalId,
                        SiblingIndex = addInstance.SiblingIndex,
                    });
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
                    case Change.AddInstance:
                        // No-op in pass-B: parenting and sibling ordering already ride on the
                        // InstantiatePrefab op emitted in pass-A above, unlike AddNode which needs a
                        // separate SetParent.
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
                    case Change.SetRectTransform setRectTransform:
                        EmitRectTransformOps(setRectTransform, passB);
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
                    case Change.SetInstanceOverride setInstanceOverride:
                        passB.Add(new SetInstanceOverride
                        {
                            LogicalId = setInstanceOverride.LogicalId,
                            Target = setInstanceOverride.Target,
                            PropertyPath = setInstanceOverride.PropertyPath,
                            Value = setInstanceOverride.Value,
                            ObjectReference = setInstanceOverride.ObjectReference,
                        });
                        break;
                    case Change.AddInstanceComponent addInstanceComponent:
                        passB.Add(new AddInstanceComponent
                        {
                            LogicalId = addInstanceComponent.LogicalId,
                            Target = addInstanceComponent.Target,
                            Component = addInstanceComponent.Component,
                        });
                        break;
                    case Change.RemoveInstanceComponent removeInstanceComponent:
                        passB.Add(new RemoveInstanceComponent
                        {
                            LogicalId = removeInstanceComponent.LogicalId,
                            Target = removeInstanceComponent.Target,
                        });
                        break;
                    case Change.RevertInstanceOverride revertInstanceOverride:
                        passB.Add(new RevertInstanceOverride
                        {
                            LogicalId = revertInstanceOverride.LogicalId,
                            Target = revertInstanceOverride.Target,
                            PropertyPath = revertInstanceOverride.PropertyPath,
                        });
                        break;
                    case Change.RevertAddedComponent revertAddedComponent:
                        passB.Add(new RevertAddedComponent
                        {
                            LogicalId = revertAddedComponent.LogicalId,
                            Target = revertAddedComponent.Target,
                            ComponentLogicalId = revertAddedComponent.ComponentLogicalId,
                        });
                        break;
                    case Change.RevertRemovedComponent revertRemovedComponent:
                        passB.Add(new RevertRemovedComponent
                        {
                            LogicalId = revertRemovedComponent.LogicalId,
                            Target = revertRemovedComponent.Target,
                        });
                        break;
                    case Change.AddInstanceChild addInstanceChild:
                        passB.Add(new AddInstanceChild
                        {
                            LogicalId = addInstanceChild.LogicalId,
                            Target = addInstanceChild.Target,
                            Node = addInstanceChild.Node,
                        });
                        break;
                    case Change.RemoveInstanceChild removeInstanceChild:
                        passB.Add(new RemoveInstanceChild
                        {
                            LogicalId = removeInstanceChild.LogicalId,
                            Target = removeInstanceChild.Target,
                        });
                        break;
                    case Change.RevertAddedChild revertAddedChild:
                        passB.Add(new RevertAddedChild
                        {
                            LogicalId = revertAddedChild.LogicalId,
                            Target = revertAddedChild.Target,
                            ChildLogicalId = revertAddedChild.ChildLogicalId,
                        });
                        break;
                    case Change.RevertRemovedChild revertRemovedChild:
                        passB.Add(new RevertRemovedChild
                        {
                            LogicalId = revertRemovedChild.LogicalId,
                            Target = revertRemovedChild.Target,
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
                Diagnostics = changeSet.Diagnostics,
                Conflicts = changeSet.Conflicts,
            };
        }

        // b2-t2: lowers a matched/create SetRectTransform to one SetField per whole `m_*` path with any
        // bit set in Changed. D6: a promotion (Transform.IsRectTransform == false) has no authored
        // layout — lowering it would write RectTransform defaults over the user's live scene layout
        // AND pin DriftState.CodeAhead true forever, so it lowers to ZERO ops.
        private static void EmitRectTransformOps(Change.SetRectTransform op, List<PlanOp> passB)
        {
            if (!op.Transform.IsRectTransform)
            {
                return;
            }

            foreach (var field in RectTransformFields.All)
            {
                if ((op.Changed & field.Mask) == ChannelMask.None)
                {
                    continue;
                }

                passB.Add(new SetField
                {
                    LogicalId = op.LogicalId,
                    Path = field.SerializedPath,
                    Value = new ValueNode.Vec2(field.Get(op.Transform) ?? field.Default),
                });
            }
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
            else if (value is ValueNode.List list && IsReferenceList(list))
            {
                passB.Add(new SetArraySize { LogicalId = logicalId, Path = path, Size = list.Items.Count });
                for (var i = 0; i < list.Items.Count; i++)
                {
                    EmitFieldOp(logicalId, $"{path}[{i}]", list.Items[i], passB, skipped);
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

        // A list of items where every item is a reference kind (ObjectRef/AssetRef/Unsupported) and
        // at least one is a real reference. Both reference kinds and the unresolved placeholder share
        // one lowering rule: SetArraySize carrying the authored count, then one op per index.
        private static bool IsReferenceList(ValueNode.List list) =>
            list.Items.Count > 0
            && list.Items.All(item => item is ValueNode.AssetRef or ValueNode.ObjectRef or ValueNode.Unsupported)
            && list.Items.Any(item => item is ValueNode.AssetRef or ValueNode.ObjectRef);

        // A ref that EXISTS but carries no resolved GUID named something the resolver could not find.
        // Emitting it as SetAssetRef(guid: "") makes the adapter CLEAR the live field
        // (AssetReferenceResolver.cs:239-244) — silent data loss. Skip instead.
        // Ref == null is the genuine None/clear form and is NOT this — callers test it first.
        private static bool IsUnresolved(global::SceneBuilder.Core.Model.AssetRef? reference) =>
            reference is not null && string.IsNullOrEmpty(reference.Guid);
    }
}
