namespace SceneBuilder.Core.Model
{
    public static class SpatialComponents
    {
        public const string FitSizeTypeName   = "SceneBuilder.Authoring.FitSize";
        public const string SurfaceSnapTypeName = "SceneBuilder.Authoring.SurfaceSnap";

        /// <summary>A FitSize always drives the full Scale mask.</summary>
        public const ChannelMask FitSizeMask = ChannelMask.Scale;

        /// <summary>
        /// The single shared SurfaceSnap axis-&gt;mask mapping. Both parse (desired side) and the
        /// live-scene snapshot reader (actual side) MUST call this so the two never diverge:
        /// horizontal (left/right) -&gt; PositionX, vertical (up/down) -&gt; PositionY,
        /// depth (forward/back) -&gt; PositionZ.
        /// </summary>
        public static ChannelMask SurfaceSnapMask(bool verticalSet, bool horizontalSet, bool depthSet)
        {
            var mask = ChannelMask.None;
            if (horizontalSet) mask |= ChannelMask.PositionX;
            if (verticalSet) mask |= ChannelMask.PositionY;
            if (depthSet) mask |= ChannelMask.PositionZ;
            return mask;
        }

        public static class FitSizeFields
        {
            // The real runtime write contract (mode/value/size — b3-t1's enum migration, replacing the
            // old NaN-sentinel width/height/depth floats). Parse/emit/materialize/reader all key on
            // these three.
            public const string Mode  = "mode";
            public const string Value = "value";
            public const string Size  = "size";

            // Legacy field-name literals. No longer the FitSize write contract (superseded by
            // Mode/Value/Size above) — kept ONLY because DifferTests.Diff_FitSizeFieldChanged_
            // ProducesComponentFieldChange (out of b3-t1's scope; explicitly marked KEEP in
            // tasks.md's b1-t1 STALE_TESTS) uses Height as a generic example field key for the
            // component-agnostic Differ. Do not reintroduce these into parse/emit/reader.
            public const string Width  = "width";
            public const string Height = "height";
            public const string Depth  = "depth";
        }

        /// <summary>b3-t1: the FitSize mode enum type FullName (nested-type "+" separator) and member
        /// names, mirroring the runtime <c>SceneBuilder.Authoring.FitSize+Mode</c> nested enum
        /// byte-for-byte. Mirrors <see cref="SurfaceSnapEnums"/>'s role for SurfaceSnap. None MUST be
        /// index 0 (default == inert == survives default-value pruning for every authored mode).</summary>
        public static class FitSizeEnums
        {
            public const string ModeTypeName = "SceneBuilder.Authoring.FitSize+Mode";

            public const string None     = "None";
            public const string Width    = "Width";
            public const string Height   = "Height";
            public const string Depth    = "Depth";
            public const string Explicit = "Explicit";
        }

        // The ONE keyword<->member table shared by parse (aspect keyword -> mode member) and emit
        // (mode member -> aspect keyword). Never duplicate this mapping elsewhere. Explicit<->`size`
        // is handled directly by callers (not in this table — there is no "explicit" authoring keyword).
        private static readonly (string Keyword, string Member)[] FitAspectModes =
        {
            ("width",  FitSizeEnums.Width),
            ("height", FitSizeEnums.Height),
            ("depth",  FitSizeEnums.Depth),
        };

        /// <summary>Parse direction: aspect keyword (width/height/depth) -&gt; the FitSize.Mode member
        /// it sets.</summary>
        public static bool TryFitAspectMode(string keyword, out string member)
        {
            foreach (var entry in FitAspectModes)
            {
                if (entry.Keyword == keyword)
                {
                    member = entry.Member;
                    return true;
                }
            }

            member = null!;
            return false;
        }

        /// <summary>Emit direction: a FitSize.Mode member (Width/Height/Depth) -&gt; the authoring
        /// keyword that set it.</summary>
        public static bool TryFitAspectKeyword(string member, out string keyword)
        {
            foreach (var entry in FitAspectModes)
            {
                if (entry.Member == member)
                {
                    keyword = entry.Keyword;
                    return true;
                }
            }

            keyword = null!;
            return false;
        }

        public static class SurfaceSnapFields
        {
            // The per-axis enum field keys — the real runtime component write/read contract
            // (SpatialComponents.SurfaceSnapEnums mirrors the enum type FullNames/member names).
            public const string Vertical   = "vertical";
            public const string Horizontal = "horizontal";
            public const string Depth      = "depth";

            public const string Target  = "target";

            // Retained ONLY as a generic Core-side field-key literal used by pre-existing
            // Differ/Reconciler tests exercising arbitrary field diffs (not the SurfaceSnap
            // runtime component, which no longer has a "down" bool field). Do not wire this
            // into parse/emit/reader — those all go through Vertical/Horizontal/Depth above.
            public const string Down = "down";
        }

        /// <summary>b2-t1: the SurfaceSnap per-axis enum type FullNames (nested-type "+" separator) and
        /// member names, mirroring the runtime <c>SceneBuilder.Authoring.SurfaceSnap</c> nested enums
        /// byte-for-byte. This is the ONE place the Core-string&lt;-&gt;runtime-type contract lives; a
        /// gate test reflects the runtime enums and asserts they agree (idempotence depends on it — see
        /// research.md's REFINED finding on ValueNode.Enum vs Primitive.Int).</summary>
        public static class SurfaceSnapEnums
        {
            public const string VerticalTypeName   = "SceneBuilder.Authoring.SurfaceSnap+Vertical";
            public const string HorizontalTypeName = "SceneBuilder.Authoring.SurfaceSnap+Horizontal";
            public const string DepthTypeName      = "SceneBuilder.Authoring.SurfaceSnap+Depth";

            // Member names — None MUST be index 0 in each runtime enum (default-value pruning on read).
            public const string None    = "None";
            public const string Up      = "Up";
            public const string Down    = "Down";
            public const string Left    = "Left";
            public const string Right   = "Right";
            public const string Forward = "Forward";
            public const string Back    = "Back";
        }

        // The ONE keyword&lt;-&gt;(fieldKey, enumTypeName, memberName) table shared by parse (keyword ->
        // enum field) and emit (enum field -> keyword). Never duplicate this mapping elsewhere.
        private static readonly (string Keyword, string FieldKey, string EnumTypeName, string Member)[] AxisKeywords =
        {
            ("up",      SurfaceSnapFields.Vertical,   SurfaceSnapEnums.VerticalTypeName,   SurfaceSnapEnums.Up),
            ("down",    SurfaceSnapFields.Vertical,   SurfaceSnapEnums.VerticalTypeName,   SurfaceSnapEnums.Down),
            ("left",    SurfaceSnapFields.Horizontal, SurfaceSnapEnums.HorizontalTypeName, SurfaceSnapEnums.Left),
            ("right",   SurfaceSnapFields.Horizontal, SurfaceSnapEnums.HorizontalTypeName, SurfaceSnapEnums.Right),
            ("forward", SurfaceSnapFields.Depth,      SurfaceSnapEnums.DepthTypeName,      SurfaceSnapEnums.Forward),
            ("back",    SurfaceSnapFields.Depth,      SurfaceSnapEnums.DepthTypeName,      SurfaceSnapEnums.Back),
        };

        /// <summary>Parse direction: authoring keyword (up/down/left/right/forward/back) -&gt; the
        /// per-axis enum field it sets.</summary>
        public static bool TryAxisKeyword(string keyword, out string fieldKey, out string enumTypeName, out string member)
        {
            foreach (var entry in AxisKeywords)
            {
                if (entry.Keyword == keyword)
                {
                    fieldKey = entry.FieldKey;
                    enumTypeName = entry.EnumTypeName;
                    member = entry.Member;
                    return true;
                }
            }

            fieldKey = null!;
            enumTypeName = null!;
            member = null!;
            return false;
        }

        /// <summary>Emit direction: a per-axis enum field's (fieldKey, member) -&gt; the authoring
        /// keyword that set it.</summary>
        public static bool TryAxisFromEnumField(string fieldKey, string member, out string keyword)
        {
            foreach (var entry in AxisKeywords)
            {
                if (entry.FieldKey == fieldKey && entry.Member == member)
                {
                    keyword = entry.Keyword;
                    return true;
                }
            }

            keyword = null!;
            return false;
        }
    }
}
