using System.Security.Cryptography;
using System.Text;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Serialization;

namespace SceneBuilder.Core.Sync
{
    // The public shape (three strongly-typed overloads, no string/stream overload) is the
    // compile-time guard that a caller cannot hash a hand-rolled non-canonical form through
    // this helper.
    public static class CanonicalHash
    {
        public static string Of(SceneModel model) => Compute(SceneModelSerializer.Serialize(model));

        public static string Of(SceneSnapshot snapshot) => Compute(CanonicalJson.Serialize(snapshot));

        public static string Of(IdentityMap map) => Compute(IdentityMapJson.Serialize(map));

        private static string Compute(string canonicalJson)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonicalJson));
            // netstandard2.1: Convert.ToHexString does not exist (net5+); use BitConverter.
            return System.BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }
    }
}
