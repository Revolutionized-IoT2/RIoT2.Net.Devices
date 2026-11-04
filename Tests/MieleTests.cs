using System.Collections.Concurrent;
using System.Net;
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
using RIoT2.Net.Devices.Services.Miele;

namespace RIoT2.Net.Devices.Tests;

/// <summary>The Miele device against a scripted Miele API and event stream; nothing reaches Miele.</summary>
[TestClass]
public class MieleTests
{
    private const string Washer = "000123";
    private const string Oven = "000456";
    private const string Api = MieleClient.ApiUrl;

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
    public async Task ALoginWithThePasswordReadsTheAppliancesAndBuildsTheTemplate()
    {
        var time = new ManualTime();
        var api = new FakeMiele();
        var device = Device(api, time, reports: Reports(Washer, Miele.Status, Miele.Program, Miele.ProgramPhase, Miele.RemainingTime, Miele.FinishTime,
                Miele.SpinningSpeed, Miele.TargetTemperature, Miele.TwinDos1Level, Miele.RemoteStart, Miele.EnergyForecast, Miele.Connected, Miele.ProgramEvent)
            .Concat(Reports(Oven, Miele.Temperature, Miele.TargetTemperature, Miele.CoreTemperature, Miele.Status)).ToList());
        var reports = Capture(device);

        await device.StartAsync(CancellationToken.None);

        Assert.AreEqual(DeviceState.Running, device.State);
        var login = api.Requests[0];
        Assert.AreEqual(("POST", MieleAuth.HeadlessUrl), (login.Method, login.Url));
        var form = Form(login.Body!);
        Assert.AreEqual(("user@example.com", "secret", "fi-FI", "client", "/v1/"),
            (form["email"], form["password"], form["vgInformationSelector"], form["client_id"], form["redirect_uri"]));
        var token = Form(api.Requests[1].Body!);
        Assert.AreEqual(("authorization_code", "code-1", "/v1/devices", "client-secret"),
            (token["grant_type"], token["code"], token["redirect_uri"], token["client_secret"]));
        var devices = api.Requests[2];
        Assert.AreEqual(($"{Api}/devices?language=en", "Bearer access-1"), (devices.Url, devices.Header("Authorization")));
        StringAssert.Contains(File.ReadAllText(device.TokenFile("client")), "refresh-1", "the tokens are kept for the next start");

        var values = Last(reports);
        Assert.AreEqual("running", values[$"{Washer}|status"].GetValue<string>());
        Assert.AreEqual("Cottons", values[$"{Washer}|program"].GetValue<string>());
        Assert.AreEqual("Main wash", values[$"{Washer}|programPhase"].GetValue<string>());
        Assert.AreEqual(75, values[$"{Washer}|remainingTime"].GetValue<int>());
        Assert.AreEqual("2026-11-04T13:35:00+00:00", values[$"{Washer}|finishTime"].GetValue<string>());
        Assert.AreEqual(1400, values[$"{Washer}|spinningSpeed"].GetValue<int>());
        Assert.AreEqual(40d, values[$"{Washer}|targetTemperature"].GetValue<double>());
        Assert.AreEqual(60, values[$"{Washer}|twinDos1Level"].GetValue<int>());
        Assert.IsTrue(values[$"{Washer}|remoteStart"].GetValue<bool>());
        Assert.AreEqual(50d, values[$"{Washer}|energyForecast"].GetValue<double>());
        Assert.IsTrue(values[$"{Washer}|connected"].GetValue<bool>());
        Assert.AreEqual("started", values[$"{Washer}|programEvent"].GetValue<string>(), "a run already going counts from its start");
        Assert.AreEqual(180.5, values[$"{Oven}|temperature"].GetValue<double>());
        Assert.AreEqual(200d, values[$"{Oven}|targetTemperature"].GetValue<double>());
        Assert.IsFalse(values.ContainsKey($"{Oven}|coreTemperature"), "-32768 is no value");

        var calls = api.Requests.Count;
        var template = device.GetConfigurationTemplate();
        Assert.HasCount(calls, api.Requests, "the template comes from the last poll");
        var addresses = template.ReportTemplates.Select(r => r.Address).ToList();
        CollectionAssert.IsSubsetOf(new[] { $"{Washer}|energyToday", $"{Washer}|lastRun", $"{Washer}|twinDos2Level", $"{Oven}|coreTemperature" }, addresses);
        CollectionAssert.DoesNotContain(addresses, $"{Washer}|temperature", "the washer has no measured temperature");
        CollectionAssert.DoesNotContain(addresses, $"{Oven}|energyToday", "an oven doesn't report its consumption");
        Assert.AreEqual("Washing machine status", template.ReportTemplates.Single(r => r.Address == $"{Washer}|status").Name);
        var commands = template.CommandTemplates!.Select(c => c.Address).ToList();
        CollectionAssert.IsSubsetOf(new[] { $"{Washer}|start", $"{Washer}|startAt", $"{Oven}|program", $"{Oven}|targetTemperature", "refresh" }, commands);
        CollectionAssert.DoesNotContain(commands, $"{Washer}|program", "the washer's programs can't be chosen remotely");
        CollectionAssert.AreEqual(new object[] { "Fan plus", "Grill" }, template.CommandTemplates!.Single(c => c.Address == $"{Oven}|program").Schema!.Enum);

        var endpoints = device.GetMatterEndpoints(template).ToList();
        CollectionAssert.AreEquivalent(new[] { $"miele:{Oven}:temperature", $"miele:{Oven}:core" }, endpoints.Select(e => e.Id).ToArray());
        var oven = endpoints.Single(e => e.Id.EndsWith(":temperature", StringComparison.Ordinal));
        Assert.AreEqual(("Kitchen oven temperature", MatterDeviceType.TemperatureSensor, "H7860BP"), (oven.Name, oven.DeviceType, oven.ProductName));
        Assert.AreEqual((template.ReportTemplates.Single(r => r.Address == $"{Oven}|temperature").Id, MatterValueScale.CelsiusToHundredths),
            oven.Attributes.Where(a => a.Attribute == MatterAttribute.TemperatureMeasuredValue).Select(a => (a.ReportTemplateId, a.Scale)).Single());
    }

    [TestMethod]
    public async Task StoredTokensAreUsedAndRefreshedBeforeTheyExpireOrWhenRefused()
    {
        var time = new ManualTime();
        var api = new FakeMiele();
        var device = Device(api, time, reports: Reports(Washer, Miele.Status));
        File.WriteAllText(device.TokenFile("client"), JsonSerializer.Serialize(new MieleTokens
        {
            AccessToken = "stored", RefreshToken = "refresh-0", ExpiresAt = time.Now.AddDays(10)
        }));

        await device.StartAsync(CancellationToken.None);
        Assert.IsFalse(api.Requests.Any(r => r.Url is MieleAuth.TokenUrl or MieleAuth.HeadlessUrl), "no login with valid tokens");
        Assert.AreEqual("Bearer stored", api.Requests[0].Header("Authorization"));

        time.Now += TimeSpan.FromDays(9.5);
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);
        var refresh = Form(api.Requests.Single(r => r.Url == MieleAuth.TokenUrl).Body!);
        Assert.AreEqual(("refresh_token", "refresh-0"), (refresh["grant_type"], refresh["refresh_token"]));
        Assert.AreEqual("Bearer access-1", api.Requests.Last().Header("Authorization"));

        api.RejectToken = "access-1";
        time.Now += TimeSpan.FromMinutes(5);
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);
        Assert.AreEqual(2, api.Requests.Count(r => r.Url == MieleAuth.TokenUrl), "a refused token is refreshed once");
        Assert.AreEqual("Bearer access-2", api.Requests.Last().Header("Authorization"));
        Assert.IsFalse(api.Requests.Any(r => r.Url == MieleAuth.HeadlessUrl));
    }

    [TestMethod]
    public async Task APastedCodeIsUsedOnceAndWithoutALoginTheDeviceShowsWhereToLogIn()
    {
        var time = new ManualTime();
        var api = new FakeMiele();
        var device = Device(api, time, email: "", code: "http://localhost/miele?code=pasted%2D1&state=riot2", reports: Reports(Washer, Miele.Status));

        await device.StartAsync(CancellationToken.None);

        Assert.AreEqual(DeviceState.Running, device.State);
        var exchange = Form(api.Requests[0].Body!);
        Assert.AreEqual(("authorization_code", "pasted-1", "http://localhost/miele"), (exchange["grant_type"], exchange["code"], exchange["redirect_uri"]));

        // The tokens are lost and the refresh token refused: the used code isn't sent again.
        await device.StopAsync(CancellationToken.None);
        api.RefuseRefresh = true;
        time.Now += TimeSpan.FromDays(40);
        var again = Device(api, time, email: "", code: "pasted-1", reports: Reports(Washer, Miele.Status));
        await Assert.ThrowsAsync<Exception>(() => again.StartAsync(CancellationToken.None));
        Assert.AreEqual(1, api.Requests.Count(r => r.Body?.Contains("grant_type=authorization_code", StringComparison.Ordinal) == true));
        Assert.AreEqual(DeviceState.Error, again.State);
        StringAssert.Contains(again.StateMessage, $"{MieleAuth.BrowserUrl}?client_id=client");

        var none = Device(new FakeMiele(), time, email: "", clientId: "other");
        await Assert.ThrowsAsync<Exception>(() => none.StartAsync(CancellationToken.None));
        Assert.AreEqual(DeviceState.Error, none.State);
        StringAssert.Contains(none.StateMessage, "paste the code");

        var wrong = Device(new FakeMiele { Password = "other" }, time, clientId: "third");
        await Assert.ThrowsAsync<Exception>(() => wrong.StartAsync(CancellationToken.None));
        StringAssert.Contains(wrong.StateMessage, "check the e-mail, password and country");
        Assert.IsFalse(wrong.StateMessage.Contains("user@example.com", StringComparison.Ordinal), "the e-mail is masked");
    }

    [TestMethod]
    public async Task TheEventStreamUpdatesTheReportsAndARunIsReportedWhenItEnds()
    {
        var time = new ManualTime();
        var api = new FakeMiele();
        api.SetWasher(MieleCodes.Off, energy: null, water: null);
        var device = Device(api, time, listen: true, reports: Reports(Washer, Miele.Status, Miele.ProgramEvent, Miele.LastRun, Miele.EnergyLastRun,
            Miele.WaterLastRun, Miele.EnergyToday, Miele.WaterToday), commands: Commands(Washer, Miele.StartCommand));
        var reports = Capture(device);
        await device.StartAsync(CancellationToken.None);
        try
        {
            await WaitFor(() => api.Stream != null);
            var open = api.Requests.Single(r => r.Url.StartsWith($"{Api}/devices/all/events", StringComparison.Ordinal));
            Assert.AreEqual(("text/event-stream", "Bearer access-1"), (open.Header("Accept"), open.Header("Authorization")));
            Assert.AreEqual("off", Last(reports)[$"{Washer}|status"].GetValue<string>());

            await api.Stream!.SendAsync("ping", "{}");
            await api.Stream.SendAsync("devices", Devices(Washer, FakeMiele.WasherJson(MieleCodes.Running, 0.2, 10, elapsed: 0)));
            await WaitFor(() => reports.Any(r => r.Id == $"{Washer}|programEvent"));
            Assert.AreEqual("running", Last(reports)[$"{Washer}|status"].GetValue<string>());

            time.Now += TimeSpan.FromMinutes(70);
            await api.Stream.SendAsync("devices", Devices(Washer, FakeMiele.WasherJson(MieleCodes.Running, 0.8, 45, elapsed: 70)));
            time.Now += TimeSpan.FromMinutes(20);
            await api.Stream.SendAsync("devices", Devices(Washer, FakeMiele.WasherJson(MieleCodes.ProgramEnded, 0.9, 48, elapsed: 90)));
            await WaitFor(() => reports.Any(r => r.Id == $"{Washer}|lastRun"));

            CollectionAssert.AreEqual(new[] { "started", "finished" },
                reports.Where(r => r.Id == $"{Washer}|programEvent").Select(r => r.Value.GetValue<string>()).ToArray());
            var run = JsonNode.Parse(reports.Single(r => r.Id == $"{Washer}|lastRun").Value.ToJson())!;
            Assert.AreEqual(("Cottons", 90, 0.9, 48d, "2026-11-04T12:20:00+00:00"),
                ((string)run["program"]!, (int)run["durationMinutes"]!, (double)run["energy"]!, (double)run["water"]!, (string)run["startedAt"]!));
            var energy = reports.Single(r => r.Id == $"{Washer}|energyLastRun");
            Assert.AreEqual((0.9, time.Now.ToUnixTimeSeconds()), (energy.Value.GetValue<double>(), energy.TimeStamp), "time-stamped at the run's end");
            var values = Last(reports);
            Assert.AreEqual((0.9, 48d), (values[$"{Washer}|energyToday"].GetValue<double>(), values[$"{Washer}|waterToday"].GetValue<double>()));

            // An actions event allows the start that the poll's actions didn't.
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => device.ExecuteCommandAsync($"{Washer}|start", "true", CancellationToken.None));
            await api.Stream.SendAsync("actions", new JsonObject { [Washer] = new JsonObject { ["processAction"] = new JsonArray(1) } }.ToJsonString());
            await WaitFor(() => device.ActionsFor(Washer).ProcessActions.Contains(MieleCodes.Start));
            await device.ExecuteCommandAsync($"{Washer}|start", "true", CancellationToken.None);
            Assert.AreEqual("""{"processAction":1}""", api.Requests.Last(r => r.Method == "PUT").Body);

            // The stream ends: the listener opens it again.
            var opened = api.Opened;
            api.Stream.Complete();
            await WaitFor(() => api.Opened > opened);
        }
        finally
        {
            await device.StopAsync(CancellationToken.None);
        }
        Assert.IsTrue(device.Listener == null, "the listener stops with the device");
    }

    [TestMethod]
    public async Task ARunAcrossARestartKeepsItsStartAndTheDaysTotalsStartAtMidnight()
    {
        var time = new ManualTime();
        var api = new FakeMiele();
        api.SetWasher(MieleCodes.Running, energy: 0.3, water: 12, elapsed: 30);
        var device = Device(api, time, reports: Reports(Washer, Miele.ProgramEvent, Miele.LastRun, Miele.EnergyToday));
        await device.StartAsync(CancellationToken.None);
        await device.StopAsync(CancellationToken.None);

        time.Now += TimeSpan.FromMinutes(60);
        api.SetWasher(MieleCodes.ProgramEnded, energy: 1.1, water: 50, elapsed: 90);
        var restarted = Device(api, time, reports: Reports(Washer, Miele.ProgramEvent, Miele.LastRun, Miele.EnergyToday));
        var reports = Capture(restarted);
        await restarted.StartAsync(CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "finished" }, reports.Where(r => r.Id == $"{Washer}|programEvent").Select(r => r.Value.GetValue<string>()).ToArray());
        var run = JsonNode.Parse(reports.Single(r => r.Id == $"{Washer}|lastRun").Value.ToJson())!;
        Assert.AreEqual(("2026-11-04T11:50:00+00:00", 90), ((string)run["startedAt"]!, (int)run["durationMinutes"]!), "the start from before the restart");
        Assert.AreEqual(1.1, Last(reports)[$"{Washer}|energyToday"].GetValue<double>());

        time.Now = new DateTimeOffset(2026, 11, 5, 0, 5, 0, TimeSpan.Zero);
        api.SetWasher(MieleCodes.Off, energy: null, water: null);
        await restarted.RefreshReportAsync(restarted.Id, null!, CancellationToken.None);
        Assert.AreEqual(0d, Last(reports)[$"{Washer}|energyToday"].GetValue<double>());
    }

    [TestMethod]
    public async Task CommandsAreCheckedAgainstWhatMieleAllowsAndSent()
    {
        var time = new ManualTime();
        var api = new FakeMiele();
        api.SetWasher(MieleCodes.Programmed, energy: null, water: null);
        var device = Device(api, time, commands: Commands(Washer, Miele.StartCommand, Miele.StartDelay, Miele.StartAt, Miele.Power, Miele.Program)
            .Concat(Commands(Oven, Miele.Program, Miele.TargetTemperature, Miele.StopCommand)).ToList());
        await device.StartAsync(CancellationToken.None);

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => device.ExecuteCommandAsync($"{Washer}|start", "true", CancellationToken.None));
        StringAssert.Contains(error.Message, "MobileStart");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => device.ExecuteCommandAsync($"{Washer}|startDelay", "60", CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => device.ExecuteCommandAsync($"{Washer}|program", "\"Cottons\"", CancellationToken.None));

        api.WasherActions = """{"processAction":[1],"startTime":[[0,0],[24,0]],"powerOn":false,"powerOff":true,"programId":[],"targetTemperature":[]}""";
        time.Now += TimeSpan.FromMinutes(5);
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);

        await device.ExecuteCommandAsync($"{Washer}|start", "true", CancellationToken.None);
        Assert.AreEqual(($"PUT {Api}/devices/{Washer}/actions?language=en", """{"processAction":1}"""), Put(api));
        await device.ExecuteCommandAsync($"{Washer}|startDelay", "90", CancellationToken.None);
        Assert.AreEqual("""{"startTime":[1,30]}""", Put(api).Body);
        // 12:25 now; 14:00 is 95 minutes away.
        await device.ExecuteCommandAsync($"{Washer}|startAt", "\"2026-11-04T16:00:00+02:00\"", CancellationToken.None);
        Assert.AreEqual("""{"startTime":[1,35]}""", Put(api).Body);
        await device.ExecuteCommandAsync($"{Washer}|power", "false", CancellationToken.None);
        Assert.AreEqual("""{"powerOff":true}""", Put(api).Body);

        await device.ExecuteCommandAsync($"{Oven}|program", """{"program":"fan plus","temperature":182,"duration":45}""", CancellationToken.None);
        Assert.AreEqual(($"PUT {Api}/devices/{Oven}/programs?language=en", """{"programId":13,"temperature":180,"duration":[0,45]}"""), Put(api));
        await device.ExecuteCommandAsync($"{Oven}|program", "13", CancellationToken.None);
        Assert.AreEqual("""{"programId":13}""", Put(api).Body, "a program by id");
        await device.ExecuteCommandAsync($"{Oven}|targetTemperature", "220", CancellationToken.None);
        Assert.AreEqual("""{"targetTemperature":[{"zone":1,"value":220}]}""", Put(api).Body);

        var sent = api.Requests.Count(r => r.Method == "PUT");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => device.ExecuteCommandAsync($"{Washer}|startDelay", "2000", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => device.ExecuteCommandAsync($"{Washer}|startAt", "\"2026-11-04T10:00:00Z\"", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => device.ExecuteCommandAsync($"{Washer}|startAt", "\"tomorrow\"", CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => device.ExecuteCommandAsync($"{Washer}|power", "true", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => device.ExecuteCommandAsync($"{Oven}|program", "\"Bake\"", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => device.ExecuteCommandAsync($"{Oven}|program", "\"Grill\"", CancellationToken.None), "Grill needs a temperature");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            device.ExecuteCommandAsync($"{Oven}|program", """{"program":"Fan plus","temperature":300}""", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => device.ExecuteCommandAsync($"{Oven}|targetTemperature", "300", CancellationToken.None));
        await Assert.ThrowsExactlyAsync<UnknownCommandException>(() => device.ExecuteCommandAsync("missing", "true", CancellationToken.None));
        Assert.AreEqual(sent, api.Requests.Count(r => r.Method == "PUT"), "a command that doesn't fit isn't sent");

        api.RefuseActions = true;
        error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => device.ExecuteCommandAsync($"{Oven}|stop", "true", CancellationToken.None));
        StringAssert.Contains(error.Message, "Door is open");
    }

    [TestMethod]
    public async Task OutagesKeepTheLastStateAndAConfiguredDeviceOutlivesOneAtStart()
    {
        var time = new ManualTime();
        var api = new FakeMiele { Down = true };
        var device = Device(api, time, reports: Reports(Washer, Miele.Status), commands: Commands(Oven, Miele.TargetTemperature));

        await device.StartAsync(CancellationToken.None);
        Assert.AreEqual(DeviceState.Running, device.State, "Miele being down isn't the configuration's fault");

        api.Down = false;
        await device.ExecuteCommandAsync($"{Oven}|targetTemperature", "200", CancellationToken.None);
        Assert.AreEqual("""{"targetTemperature":[{"zone":1,"value":200}]}""", Put(api).Body, "the first command polls first");

        time.Now += TimeSpan.FromHours(1);
        api.Limited = true;
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);
        Assert.AreEqual(DeviceState.Running, device.State, "a 429 is waited out");
        Assert.IsTrue(device.GetConfigurationTemplate().ReportTemplates.Any(r => r.Address == $"{Washer}|status"), "the appliances of the last poll stay");

        var unconfigured = Device(new FakeMiele { Down = true }, time, clientId: "fresh");
        await Assert.ThrowsAsync<Exception>(() => unconfigured.StartAsync(CancellationToken.None));
        Assert.AreEqual(DeviceState.Error, unconfigured.State, "a new device shows why it has no appliances");
    }

    [TestMethod]
    public async Task EventsAndAppliancesAreRead()
    {
        var reader = new StringReader("event: ping\ndata: {}\n\nevent: devices\ndata: {\"a\":\ndata: 1}\n\n: comment\nevent: actions\ndata: {}\n\n");
        var events = new List<MieleEvent>();
        await foreach (var item in MieleClient.ReadEventsAsync(reader, TimeSpan.FromSeconds(5), CancellationToken.None))
            events.Add(item);
        CollectionAssert.AreEqual(new[] { new MieleEvent("ping", "{}"), new MieleEvent("devices", "{\"a\":\n1}"), new MieleEvent("actions", "{}") }, events);

        using var silent = new EventStream();
        await Assert.ThrowsExactlyAsync<TimeoutException>(async () =>
        {
            await foreach (var _ in MieleClient.ReadEventsAsync(new StreamReader(silent), TimeSpan.FromMilliseconds(50), CancellationToken.None))
            {
            }
        });

        var washer = MieleAppliance.Parse(Washer, JsonNode.Parse(FakeMiele.WasherJson(MieleCodes.Running, 0.5, 20, elapsed: 65))!.AsObject());
        Assert.AreEqual((1, "Washing machine", 65, 75, 0.5, 20d, 40d), (washer.Type, washer.Name, washer.ElapsedMinutes!.Value, washer.RemainingMinutes!.Value,
            washer.Energy!.Value, washer.Water!.Value, washer.TargetTemperature!.Value));
        Assert.IsNull(washer.Temperature, "-32768 is no value");
        var wh = MieleAppliance.Parse("x", JsonNode.Parse("""{"state":{"status":{"value_raw":255},"ecoFeedback":{"currentEnergyConsumption":{"unit":"Wh","value":750}}}}""")!.AsObject());
        Assert.AreEqual((0.75, false, "notConnected"), (wh.Energy!.Value, wh.Connected, wh.StatusName));

        Assert.AreEqual("abc", MieleAuth.Code("https://localhost/miele?state=x&code=abc"));
        Assert.AreEqual("abc", MieleAuth.Code(" abc "));
        Assert.IsNull(MieleAuth.Code("   "));
    }

    [TestMethod]
    public void TheTemplateNeedsNoMieleWhenTheDeviceIsNotRunning()
    {
        var device = new Miele(NullLogger.Instance);
        var template = device.GetConfigurationTemplate();

        Assert.AreEqual(typeof(Miele).FullName, template.ClassFullName);
        CollectionAssert.AreEquivalent(
            new[] { "clientId", "clientSecret", "email", "password", "country", "authorizationCode", "redirectUri", "language" },
            template.DeviceParameters.Keys.ToArray());
        Assert.AreEqual(0, device.GetMatterEndpoints(template).Count());
    }

    private TestMiele Device(FakeMiele api, ManualTime time, string email = "user@example.com", string code = "", string clientId = "client",
        bool listen = false, List<ReportTemplate>? reports = null, List<CommandTemplate>? commands = null)
    {
        var device = new TestMiele(api)
        {
            DataDirectory = _dataDirectory,
            Time = time,
            ListenToEvents = listen,
            ReconnectDelay = TimeSpan.FromMilliseconds(20),
            MaxReconnectDelay = TimeSpan.FromMilliseconds(100)
        };
        device.Initialize(new DeviceConfiguration
        {
            Id = "device",
            Name = "Miele",
            DeviceParameters = new()
            {
                ["clientId"] = clientId,
                ["clientSecret"] = "client-secret",
                ["email"] = email,
                ["password"] = "secret",
                ["authorizationCode"] = code
            },
            ReportTemplates = reports ?? [],
            CommandTemplates = commands ?? []
        });
        return device;
    }

    // The template id is the address, so the tests can find reports by address.
    private static List<ReportTemplate> Reports(string id, params string[] keys) =>
        keys.Select(k => new ReportTemplate { Id = $"{id}|{k}", Address = $"{id}|{k}" }).ToList();

    private static List<CommandTemplate> Commands(string id, params string[] keys) =>
        keys.Select(k => new CommandTemplate { Id = $"{id}|{k}", Address = $"{id}|{k}" }).ToList();

    private static ConcurrentQueue<Report> Capture(Core.Interfaces.IDevice device)
    {
        var reports = new ConcurrentQueue<Report>();
        device.ReportUpdated += (_, report) => reports.Enqueue((Report)report);
        return reports;
    }

    private static Dictionary<string, ValueModel> Last(IEnumerable<Report> reports) =>
        reports.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.Last().Value);

    private static (string Request, string? Body) Put(FakeMiele api)
    {
        var put = api.Requests.Last(r => r.Method == "PUT");
        return ($"PUT {put.Url}", put.Body);
    }

    private static string Devices(string id, string json) => new JsonObject { [id] = JsonNode.Parse(json) }.ToJsonString();

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

    private static Dictionary<string, string> Form(string body) =>
        body.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record Sent(string Method, string Url, string? Body, Dictionary<string, string> Headers)
    {
        public string? Header(string name) => Headers.GetValueOrDefault(name);
    }

    /// <summary>
    /// A washing machine (TwinDos, programs not selectable remotely) and an oven with a food probe and two programs.
    /// The login takes user@example.com with the password; each token answer gives the next access-N and refresh-N.
    /// </summary>
    private sealed class FakeMiele
    {
        private readonly object _lock = new();
        private readonly List<Sent> _requests = [];
        private string _washer = WasherJson(MieleCodes.Running, 0.4, 15, elapsed: 0);
        private int _tokens;

        public List<Sent> Requests
        {
            get { lock (_lock) return _requests.ToList(); }
        }

        public string Password { get; set; } = "secret";
        public string? RejectToken { get; set; }
        public bool RefuseRefresh { get; set; }
        public bool RefuseActions { get; set; }
        public bool Down { get; set; }
        public bool Limited { get; set; }
        public string WasherActions { get; set; } = """{"processAction":[],"powerOn":false,"powerOff":false,"programId":[],"targetTemperature":[]}""";
        public EventStream? Stream { get; private set; }
        public int Opened { get; private set; }

        public void SetWasher(int status, double? energy, double? water, int elapsed = 0) => _washer = WasherJson(status, energy, water, elapsed);

        public static string WasherJson(int status, double? energy, double? water, int elapsed)
        {
            var eco = energy == null ? null : new JsonObject
            {
                ["currentWaterConsumption"] = new JsonObject { ["unit"] = "l", ["value"] = water },
                ["currentEnergyConsumption"] = new JsonObject { ["unit"] = "kWh", ["value"] = energy },
                ["waterForecast"] = 0.4,
                ["energyForecast"] = 0.5
            };
            return new JsonObject
            {
                ["ident"] = new JsonObject
                {
                    ["type"] = new JsonObject { ["value_raw"] = 1, ["value_localized"] = "Washing machine" },
                    ["deviceName"] = "",
                    ["deviceIdentLabel"] = new JsonObject { ["techType"] = "WWV980" }
                },
                ["state"] = new JsonObject
                {
                    ["ProgramID"] = new JsonObject { ["value_raw"] = 1, ["value_localized"] = "Cottons" },
                    ["status"] = new JsonObject { ["value_raw"] = status, ["value_localized"] = "" },
                    ["programPhase"] = new JsonObject { ["value_raw"] = 260, ["value_localized"] = "Main wash" },
                    ["remainingTime"] = new JsonArray(1, 15),
                    ["startTime"] = new JsonArray(0, 0),
                    ["elapsedTime"] = new JsonArray(elapsed / 60, elapsed % 60),
                    ["targetTemperature"] = new JsonArray(new JsonObject { ["value_raw"] = 4000 }, new JsonObject { ["value_raw"] = -32768 }),
                    ["temperature"] = new JsonArray(new JsonObject { ["value_raw"] = -32768 }),
                    ["signalDoor"] = false,
                    ["signalFailure"] = false,
                    ["signalInfo"] = false,
                    ["remoteEnable"] = new JsonObject { ["fullRemoteControl"] = true, ["smartGrid"] = false, ["mobileStart"] = true },
                    ["spinningSpeed"] = new JsonObject { ["value_raw"] = 1400 },
                    ["ecoFeedback"] = eco
                }
            }.ToJsonString();
        }

        private const string OvenJson = """
            {"ident":{"type":{"value_raw":12,"value_localized":"Oven"},"deviceName":"Kitchen oven","deviceIdentLabel":{"techType":"H7860BP"}},
             "state":{"ProgramID":{"value_raw":13,"value_localized":"Fan plus"},"status":{"value_raw":5},"programPhase":{"value_raw":3073,"value_localized":"Heating up"},
              "remainingTime":[0,0],"startTime":[0,0],"elapsedTime":[0,10],
              "targetTemperature":[{"value_raw":20000}],"temperature":[{"value_raw":18050}],"coreTemperature":[{"value_raw":-32768}],
              "coreTargetTemperature":[{"value_raw":-32768}],"signalDoor":false,"signalFailure":false,"signalInfo":false,
              "remoteEnable":{"fullRemoteControl":true,"smartGrid":false,"mobileStart":false},"ecoFeedback":null}}
            """;

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
            lock (_lock)
                _requests.Add(new Sent(request.Method.Method, request.RequestUri!.ToString(), body, headers));
            return Respond(request, body, headers);
        }

        private HttpResponseMessage Respond(HttpRequestMessage request, string? body, Dictionary<string, string> headers)
        {
            if (Down)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (Limited)
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            var url = request.RequestUri!.ToString();
            if (url == MieleAuth.HeadlessUrl)
            {
                var form = Form(body!);
                if (form["email"] != "user@example.com" || form["password"] != Password)
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>") };
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("/v1/?code=code-1&state=login", UriKind.Relative);
                return redirect;
            }
            if (url == MieleAuth.TokenUrl)
            {
                var form = Form(body!);
                if (form["grant_type"] == "refresh_token" && RefuseRefresh)
                    return Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
                if (form["grant_type"] == "authorization_code" && form["code"] is not ("code-1" or "pasted-1"))
                    return Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
                var n = Interlocked.Increment(ref _tokens);
                return Json($$"""{"access_token":"access-{{n}}","refresh_token":"refresh-{{n}}","token_type":"Bearer","expires_in":2592000}""");
            }
            if (RejectToken != null && headers.GetValueOrDefault("Authorization") == $"Bearer {RejectToken}")
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);

            var path = request.RequestUri.AbsolutePath;
            switch (path)
            {
                case "/v1/devices":
                    return Json(new JsonObject { [Washer] = JsonNode.Parse(_washer), [Oven] = JsonNode.Parse(OvenJson) }.ToJsonString());
                case "/v1/devices/all/events":
                    Stream = new EventStream();
                    Opened++;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(Stream) };
                case "/v1/devices/fillingLevels":
                    return Json("""[{"deviceId":"__ID__","fillingLevels":{"twinDosContainer1FillingLevel":60,"twinDosContainer2FillingLevel":80}}]"""
                        .Replace("__ID__", Washer, StringComparison.Ordinal));
                case $"/v1/devices/{Washer}/actions" when request.Method == HttpMethod.Get:
                    return Json(WasherActions);
                case $"/v1/devices/{Oven}/actions" when request.Method == HttpMethod.Get:
                    return Json("""{"processAction":[2],"powerOn":false,"powerOff":true,"programId":[13,14],"targetTemperature":[{"zone":1,"min":30,"max":250}]}""");
                case $"/v1/devices/{Washer}/programs":
                    return Json("""{"message":"Not supported for this device"}""", HttpStatusCode.BadRequest);
                case $"/v1/devices/{Oven}/programs" when request.Method == HttpMethod.Get:
                    return Json("""
                        [{"programId":13,"program":"Fan plus","parameters":{"temperature":{"min":30,"max":250,"step":5,"mandatory":false},
                          "duration":{"min":[0,1],"max":[12,0],"mandatory":false}}},
                         {"programId":14,"program":"Grill","parameters":{"temperature":{"min":100,"max":250,"step":5,"mandatory":true}}}]
                        """);
                default:
                    if (request.Method == HttpMethod.Put && path.StartsWith("/v1/devices/", StringComparison.Ordinal))
                        return RefuseActions ? Json("""{"message":"Door is open"}""", HttpStatusCode.BadRequest) : new HttpResponseMessage(HttpStatusCode.NoContent);
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }
    }

    /// <summary>An event stream whose lines the test sends; a read waits for the next one.</summary>
    private sealed class EventStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        private byte[] _current = [];
        private int _offset;

        public async Task SendAsync(string type, string data)
        {
            await _chunks.Writer.WriteAsync(Encoding.UTF8.GetBytes($"event: {type}\ndata: {data}\n\n"));
            // Let the listener take the event before the test goes on.
            await Task.Delay(30);
        }

        public void Complete() => _chunks.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset >= _current.Length)
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken) || !_chunks.Reader.TryRead(out var next))
                    return 0;
                _current = next;
                _offset = 0;
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _chunks.Writer.TryComplete();
            base.Dispose(disposing);
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 4, 12, 20, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class TestMiele(FakeMiele api) : Miele(NullLogger.Instance)
    {
        internal override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completion, CancellationToken cancellationToken) =>
            api.SendAsync(request, cancellationToken);
    }
}
