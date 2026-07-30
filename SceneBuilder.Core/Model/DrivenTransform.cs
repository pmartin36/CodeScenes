namespace SceneBuilder.Core.Model
{
    // b2-t1/i3: the ONE per-axis base-transform hold shared by BOTH sync directions. Code->scene
    // (RectTransformDiff.HoldForWrite) and scene->code (Reconciler.MaskDriven) each call this with
    // their arguments swapped: the side that MUST NOT CHANGE supplies the driven axes, the other
    // side is `target` and keeps every free axis it already has.
    public static class DrivenTransform
    {
        public const ChannelMask BaseChannels =
            ChannelMask.PositionX | ChannelMask.PositionY | ChannelMask.PositionZ
            | ChannelMask.ScaleX | ChannelMask.ScaleY | ChannelMask.ScaleZ;

        // `target` with every BASE Position/Scale axis named in `driven` taken from `drivenSource`;
        // every other axis keeps `target`'s own value. `driven` bits outside BaseChannels (e.g. the
        // ten rect-field channels) are ignored here — this holds only Position/Scale. Returns
        // `target` unchanged when no base channel is driven.
        public static TransformData HoldBase(TransformData target, TransformData drivenSource, ChannelMask driven)
        {
            if ((driven & BaseChannels) == ChannelMask.None)
            {
                return target;
            }

            var pos = new Vec3(
                (driven & ChannelMask.PositionX) != 0 ? drivenSource.Position.X : target.Position.X,
                (driven & ChannelMask.PositionY) != 0 ? drivenSource.Position.Y : target.Position.Y,
                (driven & ChannelMask.PositionZ) != 0 ? drivenSource.Position.Z : target.Position.Z);
            var scale = new Vec3(
                (driven & ChannelMask.ScaleX) != 0 ? drivenSource.Scale.X : target.Scale.X,
                (driven & ChannelMask.ScaleY) != 0 ? drivenSource.Scale.Y : target.Scale.Y,
                (driven & ChannelMask.ScaleZ) != 0 ? drivenSource.Scale.Z : target.Scale.Z);

            return target with { Position = pos, Scale = scale };
        }
    }
}
