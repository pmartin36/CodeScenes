namespace SceneBuilder.Core.Model
{
    public static class SpatialComponents
    {
        public const string SizerTypeName   = "SceneBuilder.Authoring.Sizer";
        public const string SnapperTypeName = "SceneBuilder.Authoring.Snapper";

        /// <summary>A Sizer always drives the full Scale mask.</summary>
        public const ChannelMask SizerMask = ChannelMask.Scale;

        /// <summary>
        /// The single shared Snapper flag-&gt;mask mapping. Both parse (desired side) and the
        /// live-scene snapshot reader (actual side) MUST call this so the two never diverge:
        /// left/right -&gt; PositionX, up/down -&gt; PositionY, forward/back -&gt; PositionZ.
        /// </summary>
        public static ChannelMask SnapperMask(bool up, bool down, bool left, bool right, bool forward, bool back)
        {
            var mask = ChannelMask.None;
            if (left || right) mask |= ChannelMask.PositionX;
            if (up || down) mask |= ChannelMask.PositionY;
            if (forward || back) mask |= ChannelMask.PositionZ;
            return mask;
        }

        public static class SizerFields
        {
            public const string Width  = "width";
            public const string Height = "height";
            public const string Depth  = "depth";
            public const string Size   = "size";
        }

        public static class SnapperFields
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
