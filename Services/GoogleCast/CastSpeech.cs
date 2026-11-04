using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RIoT2.Net.Devices.Services.GoogleCast
{
    /// <summary>Turns text into audio a Cast speaker can play: Google Translate's speech URLs or Google Cloud Text-to-Speech.</summary>
    public static class CastSpeech
    {
        /// <summary>The longest text Google Translate speaks in one request.</summary>
        public const int TranslateChunkLength = 200;

        public const string TranslateEndpoint = "https://translate.google.com/translate_tts";
        public const string CloudEndpoint = "https://texttospeech.googleapis.com/v1/text:synthesize";

        /// <summary>
        /// The Google Translate speech URLs for <paramref name="text"/>: one per chunk of at most
        /// <see cref="TranslateChunkLength"/> characters. The speaker downloads them from Google itself.
        /// </summary>
        public static IReadOnlyList<string> TranslateUrls(string text, string language) =>
            Chunks(text, TranslateChunkLength)
                .Select(chunk => $"{TranslateEndpoint}?ie=UTF-8&client=tw-ob&tl={Uri.EscapeDataString(language)}&q={Uri.EscapeDataString(chunk)}")
                .ToList();

        /// <summary>
        /// Splits <paramref name="text"/> into chunks of at most <paramref name="length"/> characters: after a sentence if
        /// one fits, else after a word, else in the middle of a word that is too long.
        /// </summary>
        public static IReadOnlyList<string> Chunks(string text, int length)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
            var chunks = new List<string>();
            var rest = Normalize(text);
            while (rest.Length > length)
            {
                var window = rest[..(length + 1)];
                var cut = LastBreak(window, ".!?;", length);
                if (cut <= 0)
                    cut = LastBreak(window, ",:", length);
                if (cut <= 0)
                    cut = window.LastIndexOf(' ');
                if (cut <= 0)
                    cut = length;
                chunks.Add(rest[..cut].Trim());
                rest = rest[cut..].Trim();
            }
            if (rest.Length > 0)
                chunks.Add(rest);
            return chunks;
        }

        /// <summary>The text on one line, with single spaces.</summary>
        public static string Normalize(string text) =>
            string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        // After the last punctuation mark followed by a space (or at the window's end), so the mark stays in the chunk.
        private static int LastBreak(string window, string marks, int length)
        {
            for (var i = Math.Min(window.Length - 1, length) - 1; i > 0; i--)
            {
                if (marks.Contains(window[i]) && (i + 1 >= window.Length || window[i + 1] == ' '))
                    return i + 1;
            }
            return -1;
        }

        /// <summary>The cache key of a speech: the same text, language, voice and engine give the same audio.</summary>
        public static string Key(string engine, string language, string voice, string text) =>
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{engine}\n{language}\n{voice}\n{text}")));

        /// <summary>The Google Cloud Text-to-Speech request for MP3 audio. The API key goes in a header, not in the URL.</summary>
        public static HttpRequestMessage CloudRequest(string text, string language, string? voice, string apiKey)
        {
            var body = new CloudSynthesizeRequest
            {
                Input = new CloudInput { Text = text },
                Voice = new CloudVoice { LanguageCode = language, Name = string.IsNullOrWhiteSpace(voice) ? null : voice.Trim() },
                AudioConfig = new CloudAudioConfig { AudioEncoding = "MP3" }
            };
            var request = new HttpRequestMessage(HttpMethod.Post, CloudEndpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("X-Goog-Api-Key", apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return request;
        }

        /// <summary>The MP3 audio of a Google Cloud Text-to-Speech answer; throws with Google's reason when it failed.</summary>
        public static async Task<byte[]> CloudAudioAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new CastSpeechException($"Google Cloud Text-to-Speech refused the request ({(int)response.StatusCode}): {CloudError(json)}");

            CloudSynthesizeResponse? answer;
            try { answer = JsonSerializer.Deserialize<CloudSynthesizeResponse>(json, JsonOptions); }
            catch (JsonException) { answer = null; }
            if (string.IsNullOrEmpty(answer?.AudioContent))
                throw new CastSpeechException("Google Cloud Text-to-Speech answered without audio.");
            try { return Convert.FromBase64String(answer.AudioContent); }
            catch (FormatException) { throw new CastSpeechException("Google Cloud Text-to-Speech answered with invalid audio."); }
        }

        private static string CloudError(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("error", out var error) &&
                    error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                    return message.GetString()!;
            }
            catch (JsonException) { }
            return "no reason given";
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private sealed class CloudSynthesizeRequest
        {
            public CloudInput? Input { get; set; }
            public CloudVoice? Voice { get; set; }
            public CloudAudioConfig? AudioConfig { get; set; }
        }

        private sealed class CloudInput { public string? Text { get; set; } }

        private sealed class CloudVoice
        {
            public string? LanguageCode { get; set; }
            public string? Name { get; set; }
        }

        private sealed class CloudAudioConfig { public string? AudioEncoding { get; set; } }

        private sealed class CloudSynthesizeResponse { public string? AudioContent { get; set; } }
    }

    /// <summary>The speech couldn't be made.</summary>
    public sealed class CastSpeechException(string message) : Exception(message);
}
