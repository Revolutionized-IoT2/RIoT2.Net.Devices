// Ported from eufy-sdk src/transport/push/proto.ts @ 5f92bea5 (Apache-2.0), modified for RIoT2.
// Upstream parses Google's public MCS and GServices check-in .proto schemas with protobufjs. This port
// hand-writes the few messages it uses on a minimal protobuf reader/writer; field numbers and wire
// types are exactly those of the upstream schemas. Unknown fields are skipped on decode.

using System.Buffers.Binary;
using System.Text;

namespace RIoT2.Net.Devices.Services.Eufy.Push
{
    internal enum ProtoWireType
    {
        Varint = 0,
        Fixed64 = 1,
        LengthDelimited = 2,
        StartGroup = 3,
        EndGroup = 4,
        Fixed32 = 5,
    }

    /// <summary>A minimal protobuf (proto2) encoder.</summary>
    internal sealed class ProtoWriter
    {
        private readonly MemoryStream _buffer = new();

        public int Length => (int)_buffer.Length;

        public byte[] ToArray() => _buffer.ToArray();

        public static int VarintSize(ulong value)
        {
            var size = 1;
            while (value >= 0x80)
            {
                value >>= 7;
                size++;
            }
            return size;
        }

        public void WriteRawByte(byte value) => _buffer.WriteByte(value);

        public void WriteRawBytes(ReadOnlySpan<byte> value) => _buffer.Write(value);

        public void WriteVarint(ulong value)
        {
            while (value >= 0x80)
            {
                _buffer.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }
            _buffer.WriteByte((byte)value);
        }

        public void WriteTag(int field, ProtoWireType wireType) => WriteVarint(((ulong)field << 3) | (uint)wireType);

        /// <summary>int32: a negative value is sign-extended to ten bytes, as protobuf does.</summary>
        public void WriteInt32(int field, int value)
        {
            WriteTag(field, ProtoWireType.Varint);
            WriteVarint((ulong)(long)value);
        }

        public void WriteInt64(int field, long value)
        {
            WriteTag(field, ProtoWireType.Varint);
            WriteVarint((ulong)value);
        }

        public void WriteBool(int field, bool value)
        {
            WriteTag(field, ProtoWireType.Varint);
            WriteVarint(value ? 1UL : 0UL);
        }

        public void WriteString(int field, string value) => WriteBytes(field, Encoding.UTF8.GetBytes(value));

        public void WriteBytes(int field, ReadOnlySpan<byte> value)
        {
            WriteTag(field, ProtoWireType.LengthDelimited);
            WriteVarint((ulong)value.Length);
            _buffer.Write(value);
        }

        public void WriteFixed64(int field, ulong value)
        {
            WriteTag(field, ProtoWireType.Fixed64);
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
            _buffer.Write(bytes);
        }

        public void WriteMessage(int field, ProtoWriter message) => WriteBytes(field, message.ToArray());
    }

    /// <summary>A minimal protobuf (proto2) decoder over one message's bytes.</summary>
    internal ref struct ProtoReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _position;

        public ProtoReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _position = 0;
        }

        public readonly int Position => _position;

        public bool TryReadTag(out int field, out ProtoWireType wireType)
        {
            if (_position >= _data.Length)
            {
                field = 0;
                wireType = default;
                return false;
            }
            var tag = ReadVarint();
            field = (int)(tag >> 3);
            wireType = (ProtoWireType)(int)(tag & 7);
            if (field <= 0) throw new InvalidDataException("protobuf: invalid field number 0");
            return true;
        }

        public ulong ReadVarint()
        {
            ulong result = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                if (_position >= _data.Length) throw new InvalidDataException("protobuf: truncated varint");
                var b = _data[_position++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
            }
            throw new InvalidDataException("protobuf: malformed varint");
        }

        public int ReadInt32() => unchecked((int)ReadVarint());

        public long ReadInt64() => unchecked((long)ReadVarint());

        public bool ReadBool() => ReadVarint() != 0;

        public ReadOnlySpan<byte> ReadBytes()
        {
            var length = ReadVarint();
            if (length > (ulong)(_data.Length - _position)) throw new InvalidDataException("protobuf: truncated length-delimited field");
            var slice = _data.Slice(_position, (int)length);
            _position += (int)length;
            return slice;
        }

        public string ReadString() => Encoding.UTF8.GetString(ReadBytes());

        public ulong ReadFixed64()
        {
            if (_data.Length - _position < 8) throw new InvalidDataException("protobuf: truncated fixed64");
            var value = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(_position, 8));
            _position += 8;
            return value;
        }

        public uint ReadFixed32()
        {
            if (_data.Length - _position < 4) throw new InvalidDataException("protobuf: truncated fixed32");
            var value = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_position, 4));
            _position += 4;
            return value;
        }

        public void Skip(ProtoWireType wireType)
        {
            switch (wireType)
            {
                case ProtoWireType.Varint:
                    ReadVarint();
                    break;
                case ProtoWireType.Fixed64:
                    ReadFixed64();
                    break;
                case ProtoWireType.LengthDelimited:
                    ReadBytes();
                    break;
                case ProtoWireType.Fixed32:
                    ReadFixed32();
                    break;
                default:
                    throw new InvalidDataException($"protobuf: unsupported wire type {(int)wireType}");
            }
        }
    }

    // ---- mcs_proto ----

    /// <summary><c>mcs_proto.HeartbeatPing</c> / <c>HeartbeatAck</c> (same fields).</summary>
    internal sealed class McsHeartbeat
    {
        public int? StreamId { get; set; }
        public int? LastStreamIdReceived { get; set; }
        public long? Status { get; set; }

        public byte[] Encode()
        {
            var w = new ProtoWriter();
            if (StreamId is { } streamId) w.WriteInt32(1, streamId);
            if (LastStreamIdReceived is { } last) w.WriteInt32(2, last);
            if (Status is { } status) w.WriteInt64(3, status);
            return w.ToArray();
        }

        public static McsHeartbeat Decode(ReadOnlySpan<byte> data)
        {
            var m = new McsHeartbeat();
            var r = new ProtoReader(data);
            while (r.TryReadTag(out var field, out var wt))
            {
                switch (field)
                {
                    case 1 when wt == ProtoWireType.Varint: m.StreamId = r.ReadInt32(); break;
                    case 2 when wt == ProtoWireType.Varint: m.LastStreamIdReceived = r.ReadInt32(); break;
                    case 3 when wt == ProtoWireType.Varint: m.Status = r.ReadInt64(); break;
                    default: r.Skip(wt); break;
                }
            }
            return m;
        }
    }

    /// <summary><c>mcs_proto.ErrorInfo</c> (the <c>extension</c> field is not decoded).</summary>
    internal sealed class McsErrorInfo
    {
        public int Code { get; set; }
        public string? Message { get; set; }
        public string? Type { get; set; }

        public static McsErrorInfo Decode(ReadOnlySpan<byte> data)
        {
            var m = new McsErrorInfo();
            var r = new ProtoReader(data);
            while (r.TryReadTag(out var field, out var wt))
            {
                switch (field)
                {
                    case 1 when wt == ProtoWireType.Varint: m.Code = r.ReadInt32(); break;
                    case 2 when wt == ProtoWireType.LengthDelimited: m.Message = r.ReadString(); break;
                    case 3 when wt == ProtoWireType.LengthDelimited: m.Type = r.ReadString(); break;
                    default: r.Skip(wt); break;
                }
            }
            return m;
        }

        public override string ToString() => $"{{\"code\":{Code},\"message\":\"{Message}\",\"type\":\"{Type}\"}}";
    }

    /// <summary><c>mcs_proto.Setting</c>.</summary>
    internal sealed record McsSetting(string Name, string Value)
    {
        public byte[] Encode()
        {
            var w = new ProtoWriter();
            w.WriteString(1, Name);
            w.WriteString(2, Value);
            return w.ToArray();
        }

        public static McsSetting Decode(ReadOnlySpan<byte> data)
        {
            string name = "", value = "";
            var r = new ProtoReader(data);
            while (r.TryReadTag(out var field, out var wt))
            {
                switch (field)
                {
                    case 1 when wt == ProtoWireType.LengthDelimited: name = r.ReadString(); break;
                    case 2 when wt == ProtoWireType.LengthDelimited: value = r.ReadString(); break;
                    default: r.Skip(wt); break;
                }
            }
            return new McsSetting(name, value);
        }
    }

    /// <summary><c>mcs_proto.LoginRequest</c> (<c>heartbeat_stat</c>, <c>account_id</c>, <c>status</c>, <c>client_event</c> omitted: upstream never sets them).</summary>
    internal sealed class McsLoginRequest
    {
        public const int AuthServiceAndroidId = 2;

        public string Id { get; set; } = "";
        public string Domain { get; set; } = "";
        public string User { get; set; } = "";
        public string Resource { get; set; } = "";
        public string AuthToken { get; set; } = "";
        public string? DeviceId { get; set; }
        public long? LastRmqId { get; set; }
        public List<McsSetting> Setting { get; } = [];
        public List<string> ReceivedPersistentId { get; } = [];
        public bool? AdaptiveHeartbeat { get; set; }
        public bool? UseRmq2 { get; set; }
        public int? AuthService { get; set; }
        public int? NetworkType { get; set; }

        public byte[] Encode()
        {
            var w = new ProtoWriter();
            w.WriteString(1, Id);
            w.WriteString(2, Domain);
            w.WriteString(3, User);
            w.WriteString(4, Resource);
            w.WriteString(5, AuthToken);
            if (DeviceId is not null) w.WriteString(6, DeviceId);
            if (LastRmqId is { } rmq) w.WriteInt64(7, rmq);
            foreach (var s in Setting) w.WriteBytes(8, s.Encode());
            foreach (var id in ReceivedPersistentId) w.WriteString(10, id);
            if (AdaptiveHeartbeat is { } adaptive) w.WriteBool(12, adaptive);
            if (UseRmq2 is { } rmq2) w.WriteBool(14, rmq2);
            if (AuthService is { } auth) w.WriteInt32(16, auth);
            if (NetworkType is { } network) w.WriteInt32(17, network);
            return w.ToArray();
        }

        public static McsLoginRequest Decode(ReadOnlySpan<byte> data)
        {
            var m = new McsLoginRequest();
            var r = new ProtoReader(data);
            while (r.TryReadTag(out var field, out var wt))
            {
                switch (field)
                {
                    case 1 when wt == ProtoWireType.LengthDelimited: m.Id = r.ReadString(); break;
                    case 2 when wt == ProtoWireType.LengthDelimited: m.Domain = r.ReadString(); break;
                    case 3 when wt == ProtoWireType.LengthDelimited: m.User = r.ReadString(); break;
                    case 4 when wt == ProtoWireType.LengthDelimited: m.Resource = r.ReadString(); break;
                    case 5 when wt == ProtoWireType.LengthDelimited: m.AuthToken = r.ReadString(); break;
                    case 6 when wt == ProtoWireType.LengthDelimited: m.DeviceId = r.ReadString(); break;
                    case 7 when wt == ProtoWireType.Varint: m.LastRmqId = r.ReadInt64(); break;
                    case 8 when wt == ProtoWireType.LengthDelimited: m.Setting.Add(McsSetting.Decode(r.ReadBytes())); break;
                    case 10 when wt == ProtoWireType.LengthDelimited: m.ReceivedPersistentId.Add(r.ReadString()); break;
                    case 12 when wt == ProtoWireType.Varint: m.AdaptiveHeartbeat = r.ReadBool(); break;
                    case 14 when wt == ProtoWireType.Varint: m.UseRmq2 = r.ReadBool(); break;
                    case 16 when wt == ProtoWireType.Varint: m.AuthService = r.ReadInt32(); break;
                    case 17 when wt == ProtoWireType.Varint: m.NetworkType = r.ReadInt32(); break;
                    default: r.Skip(wt); break;
                }
            }
            return m;
        }
    }

    /// <summary><c>mcs_proto.LoginResponse</c> (<c>heartbeat_config</c> is skipped).</summary>
    internal sealed class McsLoginResponse
    {
        public string Id { get; set; } = "";
        public string? Jid { get; set; }
        public McsErrorInfo? Error { get; set; }
        public List<McsSetting> Setting { get; } = [];
        public int? StreamId { get; set; }
        public int? LastStreamIdReceived { get; set; }
        public long? ServerTimestamp { get; set; }

        public byte[] Encode()
        {
            var w = new ProtoWriter();
            w.WriteString(1, Id);
            if (Jid is not null) w.WriteString(2, Jid);
            if (Error is not null)
            {
                var e = new ProtoWriter();
                e.WriteInt32(1, Error.Code);
                if (Error.Message is not null) e.WriteString(2, Error.Message);
                if (Error.Type is not null) e.WriteString(3, Error.Type);
                w.WriteMessage(3, e);
            }
            foreach (var s in Setting) w.WriteBytes(4, s.Encode());
            if (StreamId is { } streamId) w.WriteInt32(5, streamId);
            if (LastStreamIdReceived is { } last) w.WriteInt32(6, last);
            if (ServerTimestamp is { } ts) w.WriteInt64(8, ts);
            return w.ToArray();
        }

        public static McsLoginResponse Decode(ReadOnlySpan<byte> data)
        {
            var m = new McsLoginResponse();
            var r = new ProtoReader(data);
            while (r.TryReadTag(out var field, out var wt))
            {
                switch (field)
                {
                    case 1 when wt == ProtoWireType.LengthDelimited: m.Id = r.ReadString(); break;
                    case 2 when wt == ProtoWireType.LengthDelimited: m.Jid = r.ReadString(); break;
                    case 3 when wt == ProtoWireType.LengthDelimited: m.Error = McsErrorInfo.Decode(r.ReadBytes()); break;
                    case 4 when wt == ProtoWireType.LengthDelimited: m.Setting.Add(McsSetting.Decode(r.ReadBytes())); break;
                    case 5 when wt == ProtoWireType.Varint: m.StreamId = r.ReadInt32(); break;
                    case 6 when wt == ProtoWireType.Varint: m.LastStreamIdReceived = r.ReadInt32(); break;
                    case 8 when wt == ProtoWireType.Varint: m.ServerTimestamp = r.ReadInt64(); break;
                    default: r.Skip(wt); break;
                }
            }
            return m;
        }
    }

    /// <summary><c>mcs_proto.StreamErrorStanza</c>.</summary>
    internal sealed class McsStreamErrorStanza
    {
        public string Type { get; set; } = "";
        public string? Text { get; set; }

        public static McsStreamErrorStanza Decode(ReadOnlySpan<byte> data)
        {
            var m = new McsStreamErrorStanza();
            var r = new ProtoReader(data);
            while (r.TryReadTag(out var field, out var wt))
            {
                switch (field)
                {
                    case 1 when wt == ProtoWireType.LengthDelimited: m.Type = r.ReadString(); break;
                    case 2 when wt == ProtoWireType.LengthDelimited: m.Text = r.ReadString(); break;
                    default: r.Skip(wt); break;
                }
            }
            return m;
        }
    }

    /// <summary><c>mcs_proto.Close</c> (no fields).</summary>
    internal sealed class McsClose
    {
        public static readonly McsClose Instance = new();
    }

    /// <summary><c>mcs_proto.IqStanza</c> (the fields this port reads; upstream does not act on it either).</summary>
    internal sealed class McsIqStanza
    {
        public long? RmqId { get; set; }
        public int Type { get; set; }
        public string Id { get; set; } = "";
        public string? PersistentId { get; set; }

        public static McsIqStanza Decode(ReadOnlySpan<byte> data)
        {
            var m = new McsIqStanza();
            var r = new ProtoReader(data);
            while (r.TryReadTag(out var field, out var wt))
            {
                switch (field)
                {
                    case 1 when wt == ProtoWireType.Varint: m.RmqId = r.ReadInt64(); break;
                    case 2 when wt == ProtoWireType.Varint: m.Type = r.ReadInt32(); break;
                    case 3 when wt == ProtoWireType.LengthDelimited: m.Id = r.ReadString(); break;
                    case 8 when wt == ProtoWireType.LengthDelimited: m.PersistentId = r.ReadString(); break;
                    default: r.Skip(wt); break;
                }
            }
            return m;
        }
    }

    /// <summary><c>mcs_proto.AppData</c>.</summary>
    internal sealed record McsAppData(string Key, string Value);

    /// <summary><c>mcs_proto.DataMessageStanza</c>.</summary>
    internal sealed class McsDataMessageStanza
    {
        public string? Id { get; set; }
        public string From { get; set; } = "";
        public string? To { get; set; }
        public string Category { get; set; } = "";
        public string? Token { get; set; }
        public List<McsAppData> AppData { get; } = [];
        public bool? FromTrustedServer { get; set; }
        public string? PersistentId { get; set; }
        public int? StreamId { get; set; }
        public int? LastStreamIdReceived { get; set; }
        public string? RegId { get; set; }
        public long? DeviceUserId { get; set; }
        public int? Ttl { get; set; }
        public long? Sent { get; set; }
        public int? Queued { get; set; }
        public long? Status { get; set; }
        public byte[]? RawData { get; set; }
        public bool? ImmediateAck { get; set; }

        public byte[] Encode()
        {
            var w = new ProtoWriter();
            if (Id is not null) w.WriteString(2, Id);
            w.WriteString(3, From);
            if (To is not null) w.WriteString(4, To);
            w.WriteString(5, Category);
            if (Token is not null) w.WriteString(6, Token);
            foreach (var kv in AppData)
            {
                var a = new ProtoWriter();
                a.WriteString(1, kv.Key);
                a.WriteString(2, kv.Value);
                w.WriteMessage(7, a);
            }
            if (FromTrustedServer is { } trusted) w.WriteBool(8, trusted);
            if (PersistentId is not null) w.WriteString(9, PersistentId);
            if (StreamId is { } streamId) w.WriteInt32(10, streamId);
            if (LastStreamIdReceived is { } last) w.WriteInt32(11, last);
            if (RegId is not null) w.WriteString(13, RegId);
            if (DeviceUserId is { } user) w.WriteInt64(16, user);
            if (Ttl is { } ttl) w.WriteInt32(17, ttl);
            if (Sent is { } sent) w.WriteInt64(18, sent);
            if (Queued is { } queued) w.WriteInt32(19, queued);
            if (Status is { } status) w.WriteInt64(20, status);
            if (RawData is not null) w.WriteBytes(21, RawData);
            if (ImmediateAck is { } ack) w.WriteBool(24, ack);
            return w.ToArray();
        }

        public static McsDataMessageStanza Decode(ReadOnlySpan<byte> data)
        {
            var m = new McsDataMessageStanza();
            var r = new ProtoReader(data);
            while (r.TryReadTag(out var field, out var wt))
            {
                switch (field)
                {
                    case 2 when wt == ProtoWireType.LengthDelimited: m.Id = r.ReadString(); break;
                    case 3 when wt == ProtoWireType.LengthDelimited: m.From = r.ReadString(); break;
                    case 4 when wt == ProtoWireType.LengthDelimited: m.To = r.ReadString(); break;
                    case 5 when wt == ProtoWireType.LengthDelimited: m.Category = r.ReadString(); break;
                    case 6 when wt == ProtoWireType.LengthDelimited: m.Token = r.ReadString(); break;
                    case 7 when wt == ProtoWireType.LengthDelimited: m.AppData.Add(DecodeAppData(r.ReadBytes())); break;
                    case 8 when wt == ProtoWireType.Varint: m.FromTrustedServer = r.ReadBool(); break;
                    case 9 when wt == ProtoWireType.LengthDelimited: m.PersistentId = r.ReadString(); break;
                    case 10 when wt == ProtoWireType.Varint: m.StreamId = r.ReadInt32(); break;
                    case 11 when wt == ProtoWireType.Varint: m.LastStreamIdReceived = r.ReadInt32(); break;
                    case 13 when wt == ProtoWireType.LengthDelimited: m.RegId = r.ReadString(); break;
                    case 16 when wt == ProtoWireType.Varint: m.DeviceUserId = r.ReadInt64(); break;
                    case 17 when wt == ProtoWireType.Varint: m.Ttl = r.ReadInt32(); break;
                    case 18 when wt == ProtoWireType.Varint: m.Sent = r.ReadInt64(); break;
                    case 19 when wt == ProtoWireType.Varint: m.Queued = r.ReadInt32(); break;
                    case 20 when wt == ProtoWireType.Varint: m.Status = r.ReadInt64(); break;
                    case 21 when wt == ProtoWireType.LengthDelimited: m.RawData = r.ReadBytes().ToArray(); break;
                    case 24 when wt == ProtoWireType.Varint: m.ImmediateAck = r.ReadBool(); break;
                    default: r.Skip(wt); break;
                }
            }
            return m;
        }

        private static McsAppData DecodeAppData(ReadOnlySpan<byte> data)
        {
            string key = "", value = "";
            var r = new ProtoReader(data);
            while (r.TryReadTag(out var field, out var wt))
            {
                switch (field)
                {
                    case 1 when wt == ProtoWireType.LengthDelimited: key = r.ReadString(); break;
                    case 2 when wt == ProtoWireType.LengthDelimited: value = r.ReadString(); break;
                    default: r.Skip(wt); break;
                }
            }
            return new McsAppData(key, value);
        }
    }

    // ---- GServices check-in (no package) ----

    /// <summary><c>CheckinRequest.Checkin.Build</c> (the fields upstream sets).</summary>
    internal sealed class CheckinBuild
    {
        public string? Fingerprint { get; set; }
        public string? Hardware { get; set; }
        public string? Brand { get; set; }
        public string? Radio { get; set; }
        public string? ClientId { get; set; }

        public ProtoWriter Encode()
        {
            var w = new ProtoWriter();
            if (Fingerprint is not null) w.WriteString(1, Fingerprint);
            if (Hardware is not null) w.WriteString(2, Hardware);
            if (Brand is not null) w.WriteString(3, Brand);
            if (Radio is not null) w.WriteString(4, Radio);
            if (ClientId is not null) w.WriteString(6, ClientId);
            return w;
        }
    }

    /// <summary><c>CheckinRequest</c> (the fields upstream sets).</summary>
    internal sealed class CheckinRequest
    {
        public long? AndroidId { get; set; }
        public CheckinBuild Build { get; set; } = new();
        public long? LastCheckinMs { get; set; }
        public string? Locale { get; set; }
        public long? LoggingId { get; set; }
        public string? TimeZone { get; set; }
        public int? Version { get; set; }
        public int Fragment { get; set; }
        public int? UserSerialNumber { get; set; }

        public byte[] Encode()
        {
            var w = new ProtoWriter();
            if (AndroidId is { } androidId) w.WriteInt64(2, androidId);
            var checkin = new ProtoWriter();
            checkin.WriteMessage(1, Build.Encode());
            if (LastCheckinMs is { } last) checkin.WriteInt64(2, last);
            w.WriteMessage(4, checkin);
            if (Locale is not null) w.WriteString(6, Locale);
            if (LoggingId is { } loggingId) w.WriteInt64(7, loggingId);
            if (TimeZone is not null) w.WriteString(12, TimeZone);
            if (Version is { } version) w.WriteInt32(14, version);
            w.WriteInt32(20, Fragment);
            if (UserSerialNumber is { } serial) w.WriteInt32(22, serial);
            return w.ToArray();
        }
    }

    /// <summary><c>CheckinResponse</c> (the fields upstream reads).</summary>
    internal sealed class CheckinResponse
    {
        public bool? StatsOk { get; set; }
        public long? TimeMs { get; set; }
        public ulong? AndroidId { get; set; }
        public ulong? SecurityToken { get; set; }

        public static CheckinResponse Decode(ReadOnlySpan<byte> data)
        {
            var m = new CheckinResponse();
            var r = new ProtoReader(data);
            while (r.TryReadTag(out var field, out var wt))
            {
                switch (field)
                {
                    case 1 when wt == ProtoWireType.Varint: m.StatsOk = r.ReadBool(); break;
                    case 3 when wt == ProtoWireType.Varint: m.TimeMs = r.ReadInt64(); break;
                    case 7 when wt == ProtoWireType.Fixed64: m.AndroidId = r.ReadFixed64(); break;
                    case 8 when wt == ProtoWireType.Fixed64: m.SecurityToken = r.ReadFixed64(); break;
                    default: r.Skip(wt); break;
                }
            }
            return m;
        }
    }
}
