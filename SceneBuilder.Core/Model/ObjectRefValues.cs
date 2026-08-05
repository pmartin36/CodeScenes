using System;
using System.Collections.Generic;
using System.Linq;

namespace SceneBuilder.Core.Model
{
    /// <summary>
    /// Owns "an ObjectRef is reached at any depth" for a <see cref="ValueNode"/> — a bare
    /// <see cref="ValueNode.ObjectRef"/>, or one carried inside a <see cref="ValueNode.List"/> item
    /// or a <see cref="ValueNode.Nested"/> member at any depth. Built on <see cref="ValueWalk"/>.
    /// Every parameter is required (no defaults) so a call site cannot silently omit one.
    /// </summary>
    public static class ObjectRefValues
    {
        /// <summary>True when <paramref name="value"/> is, or reaches at any depth, an ObjectRef.</summary>
        public static bool Contains(ValueNode value) => ValueWalk.Any(value, n => n is ValueNode.ObjectRef);

        /// <summary>Every ObjectRef reachable in <paramref name="value"/>, pre-order, with its path.</summary>
        public static IEnumerable<(string Path, ValueNode.ObjectRef Ref)> Enumerate(ValueNode value)
        {
            foreach (var (path, node) in ValueWalk.Enumerate(value))
            {
                if (node is ValueNode.ObjectRef r)
                {
                    yield return (path, r);
                }
            }
        }

        /// <summary>Every non-null <c>TargetLogicalId</c> reachable in <paramref name="value"/>, pre-order, duplicates included.</summary>
        public static IEnumerable<string> Targets(ValueNode value)
        {
            foreach (var (_, r) in Enumerate(value))
            {
                if (r.TargetLogicalId != null)
                {
                    yield return r.TargetLogicalId;
                }
            }
        }

        /// <summary>Rebuilds <paramref name="value"/> with every reachable ObjectRef replaced by <paramref name="replace"/>'s result.</summary>
        public static ValueNode Substitute(ValueNode value, Func<ValueNode.ObjectRef, ValueNode> replace) =>
            ValueWalk.Map(
                value,
                0,
                visit: (n, _) => n is ValueNode.ObjectRef r ? replace(r) : n,
                descend: (_, context, _) => context,
                dropEmptiedNested: false)!;

        /// <summary>
        /// Every non-null target that a ref at some path in <paramref name="sourceValue"/> names,
        /// where <paramref name="snapshotValue"/> holds a null ref at that SAME path and the target
        /// is absent from <paramref name="liveTargets"/> — the scene deleted it, rather than the
        /// field being cleared to a still-live target.
        /// </summary>
        public static IEnumerable<string> VanishedTargets(
            ValueNode sourceValue, ValueNode snapshotValue, ISet<string> liveTargets)
        {
            var sourceRefsByPath = Enumerate(sourceValue).ToDictionary(e => e.Path, e => e.Ref);

            foreach (var (path, snapshotRef) in Enumerate(snapshotValue))
            {
                if (snapshotRef.TargetLogicalId != null)
                {
                    continue;
                }

                if (sourceRefsByPath.TryGetValue(path, out var sourceRef)
                    && sourceRef.TargetLogicalId != null
                    && !liveTargets.Contains(sourceRef.TargetLogicalId))
                {
                    yield return sourceRef.TargetLogicalId;
                }
            }
        }
    }
}
