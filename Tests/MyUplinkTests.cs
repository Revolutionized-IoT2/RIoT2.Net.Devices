using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Core;
using RIoT2.Core.Models;
using RIoT2.Core.Models.Matter;
using RIoT2.Net.Devices.Catalog;
using RIoT2.Net.Devices.Models;
using RIoT2.Net.Devices.Services.MyUplink;
using ValueType = RIoT2.Core.ValueType;

namespace RIoT2.Net.Devices.Tests;

/// <summary>The myUplink device against a scripted myUplink API; nothing reaches myUplink.</summary>
[TestClass]
public class MyUplinkTests
{
    private const string Pump = "emmy-1";

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
    public async Task StartDiscoversTheHeatPumpAndReportsItsCuratedParameters()
    {
        var api = new FakeMyUplink();
        var device = Device(api, reports: Reports("outdoorTemperature", "exhaustAirTemperature", "degreeMinutes", "hotWaterComfortMode",
            MyUplink.SetTemperature, MyUplink.ThermostatMode, MyUplink.SmartHomeMode, MyUplink.Connected, MyUplink.Alarm,
            MyUplink.ActiveAlarms, MyUplink.AlarmText, MyUplink.PremiumManage, MyUplink.EnergyToday, "energyHeating", "p50000"));
        var reports = Capture(device);

        await device.StartAsync(CancellationToken.None);

        Assert.AreEqual(DeviceState.Running, device.State);
        var token = Form(api.Requests[0].Body!);
        Assert.AreEqual(("client_credentials", "client", "secret", MyUplinkClient.Scope),
            (token["grant_type"], token["client_id"], token["client_secret"], token["scope"]));
        var discovery = api.Requests.Single(r => r.Url.EndsWith($"/v2/devices/{Pump}/points", StringComparison.Ordinal));
        Assert.AreEqual("en-GB", discovery.Header("Accept-Language"));
        StringAssert.StartsWith(discovery.Header("Authorization"), "Bearer ");
        Assert.IsFalse(api.Requests.Any(r => r.Url.Contains("?parameters=", StringComparison.Ordinal)), "discovery read the values already");

        var values = reports.ToDictionary(r => r.Id, r => r.Value);
        Assert.AreEqual(5.3, values[$"{Pump}|outdoorTemperature"].GetValue<double>());
        Assert.AreEqual(-120d, values[$"{Pump}|degreeMinutes"].GetValue<double>(), "43005 is the F750's degree minutes");
        Assert.AreEqual((ValueType.Text, "Normal"), (values[$"{Pump}|hotWaterComfortMode"].Type, values[$"{Pump}|hotWaterComfortMode"].GetValue<string>()),
            "an enum reports its text");
        Assert.AreEqual(20d, values[$"{Pump}|setTemperature"].GetValue<double>(), "offset 0 is the base temperature");
        Assert.AreEqual(MyUplink.MatterHeat, values[$"{Pump}|thermostatMode"].GetValue<int>());
        Assert.AreEqual("Default", values[$"{Pump}|smartHomeMode"].GetValue<string>());
        Assert.IsTrue(values[$"{Pump}|connected"].GetValue<bool>());
        Assert.IsTrue(values[$"{Pump}|alarm"].GetValue<bool>());
        Assert.AreEqual(1, values[$"{Pump}|activeAlarms"].GetValue<int>());
        Assert.AreEqual("High condenser out", values[$"{Pump}|alarmText"].GetValue<string>(), "the newest alarm");
        Assert.IsFalse(values[$"{Pump}|premiumManage"].GetValue<bool>());
        Assert.AreEqual(0d, values[$"{Pump}|energyToday"].GetValue<double>());
        Assert.AreEqual(1000.5, values[$"{Pump}|energyHeating"].GetValue<double>());
        Assert.AreEqual(10d, values[$"{Pump}|p50000"].GetValue<double>());

        var calls = api.Requests.Count;
        var template = device.GetConfigurationTemplate();
        Assert.HasCount(calls, api.Requests, "the template comes from the last poll");
        var addresses = template.ReportTemplates.Select(r => r.Address).ToList();
        CollectionAssert.Contains(addresses, $"{Pump}|p40033");
        CollectionAssert.DoesNotContain(addresses, $"{Pump}|p99999", "a parameter the pump doesn't have is skipped");
        CollectionAssert.DoesNotContain(addresses, $"{Pump}|supplyTemperature", "a curated parameter the pump doesn't have is skipped");
        Assert.AreEqual("Home outdoor temperature", template.ReportTemplates.Single(r => r.Address == $"{Pump}|outdoorTemperature").Name);
        Assert.AreEqual("°C", template.ReportTemplates.Single(r => r.Address == $"{Pump}|outdoorTemperature").Parameters["unit"]);
        var setpointSchema = template.ReportTemplates.Single(r => r.Address == $"{Pump}|setTemperature").Schema!;
        Assert.AreEqual((10d, 30d), (setpointSchema.Minimum, setpointSchema.Maximum), "offset -10..10 around 20 °C");
        CollectionAssert.AreEqual(new object[] { "Economy", "Normal", "Luxury" },
            template.ReportTemplates.Single(r => r.Address == $"{Pump}|hotWaterComfortMode").Schema!.Enum);
        Assert.AreEqual(100d, template.ReportTemplates.Single(r => r.Address == $"{Pump}|p50000").Schema!.Maximum, "the range is scaled");

        var commands = template.CommandTemplates!.Select(c => c.Address).ToList();
        CollectionAssert.IsSubsetOf(new[] { $"{Pump}|setTemperature", $"{Pump}|heatingOffset", $"{Pump}|hotWaterComfortMode", $"{Pump}|hotWaterBoost",
            $"{Pump}|smartHomeMode", $"{Pump}|setParameter", $"{Pump}|p50000", "refresh" }, commands);
        CollectionAssert.DoesNotContain(commands, $"{Pump}|p40033", "a read-only extra parameter has no command");

        var endpoint = device.GetMatterEndpoints(template).Single();
        Assert.AreEqual(($"myuplink:{Pump}", "Home", MatterDeviceType.Thermostat, "F750"), (endpoint.Id, endpoint.Name, endpoint.DeviceType, endpoint.ProductName));
        var exhaust = template.ReportTemplates.Single(r => r.Address == $"{Pump}|exhaustAirTemperature").Id;
        var target = template.ReportTemplates.Single(r => r.Address == $"{Pump}|setTemperature").Id;
        var mode = template.ReportTemplates.Single(r => r.Address == $"{Pump}|thermostatMode").Id;
        var setpoint = template.CommandTemplates!.Single(c => c.Address == $"{Pump}|setTemperature").Id;
        Assert.AreEqual((exhaust, MatterValueScale.CelsiusToHundredths),
            endpoint.Attributes.Where(a => a.Attribute == MatterAttribute.LocalTemperature).Select(a => (a.ReportTemplateId, a.Scale)).Single());
        Assert.AreEqual(target, endpoint.Attributes.Single(a => a.Attribute == MatterAttribute.OccupiedHeatingSetpoint).ReportTemplateId);
        Assert.AreEqual(mode, endpoint.Attributes.Single(a => a.Attribute == MatterAttribute.ThermostatSystemMode).ReportTemplateId);
        Assert.AreEqual((setpoint, MatterValueScale.CelsiusToHundredths),
            endpoint.Commands.Where(c => c.Attribute == MatterAttribute.OccupiedHeatingSetpoint).Select(c => (c.CommandTemplateId, c.Scale)).Single());
        Assert.IsFalse(endpoint.Commands.Any(c => c.Attribute == MatterAttribute.ThermostatSystemMode), "the mode is always heat");
    }

    [TestMethod]
    public async Task CommandsWriteTheParametersAndReportTheChange()
    {
        var api = new FakeMyUplink();
        var device = Device(api,
            reports: Reports(MyUplink.SetTemperature, "heatingOffset", "hotWaterComfortMode", MyUplink.SmartHomeMode),
            commands: Commands(MyUplink.SetTemperature, "hotWaterComfortMode", "hotWaterBoost", MyUplink.SmartHomeMode, MyUplink.SetParameter, "p50000"));
        var reports = Capture(device);
        await device.StartAsync(CancellationToken.None);
        reports.Clear();

        // The heat templates send a bare temperature: 22.4 °C is offset 2 around 20 °C.
        await device.ExecuteCommandAsync($"{Pump}|setTemperature", "22.4", CancellationToken.None);
        var patch = api.Requests.Last();
        Assert.AreEqual(("PATCH", $"{MyUplinkClient.ApiUrl}/v2/devices/{Pump}/points"), (patch.Method, patch.Url));
        Assert.AreEqual("2", (string)JsonNode.Parse(patch.Body!)!["47011"]!);
        var changed = reports.ToDictionary(r => r.Id, r => r.Value.ToJson());
        CollectionAssert.AreEquivalent(new[] { $"{Pump}|setTemperature", $"{Pump}|heatingOffset" }, changed.Keys.ToArray());
        Assert.AreEqual(("22", "2"), (changed[$"{Pump}|setTemperature"], changed[$"{Pump}|heatingOffset"]));

        reports.Clear();
        await device.ExecuteCommandAsync($"{Pump}|hotWaterComfortMode", "\"luxury\"", CancellationToken.None);
        Assert.AreEqual("2", (string)JsonNode.Parse(api.Requests.Last().Body!)!["47041"]!);
        Assert.AreEqual("Luxury", reports.Single().Value.GetValue<string>());

        await device.ExecuteCommandAsync($"{Pump}|hotWaterBoost", "1", CancellationToken.None);
        Assert.AreEqual("1", (string)JsonNode.Parse(api.Requests.Last().Body!)!["48132"]!, "an enum takes its value too");

        reports.Clear();
        await device.ExecuteCommandAsync($"{Pump}|smartHomeMode", "away", CancellationToken.None);
        var put = api.Requests.Last();
        Assert.AreEqual(("PUT", $"{MyUplinkClient.ApiUrl}/v2/systems/sys-1/smart-home-mode"), (put.Method, put.Url));
        Assert.AreEqual("Away", (string)JsonNode.Parse(put.Body!)!["smartHomeMode"]!);
        Assert.AreEqual("Away", reports.Single().Value.GetValue<string>());

        await device.ExecuteCommandAsync($"{Pump}|setParameter", """{"parameterId":"50000","value":55.4}""", CancellationToken.None);
        Assert.AreEqual("55", (string)JsonNode.Parse(api.Requests.Last().Body!)!["50000"]!, "rounded to the step");
        await device.ExecuteCommandAsync($"{Pump}|p50000", "12", CancellationToken.None);
        Assert.AreEqual("12", (string)JsonNode.Parse(api.Requests.Last().Body!)!["50000"]!);

        var sent = api.Requests.Count;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => device.ExecuteCommandAsync($"{Pump}|setTemperature", "35", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => device.ExecuteCommandAsync($"{Pump}|hotWaterComfortMode", "\"turbo\"", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => device.ExecuteCommandAsync($"{Pump}|smartHomeMode", "\"party\"", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            device.ExecuteCommandAsync($"{Pump}|setParameter", """{"parameterId":"40033","value":1}""", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            device.ExecuteCommandAsync($"{Pump}|setParameter", """{"parameterId":"12345","value":1}""", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => device.ExecuteCommandAsync($"{Pump}|p50000", "101", CancellationToken.None));
        await Assert.ThrowsExactlyAsync<UnknownCommandException>(() => device.ExecuteCommandAsync("missing", "1", CancellationToken.None));
        Assert.HasCount(sent, api.Requests, "a value that doesn't fit isn't sent");
    }

    [TestMethod]
    public async Task AChangeMyUplinkRefusesNamesThePremiumSubscription()
    {
        var api = new FakeMyUplink { Forbidden = true };
        var device = Device(api, reports: Reports("heatingOffset"), commands: Commands("heatingOffset"));
        var reports = Capture(device);
        await device.StartAsync(CancellationToken.None);
        reports.Clear();

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            device.ExecuteCommandAsync($"{Pump}|heatingOffset", "3", CancellationToken.None));

        StringAssert.Contains(error.Message, "Premium Manage");
        StringAssert.Contains(error.Message, "Manage active: no");
        Assert.IsEmpty(reports, "a refused change isn't reported");

        api.Forbidden = false;
        api.NotModified = true;
        error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            device.ExecuteCommandAsync($"{Pump}|heatingOffset", "3", CancellationToken.None));
        StringAssert.Contains(error.Message, "did not change parameter 47011");
    }

    [TestMethod]
    public async Task TheTokenIsFetchedAgainWhenItExpiresOrMyUplinkRejectsIt()
    {
        var api = new FakeMyUplink { RejectToken = "token-1" };
        var time = new ManualTime();
        var device = Device(api, reports: Reports("outdoorTemperature"), time: time);

        await device.StartAsync(CancellationToken.None);

        Assert.AreEqual(DeviceState.Running, device.State);
        Assert.AreEqual(2, api.Tokens, "a 401 fetches a new token once");
        Assert.AreEqual("Bearer token-2", api.Requests.Last().Header("Authorization"));

        var tokens = api.Tokens;
        time.Now += TimeSpan.FromMinutes(30);
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);
        Assert.AreEqual(tokens, api.Tokens, "the token is kept while it lives");
        Assert.IsTrue(api.Requests.Any(r => r.Url.Contains("/points?parameters=", StringComparison.Ordinal)), "a refresh reads only the polled parameters");

        time.Now += TimeSpan.FromHours(1);
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);
        Assert.AreEqual(tokens + 1, api.Tokens, "an expired token is renewed");
        Assert.AreEqual(1, api.Requests.Count(r => r.Url.EndsWith("/subscriptions", StringComparison.Ordinal)), "the subscription is checked daily");
    }

    [TestMethod]
    public async Task AWrongClientSecretPutsTheDeviceIntoTheErrorState()
    {
        var api = new FakeMyUplink { Secret = "other" };
        var device = Device(api);

        var error = await Assert.ThrowsAsync<Exception>(() => device.StartAsync(CancellationToken.None));

        Assert.IsInstanceOfType<MyUplinkAuthenticationException>(error.InnerException);
        Assert.AreEqual(DeviceState.Error, device.State);
        StringAssert.Contains(device.StateMessage, "invalid_client");
        Assert.IsFalse(device.StateMessage.Contains("client_secret", StringComparison.Ordinal), "the request with the secret isn't shown");
    }

    [TestMethod]
    public async Task EnergyTodayCountsFromTheDaysFirstPollAcrossARestart()
    {
        var api = new FakeMyUplink();
        var time = new ManualTime();
        var device = Device(api, reports: Reports(MyUplink.EnergyToday), time: time);
        var reports = Capture(device);
        await device.StartAsync(CancellationToken.None);
        Assert.AreEqual(0d, reports.Last().Value.GetValue<double>());

        api.Values["44300"] += 2.5;
        api.Values["44298"] += 0.5;
        time.Now += TimeSpan.FromHours(1);
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);
        Assert.AreEqual(3d, reports.Last().Value.GetValue<double>());

        // A restart the same day keeps counting from the saved baseline.
        api.Values["44300"] += 1;
        var restarted = Device(api, reports: Reports(MyUplink.EnergyToday), time: time);
        var restartedReports = Capture(restarted);
        await restarted.StartAsync(CancellationToken.None);
        Assert.AreEqual(4d, restartedReports.Last().Value.GetValue<double>());

        // The next day starts from zero.
        api.Values["44300"] += 1;
        time.Now += TimeSpan.FromDays(1);
        await restarted.RefreshReportAsync(restarted.Id, null!, CancellationToken.None);
        Assert.AreEqual(0d, restartedReports.Last().Value.GetValue<double>());
    }

    [TestMethod]
    public async Task OutagesKeepTheLastStateAndAConfiguredDeviceOutlivesOneAtStart()
    {
        var api = new FakeMyUplink { Down = true };
        var time = new ManualTime();
        var device = Device(api, reports: Reports("outdoorTemperature"), commands: Commands("heatingOffset"), time: time);

        await device.StartAsync(CancellationToken.None);
        Assert.AreEqual(DeviceState.Running, device.State, "myUplink being down isn't the configuration's fault");

        api.Down = false;
        await device.ExecuteCommandAsync($"{Pump}|heatingOffset", "1", CancellationToken.None);
        Assert.AreEqual("PATCH", api.Requests.Last().Method, "the first command polls first");

        time.Now += TimeSpan.FromHours(1);
        api.Limited = true;
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);
        Assert.AreEqual(DeviceState.Running, device.State, "a 429 is waited out");
        Assert.AreEqual(Pump, device.GetConfigurationTemplate().ReportTemplates[0].Address.Split('|')[0], "the pumps of the last poll stay");

        var unconfigured = Device(new FakeMyUplink { Down = true });
        await Assert.ThrowsAsync<Exception>(() => unconfigured.StartAsync(CancellationToken.None));
        Assert.AreEqual(DeviceState.Error, unconfigured.State, "a new device shows why it has no heat pumps");
    }

    [TestMethod]
    public void CommandValuesMapToParameterValues()
    {
        var pump = new MyUplinkPump { Id = Pump };
        pump.Points["47011"] = new MyUplinkPoint { Id = "47011", Name = "Offset", Writable = true, Minimum = -10, Maximum = 10, Step = 1 };
        pump.Points["47041"] = new MyUplinkPoint
        {
            Id = "47041", Name = "Comfort mode", Writable = true, Enum = [new("0", "Economy"), new("1", "Normal"), new("2", "Luxury")]
        };
        pump.Known["heatingOffset"] = "47011";
        pump.Known["hotWaterComfortMode"] = "47041";

        Assert.AreEqual(("47011", "2"), MyUplink.ParseCommand(MyUplink.SetTemperature, "21", pump, 20, 0.5));
        Assert.AreEqual(("47011", "-3"), MyUplink.ParseCommand(MyUplink.SetTemperature, "\"17.2\"", pump, 20, 1));
        Assert.AreEqual(("47041", "0"), MyUplink.ParseCommand("hotWaterComfortMode", "\"ECONOMY\"", pump, 20, 1));
        Assert.AreEqual(("47041", "2"), MyUplink.ParseCommand("hotWaterComfortMode", "2", pump, 20, 1));
        Assert.IsNull(MyUplink.ParseCommand("hotWaterBoost", "1", pump, 20, 1), "a curated setting the pump doesn't have");
        Assert.IsNull(MyUplink.ParseCommand("p47011", "1", pump, 20, 1), "not configured as an extra parameter");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MyUplink.ParseCommand(MyUplink.SetTemperature, "26", pump, 20, 0.5));
        Assert.ThrowsExactly<ArgumentException>(() => MyUplink.ParseCommand("heatingOffset", "\"warm\"", pump, 20, 1));
        Assert.ThrowsExactly<ArgumentException>(() => MyUplink.ParseCommand(MyUplink.SetParameter, "1", pump, 20, 1));
        Assert.AreEqual(25d, MyUplinkSetpoint.ToTemperature(10, 20, 0.5));

        var point = MyUplinkClient.ReadPoint(JsonNode.Parse("""
            {"parameterId":"40004","parameterName":"Outdoor temp. (BT1)","parameterUnit":"°C","writable":false,"value":-3.2,
             "minValue":-400,"maxValue":800,"stepValue":1,"scaleValue":"0.1","enumValues":[]}
            """)!.AsObject());
        Assert.AreEqual(("40004", "°C", -3.2, -40d, 80d, false), (point.Id, point.Unit, point.Value!.Value, point.Minimum!.Value, point.Maximum!.Value, point.Writable));
    }

    [TestMethod]
    public void TheTemplateNeedsNoMyUplinkWhenTheDeviceIsNotRunning()
    {
        var device = new MyUplink(NullLogger.Instance);
        var template = device.GetConfigurationTemplate();

        Assert.AreEqual(typeof(MyUplink).FullName, template.ClassFullName);
        CollectionAssert.AreEquivalent(
            new[] { "clientId", "clientSecret", "language", "extraParameters", "offsetParameter", "baseTemperature", "degreesPerStep" },
            template.DeviceParameters.Keys.ToArray());
        Assert.AreEqual(0, device.GetMatterEndpoints(template).Count());
    }

    private TestMyUplink Device(FakeMyUplink api, List<ReportTemplate>? reports = null, List<CommandTemplate>? commands = null, ManualTime? time = null)
    {
        var device = new TestMyUplink(api) { DataDirectory = _dataDirectory };
        if (time != null)
            device.Time = time;
        device.Initialize(new DeviceConfiguration
        {
            Id = "device",
            Name = "myUplink",
            DeviceParameters = new()
            {
                ["clientId"] = "client",
                ["clientSecret"] = "secret",
                ["extraParameters"] = "50000, 40033,99999"
            },
            ReportTemplates = reports ?? [],
            CommandTemplates = commands ?? []
        });
        return device;
    }

    // The template id is the address, so the tests can find reports by address.
    private static List<ReportTemplate> Reports(params string[] keys) =>
        keys.Select(k => new ReportTemplate { Id = $"{Pump}|{k}", Address = $"{Pump}|{k}" }).ToList();

    private static List<CommandTemplate> Commands(params string[] keys) =>
        keys.Select(k => new CommandTemplate { Id = $"{Pump}|{k}", Address = $"{Pump}|{k}" }).ToList();

    private static ConcurrentQueue<Report> Capture(Core.Interfaces.IDevice device)
    {
        var reports = new ConcurrentQueue<Report>();
        device.ReportUpdated += (_, report) => reports.Enqueue((Report)report);
        return reports;
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Dictionary<string, string> Form(string body) =>
        body.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));

    private sealed record Sent(string Method, string Url, string? Body, Dictionary<string, string> Headers)
    {
        public string? Header(string name) => Headers.GetValueOrDefault(name);
    }

    /// <summary>
    /// One system with one F750: no supply temperature (40008), degree minutes as 43005, a writable extra parameter
    /// 50000 with a scaled range, a read-only one 40033, and no 99999.
    /// </summary>
    private sealed class FakeMyUplink
    {
        public Dictionary<string, double> Values { get; } = new()
        {
            ["40004"] = 5.3, ["40025"] = 21.4, ["43005"] = -120, ["47011"] = 0, ["47041"] = 1, ["48132"] = 0,
            ["44300"] = 1000.5, ["44298"] = 500.25, ["50000"] = 10, ["40033"] = 19.5
        };

        public List<Sent> Requests { get; } = [];
        public string Secret { get; set; } = "secret";
        public string? RejectToken { get; set; }
        public bool Down { get; set; }
        public bool Limited { get; set; }
        public bool Forbidden { get; set; }
        public bool NotModified { get; set; }
        public int Tokens { get; private set; }

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
            Requests.Add(new Sent(request.Method.Method, request.RequestUri!.ToString(), body, headers));
            return Respond(request, body, headers);
        }

        private HttpResponseMessage Respond(HttpRequestMessage request, string? body, Dictionary<string, string> headers)
        {
            if (Down)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (Limited)
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            var url = request.RequestUri!.ToString();
            if (url == MyUplinkClient.TokenUrl)
            {
                if (Form(body!)["client_secret"] != Secret)
                    return Json("""{"error":"invalid_client"}""", HttpStatusCode.BadRequest);
                Tokens++;
                return Json($$"""{"access_token":"token-{{Tokens}}","expires_in":3600,"token_type":"Bearer"}""");
            }
            if (RejectToken != null && headers.GetValueOrDefault("Authorization") == $"Bearer {RejectToken}")
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);

            var path = url[MyUplinkClient.ApiUrl.Length..];
            if (path.StartsWith("/v2/systems/me", StringComparison.Ordinal))
                return Json($$$"""
                    {"page":1,"itemsPerPage":100,"numItems":1,"systems":[{"systemId":"sys-1","name":"Home","hasAlarm":true,"country":"Finland",
                      "devices":[{"id":"{{{Pump}}}","connectionState":"Connected","currentFwVersion":"9682R7","product":{"serialNumber":"0123","name":"F750"}}]}]}
                    """);
            if (path == "/v2/systems/sys-1/smart-home-mode")
                return request.Method == HttpMethod.Put ? new HttpResponseMessage(HttpStatusCode.NoContent) : Json("""{"smartHomeMode":"Default"}""");
            if (path.StartsWith("/v2/systems/sys-1/notifications/active", StringComparison.Ordinal))
                return Json("""
                    {"page":1,"itemsPerPage":10,"numItems":1,"notifications":[
                      {"id":"a2","alarmNumber":163,"header":"High condenser out","createdDatetime":"2026-11-04T10:00:00Z"},
                      {"id":"a1","alarmNumber":162,"header":"Older alarm","createdDatetime":"2026-11-01T10:00:00Z"}]}
                    """);
            if (path == "/v2/systems/sys-1/subscriptions")
                return Json("""[{"subscriptions":[{"type":"history","validUntil":"2030-01-01T00:00:00Z"}]}]""");
            if (path.StartsWith($"/v2/devices/{Pump}/points", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Patch)
                {
                    if (Forbidden)
                        return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("") };
                    var changes = JsonNode.Parse(body!)!.AsObject();
                    foreach (var change in changes)
                        Values[change.Key] = double.Parse((string)change.Value!, System.Globalization.CultureInfo.InvariantCulture);
                    var result = new JsonObject();
                    foreach (var change in changes)
                        result[change.Key] = NotModified ? "error" : "modified";
                    return Json(result.ToJsonString());
                }
                var query = request.RequestUri.Query;
                var wanted = query.StartsWith("?parameters=", StringComparison.Ordinal)
                    ? Uri.UnescapeDataString(query["?parameters=".Length..]).Split(',').ToHashSet()
                    : null;
                var points = new JsonArray(Points().Where(p => wanted == null || wanted.Contains((string)p["parameterId"]!)).Select(p => (JsonNode)p).ToArray());
                return Json(points.ToJsonString());
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private IEnumerable<JsonObject> Points()
        {
            yield return Point("40004", "Outdoor temp. (BT1)", "°C", scale: "0.1", min: -400, max: 800);
            yield return Point("40025", "Exhaust air (BT20)", "°C", scale: "0.1");
            yield return Point("40033", "Room temperature (BT50)", "°C", scale: "0.1");
            yield return Point("43005", "Degree minutes", "DM");
            yield return Point("47011", "Heating offset climate system 1", "", writable: true, min: -10, max: 10, step: 1);
            yield return Point("47041", "Hot water comfort mode", "", writable: true, enums: [("0", "Economy"), ("1", "Normal"), ("2", "Luxury")]);
            yield return Point("48132", "Temporary lux", "", writable: true, enums: [("0", "Off"), ("1", "3 hours"), ("2", "6 hours")]);
            yield return Point("44300", "Heat meter heating", "kWh");
            yield return Point("44298", "Heat meter hot water", "kWh");
            yield return Point("50000", "Some setting", "%", writable: true, min: 0, max: 1000, step: 1, scale: "0.1");
        }

        private JsonObject Point(string id, string name, string unit, bool writable = false, double? min = null, double? max = null, double? step = null,
            string scale = "1", (string Value, string Text)[]? enums = null) => new()
        {
            ["category"] = "F750",
            ["parameterId"] = id,
            ["parameterName"] = name,
            ["parameterUnit"] = unit,
            ["writable"] = writable,
            ["timestamp"] = "2026-11-04T11:55:00+00:00",
            ["value"] = Values[id],
            ["strVal"] = $"{Values[id]}{unit}",
            ["smartHomeCategories"] = new JsonArray(),
            ["minValue"] = min,
            ["maxValue"] = max,
            ["stepValue"] = step,
            ["enumValues"] = new JsonArray((enums ?? []).Select(e => (JsonNode)new JsonObject { ["value"] = e.Value, ["text"] = e.Text, ["icon"] = "" }).ToArray()),
            ["scaleValue"] = scale,
            ["zoneId"] = null
        };
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class TestMyUplink(FakeMyUplink api) : MyUplink(NullLogger.Instance)
    {
        internal override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            api.SendAsync(request, cancellationToken);
    }
}
