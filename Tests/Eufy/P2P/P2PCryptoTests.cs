using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Net.Devices.Services.Eufy.P2P;

namespace RIoT2.Net.Devices.Tests.Eufy.P2P;

/// <summary>
/// The encryption levels: level 1 (AES-128-ECB, derived key), level 2 over ECC (ECIES → AES-256-GCM, sign codes 2/8)
/// and over RSA (PKCS#1 → AES-128-ECB). Port of eufy-sdk <c>p2p_level2.spec.ts</c>, <c>ecies-wrap.spec.ts</c> and
/// <c>p2p-level2-rsa.spec.ts</c>; the station side is built on .NET primitives.
/// </summary>
[TestClass]
public class P2PCryptoTests
{
    private static readonly byte[] Key = Enumerable.Repeat((byte)7, 32).ToArray();
    private static readonly byte[] Nonce = "0123456789ab"u8.ToArray();

    private static P2PSession NewSession() => new(new P2PSessionConfig { StationSn = "T8000X0000000000", P2pDid = "T8000-000000-00000", Transport = new FakeP2PNetwork() });

    [TestMethod]
    public void P256MatchesTheFrameworkEcdh()
    {
        using var a = TestCrypto.NewP256();
        using var b = TestCrypto.NewP256();
        var aPrivate = a.ExportParameters(true).D!;
        CollectionAssert.AreEqual(TestCrypto.Compressed(a), P256.CompressedPublicKey(aPrivate));
        CollectionAssert.AreEqual(TestCrypto.Uncompressed(a), P256.UncompressedPublicKey(aPrivate));
        var expected = a.DeriveRawSecretAgreement(b.PublicKey);
        CollectionAssert.AreEqual(expected, P256.SharedSecret(aPrivate, TestCrypto.Uncompressed(b)));
        CollectionAssert.AreEqual(expected, P256.SharedSecret(aPrivate, TestCrypto.Compressed(b)));
        Assert.ThrowsExactly<ArgumentException>(() => P256.SharedSecret(aPrivate, [0x05, .. new byte[32]]));
    }

    [TestMethod]
    public void RecoversTheSessionKeyFromAGatewayInfoEnvelope()
    {
        using var device = TestCrypto.NewP256();
        var sessionKey = Enumerable.Repeat((byte)0x5a, 32).ToArray();
        byte[] gwPayload = [97, 0, 0, 0, .. TestCrypto.GatewayEnvelope(sessionKey, device)];
        Assert.AreEqual(97, P2PCodec.GatewayInfoCipherId(gwPayload));
        CollectionAssert.AreEqual(sessionKey, P2PCodec.DeriveLevel2KeyFromGatewayInfo(gwPayload, TestCrypto.PrivateKeyHex(device)));

        using var other = TestCrypto.NewP256();
        var wrong = P2PCodec.DeriveLevel2KeyFromGatewayInfo(gwPayload, TestCrypto.PrivateKeyHex(other));
        Assert.IsTrue(wrong is null || !wrong.SequenceEqual(sessionKey));
        Assert.IsNull(P2PCodec.DeriveLevel2KeyFromGatewayInfo(new byte[10], new string('0', 63) + "1"));
    }

    [TestMethod]
    public void EciesWrapSealsWhatTheStationSideOpens()
    {
        using var peer = TestCrypto.NewP256();
        using var ephemeral = TestCrypto.NewP256();
        var iv = Enumerable.Repeat((byte)0xcd, 16).ToArray();
        var plaintext = Encoding.ASCII.GetBytes(new string('B', 32));
        var envelope = P2PCodec.EciesWrap(plaintext, TestCrypto.Uncompressed(peer), ephemeral.ExportParameters(true).D, iv);

        Assert.HasCount(33 + 16 + 48 + 32, envelope);
        CollectionAssert.AreEqual(TestCrypto.Compressed(ephemeral), envelope[..33]);
        CollectionAssert.AreEqual(iv, envelope[33..49]);
        var kdf = TestCrypto.Kdf(peer.DeriveRawSecretAgreement(ephemeral.PublicKey), 48);
        CollectionAssert.AreEqual(HMACSHA256.HashData(kdf.AsSpan(16, 32), envelope.AsSpan(33, 64)), envelope[97..]);
        using var aes = Aes.Create();
        aes.Key = kdf[..16];
        CollectionAssert.AreEqual(plaintext, aes.DecryptCbc(envelope[49..97], iv, PaddingMode.PKCS7));

        CollectionAssert.AreEqual(envelope, P2PCodec.EciesWrap(plaintext, TestCrypto.Uncompressed(peer), ephemeral.ExportParameters(true).D, iv));
        CollectionAssert.AreNotEqual(envelope, P2PCodec.EciesWrap(plaintext, TestCrypto.Uncompressed(peer)));
    }

    [TestMethod]
    public void EciesUnwrapVerifiesTheTagAndStripsPkcs7()
    {
        using var peer = TestCrypto.NewP256();
        var priv = TestCrypto.PrivateKeyHex(peer);
        var plaintext = Encoding.ASCII.GetBytes(new string('A', 32));
        var envelope = P2PCodec.EciesWrap(plaintext, TestCrypto.Compressed(peer));
        CollectionAssert.AreEqual(plaintext, P2PCodec.EciesUnwrap(envelope, priv, verifyHmac: true, pkcs7: true));

        var tampered = envelope.ToArray();
        tampered[60] ^= 0x01;
        Assert.IsNull(P2PCodec.EciesUnwrap(tampered, priv, verifyHmac: true, pkcs7: true));
        Assert.IsNull(P2PCodec.EciesUnwrap(envelope.AsSpan(0, 100), priv, verifyHmac: true));
    }

    private static byte[] SealLevel2(byte[] plaintext, int signCode) =>
        TestCrypto.GcmSeal(Key, plaintext, signCode == 8 ? [0x05, 0x03, 0x02, 0x01] : null, Nonce);

    [TestMethod]
    public void OpensSignCode2AtOffset28AndSignCode8AfterItsSubHeader()
    {
        var session = NewSession();
        session.SetLevel2Key(Key);
        var media = Encoding.UTF8.GetBytes("{\"cmd\":6445,\"payload\":{\"result\":5}}");
        CollectionAssert.AreEqual(media, session.DecryptLevel2(SealLevel2(media, 2), 2));
        var command = Encoding.UTF8.GetBytes("{\"account_id\":\"x\",\"cmd\":1306,\"mChannel\":255}");
        CollectionAssert.AreEqual(command, session.DecryptLevel2(SealLevel2(command, 8), 8));

        var result = SealLevel2([0, 0, 0, 0], 2);
        Assert.HasCount(32, result);
        CollectionAssert.AreEqual(new byte[4], session.DecryptLevel2(result, 2));
    }

    [TestMethod]
    public void AWrongKeyFailsClosed()
    {
        var session = NewSession();
        session.SetLevel2Key(Enumerable.Repeat((byte)9, 32).ToArray());
        Assert.IsNull(session.DecryptLevel2(SealLevel2("secret"u8.ToArray(), 2), 2));
        Assert.IsNull(NewSession().DecryptLevel2(SealLevel2("secret"u8.ToArray(), 2), 2));
    }

    [TestMethod]
    public void TheSignCode8SubHeaderCountsFrom00030201()
    {
        var session = NewSession();
        session.SetLevel2Key(Key);
        var headers = Enumerable.Range(0, 257).Select(_ => session.EncryptLevel2("{}"u8.ToArray())![28..32]).ToList();
        Assert.AreEqual("00030201", Convert.ToHexString(headers[0]));
        Assert.AreEqual("FF030201", Convert.ToHexString(headers[255]));
        Assert.AreEqual("00040201", Convert.ToHexString(headers[256]));

        var sealedBody = session.EncryptLevel2("{\"a\":1}"u8.ToArray())!;
        Assert.AreEqual("{\"a\":1}", Encoding.UTF8.GetString(TestCrypto.GcmOpen(Key, sealedBody, 32)!));
        Assert.IsNull(NewSession().EncryptLevel2([1]));
    }

    [TestMethod]
    public void SetLevel2KeyRejectsAKeyThatIsNot32Bytes()
    {
        Assert.ThrowsExactly<ArgumentException>(() => NewSession().SetLevel2Key(new byte[16]));
        var session = NewSession();
        session.SetLevel2Key(Key);
        Assert.IsTrue(session.HasLevel2Key);
    }

    private static readonly byte[] RsaSessionKey = Convert.FromHexString("00112233445566778899aabbccddeeff");

    private static byte[] RsaGatewayInfo(byte[] ciphertext) => [10, 0, 0, 0, .. ciphertext, 0x00, .. Enumerable.Repeat((byte)0x5a, 11)];

    private static byte[] RawRsa(RSA rsa, byte[] block)
    {
        var p = rsa.ExportParameters(false);
        var n = new BigInteger(p.Modulus, isUnsigned: true, isBigEndian: true);
        var e = new BigInteger(p.Exponent, isUnsigned: true, isBigEndian: true);
        var c = BigInteger.ModPow(new BigInteger(block, isUnsigned: true, isBigEndian: true), e, n).ToByteArray(isUnsigned: true, isBigEndian: true);
        return [.. new byte[p.Modulus!.Length - c.Length], .. c];
    }

    [TestMethod]
    public void RecoversTheRsaLevel2KeyAndFailsClosedOtherwise()
    {
        using var rsa = RSA.Create(1024);
        var pkcs8 = Convert.ToBase64String(rsa.ExportPkcs8PrivateKey());
        var wrapped = rsa.Encrypt(RsaSessionKey, RSAEncryptionPadding.Pkcs1);
        CollectionAssert.AreEqual(RsaSessionKey, P2PCodec.DeriveLevel2KeyFromRsaGatewayInfo(RsaGatewayInfo(wrapped), pkcs8));

        Assert.IsNull(P2PCodec.DeriveLevel2KeyFromRsaGatewayInfo(RsaGatewayInfo(wrapped).AsSpan(0, 100), pkcs8));
        Assert.IsNull(P2PCodec.DeriveLevel2KeyFromRsaGatewayInfo(RsaGatewayInfo(wrapped), "bm90IGEga2V5"));
        Assert.IsNull(P2PCodec.DeriveLevel2KeyFromRsaGatewayInfo(RsaGatewayInfo(wrapped), ""));
        using var other = RSA.Create(1024);
        Assert.IsNull(P2PCodec.DeriveLevel2KeyFromRsaGatewayInfo(RsaGatewayInfo(wrapped), Convert.ToBase64String(other.ExportPkcs8PrivateKey())));

        byte[] signaturePadded = [0x00, 0x01, .. Enumerable.Repeat((byte)0xff, 109), 0x00, .. RsaSessionKey];
        Assert.IsNull(P2PCodec.DeriveLevel2KeyFromRsaGatewayInfo(RsaGatewayInfo(RawRsa(rsa, signaturePadded)), pkcs8));
        byte[] shortPad = [0x00, 0x02, .. Enumerable.Repeat((byte)0x11, 7), 0x00, .. Enumerable.Repeat((byte)0x22, 118)];
        Assert.IsNull(P2PCodec.DeriveLevel2KeyFromRsaGatewayInfo(RsaGatewayInfo(RawRsa(rsa, shortPad)), pkcs8));
        Assert.IsNull(P2PCodec.DeriveLevel2KeyFromRsaGatewayInfo(RsaGatewayInfo(rsa.Encrypt(new byte[32], RSAEncryptionPadding.Pkcs1)), pkcs8));
    }

    [TestMethod]
    public void OpensAnE2eVideoFrameWithTheCipherKey()
    {
        using var camera = TestCrypto.NewP256();
        var mediaKey = Enumerable.Range(0, 32).Select(i => (byte)(i * 3)).ToArray();
        var h264 = new byte[] { 0, 0, 0, 1, 0x67, 0x42, 0, 0, 0, 1, 0x65, 0x88 };
        var envelope = P2PCodec.EciesWrap(mediaKey, TestCrypto.Uncompressed(camera));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ct = new byte[h264.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(mediaKey, 16)) gcm.Encrypt(nonce, h264, ct, tag, TestCrypto.Aad);
        var frame = new byte[0xb3 + ct.Length];
        frame[4] = 1;
        envelope.CopyTo(frame, 0x16);
        tag.CopyTo(frame, 0x97);
        nonce.CopyTo(frame, 0xa7);
        ct.CopyTo(frame, 0xb3);

        var decoder = new VideoFrameDecoder(camera.ExportParameters(true).D!);
        var decoded = decoder.DecodeFrame(frame);
        Assert.IsNotNull(decoded);
        Assert.IsTrue(decoded.Value.Keyframe);
        CollectionAssert.AreEqual(h264, decoded.Value.Data);
        CollectionAssert.AreEqual(mediaKey, decoder.CurrentMediaKey);
    }
}
