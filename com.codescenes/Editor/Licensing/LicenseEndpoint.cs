namespace SceneBuilder.Editor.Licensing
{
    // Single owner of the licensing backend URL. LicenseTransport reads Url (never a
    // hardcoded literal) so tests can redirect the transport to a fixture endpoint
    // without touching any call site.
    public static class LicenseEndpoint
    {
        public const string Default = "https://us-central1-codescenes.cloudfunctions.net/license";

        public static string Url { get; set; } = Default;

        public static void ResetToDefault() => Url = Default;
    }
}
