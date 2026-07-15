using SceneBuilder.Core.Model;
using SceneBuilder.Core.Serialization;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b5-t2: MonoScript identity — TypeRef anchored to MonoScript GUID resolves across AssemblyHint.
    public class AssetRefMonoScriptTests
    {
        [Fact]
        public void MonoScript_TypeRefAnchoredToGuid_ResolvesAcrossAssemblyHint()
        {
            var a = new TypeRef("Game.Health", "AsmA", "g1");
            var b = new TypeRef("Game.Health", "AsmB", "g1");

            Assert.Equal(a, b);
            Assert.True(a == b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
        }

        [Fact]
        public void MonoScript_TypeRefAnchoredToGuid_SurvivesNamespaceChurn()
        {
            var a = new TypeRef("Old.Health", null, "g1");
            var b = new TypeRef("New.Health", null, "g1");

            Assert.Equal(a, b);
        }

        [Fact]
        public void MonoScript_DifferentGuid_NotEqual()
        {
            var a = new TypeRef("Game.Health", null, "g1");
            var b = new TypeRef("Game.Health", null, "g2");

            Assert.NotEqual(a, b);
        }

        [Fact]
        public void BuiltIn_NoGuid_FallsBackToPriorFullNameAssemblyHintSemantics()
        {
            var same = new TypeRef("UnityEngine.Rigidbody");
            var sameAgain = new TypeRef("UnityEngine.Rigidbody");
            Assert.Equal(same, sameAgain);

            var differingAssembly = new TypeRef("UnityEngine.Rigidbody", "AsmA");
            var differingAssembly2 = new TypeRef("UnityEngine.Rigidbody", "AsmB");
            Assert.NotEqual(differingAssembly, differingAssembly2);
        }

        [Fact]
        public void MonoScriptGuid_RoundTripsThroughCanonicalJson()
        {
            var original = new TypeRef("Game.Health", "Asm", "g1");

            var json = CanonicalJson.Serialize(original);
            Assert.Contains("monoScriptGuid", json);

            var back = CanonicalJson.Deserialize<TypeRef>(json);
            Assert.Equal("g1", back!.MonoScriptGuid);
            Assert.Equal(original, back);
        }

        [Fact]
        public void NullMonoScriptGuid_OmittedFromCanonicalJson()
        {
            var original = new TypeRef("UnityEngine.Rigidbody");

            var json = CanonicalJson.Serialize(original);

            Assert.DoesNotContain("monoScriptGuid", json);
        }
    }
}
