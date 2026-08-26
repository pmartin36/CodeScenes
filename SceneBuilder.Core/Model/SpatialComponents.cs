namespace SceneBuilder.Core.Model
{
    // Core-side mirror of the runtime Between.Axis nested enum; order and underlying values MUST
    // match Between.Axis / BetweenEnums.Members index-for-index.
    public enum SpatialAxis
    {
        X = 0,
        Y = 1,
        Z = 2,
    }

    public static class SpatialComponents
    {
        public const string FitSizeTypeName   = "SceneBuilder.Authoring.FitSize";

        public const string BetweenTypeName = "SceneBuilder.Authoring.Between";

        public const string AlignToTypeName = "SceneBuilder.Authoring.AlignTo";

        public static class BetweenFields
        {
            public const string From = "from";
            public const string To = "to";
            public const string Fraction = "fraction";
            public const string Axis = "axis";
            public const string Orientation = "orientation";
        }

        /// <summary>The Between.Axis nested enum type FullName (nested-type "+" separator) and member
        /// names, mirroring the runtime <c>SceneBuilder.Authoring.Between+Axis</c> nested enum
        /// byte-for-byte. <see cref="Members"/> is index-ordered to match <see cref="SpatialAxis"/>'s
        /// underlying values.</summary>
        public static class BetweenEnums
        {
            public const string AxisTypeName = "SceneBuilder.Authoring.Between+Axis";
            public const string X = "X";
            public const string Y = "Y";
            public const string Z = "Z";
            public static readonly string[] Members = { X, Y, Z };
        }

        /// <summary>THE single owner of which channels a Between drives. World placement
        /// (oriented==false) drives only the one world-position component the axis names; an
        /// orientation-following placement (oriented==true) can move all three world-position
        /// components regardless of which axis it travels along.</summary>
        public static ChannelMask BetweenDrivenMask(SpatialAxis axis, bool oriented)
        {
            if (oriented)
            {
                return ChannelMask.PositionX | ChannelMask.PositionY | ChannelMask.PositionZ;
            }

            switch (axis)
            {
                case SpatialAxis.X: return ChannelMask.PositionX;
                case SpatialAxis.Y: return ChannelMask.PositionY;
                case SpatialAxis.Z: return ChannelMask.PositionZ;
                default:
                    throw new System.ArgumentOutOfRangeException(nameof(axis), axis, null);
            }
        }

        /// <summary>The ONE axis&lt;-&gt;<see cref="BetweenEnums"/> member table shared by parse (axis ->
        /// member) and emit (member -> axis). Never duplicate this mapping elsewhere.</summary>
        public static string AxisMember(SpatialAxis axis)
        {
            return BetweenEnums.Members[(int)axis];
        }

        public static bool TryAxisFromMember(string member, out SpatialAxis axis)
        {
            for (var i = 0; i < BetweenEnums.Members.Length; i++)
            {
                if (BetweenEnums.Members[i] == member)
                {
                    axis = (SpatialAxis)i;
                    return true;
                }
            }

            axis = default;
            return false;
        }

        /// <summary>A FitSize always drives the full Scale mask.</summary>
        public const ChannelMask FitSizeMask = ChannelMask.Scale;

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
        /// byte-for-byte. Mirrors <see cref="AlignToEnums"/>'s role for AlignTo. None MUST be
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

        public static class AlignToFields
        {
            public const string XMode  = "xMode";
            public const string XOffset = "xOffset";
            public const string YMode  = "yMode";
            public const string YOffset = "yOffset";
            public const string ZMode  = "zMode";
            public const string ZOffset = "zOffset";
            public const string Target = "target";
            public const string Frame  = "frame";
            public const string Space  = "space";
            public const string CaptureThreshold = "captureThreshold";
        }

        /// <summary>The AlignTo per-axis mode enum type FullName (nested-type "+" separator) and
        /// member names, mirroring the runtime <c>SceneBuilder.Authoring.AlignTo+Mode</c> nested enum
        /// byte-for-byte, plus the <see cref="SceneBuilder.Authoring.AlignSpace"/> contract. This is
        /// the ONE place the Core-string&lt;-&gt;runtime-type contract lives for AlignTo; a gate test
        /// reflects the runtime types and asserts they agree. None MUST be index 0 in the runtime enum
        /// (default == inert, survives default-value pruning). All six Mode members are declared here
        /// once; later work only appends preset FIELDS on the authoring struct, never new enum values.</summary>
        public static class AlignToEnums
        {
            public const string ModeTypeName = "SceneBuilder.Authoring.AlignTo+Mode";

            public const string None        = "None";
            public const string AbutMin     = "AbutMin";
            public const string AbutMax     = "AbutMax";
            public const string AlignMin    = "AlignMin";
            public const string AlignMax    = "AlignMax";
            public const string AlignCenter = "AlignCenter";
            public static readonly string[] Members = { None, AbutMin, AbutMax, AlignMin, AlignMax, AlignCenter };

            public const string AlignSpaceTypeName = "SceneBuilder.Authoring.AlignSpace";
            public const string TargetLocal = "TargetLocal";
            public const string World = "World";
            public static readonly string[] SpaceMembers = { TargetLocal, World };

            /// <summary>The authoring <c>AxisAlign</c> struct's FullName — the emit-side prefix source
            /// (mirrors how <see cref="BetweenEnums.AxisTypeName"/> derives the "Between.Axis" authoring
            /// prefix). Not nested ('+'-free): <c>AxisAlign</c> is a standalone authoring value type, not
            /// a member of <c>AlignTo</c>.</summary>
            public const string AxisAlignTypeName = "SceneBuilder.Authoring.AxisAlign";
        }

        /// <summary>THE single axis-&gt;channel owner for AlignTo: each pinned axis (Mode != None)
        /// drives the one world-position component it names.</summary>
        public static ChannelMask AlignToDrivenMask(bool xPinned, bool yPinned, bool zPinned)
        {
            var mask = ChannelMask.None;
            if (xPinned) mask |= ChannelMask.PositionX;
            if (yPinned) mask |= ChannelMask.PositionY;
            if (zPinned) mask |= ChannelMask.PositionZ;
            return mask;
        }

        /// <summary>An AlignTo axis is pinned whenever its Mode member is not None.</summary>
        public static bool IsAxisPinned(string modeMember) => modeMember != AlignToEnums.None;

        /// <summary>The ONE axis keyword&lt;-&gt;field-name table shared by parse (keyword -> mode/offset
        /// field) and emit (axis -> keyword). Never duplicate this mapping elsewhere.</summary>
        private static readonly (string Keyword, SpatialAxis Axis, string ModeField, string OffsetField)[] AlignAxisFields =
        {
            ("x", SpatialAxis.X, AlignToFields.XMode, AlignToFields.XOffset),
            ("y", SpatialAxis.Y, AlignToFields.YMode, AlignToFields.YOffset),
            ("z", SpatialAxis.Z, AlignToFields.ZMode, AlignToFields.ZOffset),
        };

        /// <summary>Parse direction: axis keyword (x/y/z) -&gt; the axis plus its mode/offset field
        /// names.</summary>
        public static bool TryAlignAxis(string keyword, out SpatialAxis axis, out string modeField, out string offsetField)
        {
            foreach (var entry in AlignAxisFields)
            {
                if (entry.Keyword == keyword)
                {
                    axis = entry.Axis;
                    modeField = entry.ModeField;
                    offsetField = entry.OffsetField;
                    return true;
                }
            }

            axis = default;
            modeField = null!;
            offsetField = null!;
            return false;
        }

        /// <summary>Emit direction: an axis -&gt; the authoring keyword that names it.</summary>
        public static bool TryAlignAxisKeyword(SpatialAxis axis, out string keyword)
        {
            foreach (var entry in AlignAxisFields)
            {
                if (entry.Axis == axis)
                {
                    keyword = entry.Keyword;
                    return true;
                }
            }

            keyword = null!;
            return false;
        }

        /// <summary>Emit direction (mode-flip patch): a mode FIELD name (xMode/yMode/zMode) -&gt; the
        /// authoring axis keyword that sets it — the reverse of <see cref="TryAlignAxis"/>, used by the
        /// live-edit single-field patch render (an in-scene member flip, e.g. Down-&gt;Up, replaces the
        /// WHOLE `y: AxisAlign.AbutMax` argument with `y: AxisAlign.AbutMin`).</summary>
        public static bool TryAlignAxisFromModeField(string modeField, out string keyword)
        {
            foreach (var entry in AlignAxisFields)
            {
                if (entry.ModeField == modeField)
                {
                    keyword = entry.Keyword;
                    return true;
                }
            }

            keyword = null!;
            return false;
        }

        /// <summary>The set of AlignTo.Mode presets the parser/recognizer accept as authoring
        /// identifiers (preset name == Mode member name — identity mapping). Distinct from
        /// <see cref="AlignToEnums.Members"/>, which fixes the full enum SCHEMA; this registry is the
        /// smaller, growable set of presets actually wired to authoring syntax. <c>None</c> is the
        /// default/unpinned case handled by callers, not a preset row.</summary>
        private static readonly string[] AlignPresets =
        {
            AlignToEnums.AbutMin, AlignToEnums.AbutMax,
            AlignToEnums.AlignMin, AlignToEnums.AlignMax, AlignToEnums.AlignCenter,
        };

        public static bool IsAlignPreset(string member)
        {
            foreach (var preset in AlignPresets)
            {
                if (preset == member) return true;
            }

            return false;
        }
    }
}
