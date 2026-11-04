// Ported from eufy-sdk src/core/__tests__/crypto.spec.ts, src/transport/http/__tests__/phone-model.spec.ts @ 5f92bea5 (Apache-2.0), modified for RIoT2.

using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Net.Devices.Services.Eufy.Cloud;

namespace RIoT2.Net.Devices.Tests.Eufy.Cloud;

[TestClass]
public class EufyCryptoTests
{
    // vector.json: a synthetic, self-consistent fixture guarding the algo_ecdh formula.
    private const string SharedKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Ts = "1700000000";
    private const string Once = "00112233445566778899aabbccddeeff";
    private const string BodyB64 = "Dw4NDAsKCQgHBgUEAwIBAON7nAdtN7DzIooLLHfGyy/gYJMK1jWa/qbASxRBJcl6Hw62mx/9J0Bw1nLTUhsURA==";
    private const string BodyPlaintext = "{\"example\":\"synthetic-test-vector\",\"n\":42}";
    private const string Signature = "9860cd44b02b7b68e7feb1ea128e87c8304982b3de1250a5b49c0fda96f0ba10";

    [TestMethod]
    public void AesKeyIsTheFirstHalfOfTheSharedKey()
    {
        var key = EufyCrypto.AesKey(SharedKey);
        Assert.HasCount(16, key);
        Assert.AreEqual(SharedKey[..32], Convert.ToHexStringLower(key));
    }

    [TestMethod]
    public void SignKeyIsTheFirst32HexCharsAsUtf8()
    {
        var key = EufyCrypto.SignKey(SharedKey);
        Assert.HasCount(32, key);
        Assert.AreEqual(SharedKey[..32], Encoding.UTF8.GetString(key));
    }

    [TestMethod]
    public void DecryptsTheFixtureBody()
    {
        Assert.AreEqual(BodyPlaintext, Encoding.UTF8.GetString(EufyCrypto.DecryptBody(BodyB64, SharedKey)));
    }

    [TestMethod]
    public void EncryptsTheFixtureBodyByteExactWithItsIv()
    {
        var iv = Convert.FromBase64String(BodyB64)[..16];
        Assert.AreEqual(BodyB64, EufyCrypto.EncryptBody(BodyPlaintext, SharedKey, iv));
    }

    [TestMethod]
    public void ReproducesTheFixtureSignature()
    {
        Assert.AreEqual(Signature, EufyCrypto.SignRequest(SharedKey, Ts, Once, BodyB64));
    }

    [TestMethod]
    public void SignsAnEmptyBodyAsTsPlusOnce()
    {
        var empty = EufyCrypto.SignRequest(SharedKey, Ts, Once);
        Assert.AreEqual(64, empty.Length);
        Assert.AreNotEqual(Signature, empty);
        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(SharedKey[..32]), Encoding.UTF8.GetBytes($"{Ts}+{Once}")));
        Assert.AreEqual(expected, empty);
    }

    [TestMethod]
    public void EncryptBodyRoundTripsWithARandomIv()
    {
        const string plaintext = "{\"hello\":\"world\",\"n\":42}";
        var encrypted = EufyCrypto.EncryptBody(plaintext, SharedKey);
        Assert.AreEqual(plaintext, Encoding.UTF8.GetString(EufyCrypto.DecryptBody(encrypted, SharedKey)));
        Assert.AreNotEqual(encrypted, EufyCrypto.EncryptBody(plaintext, SharedKey));
    }

    [TestMethod]
    public void GtokenIsMd5OfTheUserId()
    {
        Assert.AreEqual("900150983cd24fb0d6963f7d28e17f72", EufyCrypto.Gtoken("abc"));
    }

    [TestMethod]
    public void KeyExchangeRoundTripsWithAGeneratedServerKey()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        using var prep = EufyCrypto.PrepareKeyExchange(now);
        var localKey = Convert.FromHexString(EufyCrypto.EufyMegaLocalKeyHex);

        // The headers are signed with the localKey hex as UTF-8 over ts+once+encrypted key.
        Assert.AreEqual("algo_ecdh", prep.Headers["x-encryption-info"]);
        Assert.AreEqual("replay", prep.Headers["x-replay-info"]);
        Assert.AreEqual("1700000000", prep.Headers["x-request-ts"]);
        Assert.AreEqual(prep.KeyIdent, prep.Headers["x-key-ident"]);
        var sig = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(EufyCrypto.EufyMegaLocalKeyHex),
            Encoding.UTF8.GetBytes($"{prep.Headers["x-request-ts"]}+{prep.Headers["x-request-once"]}+{prep.EncryptedClientPublicKey}")));
        Assert.AreEqual(sig, prep.Headers["x-signature"]);

        // Server side: unwrap the client key, answer with a wrapped key of its own.
        var clientHex = Encoding.UTF8.GetString(EufyCrypto.UnwrapCbc(localKey, Convert.FromBase64String(prep.EncryptedClientPublicKey)));
        Assert.AreEqual(prep.ClientPublicKeyHex, clientHex);
        Assert.AreEqual(130, clientHex.Length);
        Assert.StartsWith("04", clientHex);
        using var server = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var serverShared = EufyCrypto.DeriveSecret(server, clientHex);
        var wrapped = Convert.ToBase64String(EufyCrypto.WrapCbc(localKey, Encoding.UTF8.GetBytes(EufyCrypto.PublicKeyHex(server))));

        var entry = EufyCrypto.FinishKeyExchange(prep, wrapped, now);

        Assert.AreEqual(Convert.ToHexStringLower(serverShared)[..32], entry.ShareKey);
        Assert.AreEqual(prep.KeyIdent, entry.KeyIdent);
        Assert.HasCount(32, serverShared);
    }

    [TestMethod]
    public void EcdhSecretIsTheRawXCoordinate()
    {
        using var a = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var b = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ab = EufyCrypto.DeriveSecret(a, EufyCrypto.PublicKeyHex(b));
        var ba = EufyCrypto.DeriveSecret(b, EufyCrypto.PublicKeyHex(a));
        CollectionAssert.AreEqual(ab, ba);
        Assert.HasCount(32, ab);
    }

    [TestMethod]
    public void LoginPasswordDecryptsWithTheCounterpartKey()
    {
        using var server = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var (clientPublicKeyHex, encrypted) = EufyCrypto.EncryptLoginPassword("synthetic p@ssword ✓", EufyCrypto.PublicKeyHex(server));

        var shared = EufyCrypto.DeriveSecret(server, clientPublicKeyHex);
        using var aes = Aes.Create();
        aes.Key = shared;
        var plaintext = aes.DecryptCbc(Convert.FromBase64String(encrypted), shared.AsSpan(0, 16), PaddingMode.PKCS7);

        Assert.AreEqual("synthetic p@ssword ✓", Encoding.UTF8.GetString(plaintext));
    }

    [TestMethod]
    public void LoginPasswordUsesTheStaticServerKeyByDefault()
    {
        var (clientPublicKeyHex, encrypted) = EufyCrypto.EncryptLoginPassword("synthetic");
        Assert.AreEqual(130, clientPublicKeyHex.Length);
        Assert.HasCount(16, Convert.FromBase64String(encrypted));
        using var serverKey = EufyCrypto.ImportPublicKey(EufyCrypto.ServerStaticPublicKeyHex);
    }

    [TestMethod]
    public void StripsAnEchoedToken()
    {
        var text = EufyCrypto.WithoutTokenEcho("token not exist, token = 0123456789abcdef0123");
        Assert.DoesNotContain("0123456789abcdef0123", text);
        Assert.Contains("<redacted>", text);
    }

    [TestMethod]
    public void PhoneModelMatchesTheUpstreamGenerator()
    {
        // Reference values computed with eufy-sdk's phone-model.ts.
        Assert.AreEqual("Redmi Note 12", EufyPhoneModel.RandomPhoneModel("udid-abc"));
        Assert.AreEqual("OnePlus 10R", EufyPhoneModel.RandomPhoneModel("seed-b"));
        Assert.AreEqual("V2050", EufyPhoneModel.RandomPhoneModel("1"));
        Assert.AreEqual("SM-A546B", EufyPhoneModel.RandomPhoneModel("0123456789abcdef"));
        Assert.AreEqual("Dalvik/2.1.0 (Linux; U; Android 14; Redmi Note 12 Build/UP1A.263122.313)", EufyPhoneModel.RandomUserAgent("udid-abc"));
        Assert.AreEqual("Dalvik/2.1.0 (Linux; U; Android 14; SM-G998B Build/UP1A.263122.313)", EufyPhoneModel.RandomUserAgent("udid-abc", "SM-G998B"));
    }

    [TestMethod]
    public void PhoneModelVariesBySeedAndWorksUnseeded()
    {
        var models = "abcdefghij".Select(c => EufyPhoneModel.RandomPhoneModel($"seed-{c}")).ToHashSet();
        Assert.IsGreaterThan(1, models.Count);
        Assert.IsGreaterThan(2, EufyPhoneModel.RandomPhoneModel().Length);
        Assert.MatchesRegex(@"^Dalvik/2\.1\.0 \(Linux; U; Android \d+; SM-G998B Build/[A-Z0-9.]+\)$", EufyPhoneModel.RandomUserAgent(null, "SM-G998B"));
    }
}
