using System.Text;

namespace RIoT2.Net.Devices.Services.EspHome
{
    /// <summary>One message of the ESPHome native API: its type (the <c>id</c> option in <c>api.proto</c>) and its protobuf.</summary>
    public sealed record EspHomeMessage(int Type, byte[] Payload);

    /// <summary>
    /// The ESPHome native API's framing over a stream, client side. Plaintext: <c>0x00</c>, the length and the type as
    /// varints, the protobuf. Noise: <c>0x01</c>, a big-endian 16-bit length, the frame; the client's hello and handshake
    /// message, the server's hello (its name and MAC) and answer, then encrypted <c>type, length, protobuf</c> frames.
    /// One caller reads; several may write, one message at a time.
    /// </summary>
    public sealed class EspHomeConnection : IAsyncDisposable
    {
        /// <summary>The largest plaintext message taken.</summary>
        public const int MaxPayloadLength = 1 << 20;

        /// <summary>The largest Noise frame.</summary>
        public const int MaxFrameLength = ushort.MaxValue;

        // What hassmic and ESPHome answer a handshake with the wrong key; aioesphomeapi names it an invalid key too.
        internal const string MacFailure = "Handshake MAC failure";

        private readonly Stream _stream;
        private readonly IDisposable? _owner;
        private readonly SemaphoreSlim _write = new(1, 1);
        private readonly byte[] _buffer = new byte[16 * 1024];
        private NoiseCipher? _send;
        private NoiseCipher? _receive;
        private int _start;
        private int _end;
        private int _disposed;

        private EspHomeConnection(Stream stream, IDisposable? owner)
        {
            _stream = stream;
            _owner = owner;
        }

        /// <summary>Whether the connection is encrypted.</summary>
        public bool Encrypted => _send != null;

        /// <summary>The name the satellite gave in its Noise hello; empty in plaintext.</summary>
        public string ServerName { get; private set; } = "";

        /// <summary>
        /// Opens the connection: plaintext without a key, otherwise the Noise handshake with it. The connection (and
        /// <paramref name="owner"/>) is disposed if the handshake fails.
        /// </summary>
        /// <param name="stream">The connection.</param>
        /// <param name="owner">Disposed with the connection, for example the stream's <c>TcpClient</c>.</param>
        /// <param name="psk">The 32-byte encryption key; all zeros for a satellite without one; null for plaintext.</param>
        /// <exception cref="EspHomeKeyException">The satellite has another key.</exception>
        public static async Task<EspHomeConnection> OpenAsync(Stream stream, IDisposable? owner, byte[]? psk, CancellationToken cancellationToken, byte[]? ephemeralPrivateKey = null)
        {
            var connection = new EspHomeConnection(stream, owner);
            if (psk == null)
                return connection;
            try
            {
                await connection.HandshakeAsync(new NoiseHandshake(psk, ephemeralPrivateKey), cancellationToken).ConfigureAwait(false);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        private async Task HandshakeAsync(NoiseHandshake handshake, CancellationToken cancellationToken)
        {
            var message1 = handshake.WriteMessage1();
            // The empty client hello, then the handshake frame (0x00: no error), in one write.
            var hello = new byte[3 + 3 + 1 + message1.Length];
            hello[0] = 1;
            hello[3] = 1;
            hello[4] = (byte)((message1.Length + 1) >> 8);
            hello[5] = (byte)(message1.Length + 1);
            message1.CopyTo(hello, 7);
            await _stream.WriteAsync(hello, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            var serverHello = await ReadFrameAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new EspHomeProtocolException("The satellite closed the connection at the encryption handshake; it may not use encryption: leave the encryptionKey empty.");
            if (serverHello.Length == 0 || serverHello[0] != 1)
                throw new EspHomeProtocolException("The satellite chose an encryption this device doesn't know.");
            var nameEnd = Array.IndexOf(serverHello, (byte)0, 1);
            if (nameEnd > 0)
                ServerName = Encoding.UTF8.GetString(serverHello, 1, Math.Min(nameEnd - 1, 120));

            var answer = await ReadFrameAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new EspHomeProtocolException("The satellite closed the connection during the encryption handshake.");
            if (answer.Length == 0)
                throw new EspHomeProtocolException("The satellite's handshake answer is empty.");
            if (answer[0] != 0)
            {
                var reason = Encoding.UTF8.GetString(answer, 1, Math.Min(answer.Length - 1, 200));
                if (reason == MacFailure)
                    throw new EspHomeKeyException("The satellite refused the encryption key.");
                throw new EspHomeProtocolException($"The satellite refused the encryption handshake: {Printable(reason)}");
            }
            (_send, _receive) = handshake.ReadMessage2(answer.AsSpan(1));
        }

        /// <summary>The next message, or null when the satellite closed the connection between messages.</summary>
        public async Task<EspHomeMessage?> ReadAsync(CancellationToken cancellationToken)
        {
            if (_receive != null)
            {
                var frame = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
                if (frame == null)
                    return null;
                var plain = _receive.Decrypt(frame);
                if (plain.Length < 4)
                    throw new EspHomeProtocolException("An encrypted message is shorter than its header.");
                // The length inside isn't needed: the frame has it.
                return new EspHomeMessage(plain[0] << 8 | plain[1], plain[4..]);
            }

            if (!await FillAsync(1, cancellationToken).ConfigureAwait(false))
                return null;
            var marker = _buffer[_start++];
            if (marker == 1)
                throw new EspHomeProtocolException("The satellite uses encryption: set the device's encryptionKey.");
            if (marker != 0)
                throw new EspHomeProtocolException($"The satellite sent the frame marker {marker}, not 0.");
            var length = await ReadVarintAsync(cancellationToken).ConfigureAwait(false);
            var type = await ReadVarintAsync(cancellationToken).ConfigureAwait(false);
            if (length > MaxPayloadLength)
                throw new EspHomeProtocolException($"A message is longer than {MaxPayloadLength} bytes.");
            if (type > ushort.MaxValue)
                throw new EspHomeProtocolException("A message type is out of range.");
            return new EspHomeMessage((int)type, await ReadExactAsync((int)length, cancellationToken).ConfigureAwait(false));
        }

        public Task WriteAsync(int type, ProtoWriter? message, CancellationToken cancellationToken) =>
            WriteAsync(type, message?.ToArray() ?? [], cancellationToken);

        /// <summary>Writes one message in one write, so that several writers never interleave.</summary>
        public async Task WriteAsync(int type, byte[] payload, CancellationToken cancellationToken)
        {
            if (type is < 0 or > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(type));
            await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                byte[] frame;
                if (_send != null)
                {
                    if (payload.Length > MaxFrameLength - 4 - NoiseHandshake.TagLength)
                        throw new ArgumentException("The message is too long for one encrypted frame.", nameof(payload));
                    var plain = new byte[4 + payload.Length];
                    plain[0] = (byte)(type >> 8);
                    plain[1] = (byte)type;
                    plain[2] = (byte)(payload.Length >> 8);
                    plain[3] = (byte)payload.Length;
                    payload.CopyTo(plain, 4);
                    var cipher = _send.Encrypt(plain);
                    frame = new byte[3 + cipher.Length];
                    frame[0] = 1;
                    frame[1] = (byte)(cipher.Length >> 8);
                    frame[2] = (byte)cipher.Length;
                    cipher.CopyTo(frame, 3);
                }
                else
                {
                    if (payload.Length > MaxPayloadLength)
                        throw new ArgumentException($"The message is longer than {MaxPayloadLength} bytes.", nameof(payload));
                    Span<byte> header = stackalloc byte[1 + 10 + 10];
                    var at = 1;
                    at += ProtoWriter.WriteVarint(header[at..], (ulong)payload.Length);
                    at += ProtoWriter.WriteVarint(header[at..], (ulong)type);
                    frame = new byte[at + payload.Length];
                    header[..at].CopyTo(frame);
                    payload.CopyTo(frame, at);
                }
                await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _write.Release();
            }
        }

        // A Noise frame's body, or null when the connection closed before it.
        private async Task<byte[]?> ReadFrameAsync(CancellationToken cancellationToken)
        {
            if (!await FillAsync(1, cancellationToken).ConfigureAwait(false))
                return null;
            var header = await ReadExactAsync(3, cancellationToken).ConfigureAwait(false);
            if (header[0] == 0)
                throw new EspHomeProtocolException("The satellite answered in plaintext: it doesn't use encryption, so leave the encryptionKey empty.");
            if (header[0] != 1)
                throw new EspHomeProtocolException($"The satellite sent the frame marker {header[0]}, not 1.");
            return await ReadExactAsync(header[1] << 8 | header[2], cancellationToken).ConfigureAwait(false);
        }

        private async Task<ulong> ReadVarintAsync(CancellationToken cancellationToken)
        {
            ulong value = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                if (!await FillAsync(1, cancellationToken).ConfigureAwait(false))
                    throw new EndOfStreamException("The satellite closed the connection inside a message.");
                var b = _buffer[_start++];
                value |= (ulong)(b & 0x7f) << shift;
                if ((b & 0x80) == 0)
                    return value;
            }
            throw new EspHomeProtocolException("A frame's varint is too long.");
        }

        private async Task<byte[]> ReadExactAsync(int count, CancellationToken cancellationToken)
        {
            var result = new byte[count];
            var have = 0;
            while (have < count)
            {
                if (_start == _end && !await FillAsync(1, cancellationToken).ConfigureAwait(false))
                    throw new EndOfStreamException("The satellite closed the connection inside a message.");
                var take = Math.Min(count - have, _end - _start);
                Buffer.BlockCopy(_buffer, _start, result, have, take);
                _start += take;
                have += take;
            }
            return result;
        }

        // At least `count` (at most the buffer's size) bytes buffered; false when the connection closed first.
        private async Task<bool> FillAsync(int count, CancellationToken cancellationToken)
        {
            while (_end - _start < count)
            {
                if (_start > 0)
                {
                    Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                    _end -= _start;
                    _start = 0;
                }
                var read = await _stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    if (_end - _start == 0)
                        return false;
                    throw new EndOfStreamException("The satellite closed the connection inside a message.");
                }
                _end += read;
            }
            return true;
        }

        private static string Printable(string text) =>
            new(text.Where(c => !char.IsControl(c)).Take(120).ToArray());

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            await _stream.DisposeAsync().ConfigureAwait(false);
            // _write isn't disposed: a writer still inside it must be able to release it.
            _owner?.Dispose();
            _send?.Dispose();
            _receive?.Dispose();
        }
    }
}
