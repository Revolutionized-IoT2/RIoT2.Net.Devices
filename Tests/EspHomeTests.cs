using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Parameters;
using RIoT2.Net.Devices.Services.EspHome;

namespace RIoT2.Net.Devices.Tests;

/// <summary>The ESPHome native API client: protobuf, the Noise handshake and the framing.</summary>
[TestClass]
public class EspHomeTests
{
    // From the noiseprotocol package aioesphomeapi uses: psk 00..1f, the initiator's ephemeral key 32 × 0x11, the
    // responder's 32 × 0x22, then "hello" and "again" sent and "world" received.
    private const string Message1 = "7b4e909bbe7ffe44c465a220037d608ee35897d31ef972f07f74892cb0f73f13d1bc1a38b6793685dc7e2ffdf8ea0933";
    private const string Message2 = "0faa684ed28867b97f4a6a2dee5df8ce974e76b7018e3f22a1c4cf2678570f207336b95933b321b1ae86673f689f5c9f";

    [TestMethod]
    public void TheNoiseHandshakeMatchesTheReferenceImplementation()
    {
        var psk = Enumerable.Range(0, 32).Select(x => (byte)x).ToArray();
        var handshake = new NoiseHandshake(psk, Enumerable.Repeat((byte)0x11, 32).ToArray());

        Assert.AreEqual(Message1, Convert.ToHexStringLower(handshake.WriteMessage1()));
        var (send, receive) = handshake.ReadMessage2(Convert.FromHexString(Message2));

        Assert.AreEqual("6048e4b822f45aa9e70ddc431cc573e5575f6d0b46", Convert.ToHexStringLower(send.Encrypt("hello"u8)));
        Assert.AreEqual("world", Encoding.ASCII.GetString(receive.Decrypt(Convert.FromHexString("ae254998e39c7de58e1497a3dfe006efd839b80408"))));
        Assert.AreEqual("227e5b426b40e3fd4a0560229db74ac6c6b9dfc51a", Convert.ToHexStringLower(send.Encrypt("again"u8)));
    }

    [TestMethod]
    public void AnAnswerUnderAnotherKeyIsAKeyError()
    {
        var handshake = new NoiseHandshake(new byte[32], Enumerable.Repeat((byte)0x11, 32).ToArray());
        handshake.WriteMessage1();
        Assert.ThrowsExactly<EspHomeKeyException>(() => handshake.ReadMessage2(Convert.FromHexString(Message2)));
    }

    [TestMethod]
    public void ProtobufFieldsRoundTrip()
    {
        var bytes = new ProtoWriter()
            .UInt(1, 300).Bool(2, true).Bool(3, false).Fixed32(4, 0xdeadbeef).Float(5, 0.25f).String(6, "Mikä on lämpötila?")
            .Message(7, new ProtoWriter().String(1, "name").String(2, "value")).Bytes(8, [1, 2, 3])
            .ToArray();

        var fields = Proto.Decode(bytes);
        CollectionAssert.AreEqual(new[] { 1, 2, 4, 5, 6, 7, 8 }, fields.Select(x => x.Number).ToArray(), "a false bool is left out");
        Assert.AreEqual(300UL, fields[0].Value);
        Assert.AreEqual(0xdeadbeefUL, fields[2].Value);
        Assert.AreEqual(0.25f, Proto.Float(fields, 5));
        Assert.AreEqual("Mikä on lämpötila?", Proto.String(fields, 6));
        Assert.AreEqual("value", Proto.String(Proto.Decode(Proto.Bytes(fields, 7)), 2));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, Proto.Bytes(fields, 8));
    }

    [TestMethod]
    public void AProtobufFieldLongerThanItsMessageIsRefused()
    {
        Assert.ThrowsExactly<EspHomeProtocolException>(() => Proto.Decode([0x0a, 0x05, 1, 2]));
        Assert.ThrowsExactly<EspHomeProtocolException>(() => Proto.Decode([0x08, 0x80]));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MessagesRoundTripInPlaintextAndEncrypted(bool encrypted)
    {
        await using var server = new FakeEspHomeServer { Key = encrypted ? RandomNumberGenerator.GetBytes(32) : null };
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var opening = EspHomeConnection.OpenAsync(client.GetStream(), client, server.Key, default);
        // In plaintext the satellite's side waits for the client's first message.
        var accepting = server.AcceptAsync();
        await using var connection = await opening;
        Assert.AreEqual(encrypted, connection.Encrypted);
        Assert.AreEqual(encrypted ? "echo-dot-2" : "", connection.ServerName);

        var audio = RandomNumberGenerator.GetBytes(4096);
        await connection.WriteAsync(EspHomeApi.VoiceAssistantAudio, new ProtoWriter().Bytes(1, audio), default);
        await using var satellite = await accepting;
        var (type, payload) = await satellite.ReadAsync();
        Assert.AreEqual(EspHomeApi.VoiceAssistantAudio, type);
        CollectionAssert.AreEqual(audio, Proto.Bytes(Proto.Decode(payload), 1));

        await satellite.WriteAsync(EspHomeApi.HelloResponse, new ProtoWriter().UInt(1, 1).UInt(2, 15));
        var hello = await connection.ReadAsync(default);
        Assert.AreEqual(EspHomeApi.HelloResponse, hello!.Type);
        Assert.AreEqual(15UL, Proto.Decode(hello.Payload)[1].Value);

        await satellite.DisposeAsync();
        Assert.IsNull(await connection.ReadAsync(default), "a connection closed between messages reads as null");
    }

    [TestMethod]
    public async Task AHandshakeWithTheWrongKeyIsAKeyError()
    {
        await using var server = new FakeEspHomeServer { Key = RandomNumberGenerator.GetBytes(32) };
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var opening = EspHomeConnection.OpenAsync(client.GetStream(), client, RandomNumberGenerator.GetBytes(32), default);
        var accepting = server.AcceptAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsExactlyAsync<EspHomeKeyException>(() => opening);
        await Assert.ThrowsAsync<OperationCanceledException>(() => accepting);
        Assert.AreEqual(1, server.RefusedHandshakes);
    }

    [TestMethod]
    public async Task APlaintextClientIsToldTheSatelliteWantsEncryption()
    {
        await using var server = new FakeEspHomeServer();
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var connection = await EspHomeConnection.OpenAsync(client.GetStream(), client, null, default);
        await connection.WriteAsync(EspHomeApi.HelloRequest, EspHomeApi.Hello("test"), default);
        await using var satellite = await server.AcceptAsync();
        await satellite.WriteRawAsync([1, 0, 0]);
        var error = await Assert.ThrowsExactlyAsync<EspHomeProtocolException>(() => connection.ReadAsync(default));
        StringAssert.Contains(error.Message, "encryptionKey");
    }

    [TestMethod]
    public void TheTimeZoneIsSentAsItsPosixRule()
    {
        // Helsinki's rule, as tzdata's footer has it: the last Sunday of March 03:00 to the last of October 04:00.
        var helsinki = TimeZoneInfo.CreateCustomTimeZone("Test/Helsinki", TimeSpan.FromHours(2), "Helsinki", "EET", "EEST",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 3, 5, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 4, 0, 0), 10, 5, DayOfWeek.Sunday))
        ]);
        Assert.AreEqual("<+02>-2<+03>-3,M3.5.0/3,M10.5.0/4", PosixTimeZone.FromRules(helsinki, DateTimeOffset.UtcNow));

        var india = TimeZoneInfo.CreateCustomTimeZone("Test/Kolkata", new TimeSpan(5, 30, 0), "Kolkata", "IST");
        Assert.AreEqual("<+0530>-5:30", PosixTimeZone.FromRules(india, DateTimeOffset.UtcNow));

        var fixedDates = TimeZoneInfo.CreateCustomTimeZone("Test/Fixed", TimeSpan.FromHours(-3), "Fixed", "A", "B",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 10, 1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 2, 15))
        ]);
        Assert.IsNull(PosixTimeZone.FromRules(fixedDates, DateTimeOffset.UtcNow), "fixed dates are left out rather than said wrong");
    }

    [TestMethod]
    public void TheRuleIsReadFromATzifFilesFooter()
    {
        byte[] file = [.. "TZif2"u8, .. new byte[40], .. "\nEET-2EEST,M3.5.0/3,M10.5.0/4\n"u8];
        Assert.AreEqual("EET-2EEST,M3.5.0/3,M10.5.0/4", PosixTimeZone.FromTzif(file));
        Assert.IsNull(PosixTimeZone.FromTzif([.. "TZif\0"u8, .. new byte[40], .. "\nEET-2\n"u8]), "version 1 has no footer");
        Assert.IsNull(PosixTimeZone.FromTzif([.. "Nope2"u8, .. "\nEET-2\n"u8]));
    }
}

/// <summary>Protobuf fields as the tests check them.</summary>
internal static class Proto
{
    public sealed record Field(int Number, WireType Type, ulong Value, byte[] Data);

    public static List<Field> Decode(byte[] payload)
    {
        var fields = new List<Field>();
        var reader = new ProtoReader(payload);
        while (reader.Next(out var field))
            fields.Add(new Field(field.Number, field.Type, field.Value, field.Data.ToArray()));
        return fields;
    }

    public static ulong UInt(List<Field> fields, int number) => fields.LastOrDefault(x => x.Number == number)?.Value ?? 0;
    public static bool Bool(List<Field> fields, int number) => UInt(fields, number) != 0;
    public static float Float(List<Field> fields, int number) => BitConverter.UInt32BitsToSingle((uint)UInt(fields, number));
    public static byte[] Bytes(List<Field> fields, int number) => fields.LastOrDefault(x => x.Number == number)?.Data ?? [];
    public static string String(List<Field> fields, int number) => Encoding.UTF8.GetString(Bytes(fields, number));
    public static string[] Strings(List<Field> fields, int number) => fields.Where(x => x.Number == number).Select(x => Encoding.UTF8.GetString(x.Data)).ToArray();
}

/// <summary>
/// A stand-in for hassmic's ESPHome API server on the loopback address: plaintext, or Noise with its key or, without
/// one, the all-zero key (noise.c, proto_esphome.c).
/// </summary>
internal sealed class FakeEspHomeServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public FakeEspHomeServer() => _listener.Start();

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>The satellite's encryption key; null: none yet.</summary>
    public byte[]? Key { get; set; }

    public int RefusedHandshakes { get; private set; }

    /// <summary>The next connection whose handshake succeeds; refused ones are counted and closed.</summary>
    public async Task<FakeEspHomeLink> AcceptAsync(TimeSpan? timeout = null)
    {
        using var cancel = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            var client = await _listener.AcceptTcpClientAsync(cancel.Token);
            var link = new FakeEspHomeLink(client);
            if (await link.HandshakeAsync(Key, cancel.Token))
                return link;
            RefusedHandshakes++;
            await link.DisposeAsync();
        }
    }

    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        _listener.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>One client's connection, the satellite's side.</summary>
internal sealed class FakeEspHomeLink(TcpClient client) : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly Stream _stream = client.GetStream();
    private NoiseCipher? _send;
    private NoiseCipher? _receive;
    private bool _markerRead;

    public bool Encrypted => _send != null;

    internal async Task<bool> HandshakeAsync(byte[]? key, CancellationToken cancellationToken)
    {
        var marker = await ReadExactAsync(1, cancellationToken);
        if (marker[0] == 0)
        {
            _markerRead = true;
            return true;
        }
        var helloLength = await ReadExactAsync(2, cancellationToken);
        await ReadExactAsync(helloLength[0] << 8 | helloLength[1], cancellationToken);
        var frame = await ReadFrameAsync(cancellationToken);
        Assert.AreEqual(0, frame[0], "the handshake frame starts with 0");

        await WriteFrameAsync([1, .. "echo-dot-2\0aabbccddeeff\0"u8]);
        var responder = new NoiseResponder(key ?? new byte[32]);
        if (!responder.ReadMessage1(frame.AsSpan(1)))
        {
            await WriteFrameAsync([1, .. Encoding.ASCII.GetBytes("Handshake MAC failure")]);
            return false;
        }
        var (message2, send, receive) = responder.WriteMessage2();
        await WriteFrameAsync([0, .. message2]);
        (_send, _receive) = (send, receive);
        return true;
    }

    /// <summary>The next message, or null when the client closed the connection.</summary>
    public async Task<(int Type, byte[] Payload)> ReadAsync()
    {
        using var cancel = new CancellationTokenSource(Wait);
        var message = await TryReadAsync(cancel.Token);
        Assert.IsNotNull(message, "the client closed the connection");
        return message.Value;
    }

    public async Task<(int Type, byte[] Payload)?> TryReadAsync(CancellationToken cancellationToken)
    {
        if (_receive != null)
        {
            var header = new byte[3];
            if (await _stream.ReadAtLeastAsync(header, 3, false, cancellationToken) < 3)
                return null;
            var plain = _receive.Decrypt(await ReadExactAsync(header[1] << 8 | header[2], cancellationToken));
            Assert.AreEqual(plain.Length - 4, plain[2] << 8 | plain[3], "the length inside the frame");
            return (plain[0] << 8 | plain[1], plain[4..]);
        }
        if (!_markerRead)
        {
            var marker = new byte[1];
            if (await _stream.ReadAtLeastAsync(marker, 1, false, cancellationToken) < 1)
                return null;
            Assert.AreEqual(0, marker[0]);
        }
        _markerRead = false;
        var length = await ReadVarintAsync(cancellationToken);
        var type = await ReadVarintAsync(cancellationToken);
        return ((int)type, await ReadExactAsync((int)length, cancellationToken));
    }

    /// <summary>The next message, which must be of <paramref name="type"/>; its fields.</summary>
    public async Task<List<Proto.Field>> ExpectAsync(int type)
    {
        var (actual, payload) = await ReadAsync();
        Assert.AreEqual(type, actual, $"expected message {type}, got {actual}");
        return Proto.Decode(payload);
    }

    public async Task WriteAsync(int type, ProtoWriter? message = null)
    {
        var payload = message?.ToArray() ?? [];
        if (_send != null)
        {
            var plain = new byte[4 + payload.Length];
            BinaryPrimitives.WriteUInt16BigEndian(plain, (ushort)type);
            BinaryPrimitives.WriteUInt16BigEndian(plain.AsSpan(2), (ushort)payload.Length);
            payload.CopyTo(plain, 4);
            await WriteFrameAsync(_send.Encrypt(plain));
            return;
        }
        var header = new byte[21];
        var at = 1;
        at += ProtoWriter.WriteVarint(header.AsSpan(at), (ulong)payload.Length);
        at += ProtoWriter.WriteVarint(header.AsSpan(at), (ulong)type);
        await WriteRawAsync([.. header[..at], .. payload]);
    }

    public async Task WriteRawAsync(byte[] bytes)
    {
        await _stream.WriteAsync(bytes);
        await _stream.FlushAsync();
    }

    private Task WriteFrameAsync(byte[] frame) =>
        WriteRawAsync([1, (byte)(frame.Length >> 8), (byte)frame.Length, .. frame]);

    private async Task<byte[]> ReadFrameAsync(CancellationToken cancellationToken)
    {
        var header = await ReadExactAsync(3, cancellationToken);
        Assert.AreEqual(1, header[0]);
        return await ReadExactAsync(header[1] << 8 | header[2], cancellationToken);
    }

    private async Task<ulong> ReadVarintAsync(CancellationToken cancellationToken)
    {
        ulong value = 0;
        for (var shift = 0; ; shift += 7)
        {
            var b = (await ReadExactAsync(1, cancellationToken))[0];
            value |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0)
                return value;
        }
    }

    private async Task<byte[]> ReadExactAsync(int count, CancellationToken cancellationToken)
    {
        var bytes = new byte[count];
        await _stream.ReadExactlyAsync(bytes, cancellationToken);
        return bytes;
    }

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        _send?.Dispose();
        _receive?.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>The responder of Noise_NNpsk0_25519_ChaChaPoly_SHA256, as hassmic's noise.c is.</summary>
internal sealed class NoiseResponder
{
    private readonly byte[] _psk;
    private readonly byte[] _ephemeral = RandomNumberGenerator.GetBytes(32);
    private byte[] _h;
    private byte[] _ck;
    private byte[] _k = [];
    private ulong _n;
    private byte[] _re = [];

    public NoiseResponder(byte[] psk)
    {
        _psk = psk;
        _h = SHA256.HashData(Encoding.ASCII.GetBytes(NoiseHandshake.ProtocolName));
        _ck = _h;
        MixHash(Encoding.ASCII.GetBytes("NoiseAPIInit\0\0"));
    }

    public bool ReadMessage1(ReadOnlySpan<byte> message)
    {
        var (ck, temp, k) = Hkdf(_ck, _psk, 3);
        (_ck, _k, _n) = (ck, k!, 0);
        MixHash(temp);
        _re = message[..32].ToArray();
        MixHash(_re);
        MixKey(_re);
        if (!Open(message[32..]))
            return false;
        MixHash(message[32..]);
        return true;
    }

    public (byte[] Message, NoiseCipher Send, NoiseCipher Receive) WriteMessage2()
    {
        var e = new X25519PrivateKeyParameters(_ephemeral).GeneratePublicKey().GetEncoded();
        MixHash(e);
        MixKey(e);
        var shared = new byte[32];
        var agreement = new X25519Agreement();
        agreement.Init(new X25519PrivateKeyParameters(_ephemeral));
        agreement.CalculateAgreement(new X25519PublicKeyParameters(_re), shared, 0);
        MixKey(shared);
        var tag = NoiseCipher.Seal(_k, _n++, _h, []);
        MixHash(tag);
        var (k1, k2, _) = Hkdf(_ck, [], 2);
        return ([.. e, .. tag], new NoiseCipher(k2), new NoiseCipher(k1));
    }

    private bool Open(ReadOnlySpan<byte> ciphertext) => NoiseCipher.Open(_k, _n++, _h, ciphertext) != null;

    private void MixHash(ReadOnlySpan<byte> data) => _h = SHA256.HashData(_h.Concat(data.ToArray()).ToArray());

    private void MixKey(byte[] input)
    {
        var (ck, k, _) = Hkdf(_ck, input, 2);
        (_ck, _k, _n) = (ck, k, 0);
    }

    private static (byte[], byte[], byte[]?) Hkdf(byte[] ck, byte[] input, int outputs)
    {
        var temp = HMACSHA256.HashData(ck, input);
        var o1 = HMACSHA256.HashData(temp, new byte[] { 1 });
        var o2 = HMACSHA256.HashData(temp, o1.Append((byte)2).ToArray());
        return (o1, o2, outputs == 3 ? HMACSHA256.HashData(temp, o2.Append((byte)3).ToArray()) : null);
    }
}
