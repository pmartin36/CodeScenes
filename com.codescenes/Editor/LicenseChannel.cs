namespace SceneBuilder.Editor
{
    // Single C# owner of the channel define name and the build-time channel flag. Lives in the
    // MAIN assembly (compiled into both channels) so it stays observable even in an Asset-Store
    // build, where the licensing assembly (and its `!CODESCENES_ASSET_STORE` defineConstraint)
    // is excluded. Absent define => Gumroad build, licensing compiled in, activation required
    // (fail-closed). Define present => licensing asmdef excluded, no activation UI, no backend
    // call, LicenseGate defaults to allowed.
    public static class LicenseChannel
    {
        public const string AssetStoreDefine = "CODESCENES_ASSET_STORE";

#if CODESCENES_ASSET_STORE
        public const bool IsAssetStoreBuild = true;
#else
        public const bool IsAssetStoreBuild = false;
#endif
    }
}
