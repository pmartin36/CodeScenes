using System;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using SceneBuilder.Editor.Licensing;
using UnityEngine;

// Pins offline RSA-2048/PKCS#1v1.5/SHA-256 token verification on the Unity Mono profile:
// a genuinely-signed backend-format token verifies, and each single-fact violation
// (tampered signature, wrong machine, expired, malformed) fails with its specific status.
public class LicenseTokenVerificationTests
{
    private const string MachineHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherMachineHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private RSA _rsa;
    private TokenVerifier _verifier;

    [SetUp]
    public void SetUp()
    {
        _rsa = RSA.Create(2048);
        _verifier = new TokenVerifier(_rsa.ExportParameters(false));
    }

    [TearDown]
    public void TearDown()
    {
        _rsa?.Dispose();
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private string SignToken(string sub, long iat, long exp, string kind = "license", string lic = "LIC-1")
    {
        var payload = new LicenseToken { v = 1, kind = kind, sub = sub, lic = lic, iat = iat, exp = exp };
        string json = JsonUtility.ToJson(payload);
        string head = Base64UrlEncode(Encoding.UTF8.GetBytes(json));
        byte[] sig = _rsa.SignData(Encoding.ASCII.GetBytes(head), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return head + "." + Base64UrlEncode(sig);
    }

    private static long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [Test]
    public void Verify_GenuinelySignedFutureExp_MatchingSub_ReturnsValid()
    {
        string token = SignToken(MachineHash, UnixNow() - 60, UnixNow() + 3600);

        var result = _verifier.Verify(token, MachineHash);

        Assert.AreEqual(TokenVerifyStatus.Valid, result.Status,
            "A genuinely-signed, unexpired token whose sub matches this machine must verify offline on the Mono profile.");
    }

    [Test]
    public void Verify_ValidToken_DecodesPayloadKind()
    {
        string token = SignToken(MachineHash, UnixNow() - 60, UnixNow() + 3600, kind: "trial", lic: null);

        var result = _verifier.Verify(token, MachineHash);

        Assert.AreEqual("trial", result.Token.kind, "The decoded payload must round-trip the signed kind.");
    }

    [Test]
    public void Verify_TamperedSignatureByte_ReturnsBadSignature()
    {
        string token = SignToken(MachineHash, UnixNow() - 60, UnixNow() + 3600);
        string[] parts = token.Split('.');
        char[] sigChars = parts[1].ToCharArray();
        sigChars[0] = sigChars[0] == 'A' ? 'B' : 'A';
        string tampered = parts[0] + "." + new string(sigChars);

        var result = _verifier.Verify(tampered, MachineHash);

        Assert.AreEqual(TokenVerifyStatus.BadSignature, result.Status,
            "Altering a single byte of the signature must fail signature verification, not silently pass.");
    }

    [Test]
    public void Verify_GenuinelySignedWrongMachine_ReturnsWrongMachine()
    {
        string token = SignToken(OtherMachineHash, UnixNow() - 60, UnixNow() + 3600);

        var result = _verifier.Verify(token, MachineHash);

        Assert.AreEqual(TokenVerifyStatus.WrongMachine, result.Status,
            "A validly-signed token issued to a different machine's hash must not verify on this machine.");
    }

    [Test]
    public void Verify_GenuinelySignedExpiredToken_ReturnsExpired()
    {
        string token = SignToken(MachineHash, UnixNow() - 7200, UnixNow() - 3600);

        var result = _verifier.Verify(token, MachineHash);

        Assert.AreEqual(TokenVerifyStatus.Expired, result.Status,
            "A validly-signed token whose exp is in the past must not verify.");
    }

    [Test]
    public void Verify_MalformedGarbage_ReturnsMalformedWithoutThrowing()
    {
        TokenVerifyResult result = default;
        Assert.DoesNotThrow(() => result = _verifier.Verify("garbage", MachineHash));
        Assert.AreEqual(TokenVerifyStatus.Malformed, result.Status,
            "A string with no valid two-segment structure must return Malformed, never throw.");
    }

    [Test]
    public void Verify_MalformedThreeSegments_ReturnsMalformedWithoutThrowing()
    {
        TokenVerifyResult result = default;
        Assert.DoesNotThrow(() => result = _verifier.Verify("a.b.c", MachineHash));
        Assert.AreEqual(TokenVerifyStatus.Malformed, result.Status,
            "A three-segment string is not the head.signature shape and must return Malformed, never throw.");
    }

    [Test]
    public void EmbeddedPublicKey_ParsesTo2048BitModulusAndStandardExponent()
    {
        RSAParameters parameters = LicensePublicKey.ToParameters();

        Assert.AreEqual(256, parameters.Modulus.Length,
            "An RSA-2048 modulus must decode to exactly 256 bytes.");
        Assert.AreEqual(new byte[] { 0x01, 0x00, 0x01 }, parameters.Exponent,
            "The embedded exponent (AQAB) must decode to the standard 65537 exponent bytes.");
    }

    [Test]
    public void Production_ConstructsWithoutThrowing()
    {
        Assert.DoesNotThrow(() => { var _ = TokenVerifier.Production; },
            "The production verifier must build from the embedded key without throwing.");
    }

    [Test]
    public void MachineIdentity_Hash_Is64LowercaseHexChars_AndStableAcrossReads()
    {
        string first = MachineIdentity.Hash;
        string second = MachineIdentity.Hash;

        Assert.AreEqual(64, first.Length, "MachineIdentity.Hash must be a 64-char hex-SHA-256 string.");
        Assert.AreEqual(first.ToLowerInvariant(), first, "MachineIdentity.Hash must be lowercase hex.");
        Assert.AreEqual(first, second, "MachineIdentity.Hash must be stable across reads within a session.");
    }
}
