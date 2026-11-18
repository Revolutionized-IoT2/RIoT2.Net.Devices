using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RIoT2.Net.Devices.Services.GoogleCast;
using RIoT2.Net.Devices.Services.Wyoming;

namespace RIoT2.Net.Devices.Services.Voice
{
    /// <summary>Speech to text.</summary>
    public interface ISpeechToText
    {
        /// <summary>The words in <paramref name="audio"/>; empty when there were none.</summary>
        Task<string> TranscribeAsync(PcmAudio audio, string language, CancellationToken cancellationToken);
    }

    /// <summary>Text to speech.</summary>
    public interface ITextToSpeech
    {
        Task<PcmAudio> SynthesizeAsync(string text, string language, CancellationToken cancellationToken);
    }

    /// <summary>A speech engine failed; the message never contains a key.</summary>
    public sealed class SpeechEngineException(string message) : Exception(message);

    /// <summary>Connects to a Wyoming server.</summary>
    public delegate Task<WyomingConnection> WyomingConnector(string host, int port, CancellationToken cancellationToken);

    /// <summary>Sends an HTTP request.</summary>
    public delegate Task<HttpResponseMessage> HttpSender(HttpRequestMessage request, CancellationToken cancellationToken);

    /// <summary>The engines' shared helpers.</summary>
    public static class SpeechEngines
    {
        /// <summary>The longest speech taken from an engine.</summary>
        public static readonly TimeSpan MaxSpeech = TimeSpan.FromMinutes(2);

        // Audio goes to a Wyoming server in chunks of a tenth of a second.
        internal const int ChunkMs = 100;

        /// <summary>The language without its region: <c>fi-FI</c> is <c>fi</c>, as Whisper wants it.</summary>
        public static string BaseLanguage(string language) => language.Split('-', 2)[0].ToLowerInvariant();

        internal static IEnumerable<byte[]> Chunks(PcmAudio audio)
        {
            var size = Math.Max(2 * audio.Channels, audio.Rate * audio.Channels * 2 * ChunkMs / 1000);
            for (var at = 0; at < audio.Data.Length; at += size)
                yield return audio.Data.AsSpan(at, Math.Min(size, audio.Data.Length - at)).ToArray();
        }

        internal static async Task<string> ReadErrorAsync(HttpResponseMessage response, string engine, CancellationToken cancellationToken)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            string? reason = null;
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var error))
                {
                    reason = error.ValueKind == JsonValueKind.String ? error.GetString()
                        : error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String ? message.GetString()
                        : null;
                }
                else if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                {
                    reason = detail.GetString();
                }
            }
            catch (JsonException) { }
            return $"{engine} refused the request ({(int)response.StatusCode}): {reason ?? "no reason given"}";
        }

        internal static void Authorize(HttpRequestMessage request, string apiKey)
        {
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }

        internal static PcmAudio Check(PcmAudio audio, string engine)
        {
            if (audio.Data.Length == 0)
                throw new SpeechEngineException($"{engine} answered without audio.");
            if (audio.Rate is < 8000 or > 48000 || audio.Channels is < 1 or > 2)
                throw new SpeechEngineException($"{engine} answered with audio the device doesn't play ({audio.Rate} Hz, {audio.Channels} channels).");
            return audio;
        }
    }

    /// <summary>Speech to text from a Wyoming server, such as wyoming-faster-whisper.</summary>
    public sealed class WyomingSpeechToText(string host, int port, WyomingConnector connect) : ISpeechToText
    {
        public async Task<string> TranscribeAsync(PcmAudio audio, string language, CancellationToken cancellationToken)
        {
            await using var connection = await connect(host, port, cancellationToken).ConfigureAwait(false);
            await connection.WriteAsync("transcribe", new JsonObject { ["language"] = SpeechEngines.BaseLanguage(language) }, cancellationToken).ConfigureAwait(false);
            await connection.WriteAsync("audio-start", WyomingEvent.AudioFormat(audio.Rate, audio.Channels), cancellationToken).ConfigureAwait(false);
            foreach (var chunk in SpeechEngines.Chunks(audio))
                await connection.WriteAsync(new WyomingEvent("audio-chunk", WyomingEvent.AudioFormat(audio.Rate, audio.Channels), chunk), cancellationToken).ConfigureAwait(false);
            await connection.WriteAsync("audio-stop", null, cancellationToken).ConfigureAwait(false);

            while (await connection.ReadAsync(cancellationToken).ConfigureAwait(false) is { } message)
            {
                if (message.Type == "transcript")
                    return (message.GetString("text") ?? "").Trim();
                if (message.Type == "error")
                    throw new SpeechEngineException($"The Wyoming speech-to-text server failed: {message.GetString("text") ?? "no reason given"}");
            }
            throw new SpeechEngineException("The Wyoming speech-to-text server closed the connection without a transcript.");
        }
    }

    /// <summary>Text to speech from a Wyoming server, such as wyoming-piper.</summary>
    public sealed class WyomingTextToSpeech(string host, int port, string voice, WyomingConnector connect) : ITextToSpeech
    {
        public async Task<PcmAudio> SynthesizeAsync(string text, string language, CancellationToken cancellationToken)
        {
            await using var connection = await connect(host, port, cancellationToken).ConfigureAwait(false);
            var data = new JsonObject { ["text"] = text };
            if (voice.Length > 0)
                data["voice"] = new JsonObject { ["name"] = voice };
            await connection.WriteAsync("synthesize", data, cancellationToken).ConfigureAwait(false);

            int? rate = null, channels = null;
            using var audio = new MemoryStream();
            while (await connection.ReadAsync(cancellationToken).ConfigureAwait(false) is { } message)
            {
                switch (message.Type)
                {
                    case "audio-start":
                    case "audio-chunk":
                        if (message.GetInt("width") is int width && width != 2)
                            throw new SpeechEngineException($"The Wyoming text-to-speech server sent {8 * width}-bit audio; only 16-bit is played.");
                        rate ??= message.GetInt("rate");
                        channels ??= message.GetInt("channels");
                        if (message.Payload.Length > 0)
                        {
                            audio.Write(message.Payload);
                            if (rate is int r && channels is int c && audio.Length > SpeechEngines.MaxSpeech.TotalSeconds * r * c * 2)
                                throw new SpeechEngineException($"The speech is longer than {SpeechEngines.MaxSpeech.TotalMinutes} minutes.");
                        }
                        break;
                    case "audio-stop":
                        return SpeechEngines.Check(new PcmAudio(rate ?? 0, channels ?? 1, audio.ToArray()), "The Wyoming text-to-speech server");
                    case "error":
                        throw new SpeechEngineException($"The Wyoming text-to-speech server failed: {message.GetString("text") ?? "no reason given"}");
                }
            }
            throw new SpeechEngineException("The Wyoming text-to-speech server closed the connection before the speech ended.");
        }
    }

    /// <summary>
    /// Speech to text from an OpenAI-compatible server's <c>/audio/transcriptions</c>: OpenAI, or a local server such as
    /// Speaches or LocalAI.
    /// </summary>
    public sealed class OpenAiSpeechToText(string baseUrl, string model, string apiKey, HttpSender send) : ISpeechToText
    {
        public const string Engine = "The speech-to-text server";

        public async Task<string> TranscribeAsync(PcmAudio audio, string language, CancellationToken cancellationToken)
        {
            var file = new ByteArrayContent(Wav.Encode(audio));
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            using var form = new MultipartFormDataContent
            {
                { file, "file", "request.wav" },
                { new StringContent(model), "model" },
                { new StringContent(SpeechEngines.BaseLanguage(language)), "language" },
                { new StringContent("json"), "response_format" }
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/audio/transcriptions") { Content = form };
            SpeechEngines.Authorize(request, apiKey);
            using var response = await send(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new SpeechEngineException(await SpeechEngines.ReadErrorAsync(response, Engine, cancellationToken).ConfigureAwait(false));

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    return text.GetString()!.Trim();
            }
            catch (JsonException) { }
            throw new SpeechEngineException($"{Engine} answered without a text.");
        }
    }

    /// <summary>Text to speech from an OpenAI-compatible server's <c>/audio/speech</c>, as a WAV file.</summary>
    public sealed class OpenAiTextToSpeech(string baseUrl, string model, string voice, string apiKey, HttpSender send) : ITextToSpeech
    {
        public const string Engine = "The text-to-speech server";

        public async Task<PcmAudio> SynthesizeAsync(string text, string language, CancellationToken cancellationToken)
        {
            var body = new JsonObject { ["model"] = model, ["input"] = text, ["voice"] = voice, ["response_format"] = "wav" };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/audio/speech")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            SpeechEngines.Authorize(request, apiKey);
            using var response = await send(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new SpeechEngineException(await SpeechEngines.ReadErrorAsync(response, Engine, cancellationToken).ConfigureAwait(false));
            var wav = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            try { return SpeechEngines.Check(Wav.Decode(wav), Engine); }
            catch (FormatException error) { throw new SpeechEngineException($"{Engine} answered with audio that isn't 16-bit WAV: {error.Message}"); }
        }
    }

    /// <summary>Text to speech from Google Cloud Text-to-Speech, as 16-bit PCM (<c>LINEAR16</c>).</summary>
    public sealed class GoogleCloudTextToSpeech(string voice, string apiKey, HttpSender send) : ITextToSpeech
    {
        public const string Engine = "Google Cloud Text-to-Speech";

        public async Task<PcmAudio> SynthesizeAsync(string text, string language, CancellationToken cancellationToken)
        {
            using var request = CastSpeech.CloudRequest(text, language, voice, apiKey, "LINEAR16");
            using var response = await send(request, cancellationToken).ConfigureAwait(false);
            byte[] wav;
            try { wav = await CastSpeech.CloudAudioAsync(response, cancellationToken).ConfigureAwait(false); }
            catch (CastSpeechException error) { throw new SpeechEngineException(error.Message); }
            try { return SpeechEngines.Check(Wav.Decode(wav), Engine); }
            catch (FormatException error) { throw new SpeechEngineException($"{Engine} answered with audio that isn't 16-bit WAV: {error.Message}"); }
        }
    }
}
