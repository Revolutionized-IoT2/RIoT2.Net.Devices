using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Core;
using RIoT2.Core.Models;
using RIoT2.Net.Devices.Catalog;
using RIoT2.Net.Devices.Models;
using RIoT2.Net.Devices.Services.DefaPower;
using ValueType = RIoT2.Core.ValueType;

namespace RIoT2.Net.Devices.Tests;

/// <summary>The DEFA Power device against a scripted CloudCharge API; nothing reaches CloudCharge.</summary>
[TestClass]
public class DefaPowerTests
{
    private const string Connector = "C1";
    private const string Code = "123456";
    private static readonly DateTimeOffset _start = new(2026, 11, 4, 11, 59, 5, TimeSpan.Zero);

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
    public async Task TheSmsLoginKeepsTheServerCookieSavesTheLoginAndReadsTheChargers()
    {
        var time = new ManualTime { Now = _start };
        var api = new FakeCloudCharge();
        var device = Device(api, time, reports: Reports(DefaPower.Status, DefaPower.ChargingState, DefaPower.Charging, DefaPower.Power,
            DefaPower.CurrentLimit, DefaPower.Online, DefaPower.ErrorCode, DefaPower.Meter, DefaPower.SessionEnergy, DefaPower.EcoMode,
            DefaPower.EcoHoursToCharge, DefaPower.ManualSchedules, DefaPower.PausedBy, DefaPower.PausedUntil, DefaPower.ScheduleOverridden));
        var reports = Capture(device);

        await device.StartAsync(CancellationToken.None);

        Assert.AreEqual(DeviceState.Running, device.State, "a device without a login runs and waits for the login commands");
        Assert.AreEqual(DefaPower.LoginRequired, device.CurrentLoginState);
        Assert.IsEmpty(api.Requests, "nothing is read before the login");
        Assert.AreEqual(DefaPower.LoginRequired, reports.Last(r => r.Id == DefaPower.LoginState).Value.GetValue<string>());

        await device.ExecuteCommandAsync(DefaPower.RequestSmsCode, "true", CancellationToken.None);
        var prelogin = api.Requests.Single();
        Assert.AreEqual($"POST {CloudChargeClient.ApiUrl}/prelogin", $"{prelogin.Method} {prelogin.Url}");
        var sms = JsonNode.Parse(prelogin.Body!)!;
        Assert.AreEqual(("358401234567", CloudChargeClient.AppId), ((string)sms["phoneNr"]!, (string)sms["devToken"]!),
            "the phone number without the + and spaces, as the CloudCharge app");
        Assert.AreEqual(DefaPower.CodeSent, device.CurrentLoginState);

        await device.ExecuteCommandAsync(DefaPower.Login, $"\"{Code}\"", CancellationToken.None);
        var login = api.Requests.Single(r => r.Url.EndsWith("/login", StringComparison.Ordinal));
        Assert.AreEqual("INGRESSCOOKIE=server-1", login.Header("Cookie"), "the login reaches the server that sent the SMS");
        Assert.AreEqual(Code, (string)JsonNode.Parse(login.Body!)!["password"]!);
        Assert.AreEqual(DefaPower.LoggedIn, device.CurrentLoginState);
        var read = api.Requests.First(r => r.Url.EndsWith("/chargers/private", StringComparison.Ordinal));
        Assert.AreEqual(("tok-1", "4711"), (read.Header("x-authorization"), read.Header("x-user")));
        Assert.AreEqual(1, Directory.GetFiles(_dataDirectory, "defapower-auth-*.json").Length, "the login is kept in the Data folder");

        var values = reports.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.Last().Value);
        Assert.AreEqual("available", values[$"{Connector}|status"].GetValue<string>());
        Assert.AreEqual("idle", values[$"{Connector}|chargingState"].GetValue<string>());
        Assert.IsFalse(values[$"{Connector}|charging"].GetValue<bool>());
        Assert.AreEqual(0d, values[$"{Connector}|power"].GetValue<double>());
        Assert.AreEqual(16, values[$"{Connector}|currentLimit"].GetValue<int>());
        Assert.IsTrue(values[$"{Connector}|online"].GetValue<bool>());
        Assert.AreEqual("", values[$"{Connector}|errorCode"].GetValue<string>(), "OCPP's NoError is no error");
        Assert.AreEqual(100d, values[$"{Connector}|meter"].GetValue<double>());
        Assert.AreEqual(0d, values[$"{Connector}|sessionEnergy"].GetValue<double>());
        Assert.IsTrue(values[$"{Connector}|ecoMode"].GetValue<bool>());
        Assert.AreEqual(4, values[$"{Connector}|ecoHoursToCharge"].GetValue<int>());
        Assert.IsFalse(values[$"{Connector}|manualSchedules"].GetValue<bool>());
        Assert.AreEqual("ecoMode", values[$"{Connector}|pausedBy"].GetValue<string>());
        Assert.AreEqual("2026-11-04T22:00:00+00:00", values[$"{Connector}|pausedUntil"].GetValue<string>(), "a time without an offset is UTC");
        Assert.IsFalse(values[$"{Connector}|scheduleOverridden"].GetValue<bool>());

        var calls = api.Requests.Count;
        var template = device.GetConfigurationTemplate();
        Assert.HasCount(calls, api.Requests, "the template comes from the last poll");
        Assert.IsNull(template.RefreshSchedule, "the device polls on its own");
        Assert.AreEqual("Garage status", template.ReportTemplates.Single(r => r.Address == $"{Connector}|status").Name);
        Assert.AreEqual(ValueSemantics.Forecast, template.ReportTemplates.Single(r => r.Address == $"{Connector}|energyQuarterly").Schema!.Semantic);
        CollectionAssert.IsSubsetOf(new[] { DefaPower.LoginState, $"{Connector}|meter", $"{Connector}|energyQuarter", $"{Connector}|energyHour",
            $"{Connector}|session", $"{Connector}|costQuarter", $"{Connector}|ecoMode", $"{Connector}|manualSchedules", $"{Connector}|pausedBy" },
            template.ReportTemplates.Select(r => r.Address).ToList());
        CollectionAssert.IsSubsetOf(new[] { DefaPower.RequestSmsCode, DefaPower.Login, DefaPower.RefreshAddress, DefaPower.Prices,
            $"{Connector}|charging", $"{Connector}|currentLimit", $"{Connector}|ecoMode", $"{Connector}|ecoHoursToCharge",
            $"{Connector}|manualSchedules", $"{Connector}|chargeNow", $"{Connector}|restart" },
            template.CommandTemplates!.Select(c => c.Address).ToList());

        // A restart logs in with the saved login, without a new SMS.
        await device.StopAsync(CancellationToken.None);
        var again = Device(api, time);
        api.Requests.Clear();
        await again.StartAsync(CancellationToken.None);
        Assert.AreEqual(DefaPower.LoggedIn, again.CurrentLoginState);
        Assert.IsFalse(api.Requests.Any(r => r.Url.Contains("login", StringComparison.Ordinal)));
        Assert.IsTrue(api.Requests.All(r => r.Header("x-authorization") == "tok-1"));
    }

    [TestMethod]
    public async Task TheMeterBecomesQuarterHourHourDayAndSessionEnergyAndCost()
    {
        var time = new ManualTime { Now = _start };
        var api = new FakeCloudCharge();
        var device = Device(api, time, reports: Reports(DefaPower.EnergyQuarter, DefaPower.EnergyHour, DefaPower.EnergyToday,
            DefaPower.EnergyQuarterly, DefaPower.Session, DefaPower.CostQuarter, DefaPower.CostToday, DefaPower.Charging, DefaPower.SessionEnergy));
        var reports = Capture(device);
        await LogInAsync(device);
        await device.ExecuteCommandAsync(DefaPower.Prices, HourlyPrices(10).ToValue().ToJson(), CancellationToken.None);

        // Plugged in at 12:04, charges 11 kW from 12:05 to 13:05, unplugged at 13:10.
        for (var minute = 1; minute <= 81; minute++)
        {
            time.Now = _start.AddMinutes(minute);
            api.Charge(time.Now);
            await device.PollAsync(force: false, CancellationToken.None);
        }

        var quarters = reports.Where(r => r.Id == $"{Connector}|energyQuarter").Select(r => (r.TimeStamp, r.Value.GetValue<double>())).ToList();
        CollectionAssert.AreEqual(new[] { (At(12, 0), 1.8333), (At(12, 15), 2.75), (At(12, 30), 2.75), (At(12, 45), 2.75), (At(13, 0), 0.9167) },
            quarters, "each finished quarter once, at its start; 11:45 started before the first reading");
        var hour = reports.Single(r => r.Id == $"{Connector}|energyHour");
        Assert.AreEqual((At(12, 0), 10.0833), (hour.TimeStamp, hour.Value.GetValue<double>()));
        Assert.AreEqual(11d, reports.Last(r => r.Id == $"{Connector}|energyToday").Value.GetValue<double>());

        var costs = reports.Where(r => r.Id == $"{Connector}|costQuarter").Select(r => r.Value.GetValue<double>()).ToList();
        CollectionAssert.AreEqual(new[] { 0.1833, 0.275, 0.275, 0.275, 0.0917 }, costs, "10 c/kWh is 0.1 € per kWh");
        Assert.AreEqual(1.1, reports.Last(r => r.Id == $"{Connector}|costToday").Value.GetValue<double>(), 1e-9);

        var session = reports.Single(r => r.Id == $"{Connector}|session");
        Assert.AreEqual(_start.AddMinutes(71).ToUnixTimeSeconds(), session.TimeStamp, "time-stamped when the car was unplugged");
        Assert.AreEqual("2026-11-04T12:04:05+00:00", session.Value.GetValue<string>("start"));
        Assert.AreEqual("2026-11-04T13:10:05+00:00", session.Value.GetValue<string>("end"));
        Assert.AreEqual(66, session.Value.GetValue<int>("durationMinutes"));
        Assert.AreEqual(11d, session.Value.GetValue<double>("energy"));
        Assert.AreEqual(1.1, session.Value.GetValue<double>("cost"), 1e-9);
        Assert.AreEqual("€", session.Value.GetValue<string>("costUnit"));

        var series = ForecastSeries.From(reports.Last(r => r.Id == $"{Connector}|energyQuarterly").Value)!;
        Assert.IsNull(series.Validate());
        Assert.AreEqual((ForecastSeries.ConfirmedKind, "kWh", "PT15M", 5), (series.Kind, series.Unit, series.Resolution, series.Points.Count));
        Assert.AreEqual(2.75, series.Points[1].Value);

        Assert.AreEqual(1, api.Requests.Count(r => r.Url.EndsWith("/startliveconsumption", StringComparison.Ordinal)),
            "the live consumption is asked for when the charging starts");
        Assert.IsTrue(reports.Any(r => r.Id == $"{Connector}|charging" && r.Value.GetValue<bool>()));
        Assert.AreEqual(0d, reports.Last(r => r.Id == $"{Connector}|sessionEnergy").Value.GetValue<double>());
    }

    [TestMethod]
    public async Task ARestartReportsTheQuartersItMissedAndPricesLaterCostTheEarlierQuarters()
    {
        var time = new ManualTime { Now = _start };
        var api = new FakeCloudCharge();
        var device = Device(api, time, reports: Reports(DefaPower.EnergyQuarter, DefaPower.CostQuarter));
        var reports = Capture(device);
        await LogInAsync(device);
        foreach (var minute in new[] { 11, 21 })
        {
            time.Now = _start.AddMinutes(minute);
            await device.PollAsync(force: false, CancellationToken.None);
        }
        Assert.AreEqual(At(12, 0), reports.Single(r => r.Id == $"{Connector}|energyQuarter").TimeStamp);
        await device.StopAsync(CancellationToken.None);

        // 3.6 kWh while the node was down, from 12:20:05 to 13:00:05.
        time.Now = new DateTimeOffset(2026, 11, 4, 13, 0, 5, TimeSpan.Zero);
        api.Meter = 103.6;
        var restarted = Device(api, time, reports: Reports(DefaPower.EnergyQuarter, DefaPower.CostQuarter));
        var after = Capture(restarted);
        await restarted.StartAsync(CancellationToken.None);

        var quarters = after.Where(r => r.Id == $"{Connector}|energyQuarter").Select(r => (r.TimeStamp, r.Value.GetValue<double>())).ToList();
        CollectionAssert.AreEqual(new[] { (At(12, 15), 0.8925), (At(12, 30), 1.35), (At(12, 45), 1.35) }, quarters,
            "the missed energy is shared by time");
        Assert.IsFalse(after.Any(r => r.Id == $"{Connector}|costQuarter"), "no prices yet");

        await restarted.ExecuteCommandAsync(DefaPower.Prices, HourlyPrices(20).ToValue().ToJson(), CancellationToken.None);
        var costs = after.Where(r => r.Id == $"{Connector}|costQuarter").Select(r => (r.TimeStamp, r.Value.GetValue<double>())).ToList();
        CollectionAssert.AreEqual(new[] { (At(12, 0), 0d), (At(12, 15), 0.1785), (At(12, 30), 0.27), (At(12, 45), 0.27) }, costs,
            "the quarters that finished before the prices came");
    }

    [TestMethod]
    public async Task CommandsCallCloudChargeAndReportTheChange()
    {
        var time = new ManualTime { Now = _start };
        var api = new FakeCloudCharge();
        var device = Device(api, time,
            reports: Reports(DefaPower.CurrentLimit, DefaPower.ManualSchedules, DefaPower.ScheduleOverridden, DefaPower.PausedBy, DefaPower.EcoMode),
            commands: Commands(DefaPower.Charging, DefaPower.CurrentLimit, DefaPower.EcoMode, DefaPower.EcoHoursToCharge, DefaPower.ManualSchedules,
                DefaPower.ChargeNow, DefaPower.Restart));
        var reports = Capture(device);
        await LogInAsync(device);
        api.Requests.Clear();

        await device.ExecuteCommandAsync($"{Connector}|charging", "true", CancellationToken.None);
        await device.ExecuteCommandAsync($"{Connector}|charging", "false", CancellationToken.None);
        Assert.AreEqual("POST /services/user/charging/start {\"alias\":\"ABC123\"}", api.Requests[0].Describe());
        Assert.AreEqual("POST /services/user/charging/stop {\"alias\":\"ABC123\"}", api.Requests[1].Describe());

        await device.ExecuteCommandAsync($"{Connector}|currentLimit", "10", CancellationToken.None);
        Assert.AreEqual("POST /services/user/connector/C1/maxcurrent?current=10", api.Requests[^1].Describe());
        Assert.AreEqual(10, reports.Last(r => r.Id == $"{Connector}|currentLimit").Value.GetValue<int>());
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => device.ExecuteCommandAsync($"{Connector}|currentLimit", "7", CancellationToken.None),
            "the charger takes 6, 10 or 16 A");
        Assert.AreEqual(1, api.Requests.Count(r => r.Url.EndsWith("/alternatives", StringComparison.Ordinal)), "the limits are read once");

        await device.ExecuteCommandAsync($"{Connector}|ecoMode", "false", CancellationToken.None);
        var eco = JsonNode.Parse(api.Requests[^1].Body!)!;
        Assert.AreEqual("PUT", api.Requests[^1].Method);
        Assert.AreEqual((false, true, 4, 420), ((bool)eco["active"]!, (bool)eco["pickupTimeEnabled"]!, (int)eco["hoursToCharge"]!, (int)eco["dayOfWeekMap"]!["MONDAY"]!),
            "the other eco mode settings stay");
        Assert.IsFalse(reports.Last(r => r.Id == $"{Connector}|ecoMode").Value.GetValue<bool>());
        await device.ExecuteCommandAsync($"{Connector}|ecoHoursToCharge", "6", CancellationToken.None);
        eco = JsonNode.Parse(api.Requests[^1].Body!)!;
        Assert.AreEqual((false, 6), ((bool)eco["active"]!, (int)eco["hoursToCharge"]!));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => device.ExecuteCommandAsync($"{Connector}|ecoHoursToCharge", "30", CancellationToken.None));

        await device.ExecuteCommandAsync($"{Connector}|manualSchedules", "on", CancellationToken.None);
        Assert.AreEqual("PUT /services/user/connector/C1/manual-schedules/enable/true", api.Requests[^1].Describe());
        Assert.IsTrue(reports.Last(r => r.Id == $"{Connector}|manualSchedules").Value.GetValue<bool>());

        await device.ExecuteCommandAsync($"{Connector}|chargeNow", "true", CancellationToken.None);
        Assert.AreEqual("PUT /services/user/connector/C1/schedule/override", api.Requests[^1].Describe());
        Assert.IsTrue(reports.Last(r => r.Id == $"{Connector}|scheduleOverridden").Value.GetValue<bool>());
        Assert.AreEqual("none", reports.Last(r => r.Id == $"{Connector}|pausedBy").Value.GetValue<string>());

        await device.ExecuteCommandAsync($"{Connector}|restart", "hard", CancellationToken.None);
        Assert.AreEqual("POST /services/user/connector/C1/reset?type=hard", api.Requests[^1].Describe());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => device.ExecuteCommandAsync($"{Connector}|restart", "reboot", CancellationToken.None));

        api.RefuseCommands = true;
        var refused = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            device.ExecuteCommandAsync($"{Connector}|charging", "true", CancellationToken.None));
        StringAssert.Contains(refused.Message, "Charger is offline");
    }

    [TestMethod]
    public async Task ALostLoginWaitsForANewSmsLogin()
    {
        var time = new ManualTime { Now = _start };
        var api = new FakeCloudCharge();
        var device = Device(api, time, commands: Commands(DefaPower.Charging));
        await LogInAsync(device);

        api.Token = "tok-2";
        await device.PollAsync(force: false, CancellationToken.None);

        Assert.AreEqual(DeviceState.Running, device.State);
        Assert.AreEqual(DefaPower.LoginRequired, device.CurrentLoginState);
        Assert.IsEmpty(Directory.GetFiles(_dataDirectory, "defapower-auth-*.json"), "the refused login is forgotten");
        await Assert.ThrowsExactlyAsync<DeviceNotReadyException>(() => device.ExecuteCommandAsync($"{Connector}|charging", "true", CancellationToken.None));

        await device.ExecuteCommandAsync(DefaPower.RequestSmsCode, "true", CancellationToken.None);
        await device.ExecuteCommandAsync(DefaPower.Login, Code, CancellationToken.None);
        Assert.AreEqual(DefaPower.LoggedIn, device.CurrentLoginState);
        await device.ExecuteCommandAsync($"{Connector}|charging", "true", CancellationToken.None);
    }

    [TestMethod]
    public async Task AWrongCodeIsRefusedAndAnOutageKeepsTheDeviceRunning()
    {
        var time = new ManualTime { Now = _start };
        var api = new FakeCloudCharge();
        var device = Device(api, time, reports: Reports(DefaPower.Meter));
        var reports = Capture(device);
        await device.StartAsync(CancellationToken.None);
        await device.ExecuteCommandAsync(DefaPower.RequestSmsCode, "true", CancellationToken.None);

        var wrong = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => device.ExecuteCommandAsync(DefaPower.Login, "000000", CancellationToken.None));
        StringAssert.Contains(wrong.Message, "No loginAttempts found");
        Assert.AreEqual(DefaPower.CodeSent, device.CurrentLoginState);

        await device.ExecuteCommandAsync(DefaPower.Login, Code, CancellationToken.None);
        var meters = reports.Count(r => r.Id == $"{Connector}|meter");
        api.Down = true;
        time.Now = _start.AddMinutes(1);
        await device.PollAsync(force: false, CancellationToken.None);
        api.Limited = true;
        await device.PollAsync(force: false, CancellationToken.None);

        Assert.AreEqual(DeviceState.Running, device.State);
        Assert.AreEqual(DefaPower.LoggedIn, device.CurrentLoginState, "an outage isn't a lost login");
        Assert.AreEqual(meters, reports.Count(r => r.Id == $"{Connector}|meter"));
        Assert.IsNotNull(device.GetConfigurationTemplate().ReportTemplates.SingleOrDefault(r => r.Address == $"{Connector}|meter"), "the last state is kept");
    }

    [TestMethod]
    public void TheTemplateNeedsNoCloudChargeWhenTheDeviceIsNotRunning()
    {
        var template = new DefaPower(NullLogger.Instance).GetConfigurationTemplate();

        Assert.AreEqual(typeof(DefaPower).FullName, template.ClassFullName);
        CollectionAssert.AreEquivalent(new[] { "phoneNumber" }, template.DeviceParameters.Keys.ToArray());
        Assert.AreEqual(DefaPower.LoginState, template.ReportTemplates.Single().Address);
        CollectionAssert.AreEqual(new[] { DefaPower.RequestSmsCode, DefaPower.Login, DefaPower.RefreshAddress, DefaPower.Prices },
            template.CommandTemplates!.Select(c => c.Address).ToArray());
        Assert.AreEqual(ValueType.Entity, template.CommandTemplates!.Single(c => c.Address == DefaPower.Prices).Type);
    }

    [TestMethod]
    public void PhoneNumbersStatesAndUnitsAreNormalized()
    {
        Assert.AreEqual("358401234567", CloudChargeClient.NormalizePhoneNumber("+358 40 123-4567"));
        Assert.AreEqual("46701234567", CloudChargeClient.NormalizePhoneNumber("0046701234567"));
        var zero = Assert.ThrowsExactly<ArgumentException>(() => CloudChargeClient.NormalizePhoneNumber("+358 040 1234567"));
        StringAssert.Contains(zero.Message, "leading zero");
        Assert.ThrowsExactly<ArgumentException>(() => CloudChargeClient.NormalizePhoneNumber("12345"));
        Assert.ThrowsExactly<ArgumentException>(() => CloudChargeClient.NormalizePhoneNumber("phone"));
        Assert.AreEqual("+358***67", CloudChargeClient.Mask("358401234567"));

        Assert.AreEqual("suspendedEvse", DefaPowerStates.CamelCase("SuspendedEVSE"));
        Assert.AreEqual("suspendedEv", DefaPowerStates.CamelCase("SuspendedEV"));
        Assert.AreEqual("evConnected", DefaPowerStates.CamelCase("EVConnected"));
        Assert.AreEqual("charging", DefaPowerStates.CamelCase("Charging"));
        Assert.AreEqual("unrecognized", DefaPowerStates.CamelCase("UNRECOGNIZED"));
        Assert.AreEqual("manualSchedules", DefaPowerStates.CamelCase("MANUAL_SCHEDULES"));
        Assert.IsNull(DefaPowerStates.CamelCase(" "));
        Assert.IsTrue(DefaPowerStates.Connected("suspendedEvse", "suspendedEvse"));
        Assert.IsFalse(DefaPowerStates.Connected(null, "available"));
        Assert.IsNull(DefaPowerStates.Connected(null, null));

        Assert.AreEqual((0.01, "€"), (DefaPowerEnergy.CostFactor("c/kWh"), DefaPowerEnergy.CostUnit("c/kWh")));
        Assert.AreEqual((0.01, "€"), (DefaPowerEnergy.CostFactor(null), DefaPowerEnergy.CostUnit(null)));
        Assert.AreEqual((0.001, "€"), (DefaPowerEnergy.CostFactor("EUR/MWh"), DefaPowerEnergy.CostUnit("EUR/MWh")));
        Assert.AreEqual((1d, "SEK"), (DefaPowerEnergy.CostFactor("SEK/kWh"), DefaPowerEnergy.CostUnit("SEK/kWh")));
        Assert.AreEqual((0.01, "kr"), (DefaPowerEnergy.CostFactor("öre/kWh"), DefaPowerEnergy.CostUnit("öre/kWh")));
    }

    [TestMethod]
    public void TheMeterIsSharedByTimeAndAResetCountsAsNoEnergy()
    {
        var state = new DefaPowerConnectorState();
        var at = new DateTimeOffset(2026, 11, 4, 12, 10, 0, TimeSpan.Zero);

        Assert.AreEqual(DefaPowerSample.First, DefaPowerEnergy.AddSample(state, at, 10));
        Assert.AreEqual(DefaPowerSample.Added, DefaPowerEnergy.AddSample(state, at.AddMinutes(10), 12));
        Assert.AreEqual(DefaPowerSample.Ignored, DefaPowerEnergy.AddSample(state, at.AddMinutes(10), 13));
        CollectionAssert.AreEqual(new[] { 1d, 1d }, state.Quarters.Select(x => Math.Round(x.Energy, 9)).ToArray(), "five minutes on each side of 12:15");
        Assert.AreEqual(DefaPowerSample.MeterReset, DefaPowerEnergy.AddSample(state, at.AddMinutes(20), 1));
        Assert.AreEqual(1d, Math.Round(state.Quarters[^1].Energy, 9), "the reset adds nothing");
        Assert.AreEqual(1, DefaPowerEnergy.FinishedQuarters(state).Count, "12:00 started before the first reading");

        var prices = new DefaPowerState { Prices = [new() { Start = at.AddHours(-1), End = at.AddHours(2), Price = 5 }] };
        var series = new ForecastSeries
        {
            Unit = "c/kWh",
            Points = [new ForecastPoint(at, at.AddHours(1), 8)]
        };
        Assert.AreEqual(1, DefaPowerEnergy.MergePrices(prices, series, at));
        CollectionAssert.AreEqual(new[] { 8d }, prices.Prices.Select(x => x.Price).ToArray(), "a new price replaces the one it overlaps");
    }

    [TestMethod]
    public void ThePollWaitsAMinuteOrUntilJustAfterTheNextQuarter()
    {
        var time = new ManualTime { Now = new DateTimeOffset(2026, 11, 4, 12, 0, 5, TimeSpan.Zero) };
        var device = new DefaPower(NullLogger.Instance) { Time = time };

        Assert.AreEqual(TimeSpan.FromSeconds(60), device.NextDelay());
        time.Now = new DateTimeOffset(2026, 11, 4, 12, 14, 30, TimeSpan.Zero);
        Assert.AreEqual(TimeSpan.FromSeconds(35), device.NextDelay());
        time.Now = new DateTimeOffset(2026, 11, 4, 12, 15, 2, TimeSpan.Zero);
        Assert.AreEqual(TimeSpan.FromSeconds(3), device.NextDelay());
    }

    [TestMethod]
    public async Task ThePollLoopStopsWithTheDevice()
    {
        var api = new FakeCloudCharge();
        var device = Device(api, new ManualTime { Now = _start });
        device.RunLoop = true;

        await device.StartAsync(CancellationToken.None);
        var stopped = device.StopAsync(CancellationToken.None);

        // Times out unless the loop's wait is cancelled.
        await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(DeviceState.Stopped, device.State, device.StateMessage);
    }

    private static long At(int hour, int minute) => new DateTimeOffset(2026, 11, 4, hour, minute, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    private static ForecastSeries HourlyPrices(double price)
    {
        var midnight = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        return new ForecastSeries
        {
            IssuedAt = midnight,
            Kind = ForecastSeries.ConfirmedKind,
            Unit = "c/kWh",
            Resolution = "PT1H",
            Points = Enumerable.Range(0, 24).Select(h => new ForecastPoint(midnight.AddHours(h), midnight.AddHours(h + 1), price)).ToList()
        };
    }

    private static async Task LogInAsync(TestDefaPower device)
    {
        await device.StartAsync(CancellationToken.None);
        await device.ExecuteCommandAsync(DefaPower.RequestSmsCode, "true", CancellationToken.None);
        await device.ExecuteCommandAsync(DefaPower.Login, Code, CancellationToken.None);
    }

    private TestDefaPower Device(FakeCloudCharge api, ManualTime time, List<ReportTemplate>? reports = null, List<CommandTemplate>? commands = null)
    {
        var device = new TestDefaPower(api) { DataDirectory = _dataDirectory, Time = time, RunLoop = false, RequestInterval = TimeSpan.Zero };
        var deviceReports = new List<ReportTemplate> { new() { Id = DefaPower.LoginState, Address = DefaPower.LoginState } };
        var deviceCommands = new[] { DefaPower.RequestSmsCode, DefaPower.Login, DefaPower.RefreshAddress, DefaPower.Prices }
            .Select(x => new CommandTemplate { Id = x, Address = x }).ToList();
        device.Initialize(new DeviceConfiguration
        {
            Id = "device",
            Name = "DEFA Power",
            DeviceParameters = new() { ["phoneNumber"] = "+358 40 123 4567" },
            ReportTemplates = [.. deviceReports, .. reports ?? []],
            CommandTemplates = [.. deviceCommands, .. commands ?? []]
        });
        return device;
    }

    // The template id is the address, so the tests can find reports by address.
    private static List<ReportTemplate> Reports(params string[] keys) =>
        keys.Select(k => new ReportTemplate { Id = $"{Connector}|{k}", Address = $"{Connector}|{k}" }).ToList();

    private static List<CommandTemplate> Commands(params string[] keys) =>
        keys.Select(k => new CommandTemplate { Id = $"{Connector}|{k}", Address = $"{Connector}|{k}" }).ToList();

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

        public string Describe() => $"{Method} {new Uri(Url).PathAndQuery}{(Body == null ? "" : " " + Body)}";
    }

    /// <summary>
    /// One charge point, Garage, with connector C1 (alias ABC123) that has eco mode and manual schedules, a 16 A limit and
    /// the limits 6, 10 and 16 A. The SMS code is 123456, and the server that sent it is the one with the cookie.
    /// </summary>
    private sealed class FakeCloudCharge
    {
        private JsonObject _eco = JsonNode.Parse("""
            {"active":true,"dayOfWeekMap":{"MONDAY":420,"TUESDAY":420},"hoursToCharge":4,"pickupTimeEnabled":true,
             "percentageNotToCharge":null,"schedule":[],"scheduleOverridden":false}
            """)!.AsObject();

        public List<Sent> Requests { get; } = [];
        public string Token { get; set; } = "tok-1";
        public bool Down { get; set; }
        public bool Limited { get; set; }
        public bool RefuseCommands { get; set; }

        public double Meter { get; set; } = 100;
        public string ChargingState { get; set; } = "Idle";
        public string Status { get; set; } = "Available";
        public double Power { get; set; }
        public double? Transaction { get; set; }

        // Plugged in at 12:04, charges 11 kW from 12:05 to 13:05, unplugged at 13:10.
        public void Charge(DateTimeOffset now)
        {
            var day = now.UtcDateTime.Date;
            DateTimeOffset At(int hour, int minute) => new(day.AddHours(hour).AddMinutes(minute), TimeSpan.Zero);
            var charged = Math.Clamp((now - At(12, 5)).TotalHours, 0, 1);
            Meter = 100 + 11 * charged;
            (ChargingState, Status, Power) = now switch
            {
                _ when now < At(12, 4) || now >= At(13, 10) => ("Idle", "Available", 0d),
                _ when now < At(12, 5) => ("EVConnected", "Preparing", 0d),
                _ when now < At(13, 5) => ("Charging", "Charging", 11d),
                _ => ("SuspendedEV", "SuspendedEV", 0d)
            };
            Transaction = Status == "Available" ? 0 : Meter - 100;
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
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            var path = request.RequestUri!.AbsolutePath["/services/user".Length..];
            var method = request.Method.Method;
            if (path == "/prelogin")
            {
                var answer = Json("{}");
                answer.Headers.Add("Set-Cookie", "INGRESSCOOKIE=server-1; Path=/; HttpOnly");
                return answer;
            }
            if (path == "/login")
            {
                var login = JsonNode.Parse(body!)!;
                if (headers.GetValueOrDefault("Cookie") != "INGRESSCOOKIE=server-1" || (string?)login["password"] != Code)
                    return Json("""{"message":"No loginAttempts found"}""", HttpStatusCode.Forbidden);
                return Json($$"""{"id":4711,"token":"{{Token}}"}""");
            }
            if (headers.GetValueOrDefault("x-authorization") != Token || headers.GetValueOrDefault("x-user") != "4711")
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            if (RefuseCommands && method == "POST" && path.StartsWith("/charging/", StringComparison.Ordinal))
                return Json("""{"message":"Charger is offline"}""", HttpStatusCode.BadRequest);

            switch ((method, path))
            {
                case ("GET", "/chargers/private"):
                    return Json("""[{"access":"owner","data":{"id":"CP1"},"type":"private"}]""");
                case ("GET", "/mychargers"):
                    return Json("""{"receivingAccess":[],"givingAccess":[]}""");
                case ("POST", "/chargepoints/get"):
                    return Json($$"""
                        {"id":"CP1","displayName":"Garage","currencyCode":"EUR","aliasMap":{"ABC123":{"id":"{{Connector}}","connector":1,
                          "status":"Available","power":0,"meterValue":{{Number(Meter)}},"maxProfileCurrent":16,"ampere":32,
                          "capabilities":{"ecoMode":true,"manualSchedules":true},"firmwareVersion":"1.2.3","model":"DEFA Power",
                          "vendor":"DEFA","hbTimeout":false,"errorCode":"NoError"}
                        }
                        }
                        """);
                case ("GET", "/connector/C1/operationaldata"):
                    return Json($$"""
                        {"id":"{{Connector}}","ocpp":{"chargingState":"{{ChargingState}}","status":"{{Status}}","version":"1.6"},
                         "meterValue":{{Number(Meter)}},"transactionMeterValue":{{(Transaction is double t ? Number(t) : "null")}},
                         "powerConsumption":{{Number(Power)}},"hbTimeout":false,"errorCode":"NoError"}
                        """);
                case ("POST", "/connector/C1/startliveconsumption"):
                case ("POST", "/charging/start"):
                case ("POST", "/charging/stop"):
                case ("POST", "/connector/C1/maxcurrent"):
                case ("POST", "/connector/C1/reset"):
                    return new HttpResponseMessage(HttpStatusCode.OK);
                case ("GET", "/connector/C1/maxcurrent/alternatives"):
                    return Json("""{"6":4.1,"10":6.9,"16":11.0}""");
                case ("GET", "/connector/C1/ecomode/configuration"):
                    return Json(_eco.ToJsonString());
                case ("PUT", "/connector/C1/ecomode/configuration"):
                    foreach (var (key, value) in JsonNode.Parse(body!)!.AsObject())
                        _eco[key] = value?.DeepClone();
                    return new HttpResponseMessage(HttpStatusCode.OK);
                case ("GET", "/connector/C1/manual-schedules"):
                    return Json("""{"schedulingEnabled":false,"scheduleOverridden":false,"schedules":[]}""");
                case ("PUT", "/connector/C1/manual-schedules/enable/true"):
                    return Json("""{"schedulingEnabled":true,"scheduleOverridden":false,"schedules":[]}""");
                case ("GET", "/connector/C1/schedule/active-settings"):
                    return Json("""{"active":["ECOMODE"],"pausedBy":"ECOMODE","pausedUntil":"2026-11-04T22:00:00","overrideSmartCharging":false}""");
                case ("PUT", "/connector/C1/schedule/override"):
                    return Json("""{"active":["ECOMODE"],"pausedBy":null,"pausedUntil":null,"overrideSmartCharging":true}""");
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class TestDefaPower(FakeCloudCharge api) : DefaPower(NullLogger.Instance)
    {
        internal override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            api.SendAsync(request, cancellationToken);
    }
}
