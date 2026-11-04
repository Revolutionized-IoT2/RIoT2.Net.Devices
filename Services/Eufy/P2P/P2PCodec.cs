// Ported from eufy-sdk src/transport/p2p/codec.ts @ 5f92bea5 (Apache-2.0), modified for RIoT2.

using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace RIoT2.Net.Devices.Services.Eufy.P2P
{
    /// <summary>A UDP endpoint as the PPCS records carry it (IPv4 host, port).</summary>
    internal readonly record struct P2PAddress(string Host, int Port)
    {
        public override string ToString() => $"{Host}:{Port}";
    }

    /// <summary>The 16-byte header of a P2P data frame (after the 4-byte "XZYH" magic it starts with).</summary>
    internal readonly record struct P2PDataFrameHeader(int CommandId, uint BytesToRead, int Channel, int SignCode, int Type);

    /// <summary>The cipher material <c>get_ciphers</c> answers for a <c>cipher_id</c>.</summary>
    internal sealed record P2PCipherKeys(string? Ecc, string? Rsa);

    /// <summary>
    /// UDP message types: every packet is <c>[msgType:2][payloadLen:2 BE][payload]</c>, the first type byte always 0xf1.
    /// </summary>
    internal static class P2PMessageType
    {
        // Requests (client → device / lookup server / relay).
        public const ushort LocalLookup = 0xf130;
        public const ushort LookupWithKey = 0xf126;
        /// <summary><c>ListReq_DSK</c>: the variant-2 lookup body, answered with <see cref="ListReqAck"/>.</summary>
        public const ushort LookupWithKey2 = 0xf16a;
        public const ushort RelayHello = 0xf170;
        public const ushort RelayPort = 0xf172;
        public const ushort RelayRequest = 0xf180;
        public const ushort RelayPacket = 0xf183;
        public const ushort CheckCam = 0xf141;
        public const ushort CheckCam2 = 0xf142;
        public const ushort Ping = 0xf1e0;
        public const ushort Pong = 0xf1e1;
        public const ushort Data = 0xf1d0;
        public const ushort Ack = 0xf1d1;
        public const ushort End = 0xf1f0;

        // Responses (device / lookup server / relay → client).
        public const ushort LocalLookupResp = 0xf141;
        public const ushort LookupAddr = 0xf140;
        public const ushort ListReqAck = 0xf169;
        public const ushort RelayHelloAck = 0xf171;
        public const ushort RelayPortAck = 0xf173;
        public const ushort RelayRequestAck = 0xf181;
        public const ushort RelayTo = 0xf182;
        public const ushort CamId = 0xf142;
        public const ushort P2PRequestAck = 0xf121;
        public const ushort CamAddr = 0xf143;
        public const ushort RelayReady = 0xf184;
    }

    /// <summary>P2P data-channel types (the second byte of the data-type header <c>[0xd1, type]</c>).</summary>
    internal static class P2PDataType
    {
        public const byte Data = 0;
        public const byte Video = 1;
        public const byte Control = 2;
        public const byte Binary = 3;
    }

    /// <summary>
    /// eufy P2P low-level primitives (the ThroughTek PPCS on-wire protocol): deterministic, unit-tested. The
    /// realtime UDP session that drives them is <see cref="P2PSession"/>.
    /// </summary>
    internal static class P2PCodec
    {
        /// <summary>Magic word at the start of every P2P data frame.</summary>
        public const string MagicWord = "XZYH";
        public static readonly byte[] MagicBytes = "XZYH"u8.ToArray();

        /// <summary>The fixed-width 16-byte header of every P2P data frame (including "XZYH").</summary>
        public const int DataHeaderBytes = 16;

        public static byte[] DataTypeHeader(byte dataType) => [0xd1, dataType];

        /// <summary>Frames a UDP packet: <c>[msgType:2][payloadLen:2 BE][payload]</c>.</summary>
        public static byte[] FrameMessage(ushort msgType, ReadOnlySpan<byte> payload = default)
        {
            var packet = new byte[4 + payload.Length];
            BinaryPrimitives.WriteUInt16BigEndian(packet, msgType);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)payload.Length);
            payload.CopyTo(packet.AsSpan(4));
            return packet;
        }

        /// <summary>True if <paramref name="msg"/> begins with the given 2-byte message type.</summary>
        public static bool HasHeader(ReadOnlySpan<byte> msg, ushort type) =>
            msg.Length >= 2 && msg[0] == (byte)(type >> 8) && msg[1] == (byte)type;

        /// <summary>Pads a string into a fixed-width (multiple-of-chunk) zero-filled buffer.</summary>
        public static byte[] StringWithLength(string input, int chunkLength = 128)
        {
            var b = Encoding.UTF8.GetBytes(input);
            var size = b.Length < chunkLength ? chunkLength : (int)Math.Ceiling(b.Length / (double)chunkLength) * chunkLength;
            var output = new byte[size];
            b.CopyTo(output, 0);
            return output;
        }

        /// <summary><c>EUPRCAM-000000-XXXXX</c> → the 20-byte buffer used in lookup/check payloads.</summary>
        public static byte[] P2pDidToBuffer(string p2pDid)
        {
            var a = p2pDid.Split('-');
            var b1 = StringWithLength(a[0], 8);
            var b2 = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b2, a.Length > 1 && uint.TryParse(a[1], out var n) ? n : 0);
            var b3 = StringWithLength(a.Length > 2 ? a[2] : "", 8);
            var all = b1.Concat(b2).Concat(b3).ToArray();
            var output = new byte[20];
            Array.Copy(all, output, Math.Min(20, all.Length));
            return output;
        }

        private static readonly byte[] CloudIpLookupTable = Convert.FromHexString(
            "4959433db5bf6da347534f6165e371e9677f02030badb3892b2f35c16b8b959711e5a70deff1050783fb9d3bc5c713171d1f2529d3df");

        /// <summary>
        /// Decodes a station's <c>p2p_conn</c> / <c>app_conn</c> string into the eufy cloud lookup server addresses
        /// (port 32100): an XOR cipher with a fixed lookup table, seed 0x39.
        /// </summary>
        public static IReadOnlyList<P2PAddress> DecodeP2PCloudIPs(string data)
        {
            var encoded = data.Split(':')[0];
            var output = new byte[encoded.Length / 2];
            for (var i = 0; i < output.Length; i++)
            {
                var z = 0x39;
                for (var j = 0; j < i; j++) z ^= output[j];
                var x = data[i * 2 + 1] - 65;
                var y = (data[i * 2] - 65) * 0x10;
                output[i] = (byte)(z ^ CloudIpLookupTable[i % CloudIpLookupTable.Length] ^ (x + y));
            }
            var result = new List<P2PAddress>();
            foreach (var ip in Encoding.UTF8.GetString(output).Split(','))
                if (ip.Length > 0) result.Add(new P2PAddress(ip, 32100));
            return result;
        }

        /// <summary>JavaScript <c>String.prototype.substring</c> semantics (clamped, swapped).</summary>
        private static string JsSubstring(string s, int start, int end)
        {
            start = Math.Clamp(start, 0, s.Length);
            end = Math.Clamp(end, 0, s.Length);
            if (start > end) (start, end) = (end, start);
            return s[start..end];
        }

        /// <summary>
        /// The "Level 1" AES-128-ECB key for P2P command/control encryption, derivable from sn + p2p_did alone.
        /// HomeBase control-channel notifications use exactly this key.
        /// </summary>
        public static string P2pCommandEncryptionKey(string stationSn, string p2pDid)
        {
            var last7 = stationSn.Length > 7 ? stationSn[^7..] : stationSn;
            var dash = p2pDid.IndexOf('-', StringComparison.Ordinal);
            return last7 + JsSubstring(p2pDid, dash, dash + 9);
        }

        /// <summary>AES-128-ECB decrypt (no padding).</summary>
        public static byte[] DecryptP2PData(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key)
        {
            using var aes = Aes.Create();
            aes.Key = key.ToArray();
            return aes.DecryptEcb(data, PaddingMode.None);
        }

        /// <summary>AES-128-ECB encrypt (no padding); the inverse of <see cref="DecryptP2PData"/>.</summary>
        public static byte[] EncryptP2PData(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key)
        {
            using var aes = Aes.Create();
            aes.Key = key.ToArray();
            return aes.EncryptEcb(data, PaddingMode.None);
        }

        /// <summary>Zero-pads to a multiple of <paramref name="blockSize"/> (eufy P2P pads with 0x00, not PKCS7).</summary>
        public static byte[] PaddingP2PData(ReadOnlySpan<byte> data, int blockSize = 16)
        {
            var size = data.Length < blockSize ? blockSize : (int)Math.Ceiling(data.Length / (double)blockSize) * blockSize;
            var output = new byte[size];
            data.CopyTo(output);
            return output;
        }

        /// <summary>The <c>cipher_id</c> named in a decrypted <c>CMD_GATEWAYINFO</c> (1100) payload (uint16 LE @0).</summary>
        public static int GatewayInfoCipherId(ReadOnlySpan<byte> gwPayload) =>
            gwPayload.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(gwPayload) : 0;

        /// <summary>
        /// The eufy ECIES KDF: HMAC-SHA256 feedback chaining under the label "ECIES". Each 32-byte block is
        /// <c>HMAC(shared, t ‖ "ECIES")</c> where <c>t</c> starts at the label and becomes <c>HMAC(shared, t)</c> before every block.
        /// </summary>
        internal static byte[] EufyKdf(byte[] shared, int outLen)
        {
            var label = "ECIES"u8.ToArray();
            var output = new List<byte>(outLen + 32);
            var t = label;
            while (output.Count < outLen)
            {
                t = HMACSHA256.HashData(shared, t);
                output.AddRange(HMACSHA256.HashData(shared, t.Concat(label).ToArray()));
            }
            return output.Take(outLen).ToArray();
        }

        /// <summary>
        /// The shared ECIES unwrap used by the gateway and the video paths. <paramref name="envelope"/> =
        /// <c>ephemeralPub(33) ‖ iv(16) ‖ ct(48) [‖ HMAC(32)]</c>. Returns null on any parse / HMAC / cipher failure.
        /// </summary>
        public static byte[]? EciesUnwrap(ReadOnlySpan<byte> envelope, string eccPrivateKeyHex, bool verifyHmac = false, bool pkcs7 = false)
        {
            try
            {
                var minLen = 33 + 16 + 48 + (verifyHmac ? 32 : 0);
                if (envelope.Length < minLen) return null;
                var ephemeralPub = envelope[..33];
                var iv = envelope.Slice(33, 16);
                var ct = envelope.Slice(49, 48);
                var shared = P256.SharedSecret(PrivateKeyBytes(eccPrivateKeyHex), ephemeralPub);
                var kdf = EufyKdf(shared, 48);
                if (verifyHmac)
                {
                    var computed = HMACSHA256.HashData(kdf.AsSpan(16, 32), envelope.Slice(33, 64));
                    if (!CryptographicOperations.FixedTimeEquals(computed, envelope.Slice(97, 32))) return null;
                }
                using var aes = Aes.Create();
                aes.Key = kdf[..16];
                var full = aes.DecryptCbc(ct, iv, PaddingMode.None);
                if (pkcs7)
                {
                    var pad = full[^1];
                    if (pad >= 1 && pad <= 16 && full.AsSpan(full.Length - pad).IndexOfAnyExcept(pad) < 0)
                        full = full[..^pad];
                }
                return full;
            }
            catch (Exception e) when (e is ArgumentException or CryptographicException or FormatException)
            {
                return null;
            }
        }

        /// <summary>
        /// Seals <paramref name="plaintext"/> for a P-256 peer as <c>ephemeralPub(33) ‖ iv(16) ‖ ct ‖ HMAC(32)</c>;
        /// the counterpart of <see cref="EciesUnwrap"/> with HMAC and PKCS7. The two random inputs are injectable.
        /// </summary>
        public static byte[] EciesWrap(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> peerPublicKey, byte[]? ephemeralPrivateKey = null, byte[]? iv = null)
        {
            var ephemeral = ephemeralPrivateKey ?? P256.NewPrivateKey();
            var kdf = EufyKdf(P256.SharedSecret(ephemeral, peerPublicKey), 48);
            iv ??= RandomNumberGenerator.GetBytes(16);
            using var aes = Aes.Create();
            aes.Key = kdf[..16];
            var ct = aes.EncryptCbc(plaintext, iv, PaddingMode.PKCS7);
            var tag = HMACSHA256.HashData(kdf.AsSpan(16, 32), [.. iv, .. ct]);
            return [.. P256.CompressedPublicKey(ephemeral), .. iv, .. ct, .. tag];
        }

        private static byte[] PrivateKeyBytes(string hex)
        {
            var raw = Convert.FromHexString(hex.Length % 2 == 1 ? "0" + hex : hex);
            if (raw.Length == 32) return raw;
            if (raw.Length > 32) throw new ArgumentException("P-256 private key longer than 32 bytes", nameof(hex));
            var padded = new byte[32];
            raw.CopyTo(padded, 32 - raw.Length);
            return padded;
        }

        /// <summary>
        /// Derives the level-2 (signCode 2/8) AES-256-GCM session key from a decrypted <c>CMD_GATEWAYINFO</c>
        /// payload (<c>cipher_id(2 LE) ‖ 0000 ‖ ECIES(129)</c>) and the station's ECC private key.
        /// </summary>
        public static byte[]? DeriveLevel2KeyFromGatewayInfo(ReadOnlySpan<byte> gwPayload, string eccPrivateKeyHex)
        {
            if (gwPayload.Length <= 4) return null;
            var end = Math.Min(gwPayload.Length, 4 + 129);
            var plain = EciesUnwrap(gwPayload[4..end], eccPrivateKeyHex);
            if (plain is null || plain.Length < 32) return null;
            return plain[..32];
        }

        /// <summary>
        /// Derives the level-2 AES-128-ECB session key from a decrypted <c>CMD_GATEWAYINFO</c> payload and the RSA
        /// private key (base64 PKCS#8 DER) of a cipher with no ECC key. The PKCS#1 v1.5 padding is checked over the
        /// raw RSA result so a wrong key fails closed. Returns the 16-byte key or null.
        /// </summary>
        public static byte[]? DeriveLevel2KeyFromRsaGatewayInfo(ReadOnlySpan<byte> gwPayload, string rsaPrivateKey)
        {
            try
            {
                if (string.IsNullOrEmpty(rsaPrivateKey)) return null;
                using var rsa = RSA.Create();
                rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(rsaPrivateKey), out _);
                var p = rsa.ExportParameters(true);
                if (p.Modulus is null || p.D is null) return null;
                var len = p.Modulus.Length;
                if (gwPayload.Length < 4 + len) return null;
                var n = new BigInteger(p.Modulus, isUnsigned: true, isBigEndian: true);
                var d = new BigInteger(p.D, isUnsigned: true, isBigEndian: true);
                var c = new BigInteger(gwPayload.Slice(4, len), isUnsigned: true, isBigEndian: true);
                if (c >= n) return null;
                var m = BigInteger.ModPow(c, d, n).ToByteArray(isUnsigned: true, isBigEndian: true);
                if (m.Length > len) return null;
                var block = new byte[len];
                m.CopyTo(block, len - m.Length);
                if (block[0] != 0x00 || block[1] != 0x02) return null;
                var separator = Array.IndexOf(block, (byte)0x00, 2);
                if (separator < 10) return null;
                var sessionKey = block[(separator + 1)..];
                return sessionKey.Length == 16 ? sessionKey : null;
            }
            catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException)
            {
                return null;
            }
        }

        /// <summary>Local-lookup payload: two zero bytes.</summary>
        public static byte[] BuildLocalLookupPayload() => [0x00, 0x00];

        /// <summary>Cloud-lookup payload (variant 2): <c>[p2pDid:20][dskKey][0x00000000]</c>.</summary>
        public static byte[] BuildLookupWithKeyPayload2(string p2pDid, string dskKey) =>
            [.. P2pDidToBuffer(p2pDid), .. Encoding.UTF8.GetBytes(dskKey), 0, 0, 0, 0];

        /// <summary>An address record: <c>[flags:2 BE=0x0002][port:2 LE][ip:4 reversed][pad:8]</c>.</summary>
        internal static byte[] EncodeAddress(string host, int port)
        {
            var output = new byte[16];
            BinaryPrimitives.WriteUInt16BigEndian(output, 0x0002);
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(2), (ushort)port);
            var octets = host.Split('.').Select(o => int.TryParse(o, out var v) ? v : 0).ToArray();
            byte At(int i) => i < octets.Length ? (byte)octets[i] : (byte)0;
            output[4] = At(3);
            output[5] = At(2);
            output[6] = At(1);
            output[7] = At(0);
            return output;
        }

        /// <summary>
        /// Cloud-lookup payload, the classic variant (<c>LOOKUP_WITH_KEY</c>, 0xf126):
        /// <c>[p2pDid:20][selfAddr:16][clientVersion:4 = 02 05 01 05][dskKey][0x00000000]</c>.
        /// </summary>
        public static byte[] BuildLookupWithKeyPayload(string p2pDid, string selfHost, int selfPort, string dskKey) =>
            [.. P2pDidToBuffer(p2pDid), .. EncodeAddress(selfHost, selfPort), 0x02, 0x05, 0x01, 0x05, .. Encoding.UTF8.GetBytes(dskKey), 0, 0, 0, 0];

        /// <summary>RLY_REQ payload: <c>[p2pDid:20][relay address record:16][mark:4 BE]</c>.</summary>
        public static byte[] BuildRelayRequestPayload(string p2pDid, P2PAddress relay, uint mark)
        {
            var tail = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(tail, mark);
            return [.. P2pDidToBuffer(p2pDid), .. EncodeAddress(relay.Host, relay.Port), .. tail];
        }

        /// <summary>RLY_PKT payload: <c>[handle:4 BE][p2pDid:20][role:1][pad:3]</c>; a connecting client's role byte is 0.</summary>
        public static byte[] BuildRelayPacketPayload(uint handle, string p2pDid)
        {
            var head = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(head, handle);
            return [.. head, .. P2pDidToBuffer(p2pDid), 0, 0, 0, 0];
        }

        /// <summary>CHECK_CAM hole-punch payload: <c>[p2pDid:20][0x000000]</c>.</summary>
        public static byte[] BuildCheckCamPayload(string p2pDid) => [.. P2pDidToBuffer(p2pDid), 0, 0, 0];

        /// <summary>10-byte command header: <c>[dataTypeHeader:2][seq:2 BE]["XZYH"][cmd:2 LE]</c>.</summary>
        public static byte[] BuildCommandHeader(int seqNumber, int commandType, byte dataType = P2PDataType.Data)
        {
            var header = new byte[10];
            header[0] = 0xd1;
            header[1] = dataType;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)seqNumber);
            MagicBytes.CopyTo(header, 4);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), (ushort)commandType);
            return header;
        }

        /// <summary>10-byte empty command body for the given channel (CMD_GATEWAYINFO etc.).</summary>
        public static byte[] BuildVoidCommandPayload(int channel = 255) =>
            [0x00, 0x00, 0x00, 0x00, 0x01, 0x00, (byte)channel, 0x00, 0x00, 0x00];

        /// <summary>
        /// Command body carrying a string value (e.g. CMD_SET_PAYLOAD JSON):
        /// <c>[len:2 LE][00 00][01 00][channel,encType][00 00][data]</c>. With a 16-byte key the data is zero-padded
        /// and AES-128-ECB encrypted and <paramref name="encType"/> is written in the channel word.
        /// </summary>
        public static byte[] BuildStringCommandPayload(string value, int channel = 0, byte[]? key = null, int encType = 1)
        {
            var encrypted = key is { Length: 16 };
            var raw = Encoding.UTF8.GetBytes(value);
            var data = encrypted ? EncryptP2PData(PaddingP2PData(raw), key!) : raw;
            return BuildRawCommandPayload(data, channel, encrypted ? encType : 0);
        }

        /// <summary>
        /// A command body around an already-sealed (or plaintext) data buffer with an explicit sign code:
        /// <c>len(u16 LE) ‖ 0000 ‖ magic(2) ‖ [channel, signCode] ‖ [streamId, 00] ‖ data</c>.
        /// </summary>
        public static byte[] BuildRawCommandPayload(ReadOnlySpan<byte> data, int channel = 0, int signCode = 0, byte magic0 = 0x01, byte magic1 = 0x00, int streamId = 0)
        {
            var output = new byte[10 + data.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(output, (ushort)data.Length);
            output[4] = magic0;
            output[5] = magic1;
            output[6] = (byte)channel;
            output[7] = (byte)signCode;
            output[8] = (byte)streamId;
            data.CopyTo(output.AsSpan(10));
            return output;
        }

        /// <summary>ACK payload for a received DATA frame: <c>[dataTypeHeader:2][count:2 BE = 1][seqNo:2 BE]</c>.</summary>
        public static byte[] BuildAckPayload(byte dataType, int seqNo)
        {
            var output = new byte[6];
            output[0] = 0xd1;
            output[1] = dataType;
            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(4), (ushort)seqNo);
            return output;
        }

        /// <summary>Parses the 16-byte data-frame header starting at the "XZYH" magic.</summary>
        public static P2PDataFrameHeader ParseDataFrameHeader(ReadOnlySpan<byte> frame) => new(
            BinaryPrimitives.ReadUInt16LittleEndian(frame[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(frame[6..]),
            frame[12],
            frame[13],
            frame[14]);

        /// <summary>Reads the 16-byte address record at <paramref name="offset"/>: port LE at +2, IPv4 reversed at +4.</summary>
        private static P2PAddress ReadAddress(ReadOnlySpan<byte> buf, int offset) => new(
            $"{buf[offset + 7]}.{buf[offset + 6]}.{buf[offset + 5]}.{buf[offset + 4]}",
            BinaryPrimitives.ReadUInt16LittleEndian(buf[(offset + 2)..]));

        /// <summary>Parses a LOOKUP_ADDR or RLY_TO message's leading address record.</summary>
        public static P2PAddress ParseLookupAddr(ReadOnlySpan<byte> msg) => ReadAddress(msg, 4);

        /// <summary>The relay servers a LIST_REQ_ACK names, in order, without the unspecified address.</summary>
        public static IReadOnlyList<P2PAddress> ParseRelayList(ReadOnlySpan<byte> msg)
        {
            var output = new List<P2PAddress>();
            var count = msg.Length > 4 ? msg[4] : 0;
            for (var i = 0; i < count && 8 + 16 * (i + 1) <= msg.Length; i++)
            {
                var addr = ReadAddress(msg, 8 + 16 * i);
                if (addr.Host != "0.0.0.0") output.Add(addr);
            }
            return output;
        }

        /// <summary>A RLY_PORT_ACK's mark and relay port, or null for a message too short to carry them.</summary>
        public static (uint Mark, int Port)? ParseRelayPortAck(ReadOnlySpan<byte> msg) =>
            msg.Length >= 10 ? (BinaryPrimitives.ReadUInt32BigEndian(msg[4..]), BinaryPrimitives.ReadUInt16BigEndian(msg[8..])) : null;

        /// <summary>A RLY_TO's relay endpoint and handle, or null for a message too short to carry them.</summary>
        public static (P2PAddress Address, uint Handle)? ParseRelayTo(ReadOnlySpan<byte> msg) =>
            msg.Length >= 24 ? (ReadAddress(msg, 4), BinaryPrimitives.ReadUInt32BigEndian(msg[20..])) : null;

        /// <summary>Reads a NUL-terminated UTF-8 string (CMD_NOTIFY_PAYLOAD carries JSON this way).</summary>
        public static string ReadNullTerminatedString(ReadOnlySpan<byte> data)
        {
            var end = data.IndexOf((byte)0);
            return Encoding.UTF8.GetString(end < 0 ? data : data[..end]);
        }
    }
}
