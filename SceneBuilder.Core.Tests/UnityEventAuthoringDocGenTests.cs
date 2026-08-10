using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using SceneBuilder.DocGen;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Deliverable: every public member the `.Ref<T>()`/`.OnClick(...)` surface adds to
    // com.codescenes/Runtime carries an XML <summary>, checked against the REAL extractor the
    // published api.json is built from.
    public class UnityEventAuthoringDocGenTests
    {
        private static string RepoRoot([CallerFilePath] string here = "")
        {
            var dir = Path.GetDirectoryName(here);
            while (dir != null && !File.Exists(Path.Combine(dir, "SceneBuilder.sln")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            if (dir == null)
            {
                throw new InvalidOperationException(
                    $"UnityEventAuthoringDocGenTests.RepoRoot: no SceneBuilder.sln found walking up from '{here}'");
            }

            return dir;
        }

        [Fact]
        public void ComponentRefAndItsWiringMembers_CarryDocumentedSummaries()
        {
            var repoRoot = RepoRoot();
            var runtimeDir = Path.Combine(repoRoot, "com.codescenes", "Runtime");

            var types = TypeExtractor.FromDirectory(runtimeDir, repoRoot);

            var componentRef = types.Single(t => t.Name == "ComponentRef");
            Assert.NotEmpty(componentRef.Summary);

            var nodeHandle = types.Single(t => t.Name == "NodeHandle");
            var refMember = nodeHandle.Members.Single(m => m.Name == "Ref");
            Assert.NotEmpty(refMember.Summary);

            var componentHandle = types.Single(t => t.Name == "ComponentHandle");
            var onClickMember = componentHandle.Members.Single(m => m.Name == "OnClick");
            Assert.NotEmpty(onClickMember.Summary);
        }

        // Deliverable: the `.OnEvent(...)` generic-event-field wiring surface and the `.As()`/
        // `.As<T>()` typed pass-throughs each carry a documented summary too.
        [Fact]
        public void OnEventAndTypedPassThroughMembers_CarryDocumentedSummaries()
        {
            var repoRoot = RepoRoot();
            var runtimeDir = Path.Combine(repoRoot, "com.codescenes", "Runtime");

            var types = TypeExtractor.FromDirectory(runtimeDir, repoRoot);

            var componentHandle = types.Single(t => t.Name == "ComponentHandle");
            var onEventMember = componentHandle.Members.Single(m => m.Name == "OnEvent");
            Assert.NotEmpty(onEventMember.Summary);

            var sceneObjectHandle = types.Single(t => t.Name == "SceneObjectHandle");
            var sceneObjectAsMember = sceneObjectHandle.Members.Single(m => m.Name == "As");
            Assert.NotEmpty(sceneObjectAsMember.Summary);

            var assetReference = types.Single(t => t.Name == "AssetReference");
            var assetAsMember = assetReference.Members.Single(m => m.Name == "As");
            Assert.NotEmpty(assetAsMember.Summary);

            var componentRef = types.Single(t => t.Name == "ComponentRef");
            var componentRefAsMember = componentRef.Members.Single(m => m.Name == "As");
            Assert.NotEmpty(componentRefAsMember.Summary);
        }
    }
}
