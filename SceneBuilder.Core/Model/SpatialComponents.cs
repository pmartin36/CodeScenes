namespace SceneBuilder.Core.Model
{
    public static class SpatialComponents
    {
        public const string SizerTypeName   = "SceneBuilder.Authoring.Sizer";
        public const string SnapperTypeName = "SceneBuilder.Authoring.Snapper";

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
