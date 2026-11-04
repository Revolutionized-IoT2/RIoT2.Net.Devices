using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RIoT2.Net.Devices.Services.Eufy;

namespace RIoT2.Net.Devices.Tests.Eufy.P2P;

/// <summary>
/// The station side of the eufy P2P crypto, built on .NET primitives only (never on the code under test), so the
/// fixtures check the port rather than echo it. All key material is synthetic.
/// </summary>
internal static class TestCrypto
{
    public static readonly byte[] Aad = "eufy security"u8.ToArray();

    /// <summary>The eufy ECIES KDF: HMAC-SHA256 feedback under the label "ECIES".</summary>
    public static byte[] Kdf(byte[] shared, int length)
    {
        var label = "ECIES"u8.ToArray();
        var output = new List<byte>();
        var t = label;
        while (output.Count < length)
        {
            t = HMACSHA256.HashData(shared, t);
            output.AddRange(HMACSHA256.HashData(shared, (byte[])[.. t, .. label]));
        }
        return [.. output.Take(length)];
    }

    public static ECDiffieHellman NewP256() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    public static string PrivateKeyHex(ECDiffieHellman key) => Convert.ToHexStringLower(key.ExportParameters(true).D!);

    public static byte[] Uncompressed(ECDiffieHellman key)
    {
        var q = key.ExportParameters(false).Q;
        return [0x04, .. q.X!, .. q.Y!];
    }

    public static byte[] Compressed(ECDiffieHellman key)
    {
        var q = key.ExportParameters(false).Q;
        return [(byte)((q.Y![^1] & 1) == 0 ? 0x02 : 0x03), .. q.X!];
    }

    /// <summary>
    /// A <c>CMD_GATEWAYINFO</c> ECIES envelope: <c>ephPub(33) ‖ iv(16) ‖ AES-128-CBC(sessionKey ‖ 16×0x10) ‖ HMAC(32)</c>.
    /// </summary>
    public static byte[] GatewayEnvelope(byte[] sessionKey, ECDiffieHellman device)
    {
        using var ephemeral = NewP256();
        var kdf = Kdf(ephemeral.DeriveRawSecretAgreement(device.PublicKey), 48);
        var iv = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Key = kdf[..16];
        var ct = aes.EncryptCbc((byte[])[.. sessionKey, .. Enumerable.Repeat((byte)0x10, 16)], iv, PaddingMode.None);
        var mac = HMACSHA256.HashData(kdf.AsSpan(16, 32), (byte[])[.. iv, .. ct]);
        return [.. Compressed(ephemeral), .. iv, .. ct, .. mac];
    }

    /// <summary>The level-1 key: the serial's last seven characters and nine of the DID from its first dash.</summary>
    public static byte[] Level1Key(string sn, string did) => Encoding.ASCII.GetBytes(string.Concat(sn.AsSpan(sn.Length - 7), did.AsSpan(did.IndexOf('-'), 9)));

    /// <summary>Zero-pads to whole 16-byte blocks, at least one (eufy's padding, not PKCS#7).</summary>
    public static byte[] ZeroPad(byte[] data)
    {
        var size = data.Length < 16 ? 16 : (data.Length + 15) / 16 * 16;
        var output = new byte[size];
        data.CopyTo(output, 0);
        return output;
    }

    public static byte[] EcbEncrypt(byte[] key, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptEcb(data, PaddingMode.None);
    }

    public static byte[] EcbDecrypt(byte[] key, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptEcb(data, PaddingMode.None);
    }

    /// <summary>A level-2 body: <c>tag(16) ‖ nonce(12) ‖ [sub-header] ‖ ct</c> under AES-256-GCM, AAD "eufy security".</summary>
    public static byte[] GcmSeal(byte[] key, byte[] plaintext, byte[]? subHeader = null, byte[]? nonce = null)
    {
        nonce ??= RandomNumberGenerator.GetBytes(12);
        var ct = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(key, 16)) gcm.Encrypt(nonce, plaintext, ct, tag, Aad);
        return [.. tag, .. nonce, .. subHeader ?? [], .. ct];
    }

    public static byte[]? GcmOpen(byte[] key, byte[] body, int ctOffset)
    {
        if (body.Length < ctOffset) return null;
        try
        {
            using var gcm = new AesGcm(key, 16);
            var plain = new byte[body.Length - ctOffset];
            gcm.Decrypt(body.AsSpan(16, 12), body.AsSpan(ctOffset), body.AsSpan(0, 16), plain, Aad);
            return plain;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public static byte[] Int32Le(int value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        return b;
    }

    public static string TrimZeros(byte[] plain) => Encoding.UTF8.GetString(plain).TrimEnd('\0');
}

/// <summary>Synthetic cloud records for the P2P specs; no real serial, DID or account.</summary>
internal static class P2PDevices
{
    public const string AdminId = "admin-0001";
    public const string AccountName = "someone+tag";

    public static EufyDeviceRecord Record(string sn, string model, string? stationSn = null, string? did = null, int? deviceType = null, int? channel = null, string? adminId = AdminId, string? parentSn = null)
    {
        var raw = new JsonObject { ["device_sn"] = sn, ["device_model"] = model };
        if (did is not null) raw["p2p_did"] = did;
        if (deviceType is not null) raw["device_type"] = deviceType;
        if (channel is not null) raw["device_channel"] = channel;
        if (stationSn is not null) raw["station_sn"] = stationSn;
        if (parentSn is not null) raw["parent_sn"] = parentSn;
        if (adminId is not null) raw["member"] = new JsonObject { ["admin_user_id"] = adminId };
        using var doc = JsonDocument.Parse(raw.ToJsonString());
        return new EufyDeviceRecord
        {
            Serial = sn,
            Name = sn,
            Model = model,
            StationSerial = stationSn ?? sn,
            Channel = channel,
            P2pDid = did,
            DeviceType = deviceType,
            Raw = doc.RootElement.Clone(),
        };
    }
}
