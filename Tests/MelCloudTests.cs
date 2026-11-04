using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Core;
using RIoT2.Core.Models;
using RIoT2.Core.Models.Matter;
using RIoT2.Net.Devices.Catalog;
using RIoT2.Net.Devices.Models;
using RIoT2.Net.Devices.Services.MelCloud;
using ValueType = RIoT2.Core.ValueType;

namespace RIoT2.Net.Devices.Tests;

/// <summary>The MELCloud device against scripted classic MELCloud and MELCloud Home answers; nothing reaches MELCloud.</summary>
[TestClass]
public class MelCloudTests
{
    private const string Classic = "https://app.melcloud.com/Mitsubishi.Wifi.Client";
    private const string Cognito = "https://live-melcloudhome.auth.eu-west-1.amazoncognito.com";

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
    public async Task ClassicLogsInFindsTheAirToAirUnitsAndReportsThem()
    {
        var http = ClassicHttp();
        var device = Device(http, "classic", reports: Reports("101", MelCloud.RoomTemperature, MelCloud.SetTemperature, MelCloud.Power,
            MelCloud.Mode, MelCloud.ThermostatMode, MelCloud.FanSpeed, MelCloud.VaneVertical, MelCloud.EnergyConsumed));
        var reports = Capture(device);

        await device.StartAsync(CancellationToken.None);

        Assert.AreEqual(DeviceState.Running, device.State);
        CollectionAssert.AreEqual(new[] { "POST Login/ClientLogin", "GET User/ListDevices", "GET Device/Get?id=101&buildingID=1" }, http.Calls());
        Assert.AreEqual("key-1", http.Requests[1].Header("X-MitsContextKey"));
        var login = JsonNode.Parse(http.Requests[0].Body!)!;
        Assert.AreEqual(("user@example.com", "secret", true), ((string)login["Email"]!, (string)login["Password"]!, (bool)login["Persist"]!));
        StringAssert.Contains(File.ReadAllText(Directory.GetFiles(_dataDirectory).Single()), "key-1", "the context key is kept for the next start");

        var values = reports.ToDictionary(r => r.Id, r => r.Value);
        Assert.AreEqual(21.5, values["101|roomTemperature"].GetValue<double>());
        Assert.AreEqual(22d, values["101|setTemperature"].GetValue<double>());
        Assert.IsTrue(values["101|power"].GetValue<bool>());
        Assert.AreEqual("heat", values["101|mode"].GetValue<string>());
        Assert.AreEqual(MelCloudModes.MatterHeat, values["101|thermostatMode"].GetValue<int>());
        Assert.AreEqual((ValueType.Text, "3"), (values["101|fanSpeed"].Type, values["101|fanSpeed"].GetValue<string>()), "a fan speed is text, not the number 3");
        Assert.AreEqual("swing", values["101|vaneVertical"].GetValue<string>());
        Assert.AreEqual(1234.567, values["101|energyConsumed"].GetValue<double>());

        var template = device.GetConfigurationTemplate();
        Assert.HasCount(3, http.Requests, "the template comes from the units the last poll read");
        var addresses = template.ReportTemplates.Select(r => r.Address).ToList();
        CollectionAssert.Contains(addresses, "101|energyConsumed");
        CollectionAssert.Contains(addresses, "101|offline");
        CollectionAssert.DoesNotContain(addresses, "101|energyToday", "classic MELCloud has a meter, not a daily sum");
        Assert.IsFalse(addresses.Any(a => a.StartsWith("202|", StringComparison.Ordinal)), "the air-to-water unit is skipped");
        Assert.AreEqual(1, template.ReportTemplates.Count(r => r.Address == "101|roomTemperature"), "a unit listed twice is one unit");
        Assert.AreEqual("Living room room temperature", template.ReportTemplates.Single(r => r.Address == "101|roomTemperature").Name);
        CollectionAssert.Contains(template.CommandTemplates!.Select(c => c.Address).ToList(), "refresh");

        var endpoint = device.GetMatterEndpoints(template).Single();
        Assert.AreEqual(("melcloud:101", "Living room", MatterDeviceType.Thermostat), (endpoint.Id, endpoint.Name, endpoint.DeviceType));
        var room = template.ReportTemplates.Single(r => r.Address == "101|roomTemperature").Id;
        var target = template.ReportTemplates.Single(r => r.Address == "101|setTemperature").Id;
        var setpoint = template.CommandTemplates!.Single(c => c.Address == "101|setTemperature").Id;
        var modeCommand = template.CommandTemplates!.Single(c => c.Address == "101|thermostatMode").Id;
        Assert.AreEqual((room, MatterValueScale.CelsiusToHundredths),
            endpoint.Attributes.Where(a => a.Attribute == MatterAttribute.LocalTemperature).Select(a => (a.ReportTemplateId, a.Scale)).Single());
        Assert.AreEqual(target, endpoint.Attributes.Single(a => a.Attribute == MatterAttribute.OccupiedHeatingSetpoint).ReportTemplateId);
        Assert.AreEqual((setpoint, MatterValueScale.CelsiusToHundredths),
            endpoint.Commands.Where(c => c.Attribute == MatterAttribute.OccupiedHeatingSetpoint).Select(c => (c.CommandTemplateId, c.Scale)).Single());
        Assert.AreEqual(modeCommand, endpoint.Commands.Single(c => c.Attribute == MatterAttribute.ThermostatSystemMode).CommandTemplateId);
    }

    [TestMethod]
    public async Task ClassicCommandsPostTheStateWithTheirFlagsAndReportTheChange()
    {
        var http = ClassicHttp();
        var device = Device(http, "classic",
            reports: Reports("101", MelCloud.SetTemperature, MelCloud.Power, MelCloud.ThermostatMode),
            commands: Commands("101", MelCloud.SetTemperature, MelCloud.ThermostatMode, MelCloud.Mode));
        var reports = Capture(device);
        await device.StartAsync(CancellationToken.None);
        reports.Clear();

        // A plan sends the bare number; 22.3 rounds to the unit's half degree.
        await device.ExecuteCommandAsync("101|setTemperature", "22.3", CancellationToken.None);
        var set = JsonNode.Parse(http.Requests.Last().Body!)!;
        Assert.AreEqual("POST Device/SetAta", http.Calls().Last());
        Assert.AreEqual((22.5, 0x04, true), ((double)set["SetTemperature"]!, (int)set["EffectiveFlags"]!, (bool)set["HasPendingCommand"]!));
        Assert.AreEqual("kept", (string)set["Unknown"]!, "the posted state is the state MELCloud sent, with the change");
        Assert.AreEqual(22.5, reports.Single().Value.GetValue<double>());

        // Matter's off mode powers the unit off.
        reports.Clear();
        await device.ExecuteCommandAsync("101|thermostatMode", "0", CancellationToken.None);
        set = JsonNode.Parse(http.Requests.Last().Body!)!;
        Assert.AreEqual((false, 0x01), ((bool)set["Power"]!, (int)set["EffectiveFlags"]!));
        var changed = reports.ToDictionary(r => r.Id, r => r.Value.ToJson());
        CollectionAssert.AreEquivalent(new[] { "101|power", "101|thermostatMode" }, changed.Keys.ToArray());
        Assert.AreEqual(("false", "0"), (changed["101|power"], changed["101|thermostatMode"]));

        var sent = http.Requests.Count;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => device.ExecuteCommandAsync("101|setTemperature", "40", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => device.ExecuteCommandAsync("101|mode", "\"turbo\"", CancellationToken.None));
        await Assert.ThrowsExactlyAsync<UnknownCommandException>(() => device.ExecuteCommandAsync("missing", "1", CancellationToken.None));
        Assert.HasCount(sent, http.Requests, "a value that doesn't fit isn't sent");

        await device.ExecuteCommandAsync("101|mode", "\"cool\"", CancellationToken.None);
        set = JsonNode.Parse(http.Requests.Last().Body!)!;
        Assert.AreEqual((3, 0x02), ((int)set["OperationMode"]!, (int)set["EffectiveFlags"]!));
    }

    [TestMethod]
    public async Task ClassicUsesTheKeptContextKeyAndLogsInAgainWhenItExpires()
    {
        var http = ClassicHttp(expiredKey: "old-key");
        var device = Device(http, "classic", reports: Reports("101", MelCloud.RoomTemperature));
        new MelCloudSessionStore(_dataDirectory, "classic", "user@example.com", NullLogger.Instance).Save(new MelCloudSession { ContextKey = "old-key" });

        await device.StartAsync(CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "GET User/ListDevices", "POST Login/ClientLogin", "GET User/ListDevices", "GET Device/Get?id=101&buildingID=1" }, http.Calls());
        Assert.AreEqual(("old-key", "key-1"), (http.Requests[0].Header("X-MitsContextKey"), http.Requests[2].Header("X-MitsContextKey")));
        Assert.AreEqual(DeviceState.Running, device.State);
    }

    [TestMethod]
    public async Task AWrongPasswordPutsTheDeviceIntoTheErrorState()
    {
        var http = new ScriptedHttp((_, _) => Json("""{"ErrorId":1,"LoginData":null}"""));
        var device = Device(http, "classic");

        var error = await Assert.ThrowsAsync<Exception>(() => device.StartAsync(CancellationToken.None));

        Assert.IsInstanceOfType<MelCloudAuthenticationException>(error.InnerException);
        Assert.AreEqual(DeviceState.Error, device.State);
        StringAssert.Contains(device.StateMessage, "check the e-mail and password");
        Assert.IsFalse(device.StateMessage.Contains("user@example.com", StringComparison.Ordinal), "the e-mail is masked");
    }

    [TestMethod]
    public async Task ARefreshKeepsTheLastStateWhileMelCloudLimitsRequests()
    {
        var limited = false;
        var classic = ClassicHttp();
        var http = new ScriptedHttp((request, body) => limited ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : classic.Respond(request, body));
        var time = new ManualTime();
        var device = Device(http, "classic", reports: Reports("101", MelCloud.RoomTemperature), time: time);
        await device.StartAsync(CancellationToken.None);
        var polled = http.Requests.Count;

        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);
        Assert.HasCount(polled, http.Requests, "a refresh within a minute of the last poll doesn't call MELCloud");

        time.Now += TimeSpan.FromHours(1);
        limited = true;
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);
        Assert.AreEqual(DeviceState.Running, device.State, "a 429 is waited out, not an error");
        Assert.AreEqual("101", device.GetConfigurationTemplate().ReportTemplates[0].Address.Split('|')[0], "the units of the last poll stay");
    }

    [TestMethod]
    public async Task AConfiguredDeviceOutlivesAnOutageAtStartAndPollsForItsFirstCommand()
    {
        var down = true;
        var classic = ClassicHttp();
        var http = new ScriptedHttp((request, body) => down ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : classic.Respond(request, body));
        var device = Device(http, "classic", reports: Reports("101", MelCloud.SetTemperature), commands: Commands("101", MelCloud.SetTemperature));

        await device.StartAsync(CancellationToken.None);
        Assert.AreEqual(DeviceState.Running, device.State, "MELCloud being down isn't the configuration's fault");

        down = false;
        await device.ExecuteCommandAsync("101|setTemperature", "21", CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "POST Login/ClientLogin", "GET User/ListDevices", "GET Device/Get?id=101&buildingID=1", "POST Device/SetAta" },
            http.Calls().Skip(1).ToArray());

        var unconfigured = Device(new ScriptedHttp((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)), "classic");
        await Assert.ThrowsAsync<Exception>(() => unconfigured.StartAsync(CancellationToken.None));
        Assert.AreEqual(DeviceState.Error, unconfigured.State, "a new device shows why it has no units");
    }

    [TestMethod]
    public async Task HomeLogsInThroughCognitoWithPkceReadsItsUnitsAndSetsThem()
    {
        var home = new HomeScript();
        var http = new ScriptedHttp(home.Respond);
        var device = Device(http, "home",
            reports: Reports("unit-a", MelCloud.RoomTemperature, MelCloud.Mode, MelCloud.FanSpeed, MelCloud.VaneVertical, MelCloud.VaneHorizontal,
                MelCloud.Offline, MelCloud.EnergyToday, MelCloud.ThermostatMode),
            commands: Commands("unit-a", MelCloud.SetTemperature, MelCloud.FanSpeed),
            time: new ManualTime());
        var reports = Capture(device);

        await device.StartAsync(CancellationToken.None);

        Assert.AreEqual(DeviceState.Running, device.State);
        var par = Form(http.Requests[0].Body!);
        var token = Form(http.Requests.Single(r => r.Url.EndsWith("/connect/token", StringComparison.Ordinal)).Body!);
        Assert.AreEqual(("S256", "homemobile", "authorization_code", "code-1"), (par["code_challenge_method"], par["client_id"], token["grant_type"], token["code"]));
        Assert.AreEqual(par["code_challenge"], Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(token["code_verifier"]))), "the verifier matches the challenge");
        var credentials = http.Requests.Single(r => r.Method == "POST" && r.Url.StartsWith(Cognito, StringComparison.Ordinal));
        var form = Form(credentials.Body!);
        Assert.AreEqual(("csrf-1", "user@example.com", "secret"), (form["_csrf"], form["username"], form["password"]));
        StringAssert.Contains(credentials.Header("Cookie"), "XSRF-TOKEN=x1", "the login keeps its cookies");
        Assert.AreEqual("Bearer access-1", http.Requests.Single(r => r.Url.EndsWith("/context", StringComparison.Ordinal)).Header("Authorization"));

        var values = reports.ToDictionary(r => r.Id, r => r.Value);
        Assert.AreEqual(20.5, values["unit-a|roomTemperature"].GetValue<double>());
        Assert.AreEqual("cool", values["unit-a|mode"].GetValue<string>());
        Assert.AreEqual(MelCloudModes.MatterCool, values["unit-a|thermostatMode"].GetValue<int>());
        Assert.AreEqual("2", values["unit-a|fanSpeed"].GetValue<string>());
        Assert.AreEqual("swing", values["unit-a|vaneVertical"].GetValue<string>(), "a numbered position is read too");
        Assert.AreEqual("3", values["unit-a|vaneHorizontal"].GetValue<string>(), "Center is Centre");
        Assert.IsFalse(values["unit-a|offline"].GetValue<bool>());
        Assert.AreEqual(0.751, values["unit-a|energyToday"].GetValue<double>());
        var kept = new MelCloudSessionStore(_dataDirectory, "home", "user@example.com", NullLogger.Instance).Load()!;
        Assert.AreEqual(("access-1", "refresh-1"), (kept.AccessToken, kept.RefreshToken));

        await device.ExecuteCommandAsync("unit-a|setTemperature", "23", CancellationToken.None);
        var put = http.Requests.Last();
        Assert.AreEqual($"PUT {MelCloudHomeBackend.ApiUrl}/monitor/ataunit/unit-a", $"{put.Method} {put.Url}");
        var body = JsonNode.Parse(put.Body!)!.AsObject();
        Assert.AreEqual(23d, (double)body["setTemperature"]!);
        Assert.IsTrue(body.ContainsKey("power") && body["power"] == null, "every field is sent; null leaves it as it is");

        await device.ExecuteCommandAsync("unit-a|fanSpeed", "\"auto\"", CancellationToken.None);
        Assert.AreEqual("Auto", (string)JsonNode.Parse(http.Requests.Last().Body!)!["setFanSpeed"]!);
    }

    [TestMethod]
    public async Task HomeRefreshesAnExpiredTokenAndKeepsTheRotatedOne()
    {
        var home = new HomeScript();
        var http = new ScriptedHttp(home.Respond);
        var time = new ManualTime();
        new MelCloudSessionStore(_dataDirectory, "home", "user@example.com", NullLogger.Instance)
            .Save(new MelCloudSession { AccessToken = "stale", RefreshToken = "refresh-0", ExpiresAt = time.Now.AddMinutes(-5) });
        var device = Device(http, "home", reports: Reports("unit-a", MelCloud.RoomTemperature), time: time);

        await device.StartAsync(CancellationToken.None);

        var token = Form(http.Requests[0].Body!);
        Assert.AreEqual(("refresh_token", "refresh-0"), (token["grant_type"], token["refresh_token"]));
        Assert.IsFalse(http.Requests.Any(r => r.Url.Contains("/connect/par", StringComparison.Ordinal)), "no new login");
        Assert.AreEqual("Bearer access-2", http.Requests.Single(r => r.Url.EndsWith("/context", StringComparison.Ordinal)).Header("Authorization"));
        Assert.AreEqual("refresh-2", new MelCloudSessionStore(_dataDirectory, "home", "user@example.com", NullLogger.Instance).Load()!.RefreshToken);
    }

    [TestMethod]
    public void CommandValuesMapToChanges()
    {
        var unit = new MelCloudUnit { Id = "u", MinTemperature = 10, MaxTemperature = 31, TemperatureStep = 1 };

        Assert.AreEqual(21d, MelCloud.ParseCommand(MelCloud.SetTemperature, "\"20.6\"", unit)!.SetTemperature);
        Assert.AreEqual((true, "heat"), (MelCloud.ParseCommand(MelCloud.ThermostatMode, "4", unit)!.Power, MelCloud.ParseCommand(MelCloud.ThermostatMode, "4", unit)!.Mode));
        Assert.AreEqual("3", MelCloud.ParseCommand(MelCloud.FanSpeed, "3", unit)!.FanSpeed);
        Assert.AreEqual("swing", MelCloud.ParseCommand(MelCloud.VaneHorizontal, "\"Swing\"", unit)!.VaneHorizontal);
        Assert.IsTrue(MelCloud.ParseCommand(MelCloud.Power, "\"on\"", unit)!.Power);
        Assert.IsNull(MelCloud.ParseCommand("unknown", "1", unit));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MelCloud.ParseCommand(MelCloud.ThermostatMode, "2", unit));
        Assert.ThrowsExactly<ArgumentException>(() => MelCloud.ParseCommand(MelCloud.FanSpeed, "\"turbo\"", unit));
    }

    [TestMethod]
    public void TheTemplateNeedsNoMelCloudWhenTheDeviceIsNotRunning()
    {
        var device = new MelCloud(NullLogger.Instance);
        var template = device.GetConfigurationTemplate();

        Assert.AreEqual(typeof(MelCloud).FullName, template.ClassFullName);
        CollectionAssert.AreEquivalent(new[] { "cloud", "email", "password" }, template.DeviceParameters.Keys.ToArray());
        Assert.AreEqual(0, device.GetMatterEndpoints(template).Count());
    }

    private TestMelCloud Device(ScriptedHttp http, string cloud, List<ReportTemplate>? reports = null, List<CommandTemplate>? commands = null, ManualTime? time = null)
    {
        var device = new TestMelCloud(http) { DataDirectory = _dataDirectory };
        if (time != null)
            device.Time = time;
        device.Initialize(new DeviceConfiguration
        {
            Id = "device",
            Name = "MELCloud",
            DeviceParameters = new() { ["cloud"] = cloud, ["email"] = "user@example.com", ["password"] = "secret" },
            ReportTemplates = reports ?? [],
            CommandTemplates = commands ?? []
        });
        return device;
    }

    // The template id is the address, so the tests can find reports by address.
    private static List<ReportTemplate> Reports(string unit, params string[] keys) =>
        keys.Select(k => new ReportTemplate { Id = $"{unit}|{k}", Address = $"{unit}|{k}" }).ToList();

    private static List<CommandTemplate> Commands(string unit, params string[] keys) =>
        keys.Select(k => new CommandTemplate { Id = $"{unit}|{k}", Address = $"{unit}|{k}" }).ToList();

    private static ConcurrentQueue<Report> Capture(Core.Interfaces.IDevice device)
    {
        var reports = new ConcurrentQueue<Report>();
        device.ReportUpdated += (_, report) => reports.Enqueue((Report)report);
        return reports;
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Redirect(string location, string? cookie = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.TryAddWithoutValidation("Location", location);
        if (cookie != null)
            response.Headers.TryAddWithoutValidation("Set-Cookie", cookie);
        return response;
    }

    private static Dictionary<string, string> Form(string body) =>
        body.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // A unit at the building root, the same unit again on a floor, and an air-to-water unit in an area.
    private static ScriptedHttp ClassicHttp(string? expiredKey = null) => new((request, _) =>
    {
        var url = request.RequestUri!.ToString();
        var key = request.Headers.TryGetValues("X-MitsContextKey", out var keys) ? keys.Single() : null;
        if (url.EndsWith("/Login/ClientLogin", StringComparison.Ordinal))
            return Json("""{"ErrorId":null,"LoginData":{"ContextKey":"key-1"}}""");
        if (expiredKey != null && key == expiredKey)
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        if (url.EndsWith("/User/ListDevices", StringComparison.Ordinal))
            return Json("""
                [{"ID":1,"Structure":{
                  "Devices":[{"DeviceID":101,"BuildingID":1,"DeviceName":"Living room","Device":{"DeviceType":0,"TemperatureIncrement":0.5,
                    "MinTempHeat":10,"MaxTempHeat":31,"HasEnergyConsumedMeter":true,"CurrentEnergyConsumed":1234567}}],
                  "Floors":[{"Devices":[{"DeviceID":101,"BuildingID":1,"DeviceName":"Living room","Device":{"DeviceType":0}}],
                    "Areas":[{"Devices":[{"DeviceID":202,"BuildingID":1,"DeviceName":"Ecodan","Device":{"DeviceType":1}}]}]}],
                  "Areas":[]}}]
                """);
        if (url.Contains("/Device/Get?id=101", StringComparison.Ordinal))
            return Json("""
                {"DeviceID":101,"Power":true,"OperationMode":1,"SetTemperature":22,"RoomTemperature":21.5,"SetFanSpeed":3,
                 "VaneVertical":7,"VaneHorizontal":0,"Offline":false,"EffectiveFlags":0,"Unknown":"kept"}
                """);
        if (url.EndsWith("/Device/SetAta", StringComparison.Ordinal))
            return Json("{}");
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    });

    // MELCloud Home's login: the authorize redirect to Cognito's form, the form post to a page that links the callback,
    // and the callback's redirect to the app with the code.
    private sealed class HomeScript
    {
        public HttpResponseMessage Respond(HttpRequestMessage request, string? body)
        {
            var url = request.RequestUri!.ToString();
            var auth = MelCloudHomeBackend.AuthUrl;
            if (url == $"{auth}/connect/par")
                return Json("""{"request_uri":"urn:par:1","expires_in":90}""", HttpStatusCode.Created);
            if (url.StartsWith($"{auth}/connect/authorize?", StringComparison.Ordinal))
                return Redirect($"{Cognito}/login?client_id=c&state=s", "idsrv=1; Path=/; Secure");
            if (url.StartsWith($"{Cognito}/login", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
            {
                var page = Json("""<form><input type="hidden" name="_csrf" value="csrf-1"/></form>""");
                page.Headers.TryAddWithoutValidation("Set-Cookie", "XSRF-TOKEN=x1; Path=/; Secure");
                return page;
            }
            if (url.StartsWith($"{Cognito}/login", StringComparison.Ordinal))
                return Redirect($"{auth}/signin-oidc");
            if (url == $"{auth}/signin-oidc")
                return Json("""<a href="/connect/authorize/callback?client_id=homemobile&amp;request_uri=urn">Continue</a>""");
            if (url == $"{auth}/connect/authorize/callback?client_id=homemobile&request_uri=urn")
                return Redirect("melcloudhome://?code=code-1&state=s");
            if (url == $"{auth}/connect/token")
            {
                return body!.Contains("grant_type=refresh_token", StringComparison.Ordinal)
                    ? Json("""{"access_token":"access-2","refresh_token":"refresh-2","expires_in":3600}""")
                    : Json("""{"access_token":"access-1","refresh_token":"refresh-1","expires_in":3600}""");
            }
            if (url == $"{MelCloudHomeBackend.ApiUrl}/context")
                return Json("""
                    {"buildings":[{"name":"Home","airToAirUnits":[{"id":"unit-a","givenDisplayName":"Bedroom","isConnected":true,
                      "settings":[{"name":"Power","value":"True"},{"name":"OperationMode","value":"Cool"},{"name":"SetTemperature","value":"22"},
                        {"name":"RoomTemperature","value":"20.5"},{"name":"SetFanSpeed","value":"Two"},{"name":"VaneVerticalDirection","value":"7"},
                        {"name":"VaneHorizontalDirection","value":"Center"}],
                      "capabilities":{"hasEnergyConsumedMeter":true,"hasHalfDegreeIncrements":true,"minTempCoolDry":16,"maxTempCoolDry":31}}]}],
                     "guestBuildings":[]}
                    """);
            if (url.StartsWith($"{MelCloudHomeBackend.ApiUrl}/telemetry/telemetry/energy/unit-a?", StringComparison.Ordinal))
                return Json("""{"measureData":[{"type":"cumulativeEnergyConsumedSinceLastUpload","values":[{"time":"2026-11-04 00:00","value":"500"},{"time":"2026-11-04 01:00","value":"251"}]}]}""");
            if (url == $"{MelCloudHomeBackend.ApiUrl}/monitor/ataunit/unit-a" && request.Method == HttpMethod.Put)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed record Sent(string Method, string Url, string? Body, Dictionary<string, string> Headers)
    {
        public string? Header(string name) => Headers.GetValueOrDefault(name);
    }

    private sealed class ScriptedHttp(Func<HttpRequestMessage, string?, HttpResponseMessage> respond)
    {
        public List<Sent> Requests { get; } = [];

        public HttpResponseMessage Respond(HttpRequestMessage request, string? body) => respond(request, body);

        public string[] Calls() => Requests.Select(r => $"{r.Method} {r.Url.Replace(Classic + "/", "", StringComparison.Ordinal)}").ToArray();

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
            Requests.Add(new Sent(request.Method.Method, request.RequestUri!.ToString(), body, headers));
            return respond(request, body);
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class TestMelCloud(ScriptedHttp http) : MelCloud(NullLogger.Instance)
    {
        internal override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            http.SendAsync(request, cancellationToken);
    }
}
