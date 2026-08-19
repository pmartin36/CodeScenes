using System;
using System.Security.Cryptography;

namespace SceneBuilder.Editor.Licensing
{
    // The embedded production RSA-2048 public key (JWK n/e, base64url, no padding),
    // and the base64url codec shared by the license token format.
    public static class LicensePublicKey
    {
        public const string ModulusBase64Url =
            "qyo_i4jQac8mjjhvuyt91PxppQTwzKEsmKB8CScrJdEZAhRPFdf29cghPofcUm2gVRswp0H_AMRZLCiF5SPzgBz4CSvrNNyTFYrxS1n6RNxVrK5xAQeCbbUX4EiAsoSjAXvp4d-uUPKBj6I4Tnt43XvwgjmK4iDOsoogrL5BNymr9Dd4S6vjoyJBI6ko0InmolnROti2ELLtw_e5fMSGN-NldgU0BXmfnEs5t6pFcpNGMk3Ym0Q3QS_y6i5A-Qi491zFYQwCpOYHVJJnvdr956_QiKMyBi5Vt0W5VDSLOk3bMB1UxQsF8KGsbA7EDGRXpYJSRbm8INo7brGdWaS3ew";

        public const string ExponentBase64Url = "AQAB";

        public static RSAParameters ToParameters()
        {
            return new RSAParameters
            {
                Modulus = Decode(ModulusBase64Url),
                Exponent = Decode(ExponentBase64Url)
            };
        }

        public static byte[] Decode(string base64Url)
        {
            string padded = base64Url.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }
            return Convert.FromBase64String(padded);
        }
    }
}
