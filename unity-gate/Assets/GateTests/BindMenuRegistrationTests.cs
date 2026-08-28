using System.IO;
using NUnit.Framework;
using SceneBuilder.Editor;
using SceneBuilder.Editor.Licensing;

// Gate for the "CodeScenes/Bind Current Scene To/<BuilderName>" submenu registration — spec 55's
// only user-facing entry point. The factored BindSceneToBuilder action is covered by
// SceneBuilderBindTests; this class covers the gap that let the submenu never appear in a live
// editor: the items were added ONLY inside the [InitializeOnLoad] static ctor, and Unity's
// post-load [MenuItem] rebuild drops reflection-added items registered during that phase, so the
// bind submenu was wiped and never restored.
//
// The fix defers registration onto EditorApplication.update via SceneBuilderBind.Refresh (mirroring
// SceneBuilderBuildStatusMenu), which re-adds the items past the post-load rebuild and keeps them
// current as builders are discovered. Unity's internal post-load menu rebuild is not reproducible
// from a headless EditMode test, so this asserts the deterministic registration entry point
// directly: after a builder is discovered, Refresh must register its bind item, and drop it once
// the builder is gone.
public class BindMenuRegistrationTests
{
    private const string Name = "BindMenuRegTarget";
    private static string MenuPath => SceneBuilderBind.MenuRoot + Name;

    private string _builderPath;

    [SetUp]
    public void SetUp()
    {
        LicenseGate.ResetToDefault();
        SceneBuilderRouter.ResetForTests();
        SceneBuilderBind.ResetForTests();

        SceneBuilderPaths.EnsureBuildersDirectory();
        _builderPath = SceneBuilderPaths.Builder(Name);
    }

    [TearDown]
    public void TearDown()
    {
        LicenseEnforcement.Register();
        SceneBuilderBind.ResetForTests();
        SceneBuilderRouter.ResetForTests();

        if (File.Exists(_builderPath)) File.Delete(_builderPath);
    }

    [Test]
    public void Refresh_RegistersBindItem_ForDiscoveredBuilder()
    {
        File.WriteAllText(_builderPath, "public class " + Name + " {}");
        SceneBuilderRouter.ResetForTests();

        // Precondition: the freshly seeded builder was not present at domain load, so the
        // static-ctor registration never covered it — exactly the state a live editor is in after
        // Unity's post-load menu rebuild wipes the ctor's reflection-added items.
        Assert.IsFalse(SceneBuilderBind.IsRegistered(MenuPath),
            "Precondition: the newly seeded builder's bind item must not be registered yet.");

        SceneBuilderBind.Refresh();

        Assert.IsTrue(SceneBuilderBind.IsRegistered(MenuPath),
            "Refresh must register the 'Bind Current Scene To/<Builder>' item for a discovered builder.");
    }

    [Test]
    public void Refresh_RemovesBindItem_WhenBuilderGone()
    {
        File.WriteAllText(_builderPath, "public class " + Name + " {}");
        SceneBuilderRouter.ResetForTests();
        SceneBuilderBind.Refresh();
        Assert.IsTrue(SceneBuilderBind.IsRegistered(MenuPath),
            "Precondition: the discovered builder's bind item must be registered.");

        File.Delete(_builderPath);
        SceneBuilderRouter.ResetForTests();
        SceneBuilderBind.Refresh();

        Assert.IsFalse(SceneBuilderBind.IsRegistered(MenuPath),
            "Refresh must remove the bind item once its builder no longer exists.");
    }
}
