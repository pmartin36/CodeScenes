using System.Collections.Generic;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;

namespace SceneBuilder.Core.Diff
{
    // b3-t2: diffs a MATCHED PrefabInstanceNode's authored override collections (desired, from code)
    // against the matched SnapshotNode's structured override collections (actual, from the live
    // scene, b6-t1). The snapshot is the live authority: an authored-only entry emits an apply op
    // (Set/Add/Remove); a snapshot-only entry emits the matching Revert* op, never a set-to-default
    // (banner #6). OpaqueOverrides (nested/deferred residue) is never touched here — that stays the
    // SB2301 flag path in Differ.WalkDesired. b3-t3: BaseValue drives DetectStaleOverrides, which
    // Emit calls first — a stale key is excluded from Set/Revert emission (neither silently kept nor
    // dropped, surfaced instead as a Conflict).
    internal static class InstanceOverrideDiff
    {
        public static void Emit(PrefabInstanceNode desired, SnapshotNode snapshot, List<ChangeOp> ops, List<Conflict> conflicts)
        {
            var staleKeys = new HashSet<(OverrideTarget Target, string PropertyPath)>();
            DetectStaleOverrides(desired, snapshot, conflicts, staleKeys);

            EmitOverrides(desired, snapshot, ops, staleKeys);
            EmitAddedComponents(desired, snapshot, ops);
            EmitRemovedComponents(desired, snapshot, ops);
            EmitAddedGameObjects(desired, snapshot, ops);
            EmitRemovedGameObjects(desired, snapshot, ops);
        }

        // b3-t3: direction-agnostic stale-override detector shared by materialize (Emit, above) and
        // reconcile (b4-t2, calls this directly). For each (Target, PropertyPath) matched between
        // desired and snapshot where the desired override's recorded BaseValue no longer matches the
        // snapshot's current BaseValue AND the snapshot's live Value equals that current BaseValue:
        // emit exactly one located ConflictDetector.StaleOverride and record the key in staleKeys so
        // the caller can suppress the corresponding apply/revert op.
        public static void DetectStaleOverrides(
            PrefabInstanceNode desired, SnapshotNode snapshot,
            List<Conflict> conflicts, HashSet<(OverrideTarget Target, string PropertyPath)> staleKeys)
        {
            var snapshotByKey = new Dictionary<(OverrideTarget Target, string PropertyPath), PropertyOverride>();
            foreach (var snapshotOverride in snapshot.Overrides)
            {
                snapshotByKey[(snapshotOverride.Target, snapshotOverride.PropertyPath)] = snapshotOverride;
            }

            foreach (var desiredOverride in desired.Overrides)
            {
                var key = (desiredOverride.Target, desiredOverride.PropertyPath);

                if (desiredOverride.BaseValue is null
                    || !snapshotByKey.TryGetValue(key, out var snapshotOverride)
                    || snapshotOverride.BaseValue is null
                    || desiredOverride.BaseValue == snapshotOverride.BaseValue
                    || snapshotOverride.Value != snapshotOverride.BaseValue)
                {
                    continue;
                }

                conflicts.Add(ConflictDetector.StaleOverride(
                    desired.LogicalId, desiredOverride.Target, desiredOverride.PropertyPath,
                    desiredOverride.BaseValue, snapshotOverride.BaseValue));
                staleKeys.Add(key);
            }
        }

        private static void EmitOverrides(
            PrefabInstanceNode desired, SnapshotNode snapshot, List<ChangeOp> ops,
            HashSet<(OverrideTarget Target, string PropertyPath)> staleKeys)
        {
            var logicalId = desired.LogicalId;

            var snapshotByKey = new Dictionary<(OverrideTarget Target, string PropertyPath), PropertyOverride>();
            foreach (var snapshotOverride in snapshot.Overrides)
            {
                snapshotByKey[(snapshotOverride.Target, snapshotOverride.PropertyPath)] = snapshotOverride;
            }

            var desiredKeys = new HashSet<(OverrideTarget Target, string PropertyPath)>();
            foreach (var desiredOverride in desired.Overrides)
            {
                var key = (desiredOverride.Target, desiredOverride.PropertyPath);
                desiredKeys.Add(key);

                if (staleKeys.Contains(key))
                {
                    continue;
                }

                if (!snapshotByKey.TryGetValue(key, out var snapshotOverride)
                    || snapshotOverride.Value != desiredOverride.Value
                    || snapshotOverride.ObjectReference != desiredOverride.ObjectReference)
                {
                    ops.Add(new SetInstanceOverride
                    {
                        LogicalId = logicalId,
                        Target = desiredOverride.Target,
                        PropertyPath = desiredOverride.PropertyPath,
                        Value = desiredOverride.Value,
                        ObjectReference = desiredOverride.ObjectReference,
                    });
                }
            }

            foreach (var snapshotOverride in snapshot.Overrides)
            {
                var key = (snapshotOverride.Target, snapshotOverride.PropertyPath);
                if (desiredKeys.Contains(key) || staleKeys.Contains(key))
                {
                    continue;
                }

                ops.Add(new RevertInstanceOverride
                {
                    LogicalId = logicalId,
                    Target = snapshotOverride.Target,
                    PropertyPath = snapshotOverride.PropertyPath,
                });
            }
        }

        private static void EmitAddedComponents(PrefabInstanceNode desired, SnapshotNode snapshot, List<ChangeOp> ops)
        {
            var logicalId = desired.LogicalId;

            var snapshotByKey = new Dictionary<(OverrideTarget Target, string TypeFullName), AddedComponent>();
            foreach (var snapshotComponent in snapshot.AddedComponents)
            {
                snapshotByKey[(snapshotComponent.Target, snapshotComponent.Component.Type.FullName)] = snapshotComponent;
            }

            var desiredKeys = new HashSet<(OverrideTarget Target, string TypeFullName)>();
            foreach (var desiredComponent in desired.AddedComponents)
            {
                var key = (desiredComponent.Target, desiredComponent.Component.Type.FullName);
                desiredKeys.Add(key);

                if (!snapshotByKey.ContainsKey(key))
                {
                    ops.Add(new AddInstanceComponent
                    {
                        LogicalId = logicalId,
                        Target = desiredComponent.Target,
                        Component = desiredComponent.Component,
                    });
                }
            }

            foreach (var snapshotComponent in snapshot.AddedComponents)
            {
                var key = (snapshotComponent.Target, snapshotComponent.Component.Type.FullName);
                if (desiredKeys.Contains(key))
                {
                    continue;
                }

                ops.Add(new RevertAddedComponent
                {
                    LogicalId = logicalId,
                    Target = snapshotComponent.Target,
                    ComponentLogicalId = snapshotComponent.Component.LogicalId,
                });
            }
        }

        private static void EmitRemovedComponents(PrefabInstanceNode desired, SnapshotNode snapshot, List<ChangeOp> ops)
        {
            var logicalId = desired.LogicalId;

            var snapshotTargets = new HashSet<OverrideTarget>(snapshot.RemovedComponents);
            var desiredTargets = new HashSet<OverrideTarget>(desired.RemovedComponents);

            foreach (var target in desired.RemovedComponents)
            {
                if (!snapshotTargets.Contains(target))
                {
                    ops.Add(new RemoveInstanceComponent { LogicalId = logicalId, Target = target });
                }
            }

            foreach (var target in snapshot.RemovedComponents)
            {
                if (!desiredTargets.Contains(target))
                {
                    ops.Add(new RevertRemovedComponent { LogicalId = logicalId, Target = target });
                }
            }
        }

        // b3-t2: mirrors EmitAddedComponents. Key is (Parent, Node child's Name); a GO Parent's
        // ComponentType is "" so this is effectively (Parent.SubKey, Name) per research.
        private static void EmitAddedGameObjects(PrefabInstanceNode desired, SnapshotNode snapshot, List<ChangeOp> ops)
        {
            var logicalId = desired.LogicalId;

            var snapshotByKey = new Dictionary<(OverrideTarget Parent, string Name), AddedGameObject>();
            foreach (var snapshotAdded in snapshot.AddedGameObjects)
            {
                snapshotByKey[(snapshotAdded.Parent, snapshotAdded.Node.Name)] = snapshotAdded;
            }

            var desiredKeys = new HashSet<(OverrideTarget Parent, string Name)>();
            foreach (var desiredAdded in desired.AddedGameObjects)
            {
                var key = (desiredAdded.Parent, desiredAdded.Node.Name);
                desiredKeys.Add(key);

                if (!snapshotByKey.ContainsKey(key))
                {
                    ops.Add(new AddInstanceChild
                    {
                        LogicalId = logicalId,
                        Target = desiredAdded.Parent,
                        Node = desiredAdded.Node,
                    });
                }
            }

            foreach (var snapshotAdded in snapshot.AddedGameObjects)
            {
                var key = (snapshotAdded.Parent, snapshotAdded.Node.Name);
                if (desiredKeys.Contains(key))
                {
                    continue;
                }

                ops.Add(new RevertAddedChild
                {
                    LogicalId = logicalId,
                    Target = snapshotAdded.Parent,
                    ChildLogicalId = snapshotAdded.Node.LogicalId,
                });
            }
        }

        // b3-t2: mirrors EmitRemovedComponents.
        private static void EmitRemovedGameObjects(PrefabInstanceNode desired, SnapshotNode snapshot, List<ChangeOp> ops)
        {
            var logicalId = desired.LogicalId;

            var snapshotTargets = new HashSet<OverrideTarget>(snapshot.RemovedGameObjects);
            var desiredTargets = new HashSet<OverrideTarget>(desired.RemovedGameObjects);

            foreach (var target in desired.RemovedGameObjects)
            {
                if (!snapshotTargets.Contains(target))
                {
                    ops.Add(new RemoveInstanceChild { LogicalId = logicalId, Target = target });
                }
            }

            foreach (var target in snapshot.RemovedGameObjects)
            {
                if (!desiredTargets.Contains(target))
                {
                    ops.Add(new RevertRemovedChild { LogicalId = logicalId, Target = target });
                }
            }
        }
    }
}
