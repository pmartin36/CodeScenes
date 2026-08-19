using System;

namespace SceneBuilder.Editor.Licensing
{
    // Decoded license token payload. Field names match the wire JSON keys exactly
    // (v, kind, sub, lic, iat, exp) so JsonUtility can deserialize it directly.
    [Serializable]
    public class LicenseToken
    {
        public int v;
        public string kind;
        public string sub;
        public string lic;
        public long iat;
        public long exp;
    }
}
