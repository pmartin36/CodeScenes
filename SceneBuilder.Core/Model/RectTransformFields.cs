using System;
using System.Collections.Generic;

namespace SceneBuilder.Core.Model
{
    /// <summary>
    /// The ONE table of RectTransform field descriptors: serialized paths, authoring argument names,
    /// ChannelMask mappings and live-verified defaults. Every later task (parse, diff, materialize,
    /// adapter, reconcile) keys off this table instead of re-spelling a five-way branch. See
    /// research.md (m-ui-recttransform/b1-t1) for the full contract.
    /// </summary>
    public static class RectTransformFields
    {
        public const string Kind = "RectTransform";

        // Serialized paths (b2-t2 lowering / b2-t3 adapter write). Whole `m_*` paths only — never a sub-path.
        public const string AnchoredPositionPath = "m_AnchoredPosition";
        public const string SizeDeltaPath        = "m_SizeDelta";
        public const string AnchorMinPath        = "m_AnchorMin";
        public const string AnchorMaxPath        = "m_AnchorMax";
        public const string PivotPath            = "m_Pivot";

        // Authoring argument names. b1-t2 re-declares these spellings LOCALLY in SceneBuilder.Grammar (D7)
        // and NodeHandle re-declares them as parameter names; all three must match character for character.
        public const string AnchoredPosArg = "anchoredPos";
        public const string SizeDeltaArg   = "sizeDelta";
        public const string AnchorMinArg   = "anchorMin";
        public const string AnchorMaxArg   = "anchorMax";
        public const string PivotArg       = "pivot";

        // LIVE-VERIFIED against Unity 6000.5.3f1 (new GameObject(n, typeof(RectTransform)),
        // ObjectFactory.CreateGameObject and Graphic-triggered promotion all agree). research.md's
        // A3 probe REFUTES spec 13:106 / D9's guessed (0,0) for sizeDelta/anchorMin/anchorMax.
        public static readonly Vec2 DefaultAnchoredPosition = new(0f, 0f);
        public static readonly Vec2 DefaultSizeDelta        = new(100f, 100f);
        public static readonly Vec2 DefaultAnchorMin        = new(0.5f, 0.5f);
        public static readonly Vec2 DefaultAnchorMax        = new(0.5f, 0.5f);
        public static readonly Vec2 DefaultPivot            = new(0.5f, 0.5f);

        /// <summary>One descriptor per RectTransform field. Every later task keys off this table
        /// instead of re-spelling a five-way branch.</summary>
        public sealed class Field
        {
            public string ArgName { get; init; } = null!;
            public string SerializedPath { get; init; } = null!;
            public ChannelMask Mask { get; init; }
            public ChannelMask MaskX { get; init; }
            public ChannelMask MaskY { get; init; }
            public Vec2 Default { get; init; }
            public Func<TransformData, Vec2?> Get { get; init; } = null!;
            public Func<TransformData, Vec2, TransformData> With { get; init; } = null!;
        }

        /// <summary>The five fields in CANONICAL ARGUMENT ORDER — anchoredPos, sizeDelta, anchorMin,
        /// anchorMax, pivot. b3-t3 inserts a missing argument in this order.</summary>
        public static readonly IReadOnlyList<Field> All = new Field[]
        {
            new Field
            {
                ArgName = AnchoredPosArg,
                SerializedPath = AnchoredPositionPath,
                Mask = ChannelMask.AnchoredPosition,
                MaskX = ChannelMask.AnchoredPositionX,
                MaskY = ChannelMask.AnchoredPositionY,
                Default = DefaultAnchoredPosition,
                Get = t => t.AnchoredPosition,
                With = (t, v) => t with { AnchoredPosition = v },
            },
            new Field
            {
                ArgName = SizeDeltaArg,
                SerializedPath = SizeDeltaPath,
                Mask = ChannelMask.SizeDelta,
                MaskX = ChannelMask.SizeDeltaX,
                MaskY = ChannelMask.SizeDeltaY,
                Default = DefaultSizeDelta,
                Get = t => t.SizeDelta,
                With = (t, v) => t with { SizeDelta = v },
            },
            new Field
            {
                ArgName = AnchorMinArg,
                SerializedPath = AnchorMinPath,
                Mask = ChannelMask.AnchorMin,
                MaskX = ChannelMask.AnchorMinX,
                MaskY = ChannelMask.AnchorMinY,
                Default = DefaultAnchorMin,
                Get = t => t.AnchorMin,
                With = (t, v) => t with { AnchorMin = v },
            },
            new Field
            {
                ArgName = AnchorMaxArg,
                SerializedPath = AnchorMaxPath,
                Mask = ChannelMask.AnchorMax,
                MaskX = ChannelMask.AnchorMaxX,
                MaskY = ChannelMask.AnchorMaxY,
                Default = DefaultAnchorMax,
                Get = t => t.AnchorMax,
                With = (t, v) => t with { AnchorMax = v },
            },
            new Field
            {
                ArgName = PivotArg,
                SerializedPath = PivotPath,
                Mask = ChannelMask.Pivot,
                MaskX = ChannelMask.PivotX,
                MaskY = ChannelMask.PivotY,
                Default = DefaultPivot,
                Get = t => t.Pivot,
                With = (t, v) => t with { Pivot = v },
            },
        };

        public static bool TryFromArgName(string argName, out Field field)
        {
            foreach (var entry in All)
            {
                if (entry.ArgName == argName)
                {
                    field = entry;
                    return true;
                }
            }

            field = null!;
            return false;
        }

        public static bool TryFromSerializedPath(string path, out Field field)
        {
            foreach (var entry in All)
            {
                if (entry.SerializedPath == path)
                {
                    field = entry;
                    return true;
                }
            }

            field = null!;
            return false;
        }

        /// <summary>offsetMin == anchoredPosition - sizeDelta * pivot (Unity's own formula,
        /// RectTransform.bindings.cs). anchorMin/anchorMax deliberately DO NOT participate: offsets are
        /// relative to the anchors, so the anchor values cancel. Derived on demand, never stored
        /// (spec 13:18-20).</summary>
        public static Vec2 OffsetMin(Vec2 anchoredPosition, Vec2 sizeDelta, Vec2 pivot)
            => new(
                anchoredPosition.X - sizeDelta.X * pivot.X,
                anchoredPosition.Y - sizeDelta.Y * pivot.Y);

        /// <summary>offsetMax == anchoredPosition + sizeDelta * (1 - pivot).</summary>
        public static Vec2 OffsetMax(Vec2 anchoredPosition, Vec2 sizeDelta, Vec2 pivot)
            => new(
                anchoredPosition.X + sizeDelta.X * (1f - pivot.X),
                anchoredPosition.Y + sizeDelta.Y * (1f - pivot.Y));

        /// <summary>b2-t1/R4: holds each DRIVEN rect axis (per <see cref="TransformData.DrivenChannels"/>,
        /// rect bits only) to the model's value so a diff can never register a difference on that axis —
        /// the rect-field mirror of Reconciler.MaskDriven's base-transform Position/Scale masking. Only
        /// fields the snapshot actually has set are rewritten; when no rect channel is driven the
        /// snapshot is returned UNCHANGED (same reference). Shared by the Differ (this task) and
        /// Reconciler.MaskDriven's rect extension (b3-t2) — never re-implement the loop.</summary>
        public static TransformData MaskDrivenRect(TransformData model, TransformData snapshot)
        {
            var driven = snapshot.DrivenChannels & ChannelMask.AllRectFields;
            if (driven == ChannelMask.None)
            {
                return snapshot;
            }

            var result = snapshot;
            foreach (var field in All)
            {
                var snapshotValue = field.Get(snapshot);
                if (snapshotValue is not { } sv)
                {
                    continue;
                }

                var drivenAxes = driven & field.Mask;
                if (drivenAxes == ChannelMask.None)
                {
                    continue;
                }

                var modelValue = field.Get(model) ?? field.Default;
                var x = (drivenAxes & field.MaskX) != 0 ? modelValue.X : sv.X;
                var y = (drivenAxes & field.MaskY) != 0 ? modelValue.Y : sv.Y;
                result = field.With(result, new Vec2(x, y));
            }

            return result;
        }
    }
}
