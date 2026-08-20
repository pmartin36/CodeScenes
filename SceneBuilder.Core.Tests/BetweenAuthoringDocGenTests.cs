using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using SceneBuilder.DocGen;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Deliverable: NodeHandle.Between carries an XML summary, checked against the REAL extractor the
    // published api.json is built from.
    public class BetweenAuthoringDocGenTests
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
                    $"BetweenAuthoringDocGenTests.RepoRoot: no SceneBuilder.sln found walking up from '{here}'");
            }

            return dir;
        }

        [Fact]
        public void NodeHandleBetween_CarriesDocumentedSummary()
        {
            var repoRoot = RepoRoot();
            var runtimeDir = Path.Combine(repoRoot, "com.codescenes", "Runtime");

            var types = TypeExtractor.FromDirectory(runtimeDir, repoRoot);

            var nodeHandle = types.Single(t => t.Name == "NodeHandle");
            var betweenMember = nodeHandle.Members.Single(m => m.Name == "Between");
            Assert.NotEmpty(betweenMember.Summary);
        }
    }
}
