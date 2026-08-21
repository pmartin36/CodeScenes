using System.Collections.Generic;
using UnityEngine;

namespace SceneBuilder.Authoring
{
    /// <summary>World position axes a live driver claims to write, or owns after arbitration.</summary>
    [System.Flags]
    internal enum AxisFlags
    {
        None = 0,
        X = 1,
        Y = 2,
        Z = 4,
    }

    /// <summary>A component that drives <c>transform.position</c> at edit time and can be arbitrated
    /// against sibling drivers on the same object via <see cref="PositionAuthority"/>.</summary>
    internal interface IPositionDriver
    {
        /// <summary>The world axes this driver will write on the next Evaluate, derived from its own
        /// live geometry (never a cached or authored mask).</summary>
        AxisFlags ClaimedWorldAxes();

        /// <summary>Deterministic priority key for a contested axis (lower wins); mirrors the driver's
        /// <c>DefaultExecutionOrder</c>.</summary>
        int PriorityOrder { get; }
    }

    /// <summary>Resolves, per <c>GameObject</c>, which world axes each <see cref="IPositionDriver"/>
    /// owns this evaluate when more than one driver claims overlapping axes. A lone driver on the
    /// object takes a no-op fast path so a single-driver scene is unaffected by arbitration.
    /// Main-thread/edit-time only (the reusable buffer is not safe off the main thread).</summary>
    internal static class PositionAuthority
    {
        private static readonly List<IPositionDriver> _buf = new List<IPositionDriver>();

        /// <summary>Returns the world axes <paramref name="me"/> owns this evaluate: the axes it
        /// claims that no lower-priority-key sibling also claims. <paramref name="yielded"/> is true
        /// when <paramref name="me"/> lost at least one of its claimed axes to a sibling.</summary>
        internal static AxisFlags ResolveOwned(Component self, IPositionDriver me, out bool yielded)
        {
            self.GetComponents(_buf);

            if (_buf.Count <= 1)
            {
                yielded = false;
                return me.ClaimedWorldAxes();
            }

            AxisFlags myClaim = me.ClaimedWorldAxes();
            AxisFlags owned = myClaim;
            int myIndex = _buf.IndexOf(me);

            for (int i = 0; i < _buf.Count; i++)
            {
                var other = _buf[i];
                if (ReferenceEquals(other, me)) continue;

                AxisFlags overlap = myClaim & other.ClaimedWorldAxes();
                if (overlap == AxisFlags.None) continue;

                if (IsHigherPriority(other.PriorityOrder, i, me.PriorityOrder, myIndex))
                {
                    owned &= ~overlap;
                }
            }

            yielded = owned != myClaim;
            return owned;
        }

        // Composite key (executionOrder, componentIndex): the order is the deterministic tie-break
        // basis; the serialized add-order index (stable across reloads) breaks a tie between two
        // drivers sharing one DefaultExecutionOrder.
        private static bool IsHigherPriority(int otherOrder, int otherIndex, int myOrder, int myIndex)
        {
            if (otherOrder != myOrder) return otherOrder < myOrder;
            return otherIndex < myIndex;
        }
    }
}
