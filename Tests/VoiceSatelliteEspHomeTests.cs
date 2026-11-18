using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Core;
using RIoT2.Core.Models;
using RIoT2.Net.Devices.Catalog;
using RIoT2.Net.Devices.Controllers;
using RIoT2.Net.Devices.Services.EspHome;
using RIoT2.Net.Devices.Services.Voice;
using static RIoT2.Net.Devices.Services.EspHome.EspHomeApi;
using static RIoT2.Net.Devices.Tests.VoiceEngineTests;
using static RIoT2.Net.Devices.Tests.VoiceSatelliteTests;

namespace RIoT2.Net.Devices.Tests;

/// <summary>The voice satellite device against a stand-in for hassmic's ESPHome native API.</summary>
[TestClass]
public class VoiceSatelliteEspHomeTests
{
    private const string NodeUrl = "http://192.0.2.10:8081";
    private const uint MediaPlayerKey = 1;
    private const uint MuteKey = 5;
    private const uint DoNotDisturbKey = 12;
    private const uint TimerRingingKey = 44;
    private const uint StopRingingKey = 50;
    private const uint ButtonKey = 51;
    private const uint LightKey = 52;
    private static readonly string Data = Path.Combine(Path.GetTempPath(), $"riot2-voice-tests-{Guid.NewGuid():N}");

    [ClassCleanup]
    public static void DeleteData()
    {
        if (Directory.Exists(Data))
            Directory.Delete(Data, recursive: true);
    }

    [TestMethod]
    public async Task ARequestIsReportedAndItsReplyIsFetchedFromTheNode()
    {
        await using var server = new FakeEspHomeServer();
        var cache = new VoiceAudioCache();
        var device = await StartAsync(server.Port, cache: cache);
        try
        {
            await using var echo = await ConnectedAsync(server, device);

            device.Stt.Text = "Mikä on olohuoneen lämpötila?";
            await RequestAsync(echo, [Audio.Silence(810), Audio.Noise(300, -60), Audio.Tone(600, -20), Audio.Noise(1200, -60)]);
            Assert.AreEqual(VoiceEvent.SttVadEnd, (await EventAsync(echo)).Type);
            var heard = await EventAsync(echo);
            Assert.AreEqual((VoiceEvent.SttEnd, "Mikä on olohuoneen lämpötila?"), (heard.Type, heard.Data["text"]));
            Assert.AreEqual(VoiceEvent.IntentStart, (await EventAsync(echo)).Type);
            Assert.AreEqual("Mikä on olohuoneen lämpötila?", (await device.WaitForReportAsync(VoiceSatellite.TranscriptAddress)).Value.GetValue<string>());

            device.Tts.Audio = new PcmAudio(22050, 1, Audio.Tone(300, -20, 22050));
            var speak = device.ExecuteCommandAsync(VoiceSatellite.SpeakAddress, "\"Olohuoneessa on 21 astetta.\"", default);
            Assert.AreEqual(VoiceEvent.IntentEnd, (await EventAsync(echo)).Type);
            var start = await EventAsync(echo);
            Assert.AreEqual((VoiceEvent.TtsStart, "Olohuoneessa on 21 astetta."), (start.Type, start.Data["text"]));
            var end = await EventAsync(echo);
            Assert.AreEqual(VoiceEvent.TtsEnd, end.Type);
            Assert.AreEqual(VoiceEvent.RunEnd, (await EventAsync(echo)).Type);

            var url = end.Data["url"];
            StringAssert.StartsWith(url, NodeUrl + "/api/voice/audio/");
            var token = url[(url.LastIndexOf('/') + 1)..];
            CollectionAssert.AreEqual(Wav.Encode(device.Tts.Audio), cache.Get(token), "the node serves the reply as WAV");
            var served = (FileContentHttpResult)new VoiceAudioController(cache).GetAudio(token);
            Assert.AreEqual("audio/wav", served.ContentType);

            // The satellite fetches and plays it: its media player goes from playing back to idle.
            Assert.AreEqual(VoiceSatellite.Speaking, device.SatelliteState);
            await echo.WriteAsync(MediaPlayerState, new ProtoWriter().Fixed32(1, MediaPlayerKey).UInt(2, MediaPlayerPlaying).Float(3, 0.5f));
            await Task.Delay(100);
            Assert.IsFalse(speak.IsCompleted, "the reply plays");
            await echo.WriteAsync(MediaPlayerState, new ProtoWriter().Fixed32(1, MediaPlayerKey).UInt(2, MediaPlayerIdle).Float(3, 0.5f));
            await speak.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(VoiceSatellite.Idle, device.SatelliteState);
            CollectionAssert.AreEqual(
                new[] { VoiceSatellite.Offline, VoiceSatellite.Idle, VoiceSatellite.Listening, VoiceSatellite.Thinking, VoiceSatellite.Speaking, VoiceSatellite.Idle },
                device.States());
            Assert.AreEqual(1, device.Reports.Count(x => x.Id == VoiceSatellite.VolumeAddress), "an unchanged volume is reported once");
        }
        finally
        {
            await device.StopAsync(default);
        }
    }

    [TestMethod]
    public async Task TheSatellitesOwnEndOfTheRequestIsRecognized()
    {
        await using var server = new FakeEspHomeServer();
        var device = await StartAsync(server.Port);
        try
        {
            await using var echo = await ConnectedAsync(server, device);
            await RequestAsync(echo, [Audio.Silence(810), Audio.Tone(600, -20)]);
            await echo.WriteAsync(VoiceAssistantAudio, new ProtoWriter().Bool(2, true));

            Assert.AreEqual(VoiceEvent.SttVadEnd, (await EventAsync(echo)).Type);
            var heard = await EventAsync(echo);
            Assert.AreEqual((VoiceEvent.SttEnd, "Turn on the lights."), (heard.Type, heard.Data["text"]));
        }
        finally
        {
            await device.StopAsync(default);
        }
    }

    [TestMethod]
    public async Task NothingSaidEndsQuietlyAndARequestTheSatelliteGivesUpEndsWithoutEvents()
    {
        await using var server = new FakeEspHomeServer();
        var device = await StartAsync(server.Port);
        try
        {
            await using var echo = await ConnectedAsync(server, device);
            await RequestAsync(echo, [Audio.Silence(500)]);
            await echo.WriteAsync(VoiceAssistantAudio, new ProtoWriter().Bool(2, true));
            var error = await EventAsync(echo);
            Assert.AreEqual((VoiceEvent.Error, VoiceSatellite.NoTextCode), (error.Type, error.Data["code"]));
            Assert.AreEqual(VoiceEvent.RunEnd, (await EventAsync(echo)).Type);

            await RequestAsync(echo, [Audio.Silence(810), Audio.Tone(600, -20)]);
            await device.WaitForStateAsync(VoiceSatellite.Listening);
            await echo.WriteAsync(VoiceAssistantRequest, new ProtoWriter().Bool(1, false));
            await device.WaitForStateAsync(VoiceSatellite.Idle);
            await echo.WriteAsync(PingRequest);
            await echo.ExpectAsync(PingResponse);
            Assert.IsFalse(device.Reports.Any(x => x.Id == VoiceSatellite.TranscriptAddress));
        }
        finally
        {
            await device.StopAsync(default);
        }
    }

    [TestMethod]
    public async Task AWakeWordToBeHeardOnTheServerIsRefused()
    {
        await using var server = new FakeEspHomeServer();
        var device = await StartAsync(server.Port);
        try
        {
            await using var echo = await ConnectedAsync(server, device);
            await echo.WriteAsync(VoiceAssistantRequest, new ProtoWriter().Bool(1, true).UInt(3, 3));
            Assert.IsTrue(Proto.Bool(await echo.ExpectAsync(VoiceAssistantResponse), 2), "error");
            Assert.AreEqual(VoiceSatellite.Idle, device.SatelliteState);
        }
        finally
        {
            await device.StopAsync(default);
        }
    }

    [TestMethod]
    public async Task SpeakWithoutARequestIsAnAnnouncement()
    {
        await using var server = new FakeEspHomeServer();
        var device = await StartAsync(server.Port);
        try
        {
            await using var echo = await ConnectedAsync(server, device);
            var speak = device.ExecuteCommandAsync(VoiceSatellite.SpeakAddress, "\"Dinner is ready.\"", default);
            var announce = await echo.ExpectAsync(VoiceAssistantAnnounce);
            StringAssert.StartsWith(Proto.String(announce, 1), NodeUrl + "/api/voice/audio/");
            Assert.AreEqual("Dinner is ready.", Proto.String(announce, 2));
            await echo.WriteAsync(VoiceAssistantAnnounceFinished, new ProtoWriter().Bool(1, true));
            await speak.WaitAsync(TimeSpan.FromSeconds(10));

            // Do not disturb: the satellite doesn't play it, and the command fails.
            speak = device.ExecuteCommandAsync(VoiceSatellite.SpeakAddress, "\"Dinner is ready.\"", default);
            await echo.ExpectAsync(VoiceAssistantAnnounce);
            await echo.WriteAsync(VoiceAssistantAnnounceFinished, new ProtoWriter().Bool(1, false));
            var refused = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => speak);
            StringAssert.Contains(refused.Message, "do not disturb");
            Assert.AreEqual(VoiceSatellite.Idle, device.SatelliteState);
        }
        finally
        {
            await device.StopAsync(default);
        }
    }

    [TestMethod]
    public async Task EntityStatesAreReportedWhenTheyChange()
    {
        await using var server = new FakeEspHomeServer();
        var device = await StartAsync(server.Port);
        try
        {
            await using var echo = await ConnectedAsync(server, device);
            await echo.WriteAsync(MediaPlayerState, new ProtoWriter().Fixed32(1, MediaPlayerKey).UInt(2, MediaPlayerIdle).Float(3, 0.42f));
            await echo.WriteAsync(SwitchState, new ProtoWriter().Fixed32(1, MuteKey).Bool(2, true));
            await echo.WriteAsync(SwitchState, new ProtoWriter().Fixed32(1, MuteKey).Bool(2, true));
            await echo.WriteAsync(SwitchState, new ProtoWriter().Fixed32(1, DoNotDisturbKey).Bool(2, false));
            await echo.WriteAsync(BinarySensorState, new ProtoWriter().Fixed32(1, TimerRingingKey).Bool(2, true));
            await echo.WriteAsync(LightState, new ProtoWriter().Fixed32(1, LightKey).Bool(2, true).Float(3, 0.5f)
                .Float(4, 1f).Float(5, 0.5f).Float(6, 0f).String(9, "Rainbow"));
            await echo.WriteAsync(Event, new ProtoWriter().Fixed32(1, ButtonKey).String(2, "action_press"));
            await echo.WriteAsync(Event, new ProtoWriter().Fixed32(1, ButtonKey).String(2, "action_press"));
            await echo.WriteAsync(Event, new ProtoWriter().Fixed32(1, 999).String(2, "unknown entity"));

            await device.WaitUntilAsync(() => device.Reports.Count(x => x.Id == VoiceSatellite.ButtonAddress) == 2, "each button press is reported");
            Assert.AreEqual(42d, Value(device, VoiceSatellite.VolumeAddress).GetValue<double>());
            Assert.IsTrue(Value(device, VoiceSatellite.MuteAddress).GetValue<bool>());
            Assert.AreEqual(1, device.Reports.Count(x => x.Id == VoiceSatellite.MuteAddress), "an unchanged state is reported once");
            Assert.IsFalse(Value(device, VoiceSatellite.DoNotDisturbAddress).GetValue<bool>());
            Assert.IsTrue(Value(device, VoiceSatellite.TimerRingingAddress).GetValue<bool>());
            var light = Value(device, VoiceSatellite.LightAddress).GetValue<JsonElement>();
            Assert.AreEqual((true, 50, "#ff8000", "Rainbow"),
                (light.GetProperty("on").GetBoolean(), light.GetProperty("brightness").GetInt32(), light.GetProperty("color").GetString(), light.GetProperty("effect").GetString()));
            Assert.AreEqual("action_press", Value(device, VoiceSatellite.ButtonAddress).GetValue<string>());
        }
        finally
        {
            await device.StopAsync(default);
        }
    }

    [TestMethod]
    public async Task CommandsReachTheSatellitesEntities()
    {
        await using var server = new FakeEspHomeServer();
        var device = await StartAsync(server.Port);
        try
        {
            await using var echo = await ConnectedAsync(server, device);

            await device.ExecuteCommandAsync(VoiceSatellite.VolumeAddress, "\"30\"", default);
            var volume = await echo.ExpectAsync(MediaPlayerCommand);
            Assert.AreEqual((MediaPlayerKey, true, 0.3f), ((uint)Proto.UInt(volume, 1), Proto.Bool(volume, 4), Proto.Float(volume, 5)));

            await device.ExecuteCommandAsync(VoiceSatellite.MuteAddress, "true", default);
            var mute = await echo.ExpectAsync(SwitchCommand);
            Assert.AreEqual((MuteKey, true), ((uint)Proto.UInt(mute, 1), Proto.Bool(mute, 2)));
            await device.ExecuteCommandAsync(VoiceSatellite.DoNotDisturbAddress, "false", default);
            var dnd = await echo.ExpectAsync(SwitchCommand);
            Assert.AreEqual((DoNotDisturbKey, false), ((uint)Proto.UInt(dnd, 1), Proto.Bool(dnd, 2)));

            await device.ExecuteCommandAsync(VoiceSatellite.LightAddress, "{\"color\":\"#ff0000\",\"brightness\":40}", default);
            var light = await echo.ExpectAsync(LightCommand);
            Assert.AreEqual(LightKey, (uint)Proto.UInt(light, 1));
            Assert.IsTrue(Proto.Bool(light, 2) && Proto.Bool(light, 3), "a colour turns the light on");
            Assert.IsTrue(Proto.Bool(light, 4) && Proto.Bool(light, 6));
            Assert.AreEqual((0.4f, 1f, 0f, 0f), (Proto.Float(light, 5), Proto.Float(light, 7), Proto.Float(light, 8), Proto.Float(light, 9)));
            Assert.IsFalse(Proto.Bool(light, 18), "no effect named");

            await device.ExecuteCommandAsync(VoiceSatellite.LightAddress, "{\"effect\":\"pulse\"}", default);
            var effect = await echo.ExpectAsync(LightCommand);
            Assert.AreEqual((true, "Pulse"), (Proto.Bool(effect, 18), Proto.String(effect, 19)));
            await device.ExecuteCommandAsync(VoiceSatellite.LightAddress, "false", default);
            var off = await echo.ExpectAsync(LightCommand);
            Assert.AreEqual((true, false, false), (Proto.Bool(off, 2), Proto.Bool(off, 3), Proto.Bool(off, 4)));

            await device.ExecuteCommandAsync(VoiceSatellite.StopRingingAddress, "true", default);
            Assert.AreEqual(StopRingingKey, (uint)Proto.UInt(await echo.ExpectAsync(ButtonCommand), 1));

            await Assert.ThrowsExactlyAsync<ArgumentException>(() => device.ExecuteCommandAsync(VoiceSatellite.LightAddress, "{\"effect\":\"Disco\"}", default));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => device.ExecuteCommandAsync(VoiceSatellite.LightAddress, "{\"color\":\"red\"}", default));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => device.ExecuteCommandAsync(VoiceSatellite.VolumeAddress, "150", default));
            await echo.WriteAsync(PingRequest);
            await echo.ExpectAsync(PingResponse);
        }
        finally
        {
            await device.StopAsync(default);
        }
    }

    [TestMethod]
    public async Task TheWakeWordIsReportedAndChosen()
    {
        await using var server = new FakeEspHomeServer();
        var device = await StartAsync(server.Port);
        try
        {
            await using var echo = await ConnectedAsync(server, device);
            await echo.WriteAsync(VoiceAssistantConfigurationResponse, WakeWords("alexa-en-US"));
            await device.WaitUntilAsync(() => device.Reports.Any(x => x.Id == VoiceSatellite.WakeWordAddress), "the wake word is reported");
            Assert.AreEqual("alexa-en-US", Value(device, VoiceSatellite.WakeWordAddress).GetValue<string>());

            await device.ExecuteCommandAsync(VoiceSatellite.WakeWordAddress, "\"computer\"", default);
            CollectionAssert.AreEqual(new[] { "computer-en-US" }, Proto.Strings(await echo.ExpectAsync(VoiceAssistantSetConfiguration), 1));
            await echo.ExpectAsync(VoiceAssistantConfigurationRequest);
            await echo.WriteAsync(VoiceAssistantConfigurationResponse, WakeWords("computer-en-US"));
            await device.WaitUntilAsync(() => Value(device, VoiceSatellite.WakeWordAddress).GetValue<string>() == "computer-en-US", "the new wake word is reported");

            var unknown = await Assert.ThrowsExactlyAsync<ArgumentException>(() => device.ExecuteCommandAsync(VoiceSatellite.WakeWordAddress, "\"hey jarvis\"", default));
            StringAssert.Contains(unknown.Message, "alexa-en-US, computer-en-US");
        }
        finally
        {
            await device.StopAsync(default);
        }
    }

    [TestMethod]
    public async Task TimersRingOnTheSatelliteAndAreToldAgainAfterAReconnect()
    {
        await using var server = new FakeEspHomeServer();
        var device = await StartAsync(server.Port);
        try
        {
            var echo = await ConnectedAsync(server, device);
            await device.ExecuteCommandAsync(VoiceSatellite.TimerAddress, "{\"seconds\":1,\"name\":\"Tea\"}", default);
            var started = await echo.ExpectAsync(VoiceAssistantTimerEvent);
            Assert.AreEqual(((ulong)TimerEvent.Started, "Tea", 1UL, 1UL, true),
                (Proto.UInt(started, 1), Proto.String(started, 3), Proto.UInt(started, 4), Proto.UInt(started, 5), Proto.Bool(started, 6)));
            var finished = await echo.ExpectAsync(VoiceAssistantTimerEvent);
            Assert.AreEqual(((ulong)TimerEvent.Finished, Proto.String(started, 2), false),
                (Proto.UInt(finished, 1), Proto.String(finished, 2), Proto.Bool(finished, 6)));

            await device.ExecuteCommandAsync(VoiceSatellite.TimerAddress, "{\"seconds\":300,\"name\":\"Pasta\"}", default);
            var pasta = Proto.String(await echo.ExpectAsync(VoiceAssistantTimerEvent), 2);

            // The connection drops: the new one hears of the timer that runs.
            await echo.DisposeAsync();
            await device.WaitForStateAsync(VoiceSatellite.Offline);
            await using var again = await ConnectedAsync(server, device);
            var told = await again.ExpectAsync(VoiceAssistantTimerEvent);
            Assert.AreEqual(((ulong)TimerEvent.Started, pasta, "Pasta", 300UL), (Proto.UInt(told, 1), Proto.String(told, 2), Proto.String(told, 3), Proto.UInt(told, 4)));
            Assert.IsTrue(Proto.UInt(told, 5) is > 290 and <= 300, "the time left");

            await device.ExecuteCommandAsync(VoiceSatellite.TimerAddress, "{\"cancel\":true,\"name\":\"pasta\"}", default);
            var cancelled = await again.ExpectAsync(VoiceAssistantTimerEvent);
            Assert.AreEqual(((ulong)TimerEvent.Cancelled, pasta), (Proto.UInt(cancelled, 1), Proto.String(cancelled, 2)));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => device.ExecuteCommandAsync(VoiceSatellite.TimerAddress, "{\"cancel\":true,\"name\":\"Pasta\"}", default));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => device.ExecuteCommandAsync(VoiceSatellite.TimerAddress, "{\"seconds\":0}", default));
        }
        finally
        {
            await device.StopAsync(default);
        }
    }

    [TestMethod]
    public async Task TheSatelliteGetsTheTime()
    {
        await using var server = new FakeEspHomeServer();
        var device = await StartAsync(server.Port);
        try
        {
            await using var echo = await ConnectedAsync(server, device);
            await echo.WriteAsync(GetTimeRequest);
            var time = await echo.ExpectAsync(GetTimeResponse);
            Assert.AreEqual(WireType.Fixed32, time.Single(x => x.Number == 1).Type, "epoch_seconds is a fixed32");
            Assert.IsTrue(Math.Abs((long)Proto.UInt(time, 1) - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) < 10);
        }
        finally
        {
            await device.StopAsync(default);
        }
    }

    [TestMethod]
    public async Task AnEncryptionKeyIsGivenToASatelliteWithoutOneAndUsedFromThenOn()
    {
        await using var server = new FakeEspHomeServer();
        var key = RandomNumberGenerator.GetBytes(32);
        var device = await StartAsync(server.Port, new() { ["encryptionKey"] = Convert.ToBase64String(key) });
        try
        {
            // The key is refused, the all-zero one let in, and the key given over that link.
            await using (var provisioning = await server.AcceptAsync())
            {
                Assert.IsTrue(provisioning.Encrypted);
                await provisioning.ExpectAsync(HelloRequest);
                CollectionAssert.AreEqual(key, Proto.Bytes(await provisioning.ExpectAsync(NoiseEncryptionSetKeyRequest), 1));
                server.Key = key;
                await provisioning.WriteAsync(NoiseEncryptionSetKeyResponse, new ProtoWriter().Bool(1, true));
            }
            await using var echo = await ConnectedAsync(server, device);
            Assert.IsTrue(echo.Encrypted);
            Assert.AreEqual(1, server.RefusedHandshakes);
        }
        finally
        {
            await device.StopAsync(default);
        }
    }

    [TestMethod]
    public async Task ASatelliteThatTookTheKeyIsNeverOfferedItAgain()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        await using var server = new FakeEspHomeServer { Key = key };
        var device = await StartAsync(server.Port, new() { ["encryptionKey"] = Convert.ToBase64String(key) });
        try
        {
            var echo = await ConnectedAsync(server, device);

            // Something without a key answers at the satellite's address now: it must not be given the key.
            server.Key = null;
            await echo.DisposeAsync();
            var accepting = server.AcceptAsync(TimeSpan.FromSeconds(3));
            await device.WaitUntilAsync(() => device.StateMessage.Contains("refused the encryption key it took before"), "the device says the key was refused");
            await Assert.ThrowsAsync<OperationCanceledException>(() => accepting);
            Assert.IsTrue(server.RefusedHandshakes >= 1);
        }
        finally
        {
            await device.StopAsync(default);
        }
    }

    [TestMethod]
    public async Task ASatelliteWithAnotherKeyIsNotConnectedAndTheDeviceSaysWhy()
    {
        await using var server = new FakeEspHomeServer { Key = RandomNumberGenerator.GetBytes(32) };
        var device = await StartAsync(server.Port, new() { ["encryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) });
        try
        {
            var accepting = server.AcceptAsync(TimeSpan.FromSeconds(3));
            await device.WaitUntilAsync(() => device.StateMessage.Contains("another encryption key"), "the device says the satellite has another key");
            Assert.AreEqual(VoiceSatellite.Offline, device.SatelliteState);
            await Assert.ThrowsAsync<OperationCanceledException>(() => accepting);
        }
        finally
        {
            await device.StopAsync(default);
        }
    }

    [TestMethod]
    [DataRow("nodeBaseUrl", "", "esphome needs the nodeBaseUrl")]
    [DataRow("nodeBaseUrl", "http://localhost:8081", "not localhost")]
    [DataRow("encryptionKey", "c2hvcnQ=", "32 bytes in base64")]
    [DataRow("encryptionKey", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "must not be all zeros")]
    [DataRow("protocol", "mqtt", "The protocol must be")]
    public async Task InvalidEspHomeParametersStopTheDeviceWithTheReason(string key, string value, string reason)
    {
        var device = Create(Parameters(1, new() { [key] = value }));
        await Assert.ThrowsAsync<Exception>(() => device.StartAsync(default));
        Assert.AreEqual(DeviceState.Error, device.State);
        StringAssert.Contains(device.StateMessage, reason);
    }

    [TestMethod]
    public async Task TheEspHomeCommandsNeedTheEspHomeProtocol()
    {
        var parameters = Parameters(1);
        parameters["protocol"] = "wyoming";
        var device = Create(parameters);
        await device.StartAsync(default);
        try
        {
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => device.ExecuteCommandAsync(VoiceSatellite.TimerAddress, "{\"seconds\":5}", default));
            StringAssert.Contains(error.Message, "esphome");
        }
        finally
        {
            await device.StopAsync(default);
        }
    }

    // The request: the satellite asks for it (its wake word was heard) and streams the microphone, in 2048-byte chunks.
    private static async Task RequestAsync(FakeEspHomeLink echo, byte[][] audio)
    {
        await echo.WriteAsync(VoiceAssistantRequest, new ProtoWriter().Bool(1, true).String(2, "conversation").UInt(3, 1).String(5, "Alexa"));
        Assert.IsFalse(Proto.Bool(await echo.ExpectAsync(VoiceAssistantResponse), 2), "the request is taken");
        Assert.AreEqual(VoiceEvent.RunStart, (await EventAsync(echo)).Type);
        Assert.AreEqual(VoiceEvent.SttStart, (await EventAsync(echo)).Type);
        foreach (var part in audio)
        {
            for (var at = 0; at < part.Length; at += 2048)
                await echo.WriteAsync(VoiceAssistantAudio, new ProtoWriter().Bytes(1, part.AsSpan(at, Math.Min(2048, part.Length - at))));
        }
    }

    private static async Task<(VoiceEvent Type, Dictionary<string, string> Data)> EventAsync(FakeEspHomeLink echo)
    {
        var fields = await echo.ExpectAsync(VoiceAssistantEvent);
        var data = fields.Where(x => x.Number == 2).Select(x => Proto.Decode(x.Data)).ToDictionary(x => Proto.String(x, 1), x => Proto.String(x, 2));
        return ((VoiceEvent)Proto.UInt(fields, 1), data);
    }

    // What hassmic answers when the node connects: its hello, device info and entities; then the node subscribes.
    private static async Task<FakeEspHomeLink> ConnectedAsync(FakeEspHomeServer server, TestVoiceSatellite device)
    {
        var echo = await server.AcceptAsync();
        Assert.AreEqual("RIoT2 voice satellite", Proto.String(await echo.ExpectAsync(HelloRequest), 1));
        await echo.ExpectAsync(ConnectRequest);
        await echo.ExpectAsync(DeviceInfoRequest);
        await echo.ExpectAsync(ListEntitiesRequest);
        await echo.WriteAsync(HelloResponse, new ProtoWriter().UInt(1, 1).UInt(2, 15).String(3, "hassmic 1.0").String(4, "echo-dot-2"));
        await echo.WriteAsync(ConnectResponse);
        await echo.WriteAsync(DeviceInfoResponse, new ProtoWriter().String(2, "echo-dot-2").String(3, "AA:BB:CC:DD:EE:FF")
            .String(8, "hassmic.echo-dot-2").String(9, "1.0").UInt(17, 1 | 4 | 8 | 16 | 32));
        await echo.WriteAsync(ListEntitiesMediaPlayer, Entity("media_player", MediaPlayerKey, "Media Player"));
        await echo.WriteAsync(ListEntitiesSwitch, Entity("mute", MuteKey, "Mute"));
        await echo.WriteAsync(ListEntitiesSwitch, Entity("do_not_disturb", DoNotDisturbKey, "Do not disturb"));
        await echo.WriteAsync(ListEntitiesEvent, Entity("button", ButtonKey, "Button").String(9, "action_press").String(9, "action_hold"));
        await echo.WriteAsync(ListEntitiesBinarySensor, Entity("timer_ringing", TimerRingingKey, "Timer ringing"));
        await echo.WriteAsync(ListEntitiesButton, Entity("stop_ringing", StopRingingKey, "Stop ringing"));
        await echo.WriteAsync(ListEntitiesLight, Entity("light_ring", LightKey, "Light ring").String(11, "None").String(11, "Rainbow").String(11, "Pulse"));
        await echo.WriteAsync(49, Entity("equalizer_bass", 14, "Equalizer bass"));
        await echo.WriteAsync(ListEntitiesDone);
        await echo.ExpectAsync(SubscribeStates);
        var subscribe = await echo.ExpectAsync(SubscribeVoiceAssistant);
        Assert.AreEqual((true, SubscribeApiAudio), (Proto.Bool(subscribe, 1), (uint)Proto.UInt(subscribe, 2)), "the microphone's audio comes over the connection");
        await echo.ExpectAsync(VoiceAssistantConfigurationRequest);
        await device.WaitForStateAsync(VoiceSatellite.Idle);
        return echo;
    }

    private static ProtoWriter Entity(string objectId, uint key, string name) =>
        new ProtoWriter().String(1, objectId).Fixed32(2, key).String(3, name);

    private static ProtoWriter WakeWords(string active) => new ProtoWriter()
        .Message(1, new ProtoWriter().String(1, "alexa-en-US").String(2, "Alexa").String(3, "en-US"))
        .Message(1, new ProtoWriter().String(1, "computer-en-US").String(2, "Computer").String(3, "en-US"))
        .String(2, active)
        .UInt(3, 1);

    private static ValueModel Value(TestVoiceSatellite device, string address) =>
        device.Reports.Last(x => x.Id == address).Value;

    private static Dictionary<string, string> Parameters(int port, Dictionary<string, string>? extra = null)
    {
        var parameters = new Dictionary<string, string>
        {
            ["host"] = "127.0.0.1",
            ["protocol"] = "esphome",
            ["port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["nodeBaseUrl"] = NodeUrl,
            ["language"] = "fi-FI",
            ["sttUrl"] = "tcp://127.0.0.1:10300",
            ["ttsUrl"] = "tcp://127.0.0.1:10200",
            ["silenceMs"] = "600"
        };
        foreach (var (key, value) in extra ?? [])
            parameters[key] = value;
        return parameters;
    }

    private static async Task<TestVoiceSatellite> StartAsync(int port, Dictionary<string, string>? extra = null, VoiceAudioCache? cache = null)
    {
        var device = Create(Parameters(port, extra), cache);
        device.ReconnectDelays = [TimeSpan.FromMilliseconds(50)];
        device.DataDirectory = Data;
        await device.StartAsync(default);
        Assert.AreEqual(DeviceState.Running, device.State, device.StateMessage);
        return device;
    }

    private static TestVoiceSatellite Create(Dictionary<string, string> parameters, VoiceAudioCache? cache = null)
    {
        var device = new TestVoiceSatellite(cache);
        var template = device.GetConfigurationTemplate();
        device.Initialize(new DeviceConfiguration
        {
            Id = "echo",
            Name = "Echo",
            DeviceParameters = parameters,
            ReportTemplates = template.ReportTemplates.Select(x => new ReportTemplate { Id = x.Address, Address = x.Address, Name = x.Name }).ToList(),
            CommandTemplates = template.CommandTemplates!.Select(x => new CommandTemplate { Id = x.Address, Address = x.Address, Name = x.Name }).ToList()
        });
        return device;
    }
}
