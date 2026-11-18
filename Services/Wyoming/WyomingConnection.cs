using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RIoT2.Net.Devices.Services.Wyoming
{
    /// <summary>A Wyoming event: its type, its data (a JSON object) and its binary payload (audio).</summary>
    public sealed class WyomingEvent
    {
        public WyomingEvent(string type, JsonObject? data = null, byte[]? payload = null)
        {
            Type = type;
            Data = data ?? [];
            Payload = payload ?? [];
        }

        public string Type { get; }
        public JsonObject Data { get; }
        public byte[] Payload { get; }

        public string? GetString(string name) =>
            Data[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

        public int? GetInt(string name) =>
            Data[name] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<int>(out var number) ? number : null;

        /// <summary>The <c>audio-start</c> or <c>audio-chunk</c> data for 16-bit PCM.</summary>
        public static JsonObject AudioFormat(int rate, int channels) =>
            new() { ["rate"] = rate, ["width"] = 2, ["channels"] = channels };
    }

    /// <summary>The other side broke the Wyoming protocol.</summary>
    public sealed class WyomingProtocolException(string message) : Exception(message);

    /// <summary>
    /// The Wyoming protocol over a stream (https://github.com/OHF-Voice/wyoming): a JSON header line with <c>type</c>,
    /// <c>data_length</c> and <c>payload_length</c>, then that many bytes of JSON data and of binary payload. One caller
    /// reads; several may write, one event at a time.
    /// </summary>
    public sealed class WyomingConnection : IAsyncDisposable
    {
        public const string Version = "1.5.4";

        /// <summary>The longest header line or data block taken.</summary>
        public const int MaxJsonLength = 64 * 1024;

        /// <summary>The largest payload taken. hassmic takes up to 1 MiB, so writes stay under it too.</summary>
        public const int MaxPayloadLength = 1 << 20;

        // Non-ASCII stays unescaped: hassmic's reader doesn't decode \u escapes, so "ä" must arrive as UTF-8.
        private static readonly JsonSerializerOptions DataOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private readonly Stream _stream;
        private readonly IDisposable? _owner;
        private readonly SemaphoreSlim _write = new(1, 1);
        private readonly byte[] _buffer = new byte[16 * 1024];
        private int _start;
        private int _end;
        private int _disposed;

        /// <param name="stream">The connection.</param>
        /// <param name="owner">Disposed with the connection, for example the stream's <c>TcpClient</c>.</param>
        public WyomingConnection(Stream stream, IDisposable? owner = null)
        {
            _stream = stream;
            _owner = owner;
        }

        /// <summary>The next event, or null when the other side closed the connection between events.</summary>
        public async Task<WyomingEvent?> ReadAsync(CancellationToken cancellationToken)
        {
            var header = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (header == null)
                return null;

            var head = ParseObject(header, "header");
            var type = head["type"] is JsonValue t && t.GetValueKind() == JsonValueKind.String ? t.GetValue<string>() : "";
            if (type.Length == 0)
                throw new WyomingProtocolException("A Wyoming header has no type.");
            var dataLength = Length(head, "data_length", MaxJsonLength);
            var payloadLength = Length(head, "payload_length", MaxPayloadLength);

            // Before version 1.0 the data came in the header; a data block takes its place.
            var data = head["data"] as JsonObject;
            head.Remove("data");
            if (dataLength > 0)
                data = ParseObject(Encoding.UTF8.GetString(await ReadExactlyAsync(dataLength, cancellationToken).ConfigureAwait(false)), "data");
            var payload = payloadLength > 0 ? await ReadExactlyAsync(payloadLength, cancellationToken).ConfigureAwait(false) : null;
            return new WyomingEvent(type, data, payload);
        }

        public async Task WriteAsync(WyomingEvent message, CancellationToken cancellationToken)
        {
            if (message.Payload.Length > MaxPayloadLength)
                throw new ArgumentException($"A Wyoming payload must be at most {MaxPayloadLength} bytes.", nameof(message));
            var data = message.Data.Count > 0 ? JsonSerializer.SerializeToUtf8Bytes(message.Data, DataOptions) : [];
            var header = new JsonObject { ["type"] = message.Type, ["version"] = Version };
            if (data.Length > 0)
                header["data_length"] = data.Length;
            if (message.Payload.Length > 0)
                header["payload_length"] = message.Payload.Length;
            var head = Encoding.UTF8.GetBytes(header.ToJsonString(DataOptions) + "\n");

            // One write per event, so a cancelled write never leaves half an event between two whole ones.
            var frame = new byte[head.Length + data.Length + message.Payload.Length];
            head.CopyTo(frame, 0);
            data.CopyTo(frame, head.Length);
            message.Payload.CopyTo(frame, head.Length + data.Length);

            await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _write.Release();
            }
        }

        public Task WriteAsync(string type, JsonObject? data, CancellationToken cancellationToken) =>
            WriteAsync(new WyomingEvent(type, data), cancellationToken);

        private static JsonObject ParseObject(string json, string what)
        {
            try
            {
                return JsonNode.Parse(json) as JsonObject ?? throw new WyomingProtocolException($"A Wyoming {what} is not a JSON object.");
            }
            catch (JsonException)
            {
                throw new WyomingProtocolException($"A Wyoming {what} is not valid JSON.");
            }
        }

        private static int Length(JsonObject head, string name, int max)
        {
            var node = head[name];
            if (node == null || node.GetValueKind() == JsonValueKind.Null)
                return 0;
            if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number || !value.TryGetValue<long>(out var length) || length < 0)
                throw new WyomingProtocolException($"The Wyoming {name} is not a length.");
            if (length > max)
                throw new WyomingProtocolException($"The Wyoming {name} {length} is over {max}.");
            return (int)length;
        }

        private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var line = new ArrayBufferWriter<byte>();
            while (true)
            {
                if (_start == _end && !await FillAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (line.WrittenCount == 0)
                        return null;
                    throw new WyomingProtocolException("The Wyoming connection closed in a header.");
                }
                var available = _buffer.AsSpan(_start, _end - _start);
                var newline = available.IndexOf((byte)'\n');
                var take = newline >= 0 ? newline : available.Length;
                if (line.WrittenCount + take > MaxJsonLength)
                    throw new WyomingProtocolException($"A Wyoming header is longer than {MaxJsonLength} bytes.");
                line.Write(available[..take]);
                _start += newline >= 0 ? take + 1 : take;
                if (newline >= 0)
                    return Encoding.UTF8.GetString(line.WrittenSpan).TrimEnd('\r');
            }
        }

        private async Task<byte[]> ReadExactlyAsync(int length, CancellationToken cancellationToken)
        {
            var result = new byte[length];
            var done = Math.Min(length, _end - _start);
            _buffer.AsSpan(_start, done).CopyTo(result);
            _start += done;
            while (done < length)
            {
                var read = await _stream.ReadAsync(result.AsMemory(done), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    throw new WyomingProtocolException("The Wyoming connection closed inside an event.");
                done += read;
            }
            return result;
        }

        private async Task<bool> FillAsync(CancellationToken cancellationToken)
        {
            _start = 0;
            _end = await _stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
            return _end > 0;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            await _stream.DisposeAsync().ConfigureAwait(false);
            // _write isn't disposed: a writer still inside it must be able to release it.
            _owner?.Dispose();
        }
    }
}
