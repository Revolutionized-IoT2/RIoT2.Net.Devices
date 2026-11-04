using System.Collections.Concurrent;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Core;
using RIoT2.Core.Models;
using RIoT2.Core.Models.Matter;
using RIoT2.Net.Devices.Catalog;
using RIoT2.Net.Devices.Models;
using RIoT2.Net.Devices.Services.Harvia;

namespace RIoT2.Net.Devices.Tests;

/// <summary>
/// The Harvia device against a scripted MyHarvia cloud: endpoint discovery, a Cognito user pool that checks the SRP
/// password proof like the real one, AppSync GraphQL and scripted push sockets. Nothing reaches Harvia.
/// </summary>
[TestClass]
public class HarviaTests
{
    private const string Sauna = "sauna-1";

    private static readonly string[] _allKeys =
    [
        Harvia.Power, Harvia.StateKey, Harvia.Mode, Harvia.Temperature, Harvia.Humidity, Harvia.TargetTemperature, Harvia.RemainingTime,
        Harvia.Heating, Harvia.Door, Harvia.Light, Harvia.Connected, Harvia.SessionEvent, Harvia.LastSession, Harvia.SessionsToday,
        Harvia.BathingMinutesToday, Harvia.HeaterPowerNow, Harvia.EnergyLastSession, Harvia.EnergyToday, Harvia.Ready, Harvia.TimeToReady,
        Harvia.ReadyAt, Harvia.ScheduleEnabled, Harvia.ScheduleReadyAt, Harvia.ScheduleStartAt, Harvia.ScheduleDuration, Harvia.ScheduleTargetTemperature
    ];

    private string _dataDirectory = null!;

    [TestInitialize]
    public void CreateDataDirectory()
    {
        _dataDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);
    }

    [TestCleanup]
    public void DeleteDataDirectory() => Directory.Delete(_dataDirectory, true);

    [TestMethod]
    public void TheSrpProofMatchesAmazonsHelper()
    {
        // A vector from pycognito's AWSSRP (the Python port of amazon-cognito-identity-js) with a fixed a, b and time.
        var a = CognitoSrp.Parse(string.Concat(Enumerable.Repeat("1f", 64))) % CognitoSrp.N;
        var bigA = BigInteger.ModPow(CognitoSrp.G, a, CognitoSrp.N);
        const string salt = "0a1b2c3d4e5f60718293a4b5c6d7e8f9";
        const string userId = "11111111-2222-3333-4444-555555555555";
        var x = CognitoSrp.Parse(CognitoSrp.HexHash(CognitoSrp.PadHex(salt) + CognitoSrp.Sha256Hex(Encoding.UTF8.GetBytes($"TestPool1{userId}:Sauna-Pass1"))));
        var v = BigInteger.ModPow(CognitoSrp.G, x, CognitoSrp.N);
        var b = CognitoSrp.Parse(string.Concat(Enumerable.Repeat("2e", 32)));
        var bigB = (CognitoSrp.K * v + BigInteger.ModPow(CognitoSrp.G, b, CognitoSrp.N)) % CognitoSrp.N;

        var timestamp = CognitoSrp.Timestamp(new DateTimeOffset(2026, 11, 4, 21, 54, 3, TimeSpan.FromHours(2)));
        Assert.AreEqual("Wed Nov 4 19:54:03 UTC 2026", timestamp, "the day isn't padded and the culture is invariant");
        var signature = CognitoSrp.Signature("TestPool1", userId, "Sauna-Pass1", a, bigA, salt, CognitoSrp.Hex(bigB),
            Convert.ToBase64String(Encoding.UTF8.GetBytes("secret-block-bytes")), timestamp);
        Assert.AreEqual("O9uHgUXG6bpNZyFg/wArs0QEvBMsUxjDd0C4VdjT0RA=", signature);

        Assert.AreEqual(("0f", "0080", "02"), (CognitoSrp.PadHex("f"), CognitoSrp.PadHex("80"), CognitoSrp.PadHex(CognitoSrp.G)));
    }

    [TestMethod]
    public async Task ALoginReadsTheSaunasAndBuildsTheTemplateAndMatterEndpoints()
    {
        var time = new ManualTime();
        var api = new FakeHarvia(time);
        var device = Device(api, time, heaterPower: "9", reports: Reports(_allKeys));
        var reports = Capture(device);

        await device.StartAsync(CancellationToken.None);

        Assert.AreEqual(DeviceState.Running, device.State);
        var cognito = api.Requests.Where(r => r.Url == FakeHarvia.Cognito).ToList();
        Assert.AreEqual(("AWSCognitoIdentityProviderService.InitiateAuth", "AWSCognitoIdentityProviderService.RespondToAuthChallenge"),
            (cognito[0].Header("X-Amz-Target"), cognito[1].Header("X-Amz-Target")));
        var initiate = JsonNode.Parse(cognito[0].Body!)!;
        Assert.AreEqual(("USER_SRP_AUTH", "client-1", "user@example.com"), (initiate["AuthFlow"]!.GetValue<string>(),
            initiate["ClientId"]!.GetValue<string>(), initiate["AuthParameters"]!["USERNAME"]!.GetValue<string>()));
        Assert.IsFalse(cognito[1].Body!.Contains("Sauna-Pass1", StringComparison.Ordinal), "the password never leaves the node");
        var tree = api.Requests.First(r => r.Url == FakeHarvia.DeviceApi);
        Assert.AreEqual("id-1", tree.Header("authorization"), "AppSync takes the ID token as it is, without Bearer");
        StringAssert.Contains(File.ReadAllText(device.TokenFile("user@example.com")), "refresh-1", "the refresh token is kept for the next start");

        var values = Last(reports);
        Assert.IsTrue(values[Key(Harvia.Power)].GetValue<bool>());
        Assert.AreEqual("heating", values[Key(Harvia.StateKey)].GetValue<string>());
        Assert.AreEqual(Harvia.MatterHeat, values[Key(Harvia.Mode)].GetValue<int>());
        Assert.AreEqual(62d, values[Key(Harvia.Temperature)].GetValue<double>());
        Assert.AreEqual(12d, values[Key(Harvia.Humidity)].GetValue<double>());
        Assert.AreEqual(80, values[Key(Harvia.TargetTemperature)].GetValue<int>());
        Assert.AreEqual(300, values[Key(Harvia.RemainingTime)].GetValue<int>());
        Assert.IsTrue(values[Key(Harvia.Heating)].GetValue<bool>());
        Assert.IsFalse(values[Key(Harvia.Door)].GetValue<bool>());
        Assert.IsTrue(values[Key(Harvia.Light)].GetValue<bool>());
        Assert.IsTrue(values[Key(Harvia.Connected)].GetValue<bool>());
        Assert.AreEqual("started", values[Key(Harvia.SessionEvent)].GetValue<string>(), "a sauna already on counts from now");
        Assert.AreEqual(9d, values[Key(Harvia.HeaterPowerNow)].GetValue<double>());
        Assert.AreEqual(0d, values[Key(Harvia.EnergyToday)].GetValue<double>());
        Assert.IsFalse(values[Key(Harvia.Ready)].GetValue<bool>());
        Assert.IsFalse(values.ContainsKey(Key(Harvia.TimeToReady)), "one temperature gives no trend yet");
        Assert.IsTrue(values[Key(Harvia.ScheduleEnabled)].GetValue<bool>());
        Assert.AreEqual("2026-11-05T18:00:00+00:00", values[Key(Harvia.ScheduleReadyAt)].GetValue<string>());
        Assert.AreEqual("2026-11-05T17:00:00+00:00", values[Key(Harvia.ScheduleStartAt)].GetValue<string>(), "the heater ignites a heat-up time before");
        Assert.AreEqual((90, 75), (values[Key(Harvia.ScheduleDuration)].GetValue<int>(), values[Key(Harvia.ScheduleTargetTemperature)].GetValue<int>()));

        var calls = api.Requests.Count;
        var template = device.GetConfigurationTemplate();
        Assert.HasCount(calls, api.Requests, "the template comes from the last poll");
        var addresses = template.ReportTemplates.Select(r => r.Address).ToList();
        CollectionAssert.IsSubsetOf(_allKeys.Select(Key).ToArray(), addresses);
        Assert.AreEqual("Sauna boven temperature", template.ReportTemplates.Single(r => r.Address == Key(Harvia.Temperature)).Name);
        var setpoint = template.CommandTemplates!.Single(c => c.Address == Key(Harvia.TargetTemperature));
        Assert.AreEqual((40d, 110d), (setpoint.Schema!.Minimum, setpoint.Schema.Maximum), "the bridge takes the thermostat's limits from here");
        CollectionAssert.IsSubsetOf(new[] { Key(Harvia.Power), Key(Harvia.Light), Key(Harvia.Mode), "refresh" }, template.CommandTemplates!.Select(c => c.Address).ToList());

        var endpoints = device.GetMatterEndpoints(template).ToDictionary(e => e.Id);
        CollectionAssert.AreEquivalent(new[] { $"harvia:{Sauna}", $"harvia:{Sauna}:temperature", $"harvia:{Sauna}:humidity", $"harvia:{Sauna}:door", $"harvia:{Sauna}:light" },
            endpoints.Keys.ToArray());
        string ReportId(string key) => template.ReportTemplates.Single(r => r.Address == Key(key)).Id;
        string CommandId(string key) => template.CommandTemplates!.Single(c => c.Address == Key(key)).Id;
        var thermostat = endpoints[$"harvia:{Sauna}"];
        Assert.AreEqual((MatterDeviceType.Thermostat, "Sauna boven", "Harvia"), (thermostat.DeviceType, thermostat.Name, thermostat.VendorName));
        CollectionAssert.AreEquivalent(new[]
            {
                (MatterAttribute.LocalTemperature, ReportId(Harvia.Temperature), MatterValueScale.CelsiusToHundredths),
                (MatterAttribute.OccupiedHeatingSetpoint, ReportId(Harvia.TargetTemperature), MatterValueScale.CelsiusToHundredths),
                (MatterAttribute.ThermostatSystemMode, ReportId(Harvia.Mode), MatterValueScale.None)
            }, thermostat.Attributes.Select(a => (a.Attribute, a.ReportTemplateId, a.Scale)).ToArray());
        CollectionAssert.AreEquivalent(new[]
            {
                (MatterAttribute.OccupiedHeatingSetpoint, CommandId(Harvia.TargetTemperature)), (MatterAttribute.ThermostatSystemMode, CommandId(Harvia.Mode))
            }, thermostat.Commands.Select(c => (c.Attribute, c.CommandTemplateId)).ToArray());
        Assert.AreEqual((MatterAttribute.HumidityMeasuredValue, MatterValueScale.PercentToHundredths),
            endpoints[$"harvia:{Sauna}:humidity"].Attributes.Select(a => (a.Attribute, a.Scale)).Single());
        Assert.AreEqual((MatterDeviceType.ContactSensor, MatterAttribute.BooleanStateValue, MatterValueScale.InvertBoolean),
            (endpoints[$"harvia:{Sauna}:door"].DeviceType, endpoints[$"harvia:{Sauna}:door"].Attributes.Single().Attribute, endpoints[$"harvia:{Sauna}:door"].Attributes.Single().Scale));
        var light = endpoints[$"harvia:{Sauna}:light"];
        Assert.AreEqual((MatterDeviceType.OnOffLight, CommandId(Harvia.Light)), (light.DeviceType, light.Commands.Single().CommandTemplateId));
    }

    [TestMethod]
    public async Task StoredTokensAreRefreshedAndARefusedTokenOrRefreshLogsInAgain()
    {
        var time = new ManualTime();
        var api = new FakeHarvia(time);
        var device = Device(api, time, reports: Reports(Harvia.Power));
        api.AcceptRefresh("refresh-0");
        File.WriteAllText(device.TokenFile("user@example.com"), JsonSerializer.Serialize(new HarviaStoredTokens { RefreshToken = "refresh-0" }));

        await device.StartAsync(CancellationToken.None);
        Assert.AreEqual(0, api.Logins, "the stored refresh token is used instead of the password");
        Assert.AreEqual("REFRESH_TOKEN_AUTH", JsonNode.Parse(api.Requests.Single(r => r.Url == FakeHarvia.Cognito).Body!)!["AuthFlow"]!.GetValue<string>());

        time.Now += TimeSpan.FromMinutes(59);
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);
        Assert.AreEqual(2, api.Refreshes, "a token expiring within two minutes is renewed first");

        time.Now += TimeSpan.FromMinutes(5);
        api.RejectToken = api.LastIdToken;
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);
        Assert.AreEqual(3, api.Refreshes, "a refused token is renewed once");
        Assert.AreEqual(DeviceState.Running, device.State);

        time.Now += TimeSpan.FromMinutes(5);
        api.RejectToken = api.LastIdToken;
        api.RefuseRefresh = true;
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);
        Assert.AreEqual(1, api.Logins, "a refused refresh token logs in with the password");
    }

    [TestMethod]
    public async Task AWrongPasswordPutsTheDeviceIntoTheErrorState()
    {
        var time = new ManualTime();
        var api = new FakeHarvia(time) { Password = "another" };
        var device = Device(api, time, reports: Reports(Harvia.Power));

        var error = (await Assert.ThrowsAsync<Exception>(() => device.StartAsync(CancellationToken.None))).InnerException!;
        Assert.AreEqual(DeviceState.Error, device.State, "a configured device can't fix a wrong password by waiting");
        StringAssert.Contains(error.Message, "u***@e***");
        Assert.IsFalse(error.Message.Contains("user@example.com", StringComparison.Ordinal), "the e-mail is masked");
    }

    [TestMethod]
    public async Task PushesFollowASessionWithReadinessAndEstimatedEnergy()
    {
        var time = new ManualTime();
        var api = new FakeHarvia(time);
        api.Reported["active"] = 0;
        api.Reported["statusCodes"] = 0;
        api.Telemetry["temperature"] = 25;
        api.Telemetry["heatOn"] = false;
        var device = Device(api, time, heaterPower: "9", reports: Reports(_allKeys));
        var reports = Capture(device);
        await device.StartAsync(CancellationToken.None);
        Assert.AreEqual("off", Last(reports)[Key(Harvia.StateKey)].GetValue<string>());
        var start = time.Now;

        device.HandlePush(State(new JsonObject { ["active"] = 1, ["statusCodes"] = Bits(8, 17) }));
        Assert.AreEqual(("started", 9d), (Last(reports)[Key(Harvia.SessionEvent)].GetValue<string>(), Last(reports)[Key(Harvia.HeaterPowerNow)].GetValue<double>()));

        time.Now = start.AddMinutes(1);
        device.HandlePush(Telemetry(40));
        time.Now = start.AddMinutes(2);
        device.HandlePush(Telemetry(50));
        var values = Last(reports);
        Assert.AreEqual(3, values[Key(Harvia.TimeToReady)].GetValue<int>(), "10 °C a minute, 30 °C to go");
        Assert.AreEqual("2026-11-04T18:05:00+00:00", values[Key(Harvia.ReadyAt)].GetValue<string>());

        time.Now = start.AddMinutes(5);
        device.HandlePush(Telemetry(80));
        device.HandlePush(State(new JsonObject { ["statusCodes"] = Bits(5, 8, 17) }));
        values = Last(reports);
        Assert.IsTrue(values[Key(Harvia.Ready)].GetValue<bool>());
        Assert.AreEqual(("ready", "ready", 0), (values[Key(Harvia.SessionEvent)].GetValue<string>(), values[Key(Harvia.StateKey)].GetValue<string>(),
            values[Key(Harvia.TimeToReady)].GetValue<int>()));
        Assert.IsFalse(values[Key(Harvia.Heating)].GetValue<bool>(), "holding the temperature isn't heating up");
        Assert.AreEqual(0d, values[Key(Harvia.HeaterPowerNow)].GetValue<double>());

        time.Now = start.AddMinutes(65);
        device.HandlePush(State(new JsonObject { ["active"] = 0, ["statusCodes"] = Bits(18) }));
        values = Last(reports);
        Assert.AreEqual(("ended", "stopped"), (values[Key(Harvia.SessionEvent)].GetValue<string>(), values[Key(Harvia.StateKey)].GetValue<string>()));
        var events = reports.Where(r => r.Id == Key(Harvia.SessionEvent)).Select(r => r.Value.GetValue<string>()).ToList();
        CollectionAssert.AreEqual(new[] { "started", "ready", "ended" }, events);
        var session = JsonNode.Parse(values[Key(Harvia.LastSession)].ToJson())!;
        Assert.AreEqual(("2026-11-04T18:00:00+00:00", "2026-11-04T18:05:00+00:00", "2026-11-04T19:05:00+00:00", 65, 5, 80d, 80, 0.75),
            (session["startedAt"]!.GetValue<string>(), session["readyAt"]!.GetValue<string>(), session["endedAt"]!.GetValue<string>(),
                session["durationMinutes"]!.GetValue<int>(), session["heatUpMinutes"]!.GetValue<int>(), session["maxTemperature"]!.GetValue<double>(),
                session["targetTemperature"]!.GetValue<int>(), session["energy"]!.GetValue<double>()), "9 kW for the 5 minutes of heat-up");
        Assert.AreEqual(0.75, values[Key(Harvia.EnergyLastSession)].GetValue<double>());
        Assert.AreEqual((1, 65, 0.75), (values[Key(Harvia.SessionsToday)].GetValue<int>(), values[Key(Harvia.BathingMinutesToday)].GetValue<int>(),
            values[Key(Harvia.EnergyToday)].GetValue<double>()));
        Assert.AreEqual(start.AddMinutes(65).ToUnixTimeSeconds(), reports.Last(r => r.Id == Key(Harvia.LastSession)).TimeStamp);

        device.HandlePush(new HarviaUpdate("unknown", new JsonObject { ["active"] = 1 }, null, null));
        Assert.IsFalse(reports.Any(r => r.Id.StartsWith("unknown", StringComparison.Ordinal)), "a sauna no poll found is left to the next poll");
    }

    [TestMethod]
    public async Task ASessionOutlivesARestartAndTheDaysTotalsStartAtMidnight()
    {
        var time = new ManualTime { Now = new DateTimeOffset(2026, 11, 4, 23, 30, 0, TimeSpan.Zero) };
        var api = new FakeHarvia(time);
        var first = Device(api, time, reports: Reports(_allKeys));
        await first.StartAsync(CancellationToken.None);
        first.HandlePush(State(new JsonObject { ["statusCodes"] = Bits(5, 8, 17) }));
        await first.StopAsync(CancellationToken.None);

        time.Now = new DateTimeOffset(2026, 11, 4, 23, 45, 0, TimeSpan.Zero);
        var second = Device(api, time, reports: Reports(_allKeys));
        var reports = Capture(second);
        await second.StartAsync(CancellationToken.None);
        Assert.IsFalse(reports.Any(r => r.Id == Key(Harvia.SessionEvent)), "the session goes on after the restart");
        Assert.AreEqual("2026-11-04T23:30:00+00:00", Last(reports)[Key(Harvia.ReadyAt)].GetValue<string>(), "readiness is kept too");

        time.Now = new DateTimeOffset(2026, 11, 5, 0, 30, 0, TimeSpan.Zero);
        second.HandlePush(State(new JsonObject { ["active"] = 0 }));
        var values = Last(reports);
        Assert.AreEqual(60, JsonNode.Parse(values[Key(Harvia.LastSession)].ToJson())!["durationMinutes"]!.GetValue<int>());
        Assert.AreEqual((1, 60), (values[Key(Harvia.SessionsToday)].GetValue<int>(), values[Key(Harvia.BathingMinutesToday)].GetValue<int>()),
            "a session counts on the day it ends");
        Assert.IsFalse(reports.Any(r => r.Id == Key(Harvia.EnergyToday)), "no energy reports without the heater's power");
    }

    [TestMethod]
    public async Task CommandsAreCheckedAndSent()
    {
        var time = new ManualTime();
        var api = new FakeHarvia(time);
        var device = Device(api, time, commands: Commands(Harvia.Power, Harvia.TargetTemperature, Harvia.Light, Harvia.Mode));
        await device.StartAsync(CancellationToken.None);

        async Task<string> Send(string key, string value)
        {
            await device.ExecuteCommandAsync(Key(key), value, CancellationToken.None);
            return api.Changes.Last();
        }

        Assert.AreEqual("""{"active":1}""", await Send(Harvia.Power, "true"));
        Assert.AreEqual("""{"active":0}""", await Send(Harvia.Mode, "0"));
        Assert.AreEqual("""{"active":1}""", await Send(Harvia.Mode, "4"));
        Assert.AreEqual("""{"targetTemp":75}""", await Send(Harvia.TargetTemperature, "75.4"), "Elsa sends a bare number");
        Assert.AreEqual("""{"targetTemp":90}""", await Send(Harvia.TargetTemperature, "\"90\""));
        Assert.AreEqual("""{"light":0}""", await Send(Harvia.Light, "off"));
        var changes = api.Changes.Count;

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => device.ExecuteCommandAsync(Key(Harvia.TargetTemperature), "120", CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => device.ExecuteCommandAsync(Key(Harvia.Mode), "3", CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => device.ExecuteCommandAsync(Key(Harvia.Power), "maybe", CancellationToken.None));
        await Assert.ThrowsExactlyAsync<UnknownCommandException>(() => device.ExecuteCommandAsync("nothing", "1", CancellationToken.None));

        device.HandlePush(State(new JsonObject { ["active"] = 0, ["statusCodes"] = Bits(1) }));
        var refused = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => device.ExecuteCommandAsync(Key(Harvia.Power), "true", CancellationToken.None));
        StringAssert.Contains(refused.Message, "door");
        Assert.AreEqual("""{"active":0}""", await Send(Harvia.Power, "false"), "switching off is always allowed");
        Assert.AreEqual(changes + 1, api.Changes.Count, "nothing else was sent");

        api.RefuseChange = true;
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => device.ExecuteCommandAsync(Key(Harvia.Light), "true", CancellationToken.None));
        StringAssert.Contains(error.Message, "Device is offline");
    }

    [TestMethod]
    public async Task ThePushSubscribesReconnectsAndRenewsARefusedToken()
    {
        var time = new ManualTime();
        var api = new FakeHarvia(time) { AckSockets = false };
        var device = Device(api, time, listen: true, reports: Reports(Harvia.Power, Harvia.Temperature));
        device.PushSettings = new HarviaPushSettings(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(1500),
            TimeSpan.FromMilliseconds(200));
        var reports = Capture(device);
        await device.StartAsync(CancellationToken.None);

        await WaitFor(() => api.SocketsFor("device").Count >= 2);
        Assert.IsTrue(api.SocketsFor("device")[0].Disposed, "a silent connection is given up after the keep-alive timeout");
        api.AckSockets = true;
        await WaitFor(() => api.SocketsFor("device").Any(s => s.Starts.Count == 2) && api.SocketsFor("data").Any(s => s.Starts.Count == 2));

        var socket = api.SocketsFor("device").Last(s => s.Starts.Count == 2);
        Assert.AreEqual("wss://device.appsync-realtime-api.eu-west-1.amazonaws.com/graphql", socket.Url.GetLeftPart(UriPartial.Path));
        var header = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(Uri.UnescapeDataString(socket.Url.Query.Split('&')[0]["?header=".Length..]))))!;
        Assert.AreEqual((api.LastIdToken, "device.appsync-api.eu-west-1.amazonaws.com"), (header["Authorization"]!.GetValue<string>(), header["host"]!.GetValue<string>()));
        CollectionAssert.AreEquivalent(new[] { "org-1", "user@example.com" },
            socket.Starts.Select(s => JsonNode.Parse(s["payload"]!["data"]!.GetValue<string>())!["variables"]!["receiver"]!.GetValue<string>()).ToArray());
        StringAssert.Contains(socket.Starts[0]["payload"]!["data"]!.GetValue<string>(), "onStateUpdated");
        Assert.AreEqual(api.LastIdToken, socket.Starts[0]["payload"]!["extensions"]!["authorization"]!["Authorization"]!.GetValue<string>());

        socket.Push(new JsonObject
        {
            ["id"] = socket.Starts[0]["id"]!.GetValue<string>(),
            ["type"] = "data",
            ["payload"] = new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["onStateUpdated"] = new JsonObject
                    {
                        ["desired"] = null,
                        ["reported"] = new JsonObject { ["active"] = 0, ["deviceId"] = Sauna }.ToJsonString(),
                        ["timestamp"] = time.Now.ToUnixTimeSeconds(),
                        ["receiver"] = "org-1"
                    }
                }
            }
        });
        await WaitFor(() => reports.Any(r => r.Id == Key(Harvia.Power) && !r.Value.GetValue<bool>()));

        var data = api.SocketsFor("data").Last(s => s.Starts.Count == 2);
        data.Push(new JsonObject
        {
            ["type"] = "data",
            ["payload"] = new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["onDataUpdates"] = new JsonObject
                    {
                        ["item"] = new JsonObject
                        {
                            ["deviceId"] = Sauna, ["timestamp"] = "1793820000000", ["type"] = "sauna", ["data"] = """{"temperature":71,"humidity":10}"""
                        }
                    }
                }
            }
        });
        await WaitFor(() => reports.Any(r => r.Id == Key(Harvia.Temperature) && r.Value.GetValue<double>() == 71));

        await WaitFor(() => socket.Disposed);
        Assert.AreEqual(2, socket.Sent.Count(m => m.Contains("\"stop\"", StringComparison.Ordinal)), "a planned reconnect ends the subscriptions first");

        var refreshes = api.Refreshes;
        var current = api.SocketsFor("device").Last();
        await WaitFor(() => current.Starts.Count == 2);
        current.Push(new JsonObject
        {
            ["type"] = "error",
            ["payload"] = new JsonObject { ["errors"] = new JsonArray(new JsonObject { ["errorType"] = "UnauthorizedException", ["message"] = "expired" }) }
        });
        await WaitFor(() => api.Refreshes > refreshes);

        await device.StopAsync(CancellationToken.None);
        Assert.IsNull(device.Listener);
    }

    [TestMethod]
    public async Task OutagesKeepTheLastStateAndAConfiguredDeviceOutlivesOneAtStart()
    {
        var time = new ManualTime();
        var api = new FakeHarvia(time) { Down = true };
        var device = Device(api, time, reports: Reports(Harvia.Power), commands: Commands(Harvia.Light));

        await device.StartAsync(CancellationToken.None);
        Assert.AreEqual(DeviceState.Running, device.State, "MyHarvia being down isn't the configuration's fault");

        api.Down = false;
        await device.ExecuteCommandAsync(Key(Harvia.Light), "true", CancellationToken.None);
        Assert.AreEqual("""{"light":1}""", api.Changes.Last(), "the first command polls first");

        time.Now += TimeSpan.FromMinutes(5);
        api.Down = true;
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);
        Assert.AreEqual(DeviceState.Running, device.State);
        Assert.IsTrue(device.GetConfigurationTemplate().ReportTemplates.Any(r => r.Address == Key(Harvia.Power)), "the saunas of the last poll stay");

        var unconfigured = Device(new FakeHarvia(time) { Down = true }, time, email: "fresh@example.com");
        await Assert.ThrowsAsync<Exception>(() => unconfigured.StartAsync(CancellationToken.None));
        Assert.AreEqual(DeviceState.Error, unconfigured.State, "a new device shows why it has no saunas");
    }

    [TestMethod]
    public void ModelsReadTheScheduleTheTreeThePushesAndTheTrend()
    {
        // A Xenio's timedStart captured by the Home Assistant integration: armed, 6 h, 90 °C, ready 2024-04-02 17:45 UTC.
        var schedule = HarviaSchedule.Decode("ARhaABxEDGY=")!;
        Assert.AreEqual((true, 360, 90, new DateTimeOffset(2024, 4, 2, 17, 45, 0, TimeSpan.Zero)),
            (schedule.Enabled, schedule.DurationMinutes, schedule.TargetTemperature, schedule.ReadyAt));
        Assert.IsNull(HarviaSchedule.Decode("AAAAAAAAAAA="), "all zeros is no schedule");
        Assert.IsNull(HarviaSchedule.Decode("not base64"));

        CollectionAssert.AreEqual(new[] { "dev-1", "dev-2" },
            HarviaClient.DeviceIds(JsonValue.Create("""[{"i":{"name":"home"},"c":[{"i":{"name":"dev-1"}},{"c":[{"i":{"name":"dev-2"}}]}]}]""")));

        var state = HarviaClient.ParseUpdate(JsonNode.Parse("""
            {"onStateUpdated":{"desired":null,"reported":"{\"active\":1,\"light\":0,\"targetTemp\":90,\"statusCodes\":133376,\"deviceId\":\"e0b8\"}","timestamp":1712078650}}
            """))!;
        Assert.AreEqual(("e0b8", 90, new DateTimeOffset(2024, 4, 2, 17, 24, 10, TimeSpan.Zero)),
            (state.DeviceId, state.State!["targetTemp"]!.GetValue<int>(), state.At!.Value));
        var data = HarviaClient.ParseUpdate(JsonNode.Parse("""
            {"onDataUpdates":{"item":{"deviceId":"e0b8","timestamp":"1712161222726","sessionId":"1","type":"sauna","data":"{\"temperature\":27,\"heatOn\":true}"}}}
            """))!;
        Assert.AreEqual(("e0b8", 27, 1712161222726L), (data.DeviceId, data.Telemetry!["temperature"]!.GetValue<int>(), data.At!.Value.ToUnixTimeMilliseconds()));
        Assert.IsNull(HarviaClient.ParseUpdate(JsonNode.Parse("""{"somethingElse":{}}""")));

        var sauna = new HarviaSauna("x");
        sauna.MergeState(new JsonObject { ["active"] = "1", ["statusCodes"] = Bits(8, 17) }, null);
        Assert.AreEqual((true, "heating"), (sauna.Heating, sauna.StateName));
        sauna.MergeState(new JsonObject { ["statusCodes"] = Bits(5, 8, 17) }, null);
        Assert.AreEqual((false, "ready"), (sauna.Heating, sauna.StateName), "a partial push keeps what it doesn't name");

        var trend = new HarviaTrend();
        var at = new DateTimeOffset(2026, 11, 4, 18, 0, 0, TimeSpan.Zero);
        trend.Add(at, 30);
        Assert.IsNull(trend.MinutesTo(80, 30));
        trend.Add(at.AddMinutes(2), 30.05);
        Assert.IsNull(trend.MinutesTo(80, 30.05), "too slow to tell");
        trend.Add(at.AddMinutes(12), 70);
        Assert.AreEqual(3, trend.MinutesTo(80, 70), "the window keeps the last 10 minutes: 4 °C a minute");
        Assert.AreEqual(0, trend.MinutesTo(80, 81));

        Assert.AreEqual("u***@e***", HarviaClient.Mask("user@example.com"));
    }

    [TestMethod]
    public void TheTemplateNeedsNoCloudWhenTheDeviceIsNotRunning()
    {
        var device = new Harvia(NullLogger.Instance);
        var template = device.GetConfigurationTemplate();

        Assert.AreEqual(typeof(Harvia).FullName, template.ClassFullName);
        Assert.AreEqual("0 0/5 * ? * *", template.RefreshSchedule);
        CollectionAssert.AreEquivalent(new[] { "email", "password", "heaterPower", "readyTemperature" }, template.DeviceParameters.Keys.ToArray());
        Assert.AreEqual(0, device.GetMatterEndpoints(template).Count());
    }

    private TestHarvia Device(FakeHarvia api, ManualTime time, string email = "user@example.com", string heaterPower = "0", bool listen = false,
        List<ReportTemplate>? reports = null, List<CommandTemplate>? commands = null)
    {
        var device = new TestHarvia(api)
        {
            DataDirectory = _dataDirectory,
            Time = time,
            ListenToPushes = listen
        };
        device.Initialize(new DeviceConfiguration
        {
            Id = "device",
            Name = "Harvia",
            DeviceParameters = new()
            {
                ["email"] = email,
                ["password"] = "Sauna-Pass1",
                ["heaterPower"] = heaterPower
            },
            ReportTemplates = reports ?? [],
            CommandTemplates = commands ?? []
        });
        return device;
    }

    private static string Key(string key) => $"{Sauna}|{key}";

    private static int Bits(params int[] bits) => bits.Aggregate(0, (codes, bit) => codes | (1 << bit));

    private static HarviaUpdate State(JsonObject state) => new(Sauna, state, null, null);

    private static HarviaUpdate Telemetry(double temperature) => new(Sauna, null, new JsonObject { ["temperature"] = temperature }, null);

    // The template id is the address, so the tests can find reports by address.
    private static List<ReportTemplate> Reports(params string[] keys) =>
        keys.Select(k => new ReportTemplate { Id = Key(k), Address = Key(k) }).ToList();

    private static List<CommandTemplate> Commands(params string[] keys) =>
        keys.Select(k => new CommandTemplate { Id = Key(k), Address = Key(k) }).ToList();

    private static ConcurrentQueue<Report> Capture(Core.Interfaces.IDevice device)
    {
        var reports = new ConcurrentQueue<Report>();
        device.ReportUpdated += (_, report) => reports.Enqueue((Report)report);
        return reports;
    }

    private static Dictionary<string, ValueModel> Last(IEnumerable<Report> reports) =>
        reports.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.Last().Value);

    private static async Task WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                Assert.Fail("The condition wasn't met in 10 seconds.");
            await Task.Delay(10);
        }
    }

    private static HttpResponseMessage Json(JsonNode body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private sealed record Sent(string Method, string Url, string? Body, Dictionary<string, string> Headers)
    {
        public string? Header(string name) => Headers.GetValueOrDefault(name);
    }

    /// <summary>
    /// The MyHarvia cloud with one sauna, "Sauna boven", switched on. The Cognito pool is a real SRP server for
    /// user@example.com: it checks the password proof against a verifier, so a wrong password fails as in Cognito.
    /// Each token answer gives the next id-N; the password login gives refresh-N.
    /// </summary>
    private sealed class FakeHarvia
    {
        public const string Users = "https://users.appsync-api.eu-west-1.amazonaws.com/graphql";
        public const string DeviceApi = "https://device.appsync-api.eu-west-1.amazonaws.com/graphql";
        public const string DataApi = "https://data.appsync-api.eu-west-1.amazonaws.com/graphql";
        public const string Cognito = "https://cognito-idp.eu-west-1.amazonaws.com/";
        private const string PoolName = "TestPool1";
        private const string UserId = "11111111-2222-3333-4444-555555555555";
        private const string Salt = "0a1b2c3d4e5f60718293a4b5c6d7e8f9";

        private readonly object _lock = new();
        private readonly List<Sent> _requests = [];
        private readonly List<string> _changes = [];
        private readonly List<FakeSocket> _sockets = [];
        private readonly HashSet<string> _idTokens = [];
        private readonly HashSet<string> _refreshTokens = [];
        private readonly ManualTime _time;
        private BigInteger _a, _b, _bigB;
        private int _tokens;

        public FakeHarvia(ManualTime time)
        {
            _time = time;
            Reported = new JsonObject
            {
                ["displayName"] = "Sauna boven",
                ["active"] = 1,
                ["light"] = 1,
                ["fan"] = 0,
                ["targetTemp"] = 80,
                ["heatUpTime"] = 60,
                ["statusCodes"] = Bits(8, 11, 17),
                ["timedStart"] = TimedStart(true, time.Now.AddDays(1), 90, 75),
                ["deviceId"] = Sauna
            };
            Telemetry = new JsonObject { ["temperature"] = 62, ["humidity"] = 12, ["remainingTime"] = 300, ["heatOn"] = true, ["wifiRSSI"] = -60 };
        }

        public JsonObject Reported { get; }
        public JsonObject Telemetry { get; }
        public string Password { get; set; } = "Sauna-Pass1";
        public bool Down { get; set; }
        public bool RefuseRefresh { get; set; }
        public bool RefuseChange { get; set; }
        public string? RejectToken { get; set; }
        public bool AckSockets { get; set; } = true;
        public int Logins { get; private set; }
        public int Refreshes { get; private set; }
        public string LastIdToken { get; private set; } = "";

        public List<Sent> Requests
        {
            get { lock (_lock) return _requests.ToList(); }
        }

        public List<string> Changes
        {
            get { lock (_lock) return _changes.ToList(); }
        }

        public List<FakeSocket> SocketsFor(string service)
        {
            lock (_lock)
                return _sockets.Where(s => s.Url.Host.StartsWith(service + ".", StringComparison.Ordinal)).ToList();
        }

        public void AcceptRefresh(string token)
        {
            lock (_lock)
                _refreshTokens.Add(token);
        }

        public static string TimedStart(bool enabled, DateTimeOffset readyAt, int durationMinutes, int target)
        {
            var raw = new byte[8];
            raw[0] = (byte)(enabled ? 1 : 0);
            raw[1] = (byte)(durationMinutes / 15);
            raw[2] = (byte)target;
            BitConverter.TryWriteBytes(raw.AsSpan(4), (uint)readyAt.ToUnixTimeSeconds());
            return Convert.ToBase64String(raw);
        }

        public Task<IHarviaSocket> ConnectAsync(Uri url)
        {
            var socket = new FakeSocket(url, this);
            lock (_lock)
                _sockets.Add(socket);
            return Task.FromResult<IHarviaSocket>(socket);
        }

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
            lock (_lock)
            {
                _requests.Add(new Sent(request.Method.Method, request.RequestUri!.ToString(), body, headers));
                return Respond(request.RequestUri.ToString(), body, headers);
            }
        }

        private HttpResponseMessage Respond(string url, string? body, Dictionary<string, string> headers)
        {
            if (Down)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            switch (url)
            {
                case HarviaClient.DiscoveryUrl + "/users/endpoint":
                    return Json(new JsonObject { ["endpoint"] = Users, ["clientId"] = "client-1", ["userPoolId"] = $"eu-west-1_{PoolName}", ["identityPoolId"] = "x" });
                case HarviaClient.DiscoveryUrl + "/device/endpoint":
                    return Json(new JsonObject { ["endpoint"] = DeviceApi });
                case HarviaClient.DiscoveryUrl + "/data/endpoint":
                    return Json(new JsonObject { ["endpoint"] = DataApi });
                case Cognito:
                    return CognitoAnswer(headers["X-Amz-Target"], JsonNode.Parse(body!)!);
                case Users:
                case DeviceApi:
                case DataApi:
                    var token = headers.GetValueOrDefault("authorization");
                    if (token == null || !_idTokens.Contains(token) || token == RejectToken)
                        return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                    return GraphQL(JsonNode.Parse(body!)!);
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private HttpResponseMessage CognitoAnswer(string target, JsonNode body)
        {
            var parameters = body["AuthParameters"];
            if (target.EndsWith("InitiateAuth", StringComparison.Ordinal) && body["AuthFlow"]!.GetValue<string>() == "REFRESH_TOKEN_AUTH")
            {
                Refreshes++;
                if (RefuseRefresh || !_refreshTokens.Contains(parameters!["REFRESH_TOKEN"]!.GetValue<string>()))
                    return CognitoError("NotAuthorizedException", "Invalid Refresh Token");
                return Tokens(null);
            }
            if (target.EndsWith("InitiateAuth", StringComparison.Ordinal))
            {
                if (parameters!["USERNAME"]!.GetValue<string>() != "user@example.com")
                    return CognitoError("UserNotFoundException", "User does not exist.");
                _a = CognitoSrp.Parse(parameters["SRP_A"]!.GetValue<string>());
                _b = new BigInteger(RandomNumberGenerator.GetBytes(64), isUnsigned: true);
                _bigB = (CognitoSrp.K * Verifier() + BigInteger.ModPow(CognitoSrp.G, _b, CognitoSrp.N)) % CognitoSrp.N;
                return Json(new JsonObject
                {
                    ["ChallengeName"] = "PASSWORD_VERIFIER",
                    ["ChallengeParameters"] = new JsonObject
                    {
                        ["SALT"] = Salt,
                        ["SRP_B"] = CognitoSrp.Hex(_bigB),
                        ["SECRET_BLOCK"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("secret-block")),
                        ["USER_ID_FOR_SRP"] = UserId,
                        ["USERNAME"] = UserId
                    }
                });
            }

            var responses = body["ChallengeResponses"]!;
            var timestamp = responses["TIMESTAMP"]!.GetValue<string>();
            if (responses["USERNAME"]!.GetValue<string>() != UserId || timestamp != CognitoSrp.Timestamp(_time.Now))
                return CognitoError("NotAuthorizedException", "Incorrect username or password.");
            var u = CognitoSrp.Parse(CognitoSrp.HexHash(CognitoSrp.PadHex(_a) + CognitoSrp.PadHex(_bigB)));
            var s = BigInteger.ModPow(_a * BigInteger.ModPow(Verifier(), u, CognitoSrp.N) % CognitoSrp.N, _b, CognitoSrp.N);
            var message = Encoding.UTF8.GetBytes(PoolName + UserId).Concat(Encoding.UTF8.GetBytes("secret-block")).Concat(Encoding.UTF8.GetBytes(timestamp)).ToArray();
            var expected = Convert.ToBase64String(HMACSHA256.HashData(CognitoSrp.SessionKey(s, u), message));
            if (responses["PASSWORD_CLAIM_SIGNATURE"]!.GetValue<string>() != expected)
                return CognitoError("NotAuthorizedException", "Incorrect username or password.");
            Logins++;
            return Tokens($"refresh-{Logins}");
        }

        // The verifier Cognito keeps for user@example.com's password: g^x.
        private BigInteger Verifier()
        {
            var x = CognitoSrp.Parse(CognitoSrp.HexHash(CognitoSrp.PadHex(Salt) + CognitoSrp.Sha256Hex(Encoding.UTF8.GetBytes($"{PoolName}{UserId}:{Password}"))));
            return BigInteger.ModPow(CognitoSrp.G, x, CognitoSrp.N);
        }

        private HttpResponseMessage Tokens(string? refreshToken)
        {
            LastIdToken = $"id-{++_tokens}";
            _idTokens.Add(LastIdToken);
            var result = new JsonObject { ["IdToken"] = LastIdToken, ["AccessToken"] = "access", ["ExpiresIn"] = 3600, ["TokenType"] = "Bearer" };
            if (refreshToken != null)
            {
                _refreshTokens.Add(refreshToken);
                result["RefreshToken"] = refreshToken;
            }
            return Json(new JsonObject { ["AuthenticationResult"] = result });
        }

        private static HttpResponseMessage CognitoError(string type, string message) =>
            Json(new JsonObject { ["__type"] = type, ["message"] = message }, HttpStatusCode.BadRequest);

        private HttpResponseMessage GraphQL(JsonNode body)
        {
            var query = body["query"]!.GetValue<string>();
            var variables = body["variables"]!;
            if (query.Contains("getCurrentUserDetails", StringComparison.Ordinal))
                return Data("getCurrentUserDetails", new JsonObject { ["email"] = "user@example.com", ["organizationId"] = "org-1" });
            if (query.Contains("getDeviceTree", StringComparison.Ordinal))
                return Data("getDeviceTree", new JsonArray(new JsonObject { ["c"] = new JsonArray(new JsonObject { ["i"] = new JsonObject { ["name"] = Sauna } }) }).ToJsonString());
            if (query.Contains("getDeviceState", StringComparison.Ordinal) && variables["deviceId"]!.GetValue<string>() == Sauna)
                return Data("getDeviceState", new JsonObject { ["desired"] = null, ["reported"] = Reported.ToJsonString(), ["timestamp"] = _time.Now.ToUnixTimeSeconds() });
            if (query.Contains("getLatestData", StringComparison.Ordinal))
                return Data("getLatestData", new JsonObject
                {
                    ["deviceId"] = Sauna, ["timestamp"] = _time.Now.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["type"] = "sauna", ["data"] = Telemetry.ToJsonString()
                });
            if (query.Contains("requestStateChange", StringComparison.Ordinal))
            {
                if (RefuseChange)
                    return Json(new JsonObject { ["data"] = null, ["errors"] = new JsonArray(new JsonObject { ["errorType"] = "BadRequest", ["message"] = "Device is offline" }) });
                _changes.Add(variables["state"]!.GetValue<string>());
                return Data("requestStateChange", true);
            }
            return Json(new JsonObject { ["errors"] = new JsonArray(new JsonObject { ["message"] = "unknown query" }) });
        }

        private static HttpResponseMessage Data(string name, JsonNode? value) => Json(new JsonObject { ["data"] = new JsonObject { [name] = value } });
    }

    /// <summary>A push socket: it acknowledges the connection and the subscriptions (unless told not to) and delivers what the test pushes.</summary>
    private sealed class FakeSocket(Uri url, FakeHarvia api) : IHarviaSocket
    {
        private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
        private readonly ConcurrentQueue<string> _sent = new();

        public Uri Url { get; } = url;
        public bool Disposed { get; private set; }
        public List<string> Sent => _sent.ToList();
        public List<JsonNode> Starts => Sent.Select(m => JsonNode.Parse(m)!).Where(m => m["type"]!.GetValue<string>() == "start").ToList();

        public void Push(JsonObject message) => _incoming.Writer.TryWrite(message.ToJsonString());

        public Task SendAsync(string message, CancellationToken cancellationToken)
        {
            _sent.Enqueue(message);
            var type = JsonNode.Parse(message)!["type"]!.GetValue<string>();
            if (type == "connection_init" && api.AckSockets)
                _incoming.Writer.TryWrite("""{"type":"connection_ack","payload":{"connectionTimeoutMs":300000}}""");
            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _incoming.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 4, 18, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class TestHarvia(FakeHarvia api) : Harvia(NullLogger.Instance)
    {
        internal override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            api.SendAsync(request, cancellationToken);

        internal override Task<IHarviaSocket> ConnectAsync(Uri url, CancellationToken cancellationToken) => api.ConnectAsync(url);
    }
}
