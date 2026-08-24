namespace SceneBuilder.Core.Validation
{
    public static class DiagnosticCodes
    {
        public const string ParseError = "SB1000";
        public const string UnresolvedType = "SB2001";
        public const string AmbiguousType = "SB2002";
        public const string AssetPathNotFound = "SB2101";
        public const string AmbiguousDuplicateSibling = "SB2201";
        public const string DuplicateLogicalId = "SB2202";
        public const string UnknownFacadeReference = "SB2203";
        public const string PrefabOverridesNotModelled = "SB2301";
        public const string UnresolvedNestedSelector = "SB2302";
        public const string PrefabMultipleRoots = "SB2401";
    }
}
