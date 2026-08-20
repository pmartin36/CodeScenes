#nullable enable annotations
using System;
using System.Reflection;
using UnityEngine;
using SceneBuilder.Authoring;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// The ONE live-Transform read (m-ui-recttransform b3-t1, research.md): both adapter read sites
    /// (<see cref="SceneSnapshotReader"/>, <see cref="PrefabInstanceProbe"/>'s nested-probe path)
    /// build their <see cref="TransformData"/> here instead of hand-constructing one, and the driven
    /// mask this stamps on a RectTransform is derived from Unity's own (internal) reflection-bound
    /// <c>RectTransform.drivenProperties</c> rather than inferred from <c>drivenByObject</c>.
    /// The ONLY public entry point is <see cref="Read(GameObject)"/> (iteration 2) — no caller can
    /// pass a hand-picked <see cref="ChannelMask"/> (e.g. <c>ChannelMask.None</c>) and silently skip
    /// driven derivation, which is exactly the bug that shipped on the AddedGameObjects read path.
    /// </summary>
    internal static class LiveTransformRead
    {
        private const string PlainKind = "Transform";

        /// <summary>The ONE live GameObject -&gt; Core TransformData read: derives the REAL driven
        /// channels for <paramref name="go"/> (see <see cref="DrivenChannels"/>) and stamps them onto
        /// the read. Every adapter call site MUST go through this overload.</summary>
        internal static TransformData Read(GameObject go) => Read(go.transform, DrivenChannels(go));

        /// <summary>The ONE live Transform -&gt; Core TransformData read. Kind and the five UI fields are
        /// stamped iff the transform IS a RectTransform (Unity's own promotion is the only way one
        /// exists); a plain Transform reads exactly as before, with all five fields null.
        /// <paramref name="drivenChannels"/> is supplied ONLY by <see cref="Read(GameObject)"/> —
        /// kept private so no caller can bypass driven derivation.</summary>
        private static TransformData Read(Transform t, ChannelMask drivenChannels)
        {
            var lp = t.localPosition;
            var lr = t.localRotation;
            var ls = t.localScale;
            var rt = t as RectTransform;

            return new TransformData
            {
                Kind = rt != null ? RectTransformFields.Kind : PlainKind,
                Position = new Vec3(lp.x, lp.y, lp.z),
                Rotation = new Quat(lr.x, lr.y, lr.z, lr.w),
                Scale = new Vec3(ls.x, ls.y, ls.z),
                AnchoredPosition = rt != null ? ToVec2(rt.anchoredPosition) : (Vec2?)null,
                SizeDelta = rt != null ? ToVec2(rt.sizeDelta) : (Vec2?)null,
                AnchorMin = rt != null ? ToVec2(rt.anchorMin) : (Vec2?)null,
                AnchorMax = rt != null ? ToVec2(rt.anchorMax) : (Vec2?)null,
                Pivot = rt != null ? ToVec2(rt.pivot) : (Vec2?)null,
                DrivenChannels = drivenChannels,
            };
        }

        /// <summary>The channels Unity itself reports as DRIVEN on this RectTransform, mapped onto
        /// ChannelMask. Per D2 an AnchoredPosition*/Scale* driven flag ALSO sets the corresponding base
        /// PositionX/Y/Z / ScaleX/Y/Z bit, because Reconciler.MaskDriven can only hold what is flagged —
        /// without it a ScreenSpaceOverlay Canvas emits a `pos:` patch every sync from its
        /// Canvas-driven m_LocalPosition. Returns None for a plain Transform.</summary>
        private static ChannelMask RectDrivenChannels(Transform t)
        {
            if (t is not RectTransform rt)
            {
                return ChannelMask.None;
            }

            if (DrivenGetter == null)
            {
                return FallbackMask(rt);
            }

            var driven = DrivenGetter(rt);
            if (driven == DrivenTransformProperties.None)
            {
                return ChannelMask.None;
            }

            var mask = ChannelMask.None;
            for (var i = 0; i < DrivenBits.Length; i++)
            {
                if ((driven & DrivenBits[i].Unity) != 0)
                {
                    mask |= DrivenBits[i].Core;
                }
            }

            return mask;
        }

        /// <summary>
        /// ORs together the driven channels of every ACTIVE-AND-ENABLED FitSize/SurfaceSnap/Between on
        /// <paramref name="go"/> — the same guard those components' own <c>Evaluate()</c> use
        /// (<c>isActiveAndEnabled</c>), so "reader says driven" always agrees with "component
        /// actually drives". A disabled/inactive component contributes nothing (releases its
        /// channel so a manual edit syncs normally). Mirrors the parse-time mapping in
        /// <c>SpatialComponents.FitSizeMask</c>/<c>SurfaceSnapMask</c>/<c>BetweenDrivenMask</c> so desired
        /// and actual never diverge. Also ORs in whatever Unity itself reports as driven on the
        /// GameObject's RectTransform (ugui layout components — Canvas, layout groups,
        /// ContentSizeFitter), via <see cref="RectDrivenChannels"/>; None for a plain Transform. This is
        /// the ONE driven-channel derivation EVERY adapter read path uses — including the
        /// prefab-instance AddedGameObjects probe — so none of them ever reports a hardcoded
        /// <c>ChannelMask.None</c>.
        /// </summary>
        internal static ChannelMask DrivenChannels(GameObject go)
        {
            var mask = RectDrivenChannels(go.transform);

            foreach (var sizer in go.GetComponents<FitSize>())
            {
                if (sizer.isActiveAndEnabled)
                {
                    mask |= SpatialComponents.FitSizeMask;
                }
            }

            foreach (var snapper in go.GetComponents<SurfaceSnap>())
            {
                if (snapper.isActiveAndEnabled)
                {
                    mask |= SpatialComponents.SurfaceSnapMask(
                        snapper.vertical != SurfaceSnap.Vertical.None,
                        snapper.horizontal != SurfaceSnap.Horizontal.None,
                        snapper.depth != SurfaceSnap.Depth.None);
                }
            }

            foreach (var between in go.GetComponents<Between>())
            {
                if (between.isActiveAndEnabled)
                {
                    mask |= SpatialComponents.BetweenDrivenMask(
                        (SpatialAxis)(int)between.axis,
                        between.orientation != null);
                }
            }

            return mask;
        }

        private static Vec2 ToVec2(Vector2 v) => new Vec2(v.x, v.y);

        // Per-SINGLE-BIT map. DrivenTransformProperties' alias members (AnchoredPosition, Anchors,
        // SizeDelta, Pivot, Scale) are plain ORs and `All` is -1, so testing single bits covers every
        // reported value. DrivenTransformProperties.Rotation has no ChannelMask counterpart (b1-t1 added
        // no rotation channel) and is deliberately unmapped.
        private static readonly (DrivenTransformProperties Unity, ChannelMask Core)[] DrivenBits =
        {
            (DrivenTransformProperties.AnchoredPositionX, ChannelMask.AnchoredPositionX | ChannelMask.PositionX),
            (DrivenTransformProperties.AnchoredPositionY, ChannelMask.AnchoredPositionY | ChannelMask.PositionY),
            (DrivenTransformProperties.AnchoredPositionZ, ChannelMask.PositionZ),
            (DrivenTransformProperties.ScaleX, ChannelMask.ScaleX),
            (DrivenTransformProperties.ScaleY, ChannelMask.ScaleY),
            (DrivenTransformProperties.ScaleZ, ChannelMask.ScaleZ),
            (DrivenTransformProperties.SizeDeltaX, ChannelMask.SizeDeltaX),
            (DrivenTransformProperties.SizeDeltaY, ChannelMask.SizeDeltaY),
            (DrivenTransformProperties.AnchorMinX, ChannelMask.AnchorMinX),
            (DrivenTransformProperties.AnchorMinY, ChannelMask.AnchorMinY),
            (DrivenTransformProperties.AnchorMaxX, ChannelMask.AnchorMaxX),
            (DrivenTransformProperties.AnchorMaxY, ChannelMask.AnchorMaxY),
            (DrivenTransformProperties.PivotX, ChannelMask.PivotX),
            (DrivenTransformProperties.PivotY, ChannelMask.PivotY),
        };

        private const ChannelMask EveryDrivableChannel =
            ChannelMask.AllRectFields | ChannelMask.PositionX | ChannelMask.PositionY | ChannelMask.PositionZ
            | ChannelMask.ScaleX | ChannelMask.ScaleY | ChannelMask.ScaleZ;

        // RectTransform.drivenProperties is INTERNAL in 6000.5.3f1 (verified: getter attrs = Assembly);
        // drivenByObject is public but carries no channel granularity, and ugui deliberately registers a
        // driver with DrivenTransformProperties.None (ContentSizeFitter, both fits Unconstrained), so it
        // cannot substitute. Bound ONCE per domain reload as a delegate — this runs per object per sync.
        // Reflection precedent: SerializedMemberMap.GetFieldRecursive (SerializedMemberMap.cs).
        private static readonly Func<RectTransform, DrivenTransformProperties>? DrivenGetter = ResolveDrivenGetter();
        private static bool _fallbackWarned;

        private static Func<RectTransform, DrivenTransformProperties>? ResolveDrivenGetter()
        {
            var getter = typeof(RectTransform)
                .GetProperty("drivenProperties", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetGetMethod(nonPublic: true);
            if (getter == null)
            {
                return null;
            }

            try
            {
                return (Func<RectTransform, DrivenTransformProperties>)Delegate.CreateDelegate(
                    typeof(Func<RectTransform, DrivenTransformProperties>), getter);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Unreachable on 6000.5.3f1. If a future Unity removes the property, freeze layout sync for a
        // driven RectTransform rather than let anchor-derived values overwrite authored source.
        private static ChannelMask FallbackMask(RectTransform rt)
        {
            if (!_fallbackWarned)
            {
                _fallbackWarned = true;
                Debug.LogWarning(
                    "CodeScenes: RectTransform.drivenProperties is not readable in this Unity version, so per-channel " +
                    "driven detection is unavailable. Every layout channel of a driven RectTransform is treated as " +
                    "driven (never synced) to avoid overwriting authored source.");
            }

            return rt.drivenByObject != null ? EveryDrivableChannel : ChannelMask.None;
        }
    }
}
