using System.Collections.Concurrent;
using System.Globalization;
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
using RIoT2.Net.Devices.Services.Themo;
using ValueType = RIoT2.Core.ValueType;

namespace RIoT2.Net.Devices.Tests;

/// <summary>The Themo device against a scripted Themo Public API; nothing reaches Themo.</summary>
[TestClass]
public class ThemoTests
{
    private const int Id = 28920;
    private const string IdText = "28920";
    private const string Base = ThemoClient.ApiUrl;

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
    public async Task StartReportsTheStateAndEnergyAndBuildsTheTemplate()
    {
        var time = new ManualTime();
        var api = new FakeThemo(time);
        var device = Device(api, time, reports: Reports(Themo.Temperature, Themo.RoomTemperature, Themo.FloorTemperature, Themo.OutsideTemperature,
            Themo.SetTemperature, Themo.Mode, Themo.ThermostatMode, Themo.Heating, Themo.Connected, Themo.Lights, Themo.Lock, Themo.FrostProtection,
            Themo.FrostTemperature, Themo.Schedule, Themo.EnergyToday, Themo.HeatingShareToday, Themo.EnergyHour, Themo.EnergyHourly));
        var reports = Capture(device);

        await device.StartAsync(CancellationToken.None);

        Assert.AreEqual(DeviceState.Running, device.State);
        var login = api.Requests[0];
        Assert.AreEqual($"POST {Base}/api/auth/login?api-version=2.1", $"{login.Method} {login.Url}");
        var credentials = JsonNode.Parse(login.Body!)!;
        Assert.AreEqual(("user@example.com", "secret"), ((string)credentials["Username"]!, (string)credentials["Password"]!));
        Assert.AreEqual("Bearer token-1", api.Requests[1].Header("Authorization"));
        Assert.IsTrue(api.Requests.Skip(1).All(r => r.Url.Contains("api-version=2.1", StringComparison.Ordinal)), "every call asks for v2.1");
        var data = api.Requests.Single(r => r.Url.Contains("/data?", StringComparison.Ordinal));
        StringAssert.Contains(Uri.UnescapeDataString(data.Url), "from=2026-11-02T12:00:00Z", "two days of finished hours");

        var values = reports.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.Last().Value);
        Assert.AreEqual(22.5, values[$"{Id}|temperature"].GetValue<double>());
        Assert.AreEqual(21d, values[$"{Id}|roomTemperature"].GetValue<double>());
        Assert.AreEqual(24d, values[$"{Id}|floorTemperature"].GetValue<double>(), "a combi thermostat has a floor sensor");
        Assert.AreEqual(-3.5, values[$"{Id}|outsideTemperature"].GetValue<double>());
        Assert.AreEqual(22d, values[$"{Id}|setTemperature"].GetValue<double>());
        Assert.AreEqual("sls", values[$"{Id}|mode"].GetValue<string>());
        Assert.AreEqual(ThemoModes.MatterAuto, values[$"{Id}|thermostatMode"].GetValue<int>());
        Assert.IsTrue(values[$"{Id}|heating"].GetValue<bool>());
        Assert.IsTrue(values[$"{Id}|connected"].GetValue<bool>());
        Assert.IsTrue(values[$"{Id}|lights"].GetValue<bool>());
        Assert.AreEqual("off", values[$"{Id}|lock"].GetValue<string>());
        Assert.IsTrue(values[$"{Id}|frostProtection"].GetValue<bool>());
        Assert.AreEqual(7d, values[$"{Id}|frostTemperature"].GetValue<double>());
        Assert.AreEqual("Home", values[$"{Id}|schedule"].GetValue<string>());
        Assert.AreEqual(6d, values[$"{Id}|energyToday"].GetValue<double>(), "twelve finished hours of 0.5 kWh");
        Assert.AreEqual(41.7, values[$"{Id}|heatingShareToday"].GetValue<double>(), "6 kWh of 1.2 kW x 12 h");

        var hour = reports.Single(r => r.Id == $"{Id}|energyHour");
        Assert.AreEqual((0.5, new DateTimeOffset(2026, 11, 4, 11, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()), (hour.Value.GetValue<double>(), hour.TimeStamp),
            "the first start reports only the latest finished hour, at its own time");
        var series = ForecastSeries.From(reports.Single(r => r.Id == $"{Id}|energyHourly").Value)!;
        Assert.IsNull(series.Validate());
        Assert.AreEqual((ForecastSeries.ConfirmedKind, "kWh", "PT1H", 48), (series.Kind, series.Unit, series.Resolution, series.Points.Count));
        Assert.AreEqual(new DateTimeOffset(2026, 11, 4, 11, 0, 0, TimeSpan.Zero), series.Points[^1].Start, "the current hour isn't finished");

        var calls = api.Requests.Count;
        var template = device.GetConfigurationTemplate();
        Assert.HasCount(calls, api.Requests, "the template comes from the last poll");
        Assert.AreEqual("Bathroom temperature", template.ReportTemplates.Single(r => r.Address == $"{Id}|temperature").Name);
        Assert.AreEqual(ValueSemantics.Forecast, template.ReportTemplates.Single(r => r.Address == $"{Id}|energyHourly").Schema!.Semantic);
        var schedule = template.CommandTemplates!.Single(c => c.Address == $"{Id}|schedule");
        CollectionAssert.AreEqual(new object[] { "Home", "Away" }, schedule.Schema!.Enum, "only the minimum temperature schedules");
        CollectionAssert.IsSubsetOf(new[] { $"{Id}|setTemperature", $"{Id}|mode", $"{Id}|thermostatMode", $"{Id}|lights", $"{Id}|lock",
            $"{Id}|frostProtection", $"{Id}|frostTemperature", $"{Id}|boost", "refresh" }, template.CommandTemplates!.Select(c => c.Address).ToList());

        var endpoint = device.GetMatterEndpoints(template).Single();
        Assert.AreEqual(($"themo:{Id}", "Bathroom", MatterDeviceType.Thermostat, "T700"), (endpoint.Id, endpoint.Name, endpoint.DeviceType, endpoint.ProductName));
        var current = template.ReportTemplates.Single(r => r.Address == $"{Id}|temperature").Id;
        var target = template.ReportTemplates.Single(r => r.Address == $"{Id}|setTemperature").Id;
        var setpoint = template.CommandTemplates!.Single(c => c.Address == $"{Id}|setTemperature").Id;
        var modeCommand = template.CommandTemplates!.Single(c => c.Address == $"{Id}|thermostatMode").Id;
        Assert.AreEqual((current, MatterValueScale.CelsiusToHundredths),
            endpoint.Attributes.Where(a => a.Attribute == MatterAttribute.LocalTemperature).Select(a => (a.ReportTemplateId, a.Scale)).Single());
        Assert.AreEqual(target, endpoint.Attributes.Single(a => a.Attribute == MatterAttribute.OccupiedHeatingSetpoint).ReportTemplateId);
        Assert.AreEqual((setpoint, MatterValueScale.CelsiusToHundredths),
            endpoint.Commands.Where(c => c.Attribute == MatterAttribute.OccupiedHeatingSetpoint).Select(c => (c.CommandTemplateId, c.Scale)).Single());
        Assert.AreEqual(modeCommand, endpoint.Commands.Single(c => c.Attribute == MatterAttribute.ThermostatSystemMode).CommandTemplateId);
    }

    [TestMethod]
    public async Task CommandsSendOneFieldPerMessageAndReportTheChange()
    {
        var time = new ManualTime();
        var api = new FakeThemo(time);
        var device = Device(api, time,
            reports: Reports(Themo.SetTemperature, Themo.Mode, Themo.ThermostatMode, Themo.Lights, Themo.Lock, Themo.Schedule),
            commands: Commands(Themo.SetTemperature, Themo.Mode, Themo.ThermostatMode, Themo.Lights, Themo.Lock, Themo.FrostProtection,
                Themo.FrostTemperature, Themo.Boost, Themo.Schedule));
        var reports = Capture(device);
        await device.StartAsync(CancellationToken.None);
        reports.Clear();

        // In SLS, a target temperature switches the thermostat to manual first; 21.3 rounds to the half degree.
        await device.ExecuteCommandAsync($"{Id}|setTemperature", "21.3", CancellationToken.None);
        CollectionAssert.AreEqual(new[] { """{"CMode":"Manual"}""", """{"CMT":21.5}""" }, api.Messages().TakeLast(2).ToArray());
        var changed = reports.ToDictionary(r => r.Id, r => r.Value.ToJson());
        Assert.AreEqual(("21.5", "\"manual\"", "4"), (changed[$"{Id}|setTemperature"], changed[$"{Id}|mode"], changed[$"{Id}|thermostatMode"]));

        await device.ExecuteCommandAsync($"{Id}|setTemperature", "20", CancellationToken.None);
        Assert.AreEqual("""{"CMT":20}""", api.Messages().Last(), "in manual mode only the temperature is sent");

        await device.ExecuteCommandAsync($"{Id}|mode", "\"off\"", CancellationToken.None);
        Assert.AreEqual("""{"CMode":"Off"}""", api.Messages().Last());
        await device.ExecuteCommandAsync($"{Id}|thermostatMode", "1", CancellationToken.None);
        Assert.AreEqual("""{"CMode":"SLS"}""", api.Messages().Last());
        await device.ExecuteCommandAsync($"{Id}|lights", "false", CancellationToken.None);
        Assert.AreEqual("""{"CLights":0}""", api.Messages().Last());
        await device.ExecuteCommandAsync($"{Id}|lock", "\"hard\"", CancellationToken.None);
        Assert.AreEqual("""{"CLock":2}""", api.Messages().Last());
        await device.ExecuteCommandAsync($"{Id}|frostProtection", "false", CancellationToken.None);
        Assert.AreEqual("""{"CFTS":0}""", api.Messages().Last());
        await device.ExecuteCommandAsync($"{Id}|frostTemperature", "8", CancellationToken.None);
        Assert.AreEqual("""{"CFT":8}""", api.Messages().Last());
        await device.ExecuteCommandAsync($"{Id}|boost", "30", CancellationToken.None);
        Assert.AreEqual("""{"CManTimer":30}""", api.Messages().Last());

        reports.Clear();
        await device.ExecuteCommandAsync($"{Id}|schedule", "away", CancellationToken.None);
        var put = api.Requests.Last();
        Assert.AreEqual(("PUT", $"{Base}/api/environments/10/devices/{Id}/schedules/2?api-version=2.1"), (put.Method, put.Url));
        var body = JsonNode.Parse(put.Body!)!;
        Assert.AreEqual(("Away", true), ((string)body["Name"]!, (bool)body["Active"]!));
        Assert.AreEqual("Away", reports.Single().Value.GetValue<string>());

        var sent = api.Requests.Count;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => device.ExecuteCommandAsync($"{Id}|setTemperature", "40", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => device.ExecuteCommandAsync($"{Id}|frostTemperature", "20", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => device.ExecuteCommandAsync($"{Id}|boost", "-1", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => device.ExecuteCommandAsync($"{Id}|thermostatMode", "3", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => device.ExecuteCommandAsync($"{Id}|mode", "\"eco\"", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => device.ExecuteCommandAsync($"{Id}|schedule", "\"Party\"", CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => device.ExecuteCommandAsync($"{Id}|lock", "\"boost\"", CancellationToken.None),
            "the timer boost lock needs software 4.4.0");
        await Assert.ThrowsExactlyAsync<UnknownCommandException>(() => device.ExecuteCommandAsync("missing", "1", CancellationToken.None));
        Assert.HasCount(sent, api.Requests, "a value that doesn't fit isn't sent");
    }

    [TestMethod]
    public async Task ARefusedCommandShowsThemosMessage()
    {
        var time = new ManualTime();
        var api = new FakeThemo(time) { RefuseCommands = true };
        var device = Device(api, time, commands: Commands(Themo.Lights));
        await device.StartAsync(CancellationToken.None);

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => device.ExecuteCommandAsync($"{Id}|lights", "true", CancellationToken.None));

        StringAssert.Contains(error.Message, "Device is offline");
    }

    [TestMethod]
    public async Task ARejectedTokenLogsInAgainOnce()
    {
        var time = new ManualTime();
        var api = new FakeThemo(time) { RejectToken = "token-1" };
        var device = Device(api, time, reports: Reports(Themo.Temperature));

        await device.StartAsync(CancellationToken.None);

        Assert.AreEqual((DeviceState.Running, 2), (device.State, api.Logins));
        Assert.AreEqual("Bearer token-2", api.Requests.Last().Header("Authorization"));
    }

    [TestMethod]
    public async Task AWrongPasswordPutsTheDeviceIntoTheErrorState()
    {
        var time = new ManualTime();
        var device = Device(new FakeThemo(time) { Password = "other" }, time);

        var error = await Assert.ThrowsAsync<Exception>(() => device.StartAsync(CancellationToken.None));

        Assert.IsInstanceOfType<ThemoAuthenticationException>(error.InnerException);
        Assert.AreEqual(DeviceState.Error, device.State);
        StringAssert.Contains(device.StateMessage, "check the e-mail and password");
        Assert.IsFalse(device.StateMessage.Contains("user@example.com", StringComparison.Ordinal), "the e-mail is masked");
    }

    [TestMethod]
    public async Task EachFinishedHourIsReportedOnceAndARestartCatchesUp()
    {
        var time = new ManualTime();
        var api = new FakeThemo(time);
        var device = Device(api, time, reports: Reports(Themo.EnergyHour, Themo.EnergyHourly, Themo.EnergyToday, Themo.HeatingShareToday));
        var reports = Capture(device);
        await device.StartAsync(CancellationToken.None);

        time.Now += TimeSpan.FromHours(3);
        reports.Clear();
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);
        CollectionAssert.AreEqual(new long[] { Hour(12), Hour(13), Hour(14) },
            reports.Where(r => r.Id == $"{Id}|energyHour").Select(r => r.TimeStamp).ToArray(), "hours with the same energy are each reported");
        Assert.AreEqual(1, reports.Count(r => r.Id == $"{Id}|energyHourly"));

        reports.Clear();
        time.Now += TimeSpan.FromMinutes(5);
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);
        Assert.IsFalse(reports.Any(r => r.Id is $"{IdText}|energyHour" or $"{IdText}|energyHourly"), "no new hour, no energy reports");

        // A restart reports the hours it missed, from the saved state.
        time.Now += TimeSpan.FromHours(2);
        var restarted = Device(api, time, reports: Reports(Themo.EnergyHour, Themo.EnergyToday, Themo.HeatingShareToday));
        var restartedReports = Capture(restarted);
        await restarted.StartAsync(CancellationToken.None);
        CollectionAssert.AreEqual(new long[] { Hour(15), Hour(16) },
            restartedReports.Where(r => r.Id == $"{Id}|energyHour").Select(r => r.TimeStamp).ToArray());

        // Just after midnight no hour of the new day is finished.
        time.Now = new DateTimeOffset(2026, 11, 5, 0, 20, 0, TimeSpan.Zero);
        await restarted.RefreshReportAsync(restarted.Id, null!, CancellationToken.None);
        var values = restartedReports.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.Last().Value.GetValue<double>());
        Assert.AreEqual((0d, 0d), (values[$"{Id}|energyToday"], values[$"{Id}|heatingShareToday"]));
    }

    [TestMethod]
    public async Task WithoutTheHourlyDataTheStateIsStillReported()
    {
        var time = new ManualTime();
        var api = new FakeThemo(time) { NoData = true };
        var device = Device(api, time, reports: Reports(Themo.Temperature, Themo.EnergyToday));
        var reports = Capture(device);

        await device.StartAsync(CancellationToken.None);

        Assert.AreEqual(DeviceState.Running, device.State);
        CollectionAssert.AreEqual(new[] { $"{Id}|temperature" }, reports.Select(r => r.Id).ToArray());
        Assert.IsFalse(device.GetConfigurationTemplate().ReportTemplates.Any(r => r.Address.Contains("energy", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task OutagesKeepTheLastStateAndAConfiguredDeviceOutlivesOneAtStart()
    {
        var time = new ManualTime();
        var api = new FakeThemo(time) { Down = true };
        var device = Device(api, time, reports: Reports(Themo.Temperature), commands: Commands(Themo.Lights));

        await device.StartAsync(CancellationToken.None);
        Assert.AreEqual(DeviceState.Running, device.State, "Themo being down isn't the configuration's fault");

        api.Down = false;
        await device.ExecuteCommandAsync($"{Id}|lights", "true", CancellationToken.None);
        Assert.AreEqual("""{"CLights":1}""", api.Messages().Last(), "the first command polls first");

        time.Now += TimeSpan.FromHours(1);
        api.Limited = true;
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);
        Assert.AreEqual(DeviceState.Running, device.State, "a 429 is waited out");
        Assert.IsTrue(device.GetConfigurationTemplate().ReportTemplates.Any(r => r.Address == $"{Id}|temperature"), "the thermostats of the last poll stay");

        var unconfigured = Device(new FakeThemo(time) { Down = true }, time);
        await Assert.ThrowsAsync<Exception>(() => unconfigured.StartAsync(CancellationToken.None));
        Assert.AreEqual(DeviceState.Error, unconfigured.State, "a new device shows why it has no thermostats");
    }

    [TestMethod]
    public void HourlyDataPowerAndSoftwareVersionsAreRead()
    {
        var hours = ThemoEnergy.Parse(JsonNode.Parse("""
            [{"Name":"Temperatures","TimeSeriesDefinitions":[{"Name":"RT","UnitName":"C"}],"TimeSeries":[{"Time":1762250400000,"Values":[21]}]},
             {"Name":"Price data","TimeSeriesDefinitions":[{"Name":"Price","UnitName":"c/kWh"},{"Name":"Consumption","UnitName":"MWh"}],
              "TimeSeries":[{"Time":1762254000000,"Values":[5.2,0.00075]},{"Time":1762250400000,"Values":[4.1,0.0012]},{"Time":1762257600000,"Values":[6.0,null]}]}]
            """));
        Assert.HasCount(2, hours, "the unfinished hour has no consumption");
        Assert.AreEqual((DateTimeOffset.FromUnixTimeMilliseconds(1762250400000), 1.2), (hours[0].Start, hours[0].Energy), "in time order, MWh to kWh");
        Assert.AreEqual(0.75, hours[1].Energy);

        Assert.AreEqual(1.2, ThemoEnergy.MaxPowerKw(0.0012)!.Value, 1e-9, "MW");
        Assert.AreEqual(2.5, ThemoEnergy.MaxPowerKw(2.5), "kW");
        Assert.AreEqual(1.8, ThemoEnergy.MaxPowerKw(1800), "W");
        Assert.IsNull(ThemoEnergy.MaxPowerKw(0));

        Assert.AreEqual(new Version(4, 3, 1), Themo.SoftwareVersion("4.3.1-rc"));
        Assert.AreEqual(new Version(5, 0), Themo.SoftwareVersion("v5"));
        Assert.IsNull(Themo.SoftwareVersion("unknown"));
        var old = new ThemoThermostat { Software = "4.1.9", Mode = ThemoModes.Manual };
        Assert.ThrowsExactly<InvalidOperationException>(() => Themo.ParseCommand(Themo.Boost, "10", old), "the boost needs software 4.2");
        Assert.AreEqual(new ThemoMessage("CMT", 22d), Themo.ParseCommand(Themo.SetTemperature, "\"22\"", old)!.Single());
        Assert.IsNull(Themo.ParseCommand("unknown", "1", old));
    }

    [TestMethod]
    public void TheTemplateNeedsNoThemoWhenTheDeviceIsNotRunning()
    {
        var device = new Themo(NullLogger.Instance);
        var template = device.GetConfigurationTemplate();

        Assert.AreEqual(typeof(Themo).FullName, template.ClassFullName);
        CollectionAssert.AreEquivalent(new[] { "email", "password" }, template.DeviceParameters.Keys.ToArray());
        Assert.AreEqual(0, device.GetMatterEndpoints(template).Count());
    }

    private static long Hour(int hour) => new DateTimeOffset(2026, 11, 4, hour, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    private TestThemo Device(FakeThemo api, ManualTime time, List<ReportTemplate>? reports = null, List<CommandTemplate>? commands = null)
    {
        var device = new TestThemo(api) { DataDirectory = _dataDirectory, Time = time };
        device.Initialize(new DeviceConfiguration
        {
            Id = "device",
            Name = "Themo",
            DeviceParameters = new() { ["email"] = "user@example.com", ["password"] = "secret" },
            ReportTemplates = reports ?? [],
            CommandTemplates = commands ?? []
        });
        return device;
    }

    // The template id is the address, so the tests can find reports by address.
    private static List<ReportTemplate> Reports(params string[] keys) =>
        keys.Select(k => new ReportTemplate { Id = $"{Id}|{k}", Address = $"{Id}|{k}" }).ToList();

    private static List<CommandTemplate> Commands(params string[] keys) =>
        keys.Select(k => new CommandTemplate { Id = $"{Id}|{k}", Address = $"{Id}|{k}" }).ToList();

    private static ConcurrentQueue<Report> Capture(Core.Interfaces.IDevice device)
    {
        var reports = new ConcurrentQueue<Report>();
        device.ReportUpdated += (_, report) => reports.Enqueue((Report)report);
        return reports;
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record Sent(string Method, string Url, string? Body, Dictionary<string, string> Headers)
    {
        public string? Header(string name) => Headers.GetValueOrDefault(name);
    }

    /// <summary>
    /// One environment with a combi T700 in SLS mode on software 4.3.1, two minimum temperature schedules and a
    /// maximum one, and 0.5 kWh of consumption in every finished hour.
    /// </summary>
    private sealed class FakeThemo(ManualTime time)
    {
        public List<Sent> Requests { get; } = [];
        public string Password { get; set; } = "secret";
        public string? RejectToken { get; set; }
        public bool Down { get; set; }
        public bool Limited { get; set; }
        public bool RefuseCommands { get; set; }
        public bool NoData { get; set; }
        public int Logins { get; private set; }

        public string[] Messages() => Requests.Where(r => r.Url.Contains("/commands/message", StringComparison.Ordinal)).Select(r => r.Body!).ToArray();

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
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/auth/login")
            {
                if ((string?)JsonNode.Parse(body!)!["Password"] != Password)
                    return Json("""{"Message":"Invalid username or password","Code":"InvalidCredentials"}""", HttpStatusCode.BadRequest);
                Logins++;
                return Json($$"""{"Status":true,"Message":null,"Token":"token-{{Logins}}"}""");
            }
            if (RejectToken != null && headers.GetValueOrDefault("Authorization") == $"Bearer {RejectToken}")
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);

            switch (path)
            {
                case "/api/environments":
                    return Json("""[{"Id":10,"Name":"Home","DeviceCount":1}]""");
                case "/api/environments/10/devices":
                    return Json("""
                        [{"Id":__ID__,"Name":"Bathroom","DeviceId":"THEMO-123","EnvironmentId":10,"Tags":"Wall|T700|Floor","SW":"4.3.1",
                          "ActiveScheduleId":1,"TemperatureSchedule":"Home",
                          "State":{"LS":1,"MP":0.0012,"CS":true,"Info":22.5,"RT":21.0,"FloorT":24.0,"Power":true,"Mode":"SLS","MT":22,"Lights":1,
                            "Lock":0,"FTS":1,"FT":7,"SW":"4.3.1","OT":-3.5,"DeviceParameters":{"IsPriceSwitch":false,"IsAir":false,"IsFloor":false,"IsCombi":true}}}]
                        """.Replace("__ID__", Id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
                case $"/api/environments/10/devices/{IdText}/schedules":
                    return Json("""
                        [{"Id":1,"Name":"Home","Parameter":"MinTemperature","Unit":"C","Active":true},
                         {"Id":2,"Name":"Away","Parameter":"MinTemperature","Unit":"C","Active":false},
                         {"Id":3,"Name":"Limit","Parameter":"MaxTemperature","Unit":"C","Active":true}]
                        """);
                case $"/api/environments/10/devices/{IdText}/schedules/2" when request.Method == HttpMethod.Put:
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };
                case $"/api/environments/10/devices/{IdText}/data":
                    return NoData ? Json("""{"Message":"Not found","Code":"NotFound"}""", HttpStatusCode.NotFound) : Data(request.RequestUri);
                case $"/api/environments/10/devices/{IdText}/commands/message":
                    return RefuseCommands
                        ? Json("""{"Message":"Device is offline","Code":"DeviceOffline"}""", HttpStatusCode.BadRequest)
                        : Json("""{"CommandId":"c-1","Status":"Sent"}""");
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        // Every hour from "from" to "to"; the current hour has no consumption yet.
        private HttpResponseMessage Data(Uri uri)
        {
            var query = uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            var from = DateTimeOffset.Parse(query["from"], CultureInfo.InvariantCulture);
            var to = DateTimeOffset.Parse(query["to"], CultureInfo.InvariantCulture);
            var current = new DateTimeOffset(time.Now.UtcDateTime.Date.AddHours(time.Now.UtcDateTime.Hour), TimeSpan.Zero);
            var points = new JsonArray();
            for (var hour = from; hour <= to; hour = hour.AddHours(1))
                points.Add(new JsonObject { ["Time"] = hour.ToUnixTimeMilliseconds(), ["Values"] = new JsonArray(5.0, hour < current ? 0.0005 : null) });
            var series = new JsonObject
            {
                ["Name"] = "Price data",
                ["TimeSeriesDefinitions"] = new JsonArray(new JsonObject { ["Name"] = "Price", ["UnitName"] = "c/kWh" },
                    new JsonObject { ["Name"] = "Consumption", ["UnitName"] = "MWh" }),
                ["TimeSeries"] = points
            };
            return Json(new JsonArray(series).ToJsonString());
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 4, 12, 20, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class TestThemo(FakeThemo api) : Themo(NullLogger.Instance)
    {
        internal override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            api.SendAsync(request, cancellationToken);
    }
}
