using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Core;
using RIoT2.Core.Models;
using RIoT2.Net.Devices.Catalog;
using RIoT2.Net.Devices.Services.Voice;
using RIoT2.Net.Devices.Services.Wyoming;
using static RIoT2.Net.Devices.Tests.VoiceEngineTests;

namespace RIoT2.Net.Devices.Tests;

/// <summary>The voice satellite device against a stand-in for hassmic in Wyoming mode.</summary>
[TestClass]
public class VoiceSatelliteTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task ASpokenRequestIsReportedAndTheSpeakCommandAnswersIt()
    {
        await using var echo = new FakeWyomingServer();
        var device = await StartAsync(echo.Port);
        try
        {
            await using var satellite = await ConnectedAsync(echo, device);

            device.Stt.Text = "Mikä on olohuoneen lämpötila?";
            await SpeakToAsync(satellite);

            Assert.AreEqual("voice-stopped", (await ReadAsync(satellite))!.Type);
            var transcript = await ReadAsync(satellite);
            Assert.AreEqual(("transcript", "Mikä on olohuoneen lämpötila?"), (transcript!.Type, transcript.GetString("text")));
            var reported = await device.WaitForReportAsync(VoiceSatellite.TranscriptAddress);
            Assert.AreEqual(Core.ValueType.Text, reported.Value.Type);
            Assert.AreEqual("Mikä on olohuoneen lämpötila?", reported.Value.GetValue<string>());
            Assert.AreEqual("fi-FI", device.Stt.Language);
            Assert.IsTrue(device.Stt.Audio!.Duration > TimeSpan.FromSeconds(1.5), "the whole request goes to speech to text");

            device.Tts.Audio = new PcmAudio(22050, 1, Audio.Tone(300, -20, 22050));
            var speak = device.ExecuteCommandAsync(VoiceSatellite.SpeakAddress, "\"Olohuoneessa on 21 astetta.\"", default);
            var start = await ReadAsync(satellite);
            Assert.AreEqual(("audio-start", 22050, 2, 1), (start!.Type, start.GetInt("rate"), start.GetInt("width"), start.GetInt("channels")));
            var played = new List<byte>();
            WyomingEvent? message;
            while ((message = await ReadAsync(satellite))!.Type == "audio-chunk")
                played.AddRange(message.Payload);
            Assert.AreEqual("audio-stop", message.Type);
            CollectionAssert.AreEqual(device.Tts.Audio.Data, played.ToArray());
            Assert.AreEqual("Olohuoneessa on 21 astetta.", device.Tts.Text);
            Assert.AreEqual(VoiceSatellite.Speaking, device.SatelliteState);

            await satellite.WriteAsync("played", null, default);
            await speak.WaitAsync(Wait);
            Assert.AreEqual(VoiceSatellite.Idle, device.SatelliteState);
            CollectionAssert.AreEqual(
                new[] { VoiceSatellite.Offline, VoiceSatellite.Idle, VoiceSatellite.Listening, VoiceSatellite.Thinking, VoiceSatellite.Speaking, VoiceSatellite.Idle },
                device.States());
        }
        finally
        {
            await device.StopAsync(default);
        }
    }

    [TestMethod]
    public async Task NothingSaidEndsTheRequestQuietlyWithoutAReport()
    {
        await using var echo = new FakeWyomingServer();
        var device = await StartAsync(echo.Port);
        try
        {
            await using var satellite = await ConnectedAsync(echo, device);
            await satellite.WriteAsync("run-pipeline", new JsonObject { ["start_stage"] = "asr", ["end_stage"] = "tts" }, default);
            await satellite.WriteAsync(new WyomingEvent("audio-chunk", WyomingEvent.AudioFormat(16000, 1), Audio.Noise(8100, -65)), default);

            var end = await ReadAsync(satellite);
            Assert.AreEqual(("error", VoiceSatellite.NoTextCode), (end!.Type, end.GetString("code")));
            await device.WaitForStateAsync(VoiceSatellite.Idle);
            Assert.IsNull(device.Stt.Audio, "nothing goes to speech to text");
            Assert.IsFalse(device.Reports.Any(x => x.Id == VoiceSatellite.TranscriptAddress));
        }
        finally
        {
            await device.StopAsync(default);
        }
    }

    [TestMethod]
    public async Task WithoutAReplyTheRequestEndsQuietlyAfterTheReplyTimeout()
    {
        await using var echo = new FakeWyomingServer();
        var device = await StartAsync(echo.Port, new() { ["replyTimeoutSeconds"] = "5" });
        try
        {
            await using var satellite = await ConnectedAsync(echo, device);
            await SpeakToAsync(satellite);
            Assert.AreEqual("voice-stopped", (await ReadAsync(satellite))!.Type);
            Assert.AreEqual("transcript", (await ReadAsync(satellite))!.Type);

            var end = await ReadAsync(satellite);
            Assert.AreEqual(("error", VoiceSatellite.NoTextCode), (end!.Type, end.GetString("code")));
            await device.WaitForStateAsync(VoiceSatellite.Idle);
        }
        finally
        {
            await device.StopAsync(default);
        }
    }

    [TestMethod]
    public async Task CancelEndsTheOpenRequestQuietly()
    {
        await using var echo = new FakeWyomingServer();
        var device = await StartAsync(echo.Port);
        try
        {
            await using var satellite = await ConnectedAsync(echo, device);
            await SpeakToAsync(satellite);
            await ReadAsync(satellite);
            await ReadAsync(satellite);

            await device.ExecuteCommandAsync(VoiceSatellite.CancelAddress, "true", default);

            var end = await ReadAsync(satellite);
            Assert.AreEqual(("error", VoiceSatellite.NoTextCode), (end!.Type, end.GetString("code")));
            await device.WaitForStateAsync(VoiceSatellite.Idle);
        }
        finally
        {
            await device.StopAsync(default);
        }
    }

    [TestMethod]
    public async Task ASpeechToTextFailureShowsTheErrorOnTheSatelliteAndTheDevice()
    {
        await using var echo = new FakeWyomingServer();
        var device = await StartAsync(echo.Port);
        try
        {
            await using var satellite = await ConnectedAsync(echo, device);
            device.Stt.Error = new SpeechEngineException("The Wyoming speech-to-text server failed: model not found");
            await SpeakToAsync(satellite);

            Assert.AreEqual("voice-stopped", (await ReadAsync(satellite))!.Type);
            var end = await ReadAsync(satellite);
            Assert.AreEqual(("error", "stt-failed"), (end!.Type, end.GetString("code")));
            await device.WaitForStateAsync(VoiceSatellite.Idle);
            Assert.AreEqual(DeviceState.Running, device.State);
            StringAssert.Contains(device.StateMessage, "model not found");
        }
        finally
        {
            await device.StopAsync(default);
        }
    }

    [TestMethod]
    public async Task ADroppedConnectionIsReportedOfflineAndConnectedAgain()
    {
        await using var echo = new FakeWyomingServer();
        var device = await StartAsync(echo.Port);
        try
        {
            var first = await ConnectedAsync(echo, device);
            await first.DisposeAsync();
            await using var second = await ConnectedAsync(echo, device);
            await device.WaitUntilAsync(() => device.States().SequenceEqual([VoiceSatellite.Offline, VoiceSatellite.Idle, VoiceSatellite.Offline, VoiceSatellite.Idle]));
        }
        finally
        {
            await device.StopAsync(default);
        }
    }

    [TestMethod]
    public async Task SpeakFailsWhileTheSatelliteIsOffline()
    {
        int port;
        await using (var closed = new FakeWyomingServer())
            port = closed.Port;
        var device = await StartAsync(port);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => device.ExecuteCommandAsync(VoiceSatellite.SpeakAddress, "\"Hello\"", default));
            StringAssert.Contains(error.Message, "isn't connected");
            await device.WaitUntilAsync(() => device.StateMessage.StartsWith("Not connected to the satellite", StringComparison.Ordinal));
            Assert.AreEqual(VoiceSatellite.Offline, device.SatelliteState);
        }
        finally
        {
            await device.StopAsync(default);
        }
    }

    [TestMethod]
    public async Task SpeechMadeWhileTheConnectionDroppedFailsAndLeavesTheNewConnectionIdle()
    {
        await using var echo = new FakeWyomingServer();
        var device = await StartAsync(echo.Port);
        try
        {
            var first = await ConnectedAsync(echo, device);
            device.Tts.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var speak = device.ExecuteCommandAsync(VoiceSatellite.SpeakAddress, "\"Hello\"", default);

            await first.DisposeAsync();
            await using var second = await ConnectedAsync(echo, device);
            device.Tts.Hold.SetResult();

            await Assert.ThrowsAsync<InvalidOperationException>(() => speak);
            Assert.AreEqual(VoiceSatellite.Idle, device.SatelliteState);
            Assert.AreEqual(VoiceSatellite.Idle, device.States()[^1]);
        }
        finally
        {
            await device.StopAsync(default);
        }
    }

    [TestMethod]
    public async Task ASatelliteThatDropsWithoutAnsweringIsTriedLessOften()
    {
        await using var echo = new FakeWyomingServer();
        var device = await StartAsync(echo.Port, configure: d => d.ReconnectDelays = [TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1)]);
        try
        {
            await (await echo.AcceptAsync()).DisposeAsync();
            await (await echo.AcceptAsync()).DisposeAsync();
            var waited = System.Diagnostics.Stopwatch.StartNew();
            await (await echo.AcceptAsync()).DisposeAsync();

            Assert.IsTrue(waited.Elapsed >= TimeSpan.FromMilliseconds(900), $"tried again after {waited.ElapsedMilliseconds} ms");
            Assert.IsFalse(device.States().Contains(VoiceSatellite.Idle), "a satellite that never answers isn't connected");
            StringAssert.StartsWith(device.StateMessage, "Not connected to the satellite");
        }
        finally
        {
            await device.StopAsync(default);
        }
    }

    [TestMethod]
    public async Task ASilentConnectionIsDroppedAfterTheDeadTime()
    {
        await using var echo = new FakeWyomingServer();
        var device = await StartAsync(echo.Port, configure: d => d.DeadAfter = TimeSpan.FromMilliseconds(500));
        try
        {
            await using var first = await ConnectedAsync(echo, device);
            await using var second = await echo.AcceptAsync();
            Assert.AreEqual("describe", (await second.ReadAsync(default))!.Type);
            Assert.AreEqual(VoiceSatellite.Offline, device.States()[2]);
        }
        finally
        {
            await device.StopAsync(default);
        }
    }

    [TestMethod]
    [DataRow("host", "http://echo", "The host must be")]
    [DataRow("language", "fi FI", "The language must be")]
    [DataRow("sttUrl", "http://whisper:10300", "wyoming needs the sttUrl as tcp://host:port")]
    [DataRow("sttUrl", "tcp://whisper", "wyoming needs the sttUrl as tcp://host:port")]
    [DataRow("ttsEngine", "espeak", "The ttsEngine must be")]
    [DataRow("silenceMs", "50", "The silenceMs must be 200-3000")]
    [DataRow("speechThresholdDb", "0", "The speechThresholdDb must be")]
    public async Task InvalidParametersStopTheDeviceWithTheReason(string key, string value, string reason)
    {
        var parameters = Parameters(1);
        parameters[key] = value;
        var device = Create(parameters);

        await Assert.ThrowsAsync<Exception>(() => device.StartAsync(default));
        Assert.AreEqual(DeviceState.Error, device.State);
        StringAssert.Contains(device.StateMessage, reason);
    }

    [TestMethod]
    public async Task GoogleCloudNeedsItsKeyAndOpenAiCompatibleNeedsNoUrl()
    {
        var google = Parameters(1);
        google["ttsEngine"] = "googleCloud";
        var device = Create(google);
        await Assert.ThrowsAsync<Exception>(() => device.StartAsync(default));
        StringAssert.Contains(device.StateMessage, "googleCloud needs the ttsApiKey");

        var openAi = Parameters(1);
        openAi["sttEngine"] = openAi["ttsEngine"] = "openAiCompatible";
        openAi.Remove("sttUrl");
        openAi.Remove("ttsUrl");
        var started = Create(openAi);
        await started.StartAsync(default);
        Assert.AreEqual(DeviceState.Running, started.State);
        await started.StopAsync(default);
    }

    // Wake word, then a request: a pause, half a second of voice and a second of the room.
    private static async Task SpeakToAsync(WyomingConnection satellite)
    {
        await satellite.WriteAsync("run-pipeline", new JsonObject { ["start_stage"] = "asr", ["end_stage"] = "tts", ["wake_word_name"] = "alexa" }, default);
        foreach (var audio in new[] { Audio.Silence(810), Audio.Noise(300, -60), Audio.Tone(600, -20), Audio.Noise(1200, -60) })
        {
            for (var at = 0; at < audio.Length; at += 3200)
                await satellite.WriteAsync(new WyomingEvent("audio-chunk", WyomingEvent.AudioFormat(16000, 1), audio[at..Math.Min(at + 3200, audio.Length)]), default);
        }
    }

    private static async Task<WyomingConnection> ConnectedAsync(FakeWyomingServer echo, TestVoiceSatellite device)
    {
        var satellite = await echo.AcceptAsync();
        Assert.AreEqual("describe", (await satellite.ReadAsync(default))!.Type);
        Assert.AreEqual("run-satellite", (await satellite.ReadAsync(default))!.Type);
        await satellite.WriteAsync("info", new JsonObject { ["satellite"] = new JsonObject { ["name"] = "Echo Dot 2 1a2b3c" } }, default);
        await device.WaitForStateAsync(VoiceSatellite.Idle);
        return satellite;
    }

    private static async Task<WyomingEvent?> ReadAsync(WyomingConnection satellite)
    {
        using var timeout = new CancellationTokenSource(Wait);
        return await satellite.ReadAsync(timeout.Token);
    }

    private static Dictionary<string, string> Parameters(int port) => new()
    {
        ["host"] = "127.0.0.1",
        ["port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["language"] = "fi-FI",
        ["sttUrl"] = "tcp://127.0.0.1:10300",
        ["ttsUrl"] = "tcp://127.0.0.1:10200",
        ["silenceMs"] = "600"
    };

    private static async Task<TestVoiceSatellite> StartAsync(int port, Dictionary<string, string>? extra = null, Action<TestVoiceSatellite>? configure = null)
    {
        var parameters = Parameters(port);
        foreach (var (key, value) in extra ?? [])
            parameters[key] = value;
        var device = Create(parameters);
        device.ReconnectDelays = [TimeSpan.FromMilliseconds(50)];
        configure?.Invoke(device);
        await device.StartAsync(default);
        Assert.AreEqual(DeviceState.Running, device.State, device.StateMessage);
        return device;
    }

    private static TestVoiceSatellite Create(Dictionary<string, string> parameters)
    {
        var device = new TestVoiceSatellite();
        device.Initialize(new DeviceConfiguration
        {
            Id = "echo",
            Name = "Echo",
            DeviceParameters = parameters,
            ReportTemplates = new[] { VoiceSatellite.StateAddress, VoiceSatellite.TranscriptAddress }
                .Select(x => new ReportTemplate { Id = x, Address = x, Name = x }).ToList(),
            CommandTemplates = new[] { VoiceSatellite.SpeakAddress, VoiceSatellite.CancelAddress }
                .Select(x => new CommandTemplate { Id = x, Address = x, Name = x }).ToList()
        });
        return device;
    }

    internal sealed class FakeStt : ISpeechToText
    {
        public string Text { get; set; } = "Turn on the lights.";
        public Exception? Error { get; set; }
        public PcmAudio? Audio { get; private set; }
        public string? Language { get; private set; }

        public Task<string> TranscribeAsync(PcmAudio audio, string language, CancellationToken cancellationToken)
        {
            (Audio, Language) = (audio, language);
            return Error != null ? Task.FromException<string>(Error) : Task.FromResult(Text);
        }
    }

    internal sealed class FakeTts : ITextToSpeech
    {
        public PcmAudio Audio { get; set; } = new(22050, 1, new byte[4410]);
        public string? Text { get; private set; }

        /// <summary>When set, the speech is made once it completes.</summary>
        public TaskCompletionSource? Hold { get; set; }

        public async Task<PcmAudio> SynthesizeAsync(string text, string language, CancellationToken cancellationToken)
        {
            Text = text;
            if (Hold != null)
                await Hold.Task.WaitAsync(cancellationToken);
            return Audio;
        }
    }

    internal sealed class TestVoiceSatellite : VoiceSatellite
    {
        public TestVoiceSatellite(VoiceAudioCache? cache = null) : base(NullLogger.Instance, cache) => ReportUpdated += (_, report) => _reports.Enqueue((Report)report);

        private readonly ConcurrentQueue<Report> _reports = new();

        public FakeStt Stt { get; } = new();
        public FakeTts Tts { get; } = new();
        public IReadOnlyList<Report> Reports => _reports.ToList();

        public string[] States() => Reports.Where(x => x.Id == StateAddress).Select(x => x.Value.GetValue<string>()!).ToArray();

        internal override ISpeechToText CreateSpeechToText() => Stt;
        internal override ITextToSpeech CreateTextToSpeech() => Tts;

        public async Task WaitForStateAsync(string state)
        {
            await WaitUntilAsync(() => SatelliteState == state, $"The satellite isn't {state}.");
        }

        public async Task WaitUntilAsync(Func<bool> condition, string? message = null)
        {
            var until = DateTime.UtcNow + Wait;
            while (!condition())
            {
                if (DateTime.UtcNow > until)
                    Assert.Fail(message ?? $"Timed out; the states were {string.Join(", ", States())}.");
                await Task.Delay(10);
            }
        }

        public async Task<Report> WaitForReportAsync(string id)
        {
            var until = DateTime.UtcNow + Wait;
            while (true)
            {
                var report = Reports.LastOrDefault(x => x.Id == id);
                if (report != null)
                    return report;
                if (DateTime.UtcNow > until)
                    Assert.Fail($"No {id} report.");
                await Task.Delay(10);
            }
        }
    }
}
