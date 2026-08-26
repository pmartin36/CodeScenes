namespace SceneBuilder.Authoring
{
    /// <summary>
    /// A single-axis alignment mode for AlignTo: which edge or center of self lands on which
    /// reference plane of the target, plus an optional world-unit offset applied after alignment.
    /// UnityEngine-free so it can appear in generated authoring source without pulling in the
    /// runtime component.
    /// </summary>
    // The nested Kind schema below is the Core-side write contract's shape (SpatialComponents
    // .AlignToEnums mirrors these six members byte-for-byte), but Kind itself is private: nothing
    // reflects over this struct, only over the runtime AlignTo.Mode enum the parser/materializer
    // actually write.
    public readonly struct AxisAlign
    {
        private enum Kind : byte
        {
            None,
            AbutMin,
            AbutMax,
            AlignMin,
            AlignMax,
            AlignCenter,
        }

        private readonly byte _mode;
        private readonly float _offset;

        private AxisAlign(Kind kind, float offset)
        {
            _mode = (byte)kind;
            _offset = offset;
        }

        /// <summary>No alignment on this axis.</summary>
        public static readonly AxisAlign None = default;

        /// <summary>Self's maximum-side edge abuts the target's minimum-side edge (self sits outside,
        /// on the target's min side).</summary>
        public static readonly AxisAlign AbutMin = new AxisAlign(Kind.AbutMin, 0f);

        /// <summary>Self's minimum-side edge abuts the target's maximum-side edge (self sits outside,
        /// on the target's max side).</summary>
        public static readonly AxisAlign AbutMax = new AxisAlign(Kind.AbutMax, 0f);

        /// <summary>Self's minimum-side edge is flush with the target's minimum-side edge (near faces
        /// coincide). Requires a target.</summary>
        public static readonly AxisAlign AlignMin = new AxisAlign(Kind.AlignMin, 0f);

        /// <summary>Self's maximum-side edge is flush with the target's maximum-side edge (far faces
        /// coincide). Requires a target.</summary>
        public static readonly AxisAlign AlignMax = new AxisAlign(Kind.AlignMax, 0f);

        /// <summary>Self's center coincides with the target's center. Requires a target.</summary>
        public static readonly AxisAlign AlignCenter = new AxisAlign(Kind.AlignCenter, 0f);

        /// <summary>Returns a copy of this alignment carrying an additional world-unit offset applied
        /// after the alignment is resolved.</summary>
        public AxisAlign Offset(float worldUnits) => new AxisAlign((Kind)_mode, worldUnits);
    }
}
