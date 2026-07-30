#nullable enable annotations
using UnityEngine;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// The write-side counterpart of <see cref="LiveTransformRead"/> (m-ui-recttransform b3-t1,
    /// research.md iteration 2): the ONE place that creates a GameObject for an authored
    /// <see cref="TransformData"/> Kind and the ONE place that writes a RectTransform's five
    /// serialized-path fields. Both live-object create sites (<c>PlanExecutor.CreateObject</c>,
    /// <c>InstanceOverrideExecutor.Children.BuildAddedChild</c>) and both rect-write sites route
    /// through this so no file outside it ever spells <c>new GameObject(...)</c> for a model-driven
    /// object or switches on a RectTransform serialized path to assign.
    /// </summary>
    internal static class LiveTransformWrite
    {
        /// <summary>Creates a GameObject AS a RectTransform when <paramref name="transformKind"/> is
        /// <see cref="RectTransformFields.Kind"/>, else a plain Transform — never a plain GameObject
        /// promoted later. The ONE spelling of a model-driven <c>new GameObject(...)</c>.</summary>
        internal static GameObject Create(string name, string? transformKind) =>
            transformKind == RectTransformFields.Kind
                ? new GameObject(name, typeof(RectTransform))
                : new GameObject(name);

        /// <summary>The ONE switch from a RectTransform serialized path to the live property write.
        /// Silently no-ops for a path RectTransformFields does not recognise (callers gate on
        /// <see cref="RectTransformFields.TryFromSerializedPath"/> first).</summary>
        internal static void SetRectField(RectTransform rt, string serializedPath, Vec2 value)
        {
            var v = new Vector2(value.X, value.Y);
            switch (serializedPath)
            {
                case RectTransformFields.AnchoredPositionPath: rt.anchoredPosition = v; break;
                case RectTransformFields.SizeDeltaPath:        rt.sizeDelta        = v; break;
                case RectTransformFields.AnchorMinPath:        rt.anchorMin        = v; break;
                case RectTransformFields.AnchorMaxPath:        rt.anchorMax        = v; break;
                case RectTransformFields.PivotPath:            rt.pivot            = v; break;
            }
        }

        /// <summary>Writes every field <paramref name="data"/> actually carries (a null field is
        /// "not authored" and is left at whatever <see cref="Create"/> already produced) onto
        /// <paramref name="rt"/>, via <see cref="SetRectField"/> — the ONE loop over
        /// <see cref="RectTransformFields.All"/> on the write side. Callers apply this AFTER the base
        /// localPosition/localRotation/localScale writes (D8: anchoredPosition re-derives
        /// m_LocalPosition and must win).</summary>
        internal static void ApplyRectFields(RectTransform rt, TransformData data)
        {
            foreach (var field in RectTransformFields.All)
            {
                if (field.Get(data) is { } value)
                {
                    SetRectField(rt, field.SerializedPath, value);
                }
            }
        }
    }
}
