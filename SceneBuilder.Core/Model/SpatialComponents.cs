namespace SceneBuilder.Core.Model
{
    public static class SpatialComponents
    {
        public const string FitSizeTypeName   = "SceneBuilder.Authoring.FitSize";
        public const string SurfaceSnapTypeName = "SceneBuilder.Authoring.SurfaceSnap";

        /// <summary>A FitSize always drives the full Scale mask.</summary>
        public const ChannelMask FitSizeMask = ChannelMask.Scale;

        /// <summary>
        /// The single shared SurfaceSnap flag-&gt;mask mapping. Both parse (desired side) and the
        /// live-scene snapshot reader (actual side) MUST call this so the two never diverge:
        /// left/right -&gt; PositionX, up/down -&gt; PositionY, forward/back -&gt; PositionZ.
        /// </summary>
        public static ChannelMask SurfaceSnapMask(bool up, bool down, bool left, bool right, bool forward, bool back)
        {
            var mask = ChannelMask.None;
            if (left || right) mask |= ChannelMask.PositionX;
            if (up || down) mask |= ChannelMask.PositionY;
            if (forward || back) mask |= ChannelMask.PositionZ;
            return mask;
        }

        public static class FitSizeFields
        {
            public const string Width  = "width";
            public const string Height = "height";
            public const string Depth  = "depth";
            public const string Size   = "size";
        }

        public static class SurfaceSnapFields
        {
            public const string Up      = "up";
            public const string Down    = "down";
            public const string Left    = "left";
            public const string Right   = "right";
            public const string Forward = "forward";
            public const string Back    = "back";
            public const string Target  = "target";
        }
    }
}
