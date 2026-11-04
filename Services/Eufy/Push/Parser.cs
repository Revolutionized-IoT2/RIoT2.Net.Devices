// Ported from eufy-sdk src/transport/push/parser.ts @ 5f92bea5 (Apache-2.0), modified for RIoT2.

namespace RIoT2.Net.Devices.Services.Eufy.Push
{
    /// <summary>
    /// MCS wire-stream parser. The TLS stream is framed as:
    /// first packet <c>[version:1][tag:1][varint length][protobuf]</c>, then each message
    /// <c>[tag:1][varint length][protobuf]</c>. Chunks arrive arbitrarily split, so bytes are
    /// accumulated and parsing re-enters as data lands.
    /// </summary>
    internal sealed class McsParser
    {
        /// <summary>The oldest MCS protocol version accepted from the server.</summary>
        public const int MinVersion = 38;

        // Not upstream: a bound so a corrupt length cannot make the parser buffer without limit.
        private const int MaxMessageSize = 16 * 1024 * 1024;

        private enum State
        {
            VersionTagAndSize = 0,
            TagAndSize = 1,
            Size = 2,
            ProtoBytes = 3,
        }

        private byte[] _data = new byte[4096];
        private int _start;
        private int _end;
        private State _state = State.VersionTagAndSize;
        private int _messageTag;
        private int _messageSize;

        private int Available => _end - _start;

        /// <summary>Reset for a fresh connection.</summary>
        public void Reset()
        {
            _start = _end = 0;
            _state = State.VersionTagAndSize;
            _messageTag = 0;
            _messageSize = 0;
        }

        /// <summary>Feed a chunk of TLS bytes; returns the messages it completed, in order.</summary>
        /// <exception cref="InvalidDataException">The stream is not MCS (wrong version, corrupt frame or protobuf).</exception>
        public IReadOnlyList<McsMessage> HandleData(ReadOnlySpan<byte> chunk)
        {
            Append(chunk);
            var messages = new List<McsMessage>();
            while (TryStep(messages))
            {
            }
            return messages;
        }

        private void Append(ReadOnlySpan<byte> chunk)
        {
            if (_end + chunk.Length > _data.Length)
            {
                var needed = Available + chunk.Length;
                if (needed > _data.Length)
                {
                    var bigger = new byte[Math.Max(needed, _data.Length * 2)];
                    Buffer.BlockCopy(_data, _start, bigger, 0, Available);
                    _data = bigger;
                }
                else
                {
                    Buffer.BlockCopy(_data, _start, _data, 0, Available);
                }
                _end = Available;
                _start = 0;
            }
            chunk.CopyTo(_data.AsSpan(_end));
            _end += chunk.Length;
        }

        private bool TryStep(List<McsMessage> messages)
        {
            switch (_state)
            {
                case State.VersionTagAndSize:
                    if (Available < 1 + 1 + 1) return false;
                    var version = (sbyte)_data[_start];
                    if (version < MinVersion) throw new InvalidDataException($"MCS: wrong protocol version {version}");
                    _start++;
                    _state = State.TagAndSize;
                    return true;
                case State.TagAndSize:
                    if (Available < 1 + 1) return false;
                    _messageTag = (sbyte)_data[_start];
                    _start++;
                    _state = State.Size;
                    return true;
                case State.Size:
                    if (!TryReadSize(out var size)) return false;
                    _messageSize = size;
                    _state = State.ProtoBytes;
                    return true;
                case State.ProtoBytes:
                    if (Available < _messageSize) return false;
                    var body = _data.AsSpan(_start, _messageSize);
                    messages.Add(new McsMessage(_messageTag, Decode(_messageTag, body)));
                    _start += _messageSize;
                    if (_start == _end) _start = _end = 0;
                    _state = State.TagAndSize;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Reads the varint length; false while its bytes have not all arrived.</summary>
        private bool TryReadSize(out int size)
        {
            ulong value = 0;
            for (var i = 0; i < 10; i++)
            {
                if (i >= Available)
                {
                    size = 0;
                    return false;
                }
                var b = _data[_start + i];
                value |= (ulong)(b & 0x7F) << (7 * i);
                if ((b & 0x80) == 0)
                {
                    // protobuf int32 semantics, as upstream's reader.int32().
                    var length = unchecked((int)value);
                    if (length < 0 || length > MaxMessageSize) throw new InvalidDataException($"MCS: invalid message size {length}");
                    _start += i + 1;
                    size = length;
                    return true;
                }
            }
            throw new InvalidDataException("MCS: malformed message size");
        }

        /// <summary>Decodes the protobuf of the tags upstream maps to a type; other tags carry no object.</summary>
        internal static object? Decode(int tag, ReadOnlySpan<byte> body) => (McsMessageTag)tag switch
        {
            McsMessageTag.HeartbeatPing or McsMessageTag.HeartbeatAck => McsHeartbeat.Decode(body),
            McsMessageTag.LoginRequest => McsLoginRequest.Decode(body),
            McsMessageTag.LoginResponse => McsLoginResponse.Decode(body),
            McsMessageTag.Close => McsClose.Instance,
            McsMessageTag.IqStanza => McsIqStanza.Decode(body),
            McsMessageTag.DataMessageStanza => McsDataMessageStanza.Decode(body),
            McsMessageTag.StreamErrorStanza => McsStreamErrorStanza.Decode(body),
            _ => null,
        };
    }
}
