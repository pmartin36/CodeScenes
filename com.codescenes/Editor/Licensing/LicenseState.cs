namespace SceneBuilder.Editor.Licensing
{
    // The three states the rest of the adapter reads. Always derived from the one persisted
    // token via TokenVerifier; there is no independent way to set a state directly.
    public enum LicenseState
    {
        Licensed,
        Trial,
        Unlicensed
    }
}
