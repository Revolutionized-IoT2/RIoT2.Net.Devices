using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Core;
using RIoT2.Core.Models;
using RIoT2.Net.Devices.Catalog;
using RIoT2.Net.Devices.Controllers;
using RIoT2.Net.Devices.Services.GoogleCast;
using Sharpcaster.Models.Media;
using ValueType = RIoT2.Core.ValueType;

namespace RIoT2.Net.Devices.Tests;

[TestClass]
public class GoogleCastTests
{
    private const string Host = "192.168.1.20";
    private const string BaseUrl = "http://192.168.1.10:8081";

    [TestMethod]
    public async Task SpeakWithGoogleTranslatePlaysTheSpeechAtTheAnnouncementVolumeAndSetsItBack()
    {
        var (device, speaker, http, _) = await StartAsync(new() { ["announcementVolume"] = "70" });
        speaker.Volume = 0.25;

        await device.ExecuteCommandAsync(GoogleCast.SpeakAddress, "\"Sauna   is\\nready\"", default);

        CollectionAssert.AreEqual(new[] { $"connect {Host}:8009", "volume?", "volume 0.70", "play", "stop", "volume 0.25", "dispose" }, speaker.Calls);
        var media = speaker.Played.Single();
        Assert.AreEqual("https://translate.google.com/translate_tts?ie=UTF-8&client=tw-ob&tl=en-GB&q=Sauna%20is%20ready", media.Url);
        Assert.AreEqual(("audio/mpeg", "Sauna is ready"), (media.ContentType, media.Title));
        Assert.IsEmpty(http.Requests, "the speaker downloads Google Translate's speech itself");
        Assert.AreEqual((DeviceState.Running, ""), (device.State, device.StateMessage));
    }

    [TestMethod]
    public async Task WithoutAnAnnouncementVolumeTheVolumeIsLeftAlone()
    {
        var (device, speaker, _, _) = await StartAsync([]);
        await device.ExecuteCommandAsync(GoogleCast.SpeakAddress, "\"Hello\"", default);
        CollectionAssert.AreEqual(new[] { $"connect {Host}:8009", "play", "stop", "dispose" }, speaker.Calls);
    }

    [TestMethod]
    public async Task WithoutRestoreTheAnnouncementVolumeStays()
    {
        var (device, speaker, _, _) = await StartAsync(new() { ["announcementVolume"] = "50", ["restoreVolume"] = "false" });
        await device.ExecuteCommandAsync(GoogleCast.SpeakAddress, "\"Hello\"", default);
        CollectionAssert.AreEqual(new[] { $"connect {Host}:8009", "volume 0.50", "play", "stop", "dispose" }, speaker.Calls);
    }

    [TestMethod]
    public async Task ALongTextIsSpokenInChunks()
    {
        var (device, speaker, _, _) = await StartAsync([]);
        var sentence = "The sauna has reached eighty degrees and is ready for you now. ";
        await device.ExecuteCommandAsync(GoogleCast.SpeakAddress, System.Text.Json.JsonSerializer.Serialize(string.Concat(Enumerable.Repeat(sentence, 6))), default);

        Assert.HasCount(2, speaker.Played);
        Assert.IsTrue(speaker.Played.All(x => x.Url.Length < 400));
        Assert.AreEqual(1, speaker.Calls.Count(x => x == "stop"), "the player stops once, after the last chunk");
    }

    [TestMethod]
    public void ChunksBreakAfterSentencesThenWordsAndKeepEveryWord()
    {
        var text = "Sauna is ready. " + string.Join(' ', Enumerable.Repeat("word", 60)) + "! " + new string('x', 450);
        var chunks = CastSpeech.Chunks(text, 200);

        Assert.IsTrue(chunks.All(x => x.Length is > 0 and <= 200), string.Join(" | ", chunks.Select(x => x.Length)));
        Assert.AreEqual("Sauna is ready.", chunks[0][..15]);
        Assert.IsTrue(chunks.Take(chunks.Count - 3).All(x => !x.StartsWith('x')));
        Assert.AreEqual(CastSpeech.Normalize(text).Replace(" ", ""), string.Concat(chunks).Replace(" ", ""));
        CollectionAssert.AreEqual(new[] { "Short one." }, CastSpeech.Chunks("  Short\tone. ", 200).ToArray());
    }

    [TestMethod]
    public async Task GoogleCloudSynthesizesOnceAndTheSpeakerDownloadsTheAudioFromTheNode()
    {
        var (device, speaker, http, cache) = await StartAsync(new()
        {
            ["ttsEngine"] = "googleCloud", ["cloudTtsApiKey"] = "key-123", ["voice"] = "en-GB-Neural2-B", ["nodeBaseUrl"] = BaseUrl + "/"
        });
        var mp3 = new byte[] { 0x49, 0x44, 0x33, 1, 2, 3 };
        http.Answers.Enqueue((HttpStatusCode.OK, $$"""{"audioContent":"{{Convert.ToBase64String(mp3)}}"}"""));

        await device.ExecuteCommandAsync(GoogleCast.SpeakAddress, "\"Sauna is ready\"", default);
        await device.ExecuteCommandAsync(GoogleCast.SpeakAddress, "\"Sauna is ready\"", default);

        var request = http.Requests.Single();
        Assert.AreEqual(CastSpeech.CloudEndpoint, request.Uri);
        Assert.AreEqual("key-123", request.ApiKey);
        StringAssert.Contains(request.Body, "\"text\":\"Sauna is ready\"");
        StringAssert.Contains(request.Body, "\"languageCode\":\"en-GB\"");
        StringAssert.Contains(request.Body, "\"name\":\"en-GB-Neural2-B\"");
        StringAssert.Contains(request.Body, "\"audioEncoding\":\"MP3\"");

        Assert.HasCount(2, speaker.Played);
        Assert.AreEqual(speaker.Played[0].Url, speaker.Played[1].Url, "the same phrase reuses its audio");
        StringAssert.StartsWith(speaker.Played[0].Url, BaseUrl + "/api/cast/audio/");
        var token = speaker.Played[0].Url[(BaseUrl.Length + "/api/cast/audio/".Length)..];

        var result = new CastAudioController(cache).GetAudio(token);
        var file = (FileContentHttpResult)result;
        Assert.AreEqual("audio/mpeg", file.ContentType);
        CollectionAssert.AreEqual(mp3, file.FileContents.ToArray());
    }

    [TestMethod]
    public async Task AGoogleCloudRefusalFailsTheCommandAndShowsWhyButTheDeviceKeepsRunning()
    {
        var (device, speaker, http, _) = await StartAsync(new() { ["ttsEngine"] = "googleCloud", ["cloudTtsApiKey"] = "bad", ["nodeBaseUrl"] = BaseUrl });
        http.Answers.Enqueue((HttpStatusCode.Forbidden, """{"error":{"code":403,"message":"API key not valid."}}"""));

        await Assert.ThrowsAsync<CastSpeechException>(() => device.ExecuteCommandAsync(GoogleCast.SpeakAddress, "\"Hi\"", default));

        Assert.AreEqual(DeviceState.Running, device.State);
        StringAssert.Contains(device.StateMessage, "API key not valid.");
        Assert.IsFalse(device.StateMessage.Contains("bad", StringComparison.Ordinal), "the key isn't shown");
        Assert.IsEmpty(speaker.Calls);
    }

    [TestMethod]
    public async Task AnUnreachableSpeakerFailsTheCommandAndTheNextSuccessClearsTheMessage()
    {
        var (device, speaker, _, _) = await StartAsync(new() { ["announcementVolume"] = "60" });
        speaker.ConnectError = new CastException("Could not connect to the speaker at 192.168.1.20:8009: no answer in 10 s.");

        await Assert.ThrowsAsync<CastException>(() => device.ExecuteCommandAsync(GoogleCast.SpeakAddress, "\"Hi\"", default));
        Assert.AreEqual(DeviceState.Running, device.State);
        StringAssert.Contains(device.StateMessage, "Could not connect");

        speaker.ConnectError = null;
        await device.ExecuteCommandAsync(GoogleCast.SpeakAddress, "\"Hi\"", default);
        Assert.AreEqual((DeviceState.Running, ""), (device.State, device.StateMessage));
    }

    [TestMethod]
    public async Task TheVolumeIsSetBackWhenPlayingFails()
    {
        var (device, speaker, _, _) = await StartAsync(new() { ["announcementVolume"] = "80" });
        speaker.Volume = 0.3;
        speaker.PlayError = new CastException("The speaker could not play the media from translate.google.com.");

        await Assert.ThrowsAsync<CastException>(() => device.ExecuteCommandAsync(GoogleCast.SpeakAddress, "\"Hi\"", default));

        CollectionAssert.AreEqual(new[] { $"connect {Host}:8009", "volume?", "volume 0.80", "play", "volume 0.30", "dispose" }, speaker.Calls);
    }

    [TestMethod]
    public async Task TheVolumeIsSetBackWhenSettingItFails()
    {
        var (device, speaker, _, _) = await StartAsync(new() { ["announcementVolume"] = "80" });
        speaker.Volume = 0.3;
        speaker.FailVolumeOnce = 0.8;

        await Assert.ThrowsAsync<CastException>(() => device.ExecuteCommandAsync(GoogleCast.SpeakAddress, "\"Hi\"", default));

        CollectionAssert.AreEqual(new[] { $"connect {Host}:8009", "volume?", "volume 0.80", "volume 0.30", "dispose" }, speaker.Calls);
    }

    [TestMethod]
    public async Task MediaThatPlaysOnIsLeftPlayingAndTheVolumeIsSetBack()
    {
        var (device, speaker, _, _) = await StartAsync(new() { ["announcementVolume"] = "80" });
        speaker.Volume = 0.3;
        speaker.Ends = false;

        await device.ExecuteCommandAsync(GoogleCast.PlayUrlAddress, "\"https://example.com/radio/stream.mp3\"", default);

        CollectionAssert.AreEqual(new[] { $"connect {Host}:8009", "volume?", "volume 0.80", "play", "volume 0.30", "dispose" }, speaker.Calls);
    }

    [TestMethod]
    [DataRow("https://example.com/sounds/door%20bell.wav", "audio/wav", "door bell.wav")]
    [DataRow("http://192.168.1.5/chime.mp3?x=1", "audio/mpeg", "chime.mp3")]
    [DataRow("https://example.com/stream", "audio/mpeg", "stream")]
    [DataRow("https://example.com/a.ogg", "audio/ogg", "a.ogg")]
    public async Task PlayUrlPlaysTheMedia(string url, string contentType, string title)
    {
        var (device, speaker, _, _) = await StartAsync([]);
        await device.ExecuteCommandAsync(GoogleCast.PlayUrlAddress, System.Text.Json.JsonSerializer.Serialize(url), default);

        var media = speaker.Played.Single();
        Assert.AreEqual((new Uri(url).AbsoluteUri, contentType, title), (media.Url, media.ContentType, media.Title));
    }

    [TestMethod]
    [DataRow("\"file:///etc/passwd\"")]
    [DataRow("\"javascript:alert(1)\"")]
    [DataRow("\"/relative/chime.mp3\"")]
    [DataRow("\"https://user:secret@example.com/a.mp3\"")]
    [DataRow("\"\"")]
    [DataRow("null")]
    public async Task PlayUrlRefusesAnythingButAnHttpUrl(string value)
    {
        var (device, speaker, _, _) = await StartAsync([]);
        await Assert.ThrowsAsync<ArgumentException>(() => device.ExecuteCommandAsync(GoogleCast.PlayUrlAddress, value, default));
        Assert.IsEmpty(speaker.Calls);
        Assert.AreEqual((DeviceState.Running, ""), (device.State, device.StateMessage), "a bad value is the caller's problem");
    }

    [TestMethod]
    [DataRow("\"\"")]
    [DataRow("\"   \"")]
    [DataRow("null")]
    public async Task SpeakRefusesAnEmptyText(string value)
    {
        var (device, speaker, _, _) = await StartAsync([]);
        await Assert.ThrowsAsync<ArgumentException>(() => device.ExecuteCommandAsync(GoogleCast.SpeakAddress, value, default));
        Assert.IsEmpty(speaker.Calls);
    }

    [TestMethod]
    public async Task SpeakRefusesATooLongText()
    {
        var (device, speaker, _, _) = await StartAsync([]);
        var text = System.Text.Json.JsonSerializer.Serialize(new string('a', GoogleCast.MaxTextLength + 1));
        await Assert.ThrowsAsync<ArgumentException>(() => device.ExecuteCommandAsync(GoogleCast.SpeakAddress, text, default));
        Assert.IsEmpty(speaker.Calls);
    }

    [TestMethod]
    [DataRow("40", "volume 0.40")]
    [DataRow("\"25\"", "volume 0.25")]
    [DataRow("0", "volume 0.00")]
    [DataRow("100", "volume 1.00")]
    public async Task SetVolumeSetsTheSpeakersVolume(string value, string call)
    {
        var (device, speaker, _, _) = await StartAsync([]);
        await device.ExecuteCommandAsync(GoogleCast.SetVolumeAddress, value, default);
        CollectionAssert.AreEqual(new[] { $"connect {Host}:8009", call, "dispose" }, speaker.Calls);
    }

    [TestMethod]
    [DataRow("101")]
    [DataRow("-1")]
    [DataRow("\"loud\"")]
    public async Task SetVolumeRefusesAVolumeOutsideTheRange(string value)
    {
        var (device, speaker, _, _) = await StartAsync([]);
        await Assert.ThrowsAsync<ArgumentException>(() => device.ExecuteCommandAsync(GoogleCast.SetVolumeAddress, value, default));
        Assert.IsEmpty(speaker.Calls);
    }

    [TestMethod]
    public async Task StopStopsThePlayerAndFalseDoesNothing()
    {
        var (device, speaker, _, _) = await StartAsync([]);
        await device.ExecuteCommandAsync(GoogleCast.StopAddress, "false", default);
        Assert.IsEmpty(speaker.Calls);

        await device.ExecuteCommandAsync(GoogleCast.StopAddress, "true", default);
        CollectionAssert.AreEqual(new[] { $"connect {Host}:8009", "stop", "dispose" }, speaker.Calls);
    }

    [TestMethod]
    public async Task ASpeakerGroupUsesItsPort()
    {
        var (device, speaker, _, _) = await StartAsync(new() { ["port"] = "32187" });
        await device.ExecuteCommandAsync(GoogleCast.StopAddress, "true", default);
        Assert.AreEqual($"connect {Host}:32187", speaker.Calls[0]);
    }

    [TestMethod]
    [DataRow("ipAddress", "", "ipAddress")]
    [DataRow("ipAddress", "http://192.168.1.20/", "ipAddress")]
    [DataRow("port", "70000", "port")]
    [DataRow("ttsEngine", "alexa", "ttsEngine")]
    [DataRow("language", "en GB", "language")]
    [DataRow("announcementVolume", "150", "announcementVolume")]
    public async Task BadParametersStopTheStart(string key, string value, string problem)
    {
        var parameters = new Dictionary<string, string> { ["ipAddress"] = Host, [key] = value };
        var device = Create(parameters, new FakeSpeaker(), new FakeHttp(), new CastAudioCache(), out var initializeError);
        if (initializeError != null)
        {
            Assert.AreEqual(DeviceState.Error, device.State);
            StringAssert.Contains(device.StateMessage, problem);
            return;
        }
        await Assert.ThrowsAsync<Exception>(() => device.StartAsync(default));
        Assert.AreEqual(DeviceState.Error, device.State);
        StringAssert.Contains(device.StateMessage, problem);
    }

    [TestMethod]
    [DataRow("", BaseUrl, "cloudTtsApiKey")]
    [DataRow("key", "", "nodeBaseUrl")]
    [DataRow("key", "ftp://192.168.1.10", "nodeBaseUrl")]
    [DataRow("key", "http://localhost:8081", "localhost")]
    [DataRow("key", "http://127.0.0.1:8081", "localhost")]
    public async Task GoogleCloudNeedsAKeyAndAnAddressTheSpeakerReaches(string apiKey, string baseUrl, string problem)
    {
        var device = Create(new() { ["ipAddress"] = Host, ["ttsEngine"] = "googleCloud", ["cloudTtsApiKey"] = apiKey, ["nodeBaseUrl"] = baseUrl },
            new FakeSpeaker(), new FakeHttp(), new CastAudioCache(), out _);
        await Assert.ThrowsAsync<Exception>(() => device.StartAsync(default));
        Assert.AreEqual(DeviceState.Error, device.State);
        StringAssert.Contains(device.StateMessage, problem);
    }

    [TestMethod]
    public void TheTemplateDeclaresTheFourCommands()
    {
        var template = new GoogleCast(NullLogger.Instance, new CastAudioCache()).GetConfigurationTemplate();
        CollectionAssert.AreEqual(new[] { "speak", "playUrl", "setVolume", "stop" }, template.CommandTemplates!.Select(x => x.Address).ToArray());
        CollectionAssert.AreEqual(new[] { ValueType.Text, ValueType.Text, ValueType.Number, ValueType.Boolean }, template.CommandTemplates!.Select(x => x.Type).ToArray());
        Assert.AreEqual((0.0, 100.0), (template.CommandTemplates!.ElementAt(2).Schema!.Minimum, template.CommandTemplates!.ElementAt(2).Schema!.Maximum));
        Assert.IsEmpty(template.ReportTemplates);
    }

    [TestMethod]
    public async Task TheRealSessionReportsASpeakerThatDoesNotAnswer()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        await using var session = new SharpCasterSession();
        var error = await Assert.ThrowsAsync<CastException>(() => session.ConnectAsync("127.0.0.1", port, default));
        StringAssert.StartsWith(error.Message, $"Could not connect to the speaker at 127.0.0.1:{port}");
    }

    [TestMethod]
    public void AnIdleStateEndsTheMediaOnceItStartedOrWithAReason()
    {
        Assert.IsFalse(SharpCasterSession.IsEnd(new MediaStatus { PlayerState = PlayerStateType.Idle }, started: false), "idle before the media starts");
        Assert.IsTrue(SharpCasterSession.IsEnd(new MediaStatus { PlayerState = PlayerStateType.Idle }, started: true));
        Assert.IsTrue(SharpCasterSession.IsEnd(new MediaStatus { PlayerState = PlayerStateType.Idle, IdleReason = "FINISHED" }, started: false), "a short media can end before a playing state arrives");
        Assert.IsTrue(SharpCasterSession.IsEnd(new MediaStatus { PlayerState = PlayerStateType.Idle, IdleReason = "ERROR" }, started: false));
    }

    [TestMethod]
    public void TheCacheGivesRandomTokensReusesAPhraseAndForgetsIt()
    {
        var time = new ElectricityPriceTests.ManualTime(new DateTimeOffset(2026, 11, 4, 20, 0, 0, TimeSpan.Zero));
        var cache = new CastAudioCache { Time = time };

        var token = cache.Add("a", [1]);
        Assert.IsTrue(CastAudioCache.IsToken(token));
        Assert.AreNotEqual(token, new CastAudioCache().Add("a", [1]), "tokens are random, not derived from the text");
        Assert.AreEqual(token, cache.Find("a"));
        CollectionAssert.AreEqual(new byte[] { 1 }, cache.Get(token));

        time.Now += TimeSpan.FromMinutes(50);
        Assert.AreEqual(token, cache.Find("a"), "finding a phrase keeps it");
        time.Now += TimeSpan.FromMinutes(50);
        Assert.IsNotNull(cache.Get(token));
        time.Now += CastAudioCache.TimeToLive;
        Assert.IsNull(cache.Get(token));
        Assert.IsNull(cache.Find("a"));
    }

    [TestMethod]
    public void TheCacheKeepsTheNewestEntries()
    {
        var time = new ElectricityPriceTests.ManualTime(new DateTimeOffset(2026, 11, 4, 20, 0, 0, TimeSpan.Zero));
        var cache = new CastAudioCache { Time = time };
        var first = cache.Add("first", [1]);
        for (var i = 0; i < CastAudioCache.MaxEntries; i++)
        {
            time.Now += TimeSpan.FromSeconds(1);
            cache.Add($"phrase {i}", [2]);
        }
        Assert.IsNull(cache.Get(first));
        Assert.IsNotNull(cache.Find($"phrase {CastAudioCache.MaxEntries - 1}"));

        var large = cache.Add("large", new byte[CastAudioCache.MaxBytes]);
        Assert.IsNotNull(cache.Get(large));
        Assert.IsNull(cache.Find("phrase 0"), "a large entry pushes the others out");
        Assert.ThrowsExactly<ArgumentException>(() => cache.Add("too large", new byte[CastAudioCache.MaxBytes + 1]));
    }

    [TestMethod]
    public void TheAudioEndpointIsAnonymousBecauseASpeakerCannotSignIn() =>
        Assert.IsTrue(typeof(CastAudioController).IsDefined(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), inherit: false));

    [TestMethod]
    [DataRow("..%2F..%2Fetc%2Fpasswd", StatusCodes.Status400BadRequest)]
    [DataRow("ABCDEF0123456789ABCDEF0123456789", StatusCodes.Status400BadRequest)]
    [DataRow("0123456789abcdef0123456789abcde", StatusCodes.Status400BadRequest)]
    [DataRow("0123456789abcdef0123456789abcdef", StatusCodes.Status404NotFound)]
    public void TheAudioEndpointAnswersOnlyAKnownToken(string token, int status)
    {
        var result = new CastAudioController(new CastAudioCache()).GetAudio(token);
        Assert.AreEqual(status, ((IStatusCodeHttpResult)result).StatusCode);
    }

    private static async Task<(TestGoogleCast Device, FakeSpeaker Speaker, FakeHttp Http, CastAudioCache Cache)> StartAsync(Dictionary<string, string> parameters)
    {
        var all = new Dictionary<string, string>(parameters);
        all.TryAdd("ipAddress", Host);
        var (speaker, http, cache) = (new FakeSpeaker(), new FakeHttp(), new CastAudioCache());
        var device = Create(all, speaker, http, cache, out var error);
        if (error != null)
            throw error;
        await device.StartAsync(default);
        Assert.AreEqual(DeviceState.Running, device.State, device.StateMessage);
        return (device, speaker, http, cache);
    }

    private static TestGoogleCast Create(Dictionary<string, string> parameters, FakeSpeaker speaker, FakeHttp http, CastAudioCache cache, out Exception? initializeError)
    {
        var device = new TestGoogleCast(speaker, http, cache);
        initializeError = null;
        try
        {
            device.Initialize(new DeviceConfiguration
            {
                Id = "speaker",
                Name = "Speaker",
                DeviceParameters = parameters,
                ReportTemplates = [],
                CommandTemplates = new[] { GoogleCast.SpeakAddress, GoogleCast.PlayUrlAddress, GoogleCast.SetVolumeAddress, GoogleCast.StopAddress }
                    .Select(x => new CommandTemplate { Id = x, Address = x, Name = x }).ToList()
            });
        }
        catch (Exception error)
        {
            initializeError = error;
        }
        return device;
    }

    internal sealed class FakeSpeaker : ICastSession
    {
        public List<string> Calls { get; } = [];
        public List<CastMedia> Played { get; } = [];
        public double? Volume { get; set; } = 0.5;
        public bool Ends { get; set; } = true;
        public Exception? ConnectError { get; set; }
        public Exception? PlayError { get; set; }
        public double? FailVolumeOnce { get; set; }

        public Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            if (ConnectError != null)
                throw ConnectError;
            Calls.Add($"connect {host}:{port}");
            return Task.CompletedTask;
        }

        public Task<double?> GetVolumeAsync(CancellationToken cancellationToken)
        {
            Calls.Add("volume?");
            return Task.FromResult(Volume);
        }

        public Task SetVolumeAsync(double level, CancellationToken cancellationToken)
        {
            Calls.Add(FormattableString.Invariant($"volume {level:0.00}"));
            if (FailVolumeOnce is double failing && Math.Abs(failing - level) < 0.001)
            {
                FailVolumeOnce = null;
                throw new CastException("Could not set the speaker's volume: no answer in 15 s.");
            }
            return Task.CompletedTask;
        }

        public Task<bool> PlayAsync(CastMedia media, TimeSpan maxWait, CancellationToken cancellationToken)
        {
            Calls.Add("play");
            if (PlayError != null)
                throw PlayError;
            Played.Add(media);
            return Task.FromResult(Ends);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Calls.Add("stop");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (Calls.Count > 0)
                Calls.Add("dispose");
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class FakeHttp
    {
        public Queue<(HttpStatusCode Status, string Body)> Answers { get; } = new();
        public List<(string Uri, string? ApiKey, string Body)> Requests { get; } = [];

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.ToString(), request.Headers.TryGetValues("X-Goog-Api-Key", out var keys) ? keys.Single() : null, body));
            var (status, answer) = Answers.Dequeue();
            return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class TestGoogleCast(FakeSpeaker speaker, FakeHttp http, CastAudioCache cache) : GoogleCast(NullLogger.Instance, cache)
    {
        internal override ICastSession CreateSession() => speaker;

        internal override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            http.SendAsync(request, cancellationToken);
    }
}
