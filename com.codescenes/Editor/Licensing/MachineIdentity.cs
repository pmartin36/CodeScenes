using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace SceneBuilder.Editor.Licensing
{
    // Machine identity for licensing. Raw is sent un-rehashed on wire calls; Hash is the
    // lowercase-hex SHA-256 of Raw that a token's `sub` must match.
    public static class MachineIdentity
    {
        public static string Raw => SystemInfo.deviceUniqueIdentifier;

        // Seat label/os shared by activation and the daily entitlement refresh so both
        // send identical values.
        public static string Label => SystemInfo.deviceName;

        public static string Os => SystemInfo.operatingSystem;

        public static string Hash
        {
            get
            {
                using var sha = SHA256.Create();
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(Raw));
                return System.BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
