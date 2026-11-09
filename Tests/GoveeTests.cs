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
using RIoT2.Net.Devices.Services.Govee;

namespace RIoT2.Net.Devices.Tests;

/// <summary>The Govee device against a scripted Govee Open API; nothing reaches Govee.</summary>
[TestClass]
public class GoveeTests
{
    private const string Pool = "AA:BB:CC:DD:EE:FF:00:11";
    private const string Hygrometer = "11:22:33:44:55:66:77:88";
    private const string Light = "9D:FA:85:EB:D3:00:8B:FF";
    private const string Base = GoveeClient.ApiUrl;

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
    public async Task StartReadsTheSensorsAndBuildsTheTemplate()
    {
        var time = new ManualTime();
        var api = new FakeGovee();
        var device = Device(api, time, reports: Reports(Pool, Govee.Temperature, Govee.Battery, Govee.Online)
            .Concat(Reports(Hygrometer, Govee.Temperature, Govee.Humidity)).ToList());
        var reports = Capture(device);

        await device.StartAsync(CancellationToken.None);

        Assert.AreEqual(DeviceState.Running, device.State, device.StateMessage);
        Assert.AreEqual($"GET {Base}/{GoveeClient.DevicesPath}", $"{api.Requests[0].Method} {api.Requests[0].Url}");
        Assert.IsTrue(api.Requests.All(r => r.Header("Govee-API-Key") == "key-1"), "every call has the API key");
        var states = api.Requests.Where(r => r.Url.EndsWith(GoveeClient.StatePath, StringComparison.Ordinal)).ToList();
        Assert.HasCount(2, states, "the light isn't a sensor");
        var payload = JsonNode.Parse(states[0].Body!)!["payload"]!;
        Assert.AreEqual(("H5109", Pool), ((string)payload["sku"]!, (string)payload["device"]!));
        Assert.IsFalse(string.IsNullOrEmpty((string?)JsonNode.Parse(states[0].Body!)!["requestId"]));

        var values = reports.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.Last().Value);
        Assert.AreEqual(26.4, values[$"{Pool}|temperature"].GetValue<double>(), "79.52 °F");
        Assert.AreEqual(80d, values[$"{Pool}|battery"].GetValue<double>());
        Assert.IsTrue(values[$"{Pool}|online"].GetValue<bool>());
        Assert.AreEqual(21.5, values[$"{Hygrometer}|temperature"].GetValue<double>(), "70.7 °F");
        Assert.AreEqual(45.2, values[$"{Hygrometer}|humidity"].GetValue<double>());

        var calls = api.Requests.Count;
        var template = device.GetConfigurationTemplate();
        Assert.HasCount(calls, api.Requests, "the template comes from the last poll");
        Assert.AreEqual("0 0/15 * ? * *", template.RefreshSchedule);
        CollectionAssert.AreEquivalent(new[]
        {
            $"{Pool}|temperature", $"{Pool}|temperatureHourly", $"{Pool}|battery", $"{Pool}|online",
            $"{Hygrometer}|temperature", $"{Hygrometer}|temperatureHourly", $"{Hygrometer}|humidity", $"{Hygrometer}|online"
        }, template.ReportTemplates.Select(r => r.Address).ToArray());
        Assert.AreEqual("Pool temperature", template.ReportTemplates.Single(r => r.Address == $"{Pool}|temperature").Name);
        Assert.AreEqual(ValueSemantics.Temperature, template.ReportTemplates.Single(r => r.Address == $"{Pool}|temperature").Schema!.Semantic);
        Assert.AreEqual("refresh", template.CommandTemplates!.Single().Address);

        var endpoints = device.GetMatterEndpoints(template).ToList();
        var pool = endpoints.Single(e => e.Id == "govee:aabbccddeeff0011");
        Assert.AreEqual(("Pool", MatterDeviceType.TemperatureSensor, "Govee", "H5109"), (pool.Name, pool.DeviceType, pool.VendorName, pool.ProductName));
        var temperature = template.ReportTemplates.Single(r => r.Address == $"{Pool}|temperature").Id;
        Assert.AreEqual((MatterAttribute.TemperatureMeasuredValue, temperature, MatterValueScale.CelsiusToHundredths),
            pool.Attributes.Select(a => (a.Attribute, a.ReportTemplateId, a.Scale)).Single());
        var humidity = endpoints.Single(e => e.Id == "govee:1122334455667788:humidity");
        Assert.AreEqual((MatterDeviceType.HumiditySensor, "Hall humidity"), (humidity.DeviceType, humidity.Name));
        Assert.HasCount(3, endpoints);
    }

    [TestMethod]
    public async Task DevicesSelectsByIdOrModelAndCelsiusIsKept()
    {
        var time = new ManualTime();
        var api = new FakeGovee();
        var device = Device(api, time, reports: Reports(Hygrometer, Govee.Temperature), devices: " h5179 ", unit: "celsius");
        var reports = Capture(device);

        await device.StartAsync(CancellationToken.None);

        Assert.AreEqual(Hygrometer, JsonNode.Parse(api.Requests.Single(r => r.Url.EndsWith(GoveeClient.StatePath, StringComparison.Ordinal)).Body!)!["payload"]!["device"]!.GetValue<string>());
        Assert.AreEqual(70.7, reports.Single().Value.GetValue<double>(), "celsius values are kept");

        // A device the list doesn't call a sensor is read when named; its state shows what it measures.
        api.Reset();
        var named = Device(api, time, reports: Reports(Light, Govee.Temperature), devices: Light);
        await named.StartAsync(CancellationToken.None);
        Assert.IsTrue(named.GetConfigurationTemplate().ReportTemplates.Any(r => r.Address == $"{Light}|temperature"));
    }

    [TestMethod]
    public async Task AnOfflineSensorReportsOnlyThatItIsOffline()
    {
        var time = new ManualTime();
        var api = new FakeGovee();
        api.States[Pool] = State(online: false, temperature: "50.0", battery: "70");
        var device = Device(api, time, reports: Reports(Pool, Govee.Temperature, Govee.Battery, Govee.Online, Govee.TemperatureHourly));
        var reports = Capture(device);

        await device.StartAsync(CancellationToken.None);

        Assert.AreEqual($"{Pool}|online", reports.Single().Id);
        Assert.IsFalse(reports.Single().Value.GetValue<bool>());
        Assert.AreEqual(0, ReadFile(device, Pool).Samples.Count, "a history value isn't a sample");
    }

    [TestMethod]
    public async Task EveryFinishedHourIsReportedOnceAcrossRestarts()
    {
        var time = new ManualTime();
        var api = new FakeGovee();
        var device = Device(api, time, reports: Reports(Pool, Govee.TemperatureHourly));
        var reports = Capture(device);

        // 12:20 26.4 °C, 12:40 27.0 °C, 12:55 25.8 °C.
        await device.StartAsync(CancellationToken.None);
        await Poll(device, api, time, 12, 40, "80.6");
        await Poll(device, api, time, 12, 55, "78.44");
        Assert.IsEmpty(reports, "the hour isn't finished");

        await Poll(device, api, time, 13, 5, "77");
        var hour = reports.Single();
        Assert.AreEqual(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), hour.TimeStamp, "time-stamped to the hour's start");
        Assert.AreEqual(26.4, hour.Value.GetValue<double>("average"));
        Assert.AreEqual(25.8, hour.Value.GetValue<double>("minimum"));
        Assert.AreEqual(27d, hour.Value.GetValue<double>("maximum"));
        Assert.AreEqual(3, hour.Value.GetValue<int>("samples"));

        // A restart keeps the open hour's samples and doesn't repeat the reported hour.
        var restarted = Device(api, time, reports: Reports(Pool, Govee.TemperatureHourly));
        var after = Capture(restarted);
        time.Now = new DateTimeOffset(2026, 11, 9, 13, 30, 0, TimeSpan.Zero);
        api.States[Pool] = State(temperature: "79");
        await restarted.StartAsync(CancellationToken.None);
        Assert.IsEmpty(after);

        await Poll(restarted, api, time, 14, 2, "79");
        var next = after.Single();
        Assert.AreEqual(new DateTimeOffset(2026, 11, 9, 13, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), next.TimeStamp);
        Assert.AreEqual(2, next.Value.GetValue<int>("samples"), "13:05 before the restart and 13:30 after it");
        Assert.AreEqual(25.55, next.Value.GetValue<double>("average"));
    }

    [TestMethod]
    public async Task AWrongKeyIsAnErrorAndOutagesKeepTheDeviceRunning()
    {
        var time = new ManualTime();
        var wrong = Device(new FakeGovee { Key = "other" }, time, reports: Reports(Pool, Govee.Temperature));
        await Assert.ThrowsAsync<Exception>(() => wrong.StartAsync(CancellationToken.None));
        Assert.AreEqual(DeviceState.Error, wrong.State);
        StringAssert.Contains(wrong.StateMessage, "API key");
        Assert.IsFalse(wrong.StateMessage!.Contains("key-1", StringComparison.Ordinal), "the key isn't in the message");

        var api = new FakeGovee { Down = true };
        var device = Device(api, time, reports: Reports(Pool, Govee.Temperature));
        await device.StartAsync(CancellationToken.None);
        Assert.AreEqual(DeviceState.Running, device.State, "Govee being down isn't the configuration's fault");

        api.Down = false;
        api.Limited = true;
        time.Now += TimeSpan.FromMinutes(15);
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);
        Assert.AreEqual(DeviceState.Running, device.State, "a 429 is waited out");

        api.Limited = false;
        var reports = Capture(device);
        await device.ExecuteCommandAsync("refresh", "true", CancellationToken.None);
        Assert.AreEqual(26.4, reports.Single().Value.GetValue<double>(), "the refresh command polls");

        var calls = api.Requests.Count;
        await device.ExecuteCommandAsync("refresh", "true", CancellationToken.None);
        Assert.HasCount(calls, api.Requests, "at most one poll a minute");

        var unconfigured = Device(new FakeGovee { Down = true }, time);
        await Assert.ThrowsAsync<Exception>(() => unconfigured.StartAsync(CancellationToken.None));
        Assert.AreEqual(DeviceState.Error, unconfigured.State, "a new device shows why it has no sensors");
    }

    [TestMethod]
    public async Task ABadTemperatureUnitOrEndpointIsAConfigurationError()
    {
        var time = new ManualTime();
        var unit = Device(new FakeGovee(), time, unit: "kelvin");
        await Assert.ThrowsAsync<Exception>(() => unit.StartAsync(CancellationToken.None));
        StringAssert.Contains(unit.StateMessage, "temperatureUnit");

        var endpoint = Device(new FakeGovee(), time, endpoint: "ftp://example.com");
        await Assert.ThrowsAsync<Exception>(() => endpoint.StartAsync(CancellationToken.None));
        StringAssert.Contains(endpoint.StateMessage, "endpoint");
    }

    [TestMethod]
    public void StatesAreReadDefensively()
    {
        var state = GoveeClient.ReadState(JsonNode.Parse("""
            {"capabilities":[
              {"type":"devices.capabilities.online","instance":"online","state":{"value":"true"}},
              {"type":"devices.capabilities.property","instance":"sensorTemperature","state":{"value":{"currentTemperature":"32"}}},
              {"type":"devices.capabilities.property","instance":"sensorHumidity","state":{"value":""}},
              {"type":"devices.capabilities.property","instance":"batteryLevel","state":{"value":55}}]}
            """)!.AsObject(), null, fahrenheit: true);
        Assert.IsTrue(state.Online);
        Assert.AreEqual(0d, state.Temperature, "an object's number, in text");
        Assert.IsNull(state.Humidity, "\"\" is Govee's 'can't be read'");
        Assert.AreEqual(55d, state.Battery);

        var sensor = GoveeClient.ReadDevice(JsonNode.Parse("""{"sku":"H5109","device":"X","type":"devices.types.thermometer","capabilities":[]}""")!.AsObject());
        Assert.IsTrue(sensor.HasTemperature, "a thermometer measures temperature");
        Assert.AreEqual("Govee H5109", sensor.Name);

        Assert.AreEqual(-17.8, GoveeClient.Celsius(0, true));
        Assert.AreEqual(21.12, GoveeClient.Celsius(21.123, false));

        var hours = Govee.Hours(
        [
            new GoveeSample { Time = new DateTimeOffset(2026, 11, 9, 10, 10, 0, TimeSpan.Zero), Value = 20 },
            new GoveeSample { Time = new DateTimeOffset(2026, 11, 9, 11, 10, 0, TimeSpan.Zero), Value = 21 },
            new GoveeSample { Time = new DateTimeOffset(2026, 11, 9, 10, 50, 0, TimeSpan.Zero), Value = 22 },
            new GoveeSample { Time = new DateTimeOffset(2026, 11, 9, 12, 5, 0, TimeSpan.Zero), Value = 30 }
        ], new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));
        Assert.HasCount(2, hours, "the open hour waits");
        Assert.AreEqual(new GoveeHour(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero), 21, 20, 22, 2), hours[0]);
    }

    [TestMethod]
    public async Task AStatusInTheBodyCounts()
    {
        var api = new FakeGovee { BodyCode = 401 };
        var client = new GoveeClient("key-1", null, api.SendAsync);
        await Assert.ThrowsExactlyAsync<GoveeAuthenticationException>(() => client.GetDevicesAsync(CancellationToken.None));

        api.BodyCode = 429;
        await Assert.ThrowsExactlyAsync<GoveeUnavailableException>(() => client.GetDevicesAsync(CancellationToken.None));

        api.BodyCode = 400;
        var refused = await Assert.ThrowsExactlyAsync<GoveeRefusedException>(() => client.GetDevicesAsync(CancellationToken.None));
        Assert.AreEqual(400, refused.Status);

        var other = new GoveeClient("key-1", "https://govee.example/", api.SendAsync);
        api.BodyCode = null;
        await other.GetDevicesAsync(CancellationToken.None);
        Assert.AreEqual($"https://govee.example/{GoveeClient.DevicesPath}", api.Requests[^1].Url);
    }

    [TestMethod]
    public void TheTemplateNeedsNoGoveeWhenTheDeviceIsNotRunning()
    {
        var device = new Govee(NullLogger.Instance);
        var template = device.GetConfigurationTemplate();

        Assert.AreEqual(typeof(Govee).FullName, template.ClassFullName);
        CollectionAssert.AreEquivalent(new[] { "apiKey", "devices", "temperatureUnit", "endpoint" }, template.DeviceParameters.Keys.ToArray());
        Assert.IsEmpty(template.ReportTemplates);
        Assert.AreEqual(0, device.GetMatterEndpoints(template).Count());
    }

    private static async Task Poll(TestGovee device, FakeGovee api, ManualTime time, int hour, int minute, string fahrenheit)
    {
        time.Now = new DateTimeOffset(2026, 11, 9, hour, minute, 0, TimeSpan.Zero);
        api.States[Pool] = State(temperature: fahrenheit);
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);
    }

    private GoveeSensorFile ReadFile(TestGovee device, string sensor) =>
        System.Text.Json.JsonSerializer.Deserialize<GoveeSensorFile>(File.ReadAllText(device.SensorFile(sensor)))!;

    private TestGovee Device(FakeGovee api, ManualTime time, List<ReportTemplate>? reports = null, string devices = "", string unit = "fahrenheit",
        string endpoint = "")
    {
        var device = new TestGovee(api) { DataDirectory = _dataDirectory, Time = time };
        device.Initialize(new DeviceConfiguration
        {
            Id = "device",
            Name = "Govee",
            DeviceParameters = new() { ["apiKey"] = "key-1", ["devices"] = devices, ["temperatureUnit"] = unit, ["endpoint"] = endpoint },
            ReportTemplates = reports ?? [],
            CommandTemplates = [new CommandTemplate { Id = "refresh", Address = Govee.RefreshAddress }]
        });
        return device;
    }

    // The template id is the address, so the tests can find reports by address.
    private static List<ReportTemplate> Reports(string sensor, params string[] keys) =>
        keys.Select(k => new ReportTemplate { Id = $"{sensor}|{k}", Address = $"{sensor}|{k}" }).ToList();

    private static ConcurrentQueue<Report> Capture(Core.Interfaces.IDevice device)
    {
        var reports = new ConcurrentQueue<Report>();
        device.ReportUpdated += (_, report) => reports.Enqueue((Report)report);
        return reports;
    }

    private static string State(bool online = true, string? temperature = "79.52", string? humidity = null, string? battery = null)
    {
        var capabilities = new JsonArray(Capability("devices.capabilities.online", "online", online));
        if (temperature != null)
            capabilities.Add(Capability("devices.capabilities.property", "sensorTemperature", double.Parse(temperature, System.Globalization.CultureInfo.InvariantCulture)));
        if (humidity != null)
            capabilities.Add(Capability("devices.capabilities.property", "sensorHumidity", double.Parse(humidity, System.Globalization.CultureInfo.InvariantCulture)));
        if (battery != null)
            capabilities.Add(Capability("devices.capabilities.property", "battery", double.Parse(battery, System.Globalization.CultureInfo.InvariantCulture)));
        return capabilities.ToJsonString();
    }

    private static JsonObject Capability(string type, string instance, JsonNode value) =>
        new() { ["type"] = type, ["instance"] = instance, ["state"] = new JsonObject { ["value"] = value } };

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record Sent(string Method, string Url, string? Body, Dictionary<string, string> Headers)
    {
        public string? Header(string name) => Headers.GetValueOrDefault(name);
    }

    /// <summary>
    /// A pool thermometer (a thermometer with temperature and battery), a hygrometer (temperature and humidity) and a
    /// light; temperatures in °F, as the Open API sends them.
    /// </summary>
    private sealed class FakeGovee
    {
        public List<Sent> Requests { get; } = [];
        public Dictionary<string, string> States { get; } = [];
        public string Key { get; set; } = "key-1";
        public bool Down { get; set; }
        public bool Limited { get; set; }
        public int? BodyCode { get; set; }

        public FakeGovee() => Reset();

        public void Reset()
        {
            Requests.Clear();
            States[Pool] = State(battery: "80");
            States[Hygrometer] = State(temperature: "70.7", humidity: "45.2");
            States[Light] = State(temperature: "70.7");
        }

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
                return Json("""{"code":429,"message":"Too Many Requests"}""", HttpStatusCode.TooManyRequests);
            if (BodyCode is int code)
                return Json($$"""{"code":{{code}},"message":"refused"}""");
            if (headers.GetValueOrDefault("Govee-API-Key") != Key)
                return Json("""{"code":401,"message":"Invalid API Key"}""", HttpStatusCode.Unauthorized);

            switch (request.RequestUri!.AbsolutePath)
            {
                case "/" + GoveeClient.DevicesPath when request.Method == HttpMethod.Get:
                    return Json($$"""
                        {"code":200,"message":"success","data":[
                          {"sku":"H5109","device":"{{Pool}}","deviceName":"Pool","type":"devices.types.thermometer",
                           "capabilities":[{"type":"devices.capabilities.property","instance":"sensorTemperature"},
                                           {"type":"devices.capabilities.property","instance":"battery"}]},
                          {"sku":"H5179","device":"{{Hygrometer}}","deviceName":"Hall","type":"devices.types.sensor",
                           "capabilities":[{"type":"devices.capabilities.property","instance":"sensorTemperature"},
                                           {"type":"devices.capabilities.property","instance":"sensorHumidity"}]},
                          {"sku":"H6601","device":"{{Light}}","deviceName":"Desk","type":"devices.types.light",
                           "capabilities":[{"type":"devices.capabilities.on_off","instance":"powerSwitch"}]}]}
                        """);
                case "/" + GoveeClient.StatePath when request.Method == HttpMethod.Post:
                    var payload = JsonNode.Parse(body!)!["payload"]!;
                    var device = (string)payload["device"]!;
                    if (!States.TryGetValue(device, out var capabilities))
                        return Json("""{"code":400,"msg":"devices not exist"}""", HttpStatusCode.BadRequest);
                    return Json(new JsonObject
                    {
                        ["requestId"] = "r",
                        ["msg"] = "success",
                        ["code"] = 200,
                        ["payload"] = new JsonObject
                        {
                            ["sku"] = (string)payload["sku"]!,
                            ["device"] = device,
                            ["capabilities"] = JsonNode.Parse(capabilities)
                        }
                    }.ToJsonString());
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 9, 12, 20, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class TestGovee(FakeGovee api) : Govee(NullLogger.Instance)
    {
        internal override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            api.SendAsync(request, cancellationToken);
    }
}
