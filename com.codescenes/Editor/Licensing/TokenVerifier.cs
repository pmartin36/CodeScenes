using System;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace SceneBuilder.Editor.Licensing
{
    public enum TokenVerifyStatus
    {
        Valid,
        Malformed,
        BadSignature,
        WrongMachine,
        Expired
    }

    public readonly struct TokenVerifyResult
    {
        public readonly TokenVerifyStatus Status;
        public readonly LicenseToken Token;

        public TokenVerifyResult(TokenVerifyStatus status, LicenseToken token)
        {
            Status = status;
            Token = token;
        }
    }

    // Offline RSA-2048/PKCS#1v1.5/SHA-256 verification of a backend-issued license token.
    // A token is Valid iff its signature verifies, its sub matches the caller-supplied
    // machine hash, and its exp is in the future.
    public sealed class TokenVerifier
    {
        private readonly RSAParameters _publicKey;

        public TokenVerifier(RSAParameters publicKey)
        {
            _publicKey = publicKey;
        }

        public static TokenVerifier Production => new TokenVerifier(LicensePublicKey.ToParameters());

        public TokenVerifyResult Verify(string token, string machineHash)
        {
            string[] parts = token?.Split('.');
            if (parts == null || parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            {
                return new TokenVerifyResult(TokenVerifyStatus.Malformed, null);
            }

            string head = parts[0];
            string signaturePart = parts[1];

            byte[] signature;
            try
            {
                signature = LicensePublicKey.Decode(signaturePart);
            }
            catch (FormatException)
            {
                return new TokenVerifyResult(TokenVerifyStatus.Malformed, null);
            }

            using var rsa = new RSACryptoServiceProvider();
            rsa.ImportParameters(_publicKey);
            bool signatureValid = rsa.VerifyData(
                Encoding.ASCII.GetBytes(head),
                signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            if (!signatureValid)
            {
                return new TokenVerifyResult(TokenVerifyStatus.BadSignature, null);
            }

            LicenseToken payload;
            try
            {
                byte[] jsonBytes = LicensePublicKey.Decode(head);
                string json = Encoding.UTF8.GetString(jsonBytes);
                payload = JsonUtility.FromJson<LicenseToken>(json);
            }
            catch (Exception)
            {
                return new TokenVerifyResult(TokenVerifyStatus.Malformed, null);
            }

            if (payload == null || payload.sub == null)
            {
                return new TokenVerifyResult(TokenVerifyStatus.Malformed, null);
            }

            if (payload.sub != machineHash)
            {
                return new TokenVerifyResult(TokenVerifyStatus.WrongMachine, payload);
            }

            if (payload.exp <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                return new TokenVerifyResult(TokenVerifyStatus.Expired, payload);
            }

            return new TokenVerifyResult(TokenVerifyStatus.Valid, payload);
        }
    }
}
