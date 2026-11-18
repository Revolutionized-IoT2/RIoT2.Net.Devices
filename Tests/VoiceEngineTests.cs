using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Net.Devices.Services.Voice;
using RIoT2.Net.Devices.Services.Wyoming;

namespace RIoT2.Net.Devices.Tests;

/// <summary>The voice satellite's parts: the Wyoming protocol, the end of speech, WAV and the speech engines.</summary>
[TestClass]
public class VoiceEngineTests
{
    [TestMethod]
    public async Task AWyomingEventRoundTripsWithItsDataAndPayloadAndKeepsNonAsciiAsUtf8()
    {
        var stream = new MemoryStream();
        var writer = new WyomingConnection(stream);
        await writer.WriteAsync(new WyomingEvent("transcript", new JsonObject { ["text"] = "Mikä on lämpötila?" }), default);
        await writer.WriteAsync(new WyomingEvent("audio-chunk", WyomingEvent.AudioFormat(16000, 1), [1, 2, 3, 4]), default);
        await writer.WriteAsync("audio-stop", null, default);

        var bytes = stream.ToArray();
        var text = Encoding.UTF8.GetString(bytes);
        // hassmic reads the data block (after the header line) with a flat parser that doesn't decode \u escapes.
        StringAssert.StartsWith(text, "{\"type\":\"transcript\",\"version\":\"1.5.4\",\"data_length\":");
        StringAssert.Contains(text, "\n{\"text\":\"Mikä on lämpötila?\"}");

        var reader = new WyomingConnection(new MemoryStream(bytes));
        var transcript = await reader.ReadAsync(default);
        var chunk = await reader.ReadAsync(default);
        var stop = await reader.ReadAsync(default);
        Assert.AreEqual(("transcript", "Mikä on lämpötila?"), (transcript!.Type, transcript.GetString("text")));
        Assert.AreEqual(("audio-chunk", 16000, 2, 1), (chunk!.Type, chunk.GetInt("rate"), chunk.GetInt("width"), chunk.GetInt("channels")));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, chunk.Payload);
        Assert.AreEqual(("audio-stop", 0, 0), (stop!.Type, stop.Data.Count, stop.Payload.Length));
        Assert.IsNull(await reader.ReadAsync(default), "the end of the stream between events");
    }

    [TestMethod]
    public async Task AWyomingEventWithItsDataInTheHeaderIsRead()
    {
        var reader = new WyomingConnection(new MemoryStream("{\"type\":\"run-pipeline\",\"data\":{\"start_stage\":\"asr\"}}\n"u8.ToArray()));
        var message = await reader.ReadAsync(default);
        Assert.AreEqual(("run-pipeline", "asr"), (message!.Type, message.GetString("start_stage")));
    }

    [TestMethod]
    [DataRow("{\"version\":\"1.5.4\"}\n", "no type")]
    [DataRow("not json\n", "not valid JSON")]
    [DataRow("{\"type\":\"audio-chunk\",\"payload_length\":2000000}\n", "is over")]
    [DataRow("{\"type\":\"audio-chunk\",\"payload_length\":-1}\n", "not a length")]
    [DataRow("{\"type\":\"audio-chunk\",\"payload_length\":10}\nabc", "closed inside an event")]
    [DataRow("{\"type\":\"ping\"", "closed in a header")]
    public async Task ABrokenWyomingEventIsRefused(string input, string reason)
    {
        var reader = new WyomingConnection(new MemoryStream(Encoding.UTF8.GetBytes(input)));
        var error = await Assert.ThrowsAsync<WyomingProtocolException>(() => reader.ReadAsync(default));
        StringAssert.Contains(error.Message, reason);
    }

    [TestMethod]
    public void TheRequestEndsAfterTheSilenceThatFollowsSpeech()
    {
        var detector = new EndpointDetector(new EndpointSettings { SilenceMs = 600 });
        // hassmic sends digital silence ahead of each request, then the room and the voice. Lengths are whole frames.
        Assert.AreEqual(EndpointResult.Listening, detector.Process(Audio.Silence(810)));
        Assert.AreEqual(EndpointResult.Listening, detector.Process(Audio.Noise(300, -60)));
        Assert.AreEqual(EndpointResult.Listening, detector.Process(Audio.Tone(900, -20)));
        Assert.IsTrue(detector.HeardSpeech);
        Assert.AreEqual(EndpointResult.Listening, detector.Process(Audio.Noise(540, -60)));
        Assert.AreEqual(EndpointResult.Ended, detector.Process(Audio.Noise(60, -60)));
    }

    [TestMethod]
    public void SpeechStraightAfterTheWakeWordIsHeardBeforeAnyPause()
    {
        var detector = new EndpointDetector(new EndpointSettings { SilenceMs = 600 });
        detector.Process(Audio.Silence(800));
        detector.Process(Audio.Tone(1200, -25));
        Assert.IsTrue(detector.HeardSpeech);
        Assert.AreEqual(EndpointResult.Ended, detector.Process(Audio.Noise(700, -60)));
    }

    [TestMethod]
    public void InANoisyRoomOnlyTheLouderVoiceCounts()
    {
        var detector = new EndpointDetector(new EndpointSettings { SilenceMs = 600 });
        Assert.AreEqual(EndpointResult.Listening, detector.Process(Audio.Noise(1000, -40)));
        Assert.IsFalse(detector.HeardSpeech, "the noise is over the threshold but not over its own floor");
        detector.Process(Audio.Tone(600, -18));
        Assert.IsTrue(detector.HeardSpeech);
        Assert.AreEqual(EndpointResult.Ended, detector.Process(Audio.Noise(700, -40)));
    }

    [TestMethod]
    public void NothingSaidIsNoSpeechAndAClickIsNotSpeech()
    {
        var detector = new EndpointDetector(new EndpointSettings { NoSpeechMs = 3000 });
        detector.Process(Audio.Tone(60, -20));
        Assert.AreEqual(EndpointResult.Listening, detector.Process(Audio.Noise(2880, -60)));
        Assert.IsFalse(detector.HeardSpeech);
        Assert.AreEqual(EndpointResult.NoSpeech, detector.Process(Audio.Noise(60, -60)));
    }

    [TestMethod]
    public void ARequestThatNeverPausesEndsAtTheLongestRequest()
    {
        var detector = new EndpointDetector(new EndpointSettings { MaxRequestMs = 4000 });
        Assert.AreEqual(EndpointResult.Listening, detector.Process(Audio.Tone(3960, -20)));
        Assert.AreEqual(EndpointResult.Ended, detector.Process(Audio.Tone(60, -20)));
        Assert.AreEqual(EndpointResult.Ended, detector.Process(Audio.Silence(100)), "the result stays");
    }

    [TestMethod]
    public void WavRoundTripsAndAStreamedFileReadsToItsEnd()
    {
        var audio = new PcmAudio(22050, 1, Audio.Tone(100, -20, 22050));
        var decoded = Wav.Decode(Wav.Encode(audio));
        Assert.AreEqual((22050, 1), (decoded.Rate, decoded.Channels));
        CollectionAssert.AreEqual(audio.Data, decoded.Data);

        var streamed = Wav.Encode(audio);
        streamed[40] = streamed[41] = streamed[42] = streamed[43] = 0xFF;
        CollectionAssert.AreEqual(audio.Data, Wav.Decode(streamed).Data);
        Assert.ThrowsExactly<FormatException>(() => Wav.Decode("ID3 not a wav file"u8));
    }

    [TestMethod]
    public async Task WyomingSpeechToTextSendsTheRequestAndReturnsTheTranscript()
    {
        await using var server = new FakeWyomingServer();
        var audio = new PcmAudio(16000, 1, Audio.Tone(250, -20));
        var heard = server.ServeOnceAsync(async connection =>
        {
            var events = new List<WyomingEvent>();
            while (await connection.ReadAsync(default) is { } message)
            {
                events.Add(message);
                if (message.Type == "audio-stop")
                    break;
            }
            await connection.WriteAsync("transcript", new JsonObject { ["text"] = " Sytytä valot. " }, default);
            return events;
        });

        var text = await new WyomingSpeechToText("127.0.0.1", server.Port, Connect).TranscribeAsync(audio, "fi-FI", default);
        var events = await heard;

        Assert.AreEqual("Sytytä valot.", text);
        Assert.AreEqual(("transcribe", "fi"), (events[0].Type, events[0].GetString("language")));
        Assert.AreEqual(("audio-start", 16000), (events[1].Type, events[1].GetInt("rate")));
        Assert.AreEqual("audio-stop", events[^1].Type);
        CollectionAssert.AreEqual(audio.Data, events.Where(x => x.Type == "audio-chunk").SelectMany(x => x.Payload).ToArray());
    }

    [TestMethod]
    public async Task WyomingTextToSpeechAsksForTheVoiceAndCollectsTheAudio()
    {
        await using var server = new FakeWyomingServer();
        var asked = server.ServeOnceAsync(async connection =>
        {
            var request = await connection.ReadAsync(default);
            await connection.WriteAsync("audio-start", WyomingEvent.AudioFormat(22050, 1), default);
            await connection.WriteAsync(new WyomingEvent("audio-chunk", WyomingEvent.AudioFormat(22050, 1), [1, 0, 2, 0]), default);
            await connection.WriteAsync(new WyomingEvent("audio-chunk", WyomingEvent.AudioFormat(22050, 1), [3, 0]), default);
            await connection.WriteAsync("audio-stop", null, default);
            return request!;
        });

        var audio = await new WyomingTextToSpeech("127.0.0.1", server.Port, "fi_FI-harri-medium", Connect).SynthesizeAsync("Hei", "fi-FI", default);
        var request = await asked;

        Assert.AreEqual(("synthesize", "Hei"), (request.Type, request.GetString("text")));
        Assert.AreEqual("fi_FI-harri-medium", request.Data["voice"]!["name"]!.GetValue<string>());
        Assert.AreEqual((22050, 1), (audio.Rate, audio.Channels));
        CollectionAssert.AreEqual(new byte[] { 1, 0, 2, 0, 3, 0 }, audio.Data);
    }

    [TestMethod]
    public async Task OpenAiCompatibleSpeechToTextPostsAWavWithTheModelAndLanguage()
    {
        var http = new FakeHttp();
        http.Answers.Enqueue((HttpStatusCode.OK, "application/json", "{\"text\":\"Turn on the lights.\"}"u8.ToArray()));
        var stt = new OpenAiSpeechToText("http://speaches:8000/v1", "Systran/faster-whisper-small", "secret-key", http.SendAsync);

        var text = await stt.TranscribeAsync(new PcmAudio(16000, 1, Audio.Tone(100, -20)), "en-GB", default);

        var request = http.Requests.Single();
        Assert.AreEqual("Turn on the lights.", text);
        Assert.AreEqual(("http://speaches:8000/v1/audio/transcriptions", "Bearer secret-key"), (request.Uri, request.Authorization));
        Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(request.Body, "name=\"?model\"?\r\n\r\nSystran/faster-whisper-small\r\n"), request.Body[..300]);
        Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(request.Body, "name=\"?language\"?\r\n\r\nen\r\n"));
        StringAssert.Contains(request.Body, "RIFF");
    }

    [TestMethod]
    public async Task OpenAiCompatibleTextToSpeechReadsTheWavAndARefusalNamesTheReasonNotTheKey()
    {
        var http = new FakeHttp();
        var wav = Wav.Encode(new PcmAudio(24000, 1, [5, 0, 6, 0]));
        http.Answers.Enqueue((HttpStatusCode.OK, "audio/wav", wav));
        http.Answers.Enqueue((HttpStatusCode.Unauthorized, "application/json", "{\"error\":{\"message\":\"Incorrect API key provided.\"}}"u8.ToArray()));
        var tts = new OpenAiTextToSpeech("https://api.openai.com/v1", "tts-1", "alloy", "sk-secret", http.SendAsync);

        var audio = await tts.SynthesizeAsync("Hello", "en-US", default);
        var error = await Assert.ThrowsAsync<SpeechEngineException>(() => tts.SynthesizeAsync("Hello", "en-US", default));

        Assert.AreEqual((24000, 1), (audio.Rate, audio.Channels));
        Assert.AreEqual("https://api.openai.com/v1/audio/speech", http.Requests[0].Uri);
        StringAssert.Contains(http.Requests[0].Body, "\"response_format\":\"wav\"");
        StringAssert.Contains(http.Requests[0].Body, "\"voice\":\"alloy\"");
        StringAssert.Contains(error.Message, "Incorrect API key provided.");
        Assert.IsFalse(error.Message.Contains("sk-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GoogleCloudTextToSpeechAsksForLinear16()
    {
        var http = new FakeHttp();
        var wav = Wav.Encode(new PcmAudio(24000, 1, [7, 0]));
        http.Answers.Enqueue((HttpStatusCode.OK, "application/json", Encoding.UTF8.GetBytes($$"""{"audioContent":"{{Convert.ToBase64String(wav)}}"}""")));

        var audio = await new GoogleCloudTextToSpeech("fi-FI-Wavenet-B", "key-1", http.SendAsync).SynthesizeAsync("Hei", "fi-FI", default);

        CollectionAssert.AreEqual(new byte[] { 7, 0 }, audio.Data);
        StringAssert.Contains(http.Requests.Single().Body, "\"audioEncoding\":\"LINEAR16\"");
        StringAssert.Contains(http.Requests.Single().Body, "\"name\":\"fi-FI-Wavenet-B\"");
    }

    internal static async Task<WyomingConnection> Connect(string host, int port, CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken);
        return new WyomingConnection(client.GetStream(), client);
    }

    /// <summary>16-bit mono test audio.</summary>
    internal static class Audio
    {
        public static byte[] Silence(int ms, int rate = 16000) => new byte[rate * ms / 1000 * 2];

        // A tone with the given RMS level.
        public static byte[] Tone(int ms, double levelDb, int rate = 16000)
        {
            var amplitude = 32768 * Math.Pow(10, levelDb / 20) * Math.Sqrt(2);
            return Samples(ms, rate, i => amplitude * Math.Sin(2 * Math.PI * 220 * i / rate));
        }

        // Noise with about the given RMS level.
        public static byte[] Noise(int ms, double levelDb, int rate = 16000)
        {
            var random = new Random(ms);
            var amplitude = 32768 * Math.Pow(10, levelDb / 20) * Math.Sqrt(3);
            return Samples(ms, rate, _ => amplitude * (2 * random.NextDouble() - 1));
        }

        private static byte[] Samples(int ms, int rate, Func<int, double> sample)
        {
            var count = rate * ms / 1000;
            var bytes = new byte[count * 2];
            for (var i = 0; i < count; i++)
            {
                var value = (short)Math.Clamp(Math.Round(sample(i)), short.MinValue, short.MaxValue);
                bytes[2 * i] = (byte)value;
                bytes[2 * i + 1] = (byte)(value >> 8);
            }
            return bytes;
        }
    }

    /// <summary>A Wyoming server on the loopback address that serves one connection.</summary>
    internal sealed class FakeWyomingServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public FakeWyomingServer() => _listener.Start();

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public async Task<WyomingConnection> AcceptAsync(TimeSpan? timeout = null)
        {
            using var cancel = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
            var client = await _listener.AcceptTcpClientAsync(cancel.Token);
            return new WyomingConnection(client.GetStream(), client);
        }

        public async Task<T> ServeOnceAsync<T>(Func<WyomingConnection, Task<T>> serve)
        {
            await using var connection = await AcceptAsync();
            return await serve(connection);
        }

        public ValueTask DisposeAsync()
        {
            _listener.Stop();
            _listener.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class FakeHttp
    {
        public Queue<(HttpStatusCode Status, string ContentType, byte[] Body)> Answers { get; } = new();
        public List<(string Uri, string? Authorization, string Body)> Requests { get; } = [];

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? "" : Encoding.Latin1.GetString(await request.Content.ReadAsByteArrayAsync(cancellationToken));
            Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));
            var (status, type, answer) = Answers.Dequeue();
            var content = new ByteArrayContent(answer);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
            return new HttpResponseMessage(status) { Content = content };
        }
    }
}
