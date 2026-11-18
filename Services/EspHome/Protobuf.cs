using System.Buffers.Binary;
using System.Text;

namespace RIoT2.Net.Devices.Services.EspHome
{
    /// <summary>The protobuf wire types the ESPHome native API uses.</summary>
    public enum WireType { Varint = 0, Fixed64 = 1, LengthDelimited = 2, Fixed32 = 5 }

    /// <summary>Writes a protobuf message: only the field kinds <c>api.proto</c> needs. Zero values are left out, as proto3 does.</summary>
    public sealed class ProtoWriter
    {
        private readonly MemoryStream _buffer = new();

        public byte[] ToArray() => _buffer.ToArray();

        public ProtoWriter UInt(int field, ulong value)
        {
            if (value == 0)
                return this;
            Tag(field, WireType.Varint);
            Varint(value);
            return this;
        }

        public ProtoWriter Bool(int field, bool value) => UInt(field, value ? 1UL : 0UL);

        public ProtoWriter Fixed32(int field, uint value)
        {
            Tag(field, WireType.Fixed32);
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            _buffer.Write(bytes);
            return this;
        }

        public ProtoWriter Float(int field, float value) =>
            value == 0 ? this : Fixed32(field, BitConverter.SingleToUInt32Bits(value));

        public ProtoWriter String(int field, string? value) =>
            string.IsNullOrEmpty(value) ? this : Bytes(field, Encoding.UTF8.GetBytes(value));

        public ProtoWriter Bytes(int field, ReadOnlySpan<byte> value)
        {
            if (value.IsEmpty)
                return this;
            Tag(field, WireType.LengthDelimited);
            Varint((ulong)value.Length);
            _buffer.Write(value);
            return this;
        }

        /// <summary>An embedded message, written even when empty (an element of a repeated field).</summary>
        public ProtoWriter Message(int field, ProtoWriter message)
        {
            var bytes = message.ToArray();
            Tag(field, WireType.LengthDelimited);
            Varint((ulong)bytes.Length);
            _buffer.Write(bytes);
            return this;
        }

        private void Tag(int field, WireType type) => Varint((ulong)field << 3 | (ulong)type);

        private void Varint(ulong value)
        {
            Span<byte> bytes = stackalloc byte[10];
            _buffer.Write(bytes[..WriteVarint(bytes, value)]);
        }

        /// <summary>A varint on its own, as the plaintext frame header has them. Returns its length.</summary>
        public static int WriteVarint(Span<byte> destination, ulong value)
        {
            var at = 0;
            while (value >= 0x80)
            {
                destination[at++] = (byte)(value | 0x80);
                value >>= 7;
            }
            destination[at++] = (byte)value;
            return at;
        }
    }

    /// <summary>One field of a protobuf message, as <see cref="ProtoReader"/> reads it.</summary>
    public readonly ref struct ProtoField
    {
        public ProtoField(int number, WireType type, ulong value, ReadOnlySpan<byte> data)
        {
            Number = number;
            Type = type;
            Value = value;
            Data = data;
        }

        public int Number { get; }
        public WireType Type { get; }

        /// <summary>A varint's, fixed32's or fixed64's value.</summary>
        public ulong Value { get; }

        /// <summary>A length-delimited field's bytes.</summary>
        public ReadOnlySpan<byte> Data { get; }

        public bool Bool => Type == WireType.Varint && Value != 0;
        public uint UInt32 => Type == WireType.Varint ? (uint)Value : 0;
        public uint Fixed32 => Type == WireType.Fixed32 ? (uint)Value : 0;
        public float Float => Type == WireType.Fixed32 ? BitConverter.UInt32BitsToSingle((uint)Value) : 0;
        public string String => Type == WireType.LengthDelimited ? Encoding.UTF8.GetString(Data) : "";
    }

    /// <summary>
    /// Reads a protobuf message field by field. A message that doesn't parse throws
    /// <see cref="EspHomeProtocolException"/>; unknown fields are the caller's to skip.
    /// </summary>
    public ref struct ProtoReader
    {
        private ReadOnlySpan<byte> _rest;

        public ProtoReader(ReadOnlySpan<byte> message) => _rest = message;

        public bool Next(out ProtoField field)
        {
            if (_rest.IsEmpty)
            {
                field = default;
                return false;
            }
            var tag = ReadVarint(ref _rest);
            if (tag >> 3 is 0 or > int.MaxValue)
                throw new EspHomeProtocolException("A protobuf field number is out of range.");
            var number = (int)(tag >> 3);
            switch ((WireType)(tag & 7))
            {
                case WireType.Varint:
                    field = new ProtoField(number, WireType.Varint, ReadVarint(ref _rest), default);
                    return true;
                case WireType.Fixed32:
                    field = new ProtoField(number, WireType.Fixed32, BinaryPrimitives.ReadUInt32LittleEndian(Take(4)), default);
                    return true;
                case WireType.Fixed64:
                    field = new ProtoField(number, WireType.Fixed64, BinaryPrimitives.ReadUInt64LittleEndian(Take(8)), default);
                    return true;
                case WireType.LengthDelimited:
                    var length = ReadVarint(ref _rest);
                    if (length > (ulong)_rest.Length)
                        throw new EspHomeProtocolException("A protobuf field is longer than its message.");
                    field = new ProtoField(number, WireType.LengthDelimited, 0, Take((int)length));
                    return true;
                default:
                    throw new EspHomeProtocolException($"The protobuf wire type {tag & 7} isn't supported.");
            }
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count > _rest.Length)
                throw new EspHomeProtocolException("A protobuf message ends inside a field.");
            var taken = _rest[..count];
            _rest = _rest[count..];
            return taken;
        }

        /// <summary>A varint from the start of <paramref name="span"/>, which then starts after it.</summary>
        public static ulong ReadVarint(ref ReadOnlySpan<byte> span)
        {
            ulong value = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                if (span.IsEmpty)
                    throw new EspHomeProtocolException("A protobuf message ends inside a varint.");
                var b = span[0];
                span = span[1..];
                value |= (ulong)(b & 0x7f) << shift;
                if ((b & 0x80) == 0)
                    return value;
            }
            throw new EspHomeProtocolException("A protobuf varint is too long.");
        }
    }

    /// <summary>The satellite broke the ESPHome native API.</summary>
    public class EspHomeProtocolException(string message) : IOException(message);

    /// <summary>The satellite refused the encryption key.</summary>
    public sealed class EspHomeKeyException(string message) : EspHomeProtocolException(message);
}
