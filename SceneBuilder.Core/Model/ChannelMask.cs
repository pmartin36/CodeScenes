namespace SceneBuilder.Core.Model
{
    [System.Flags]
    public enum ChannelMask
    {
        None      = 0,
        PositionX = 1 << 0,
        PositionY = 1 << 1,
        PositionZ = 1 << 2,
        ScaleX    = 1 << 3,
        ScaleY    = 1 << 4,
        ScaleZ    = 1 << 5,
        Scale = ScaleX | ScaleY | ScaleZ,

        AnchoredPositionX = 1 << 6,
        AnchoredPositionY = 1 << 7,
        SizeDeltaX        = 1 << 8,
        SizeDeltaY        = 1 << 9,
        AnchorMinX        = 1 << 10,
        AnchorMinY        = 1 << 11,
        AnchorMaxX        = 1 << 12,
        AnchorMaxY        = 1 << 13,
        PivotX            = 1 << 14,
        PivotY            = 1 << 15,

        // Reserved rotation seats for a future OrientToSurface; nothing drives rotation in this
        // feature.
        RotationX         = 1 << 16,
        RotationY         = 1 << 17,
        RotationZ         = 1 << 18,

        AnchoredPosition = AnchoredPositionX | AnchoredPositionY,
        SizeDelta        = SizeDeltaX        | SizeDeltaY,
        AnchorMin        = AnchorMinX        | AnchorMinY,
        AnchorMax        = AnchorMaxX        | AnchorMaxY,
        Pivot            = PivotX            | PivotY,
        AllRectFields    = AnchoredPosition | SizeDelta | AnchorMin | AnchorMax | Pivot,
    }
}
