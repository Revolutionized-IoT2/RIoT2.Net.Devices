using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using RIoT2.Net.Devices.Services.Eufy;
using RIoT2.Net.Devices.Services.Eufy.P2P;

namespace RIoT2.Net.Devices.Tests.Eufy.P2P;

/// <summary>An in-memory UDP network: every datagram a session sends goes to the one <see cref="FakeStation"/>.</summary>
internal sealed class FakeP2PNetwork : IP2PUdpTransport
{
    private readonly object gate = new();
    private int nextPort = 40000;
    private readonly List<FakeUdpSocket> sockets = [];

    public FakeStation? Station { get; set; }

    public IReadOnlyList<FakeUdpSocket> Sockets
    {
        get { lock (gate) return [.. sockets]; }
    }

    public IP2PUdpSocket Bind(bool broadcast)
    {
        lock (gate)
        {
            var socket = new FakeUdpSocket(this, nextPort++, broadcast);
            sockets.Add(socket);
            return socket;
        }
    }

    public Task<string?> DetectLocalIpAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public Task<IReadOnlyList<P2PAddress>> ResolveAsync(IReadOnlyList<P2PAddress> addresses, CancellationToken cancellationToken) => Task.FromResult(addresses);

    internal void Deliver(FakeUdpSocket from, byte[] datagram, P2PAddress to) => Station?.Receive(from, datagram, to);
}

internal sealed class FakeUdpSocket(FakeP2PNetwork network, int port, bool broadcast) : IP2PUdpSocket
{
    private readonly Channel<P2PDatagram> inbox = Channel.CreateUnbounded<P2PDatagram>();

    public int LocalPort { get; } = port;
    public bool Broadcast { get; } = broadcast;
    public bool Disposed { get; private set; }

    public void Send(byte[] datagram, P2PAddress remote)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        network.Deliver(this, datagram, remote);
    }

    public ValueTask<P2PDatagram> ReceiveAsync(CancellationToken cancellationToken) => inbox.Reader.ReadAsync(cancellationToken);

    public int RequestReceiveBuffer(int bytes) => bytes;

    public void Push(byte[] datagram, P2PAddress from) => inbox.Writer.TryWrite(new P2PDatagram(datagram, from));

    public void Dispose()
    {
        Disposed = true;
        inbox.Writer.TryComplete();
    }
}

/// <summary>A data frame the station received, its body opened with the key its sign code names.</summary>
internal sealed record StationCommand(FakeUdpSocket From, byte DataType, int Seq, int Cmd, int Channel, int SignCode, byte Magic0, byte Magic1, int StreamId, byte[] Body, byte[]? Plain, byte[] Datagram)
{
    public string Text => Plain is null ? "" : TestCrypto.TrimZeros(Plain);
    public JsonElement Json => JsonElement.Parse(Text);
}

/// <summary>
/// A PPCS station on <see cref="Address"/>: answers the LAN lookup and CHECK_CAM, PING, acknowledges every DATA
/// frame, answers <c>CMD_GATEWAYINFO</c> with an ECIES envelope when it has a cipher, and opens the bodies it
/// receives (sign code 1 with the level-1 key, 8 with the session key).
/// </summary>
internal sealed class FakeStation
{
    private readonly object gate = new();
    private readonly List<StationCommand> commands = [];
    private readonly Dictionary<byte, int> seqByType = [];

    public FakeStation(string sn, string did)
    {
        Sn = sn;
        Did = did;
        Level1Key = TestCrypto.Level1Key(sn, did);
    }

    public string Sn { get; }
    public string Did { get; }
    public byte[] Level1Key { get; }
    public P2PAddress Address { get; init; } = new("192.168.1.50", 5000);
    /// <summary>False: the station ignores everything (unreachable).</summary>
    public bool Reachable { get; set; } = true;
    /// <summary>The cipher the station seals its session key for; null: it never answers <c>CMD_GATEWAYINFO</c>.</summary>
    public System.Security.Cryptography.ECDiffieHellman? Cipher { get; set; }
    public int CipherId { get; set; } = 7;
    public byte[] SessionKey { get; } = Enumerable.Range(0, 32).Select(i => (byte)(0x40 + i)).ToArray();
    /// <summary>The result code to answer a command with, by command id; null answers nothing.</summary>
    public Func<StationCommand, int?>? ResultFor { get; set; }
    /// <summary>Called for every received command, after the ACK, to send whatever else the station would.</summary>
    public Action<FakeStation, StationCommand>? OnCommand { get; set; }
    public int Ends { get; private set; }
    public int Pings { get; private set; }

    public IReadOnlyList<StationCommand> Commands
    {
        get { lock (gate) return [.. commands]; }
    }

    public IReadOnlyList<StationCommand> CommandsOf(int cmd) => [.. Commands.Where(c => c.Cmd == cmd)];

    public void Receive(FakeUdpSocket from, byte[] datagram, P2PAddress to)
    {
        lock (gate)
        {
            if (!Reachable || datagram.Length < 4) return;
            var type = BinaryPrimitives.ReadUInt16BigEndian(datagram);
            switch (type)
            {
                case 0xf130 when to.Port == 32108:
                    Reply(from, 0xf141, P2PCodec.P2pDidToBuffer(Did));
                    break;
                case 0xf141 when to == Address:
                    Reply(from, 0xf142, P2PCodec.P2pDidToBuffer(Did));
                    break;
                case 0xf1e0:
                    Pings++;
                    Reply(from, 0xf1e1);
                    break;
                case 0xf1f0:
                    Ends++;
                    break;
                case 0xf1d0 when to == Address:
                    OnData(from, datagram);
                    break;
            }
        }
    }

    private void OnData(FakeUdpSocket from, byte[] datagram)
    {
        var dataType = datagram[5];
        var seq = BinaryPrimitives.ReadUInt16BigEndian(datagram.AsSpan(6));
        Reply(from, 0xf1d1, [0xd1, dataType, 0x00, 0x01, (byte)(seq >> 8), (byte)seq]);
        if (!datagram.AsSpan(8, 4).SequenceEqual("XZYH"u8)) return;
        var cmd = BinaryPrimitives.ReadUInt16LittleEndian(datagram.AsSpan(12));
        var length = BinaryPrimitives.ReadUInt16LittleEndian(datagram.AsSpan(14));
        var body = datagram.AsSpan(24, Math.Min(length, datagram.Length - 24)).ToArray();
        var sign = datagram[21];
        byte[]? plain = sign switch
        {
            0 => body,
            1 when body.Length % 16 == 0 => TestCrypto.EcbDecrypt(Level1Key, body),
            8 => TestCrypto.GcmOpen(SessionKey, body, 32),
            _ => null,
        };
        var command = new StationCommand(from, dataType, seq, cmd, datagram[20], sign, datagram[18], datagram[19], datagram[22], body, plain, datagram);
        commands.Add(command);
        if (cmd == 1100 && Cipher is not null)
            SendData(from, 1100, 255, 0, [(byte)CipherId, (byte)(CipherId >> 8), 0, 0, .. TestCrypto.GatewayEnvelope(SessionKey, Cipher)]);
        if (ResultFor?.Invoke(command) is { } code)
        {
            var result = TestCrypto.Int32Le(code);
            if (sign == 8) SendData(from, cmd, command.Channel, 8, TestCrypto.GcmSeal(SessionKey, result, [0x00, 0x03, 0x02, 0x01]));
            else if (sign == 1) SendData(from, cmd, command.Channel, 1, TestCrypto.EcbEncrypt(Level1Key, TestCrypto.ZeroPad(result)));
            else SendData(from, cmd, command.Channel, 0, result);
        }
        OnCommand?.Invoke(this, command);
    }

    private void Reply(FakeUdpSocket to, ushort type, byte[]? payload = null) => to.Push(P2PCodec.FrameMessage(type, payload ?? []), Address);

    /// <summary>Sends one data frame <c>[d1 type][seq]["XZYH" cmd len 01 00 channel sign 00 00][body]</c> on the data type's own sequence.</summary>
    public void SendData(FakeUdpSocket to, int cmd, int channel, int signCode, byte[] body, byte dataType = 0)
    {
        lock (gate)
        {
            var seq = seqByType.GetValueOrDefault(dataType);
            seqByType[dataType] = (seq + 1) & 0xffff;
            to.Push(P2PCodec.FrameMessage(0xf1d0, DataDatagramBody(seq, cmd, channel, signCode, body, dataType)), Address);
        }
    }

    public static byte[] DataDatagramBody(int seq, int cmd, int channel, int signCode, byte[] body, byte dataType = 0)
    {
        var header = new byte[20];
        header[0] = 0xd1;
        header[1] = dataType;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)seq);
        "XZYH"u8.CopyTo(header.AsSpan(4));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), (ushort)cmd);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(10), (uint)body.Length);
        header[14] = 0x01;
        header[16] = (byte)channel;
        header[17] = (byte)signCode;
        return [.. header, .. body];
    }

    /// <summary>A <c>CMD_VIDEO_FRAME</c> payload: the 22-byte header (length, key flag, sequence, size, timestamp), then Annex-B.</summary>
    public static byte[] VideoPayload(byte[] annexb, bool keyframe, int sequence, uint timestamp, int width = 1920, int height = 1080)
    {
        var header = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)annexb.Length);
        header[4] = keyframe ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)sequence);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(10), (short)width);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(12), (short)height);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14), timestamp);
        return [.. header, .. annexb];
    }
}

/// <summary>The cloud as the P2P client uses it: the account identity, DSK keys and <c>get_ciphers</c>.</summary>
internal sealed class FakeP2PCloud : IEufyCloudApi
{
    private readonly object gate = new();
    private readonly List<JsonElement> cipherRequests = [];

    public bool IsAuthenticated => true;
    public string? UserId { get; set; } = "user-0001";
    public string AccountName { get; set; } = P2PDevices.AccountName;
    /// <summary>The ECC private key <c>get_ciphers</c> answers for every cipher id; null answers an empty list.</summary>
    public string? EccPrivateKeyHex { get; set; }

    public IReadOnlyList<JsonElement> CipherRequests
    {
        get { lock (gate) return [.. cipherRequests]; }
    }

    public Task<JsonElement> SecurityAppPostAsync(string path, object? body, CancellationToken cancellationToken)
    {
        if (path != "/v3/app/cipher/get_ciphers") throw new NotSupportedException(path);
        var request = JsonSerializer.SerializeToElement(body);
        lock (gate) cipherRequests.Add(request);
        var id = request.GetProperty("cipher_ids")[0].GetInt32();
        var answer = EccPrivateKeyHex is null ? "[]" : $"[{{\"cipher_id\":{id},\"ecc_private_key\":\"{EccPrivateKeyHex}\"}}]";
        return Task.FromResult(JsonElement.Parse(answer));
    }

    public Task<IReadOnlyDictionary<string, (string DskKey, long Expiration)>> GetDskKeysAsync(IReadOnlyCollection<string> stationSerials, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, (string DskKey, long Expiration)>>(new Dictionary<string, (string, long)>());

    public Task<EufyLoginResult> LoginAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<EufyLoginResult> SolveCaptchaAsync(string answer, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<EufyLoginResult> SubmitVerifyCodeAsync(string code, CancellationToken cancellationToken) => throw new NotSupportedException();
    public void ClearSession() { }
    public Task<IReadOnlyList<EufyDeviceRecord>> GetDevicesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<JsonElement> PostAsync(string service, string path, object? body, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<string> GetDevicePublicKeyAsync(string deviceSerial, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task RegisterPushTokenAsync(string fcmToken, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<byte[]> DownloadImageAsync(string url, string? p2pDid, CancellationToken cancellationToken) => throw new NotSupportedException();
    public void Dispose() { }
}

internal static class AsyncTestExtensions
{
    /// <summary>Every await in the P2P specs is bounded, so a regression fails instead of hanging the run.</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    public static Task Bounded(this Task task) => task.WaitAsync(Bound);
    public static Task<T> Bounded<T>(this Task<T> task) => task.WaitAsync(Bound);

    public static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"timed out waiting for {what}");
            await Task.Delay(10);
        }
    }
}
