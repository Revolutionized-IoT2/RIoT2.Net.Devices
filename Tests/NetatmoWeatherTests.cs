using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Core;
using RIoT2.Core.Models;
using RIoT2.Core.Plugins;
using static RIoT2.Net.Devices.Tests.NetatmoFixtures;

namespace RIoT2.Net.Devices.Tests;

/// <summary>
/// The Netatmo Weather device (3.0.0): each module reports what it measures, with its battery, reachability and
/// signal, stamped with the time of the measurement; an upload already reported isn't reported again.
/// </summary>
[TestClass]
public class NetatmoWeatherTests
{
    private const string Main = "70:ee:50:00:00:01";
    private const string Outdoor = "02:00:00:00:00:02";
    private const string Wind = "03:00:00:00:00:03";
    private const string Rain = "05:00:00:00:00:05";
    private const string Coach = "70:ee:50:00:00:99";

    private NetatmoTestKit _kit = null!;

    [TestInitialize]
    public void Initialize() => _kit = new NetatmoTestKit();

    [TestCleanup]
    public void Cleanup() => _kit.Dispose();

    [TestMethod]
    public async Task TemplatesListWhatEachModuleMeasuresAndItsHealth()
    {
        _kit.Api.Route("GET api/getstationsdata", Stations());
        var device = _kit.Weather();
        device.Initialize(Configuration(Parameters()));
        await device.StartAsync(CancellationToken.None);
        var calls = _kit.Api.Calls.Count;

        var template = DeviceTemplates.Create(device);
        var addresses = template.ReportTemplates.Select(r => r.Address).ToList();

        Assert.AreEqual(calls, _kit.Api.Calls.Count, "the template comes from the data the last refresh read");
        CollectionAssert.IsSubsetOf(new[] { $"{Main}|CO2", $"{Main}|Noise", $"{Main}|pressure_trend", $"{Main}|signal", $"{Main}|reachable",
            $"{Outdoor}|Temperature", $"{Outdoor}|max_temp", $"{Outdoor}|Humidity", $"{Outdoor}|battery", $"{Outdoor}|signal",
            $"{Wind}|WindStrength", $"{Wind}|GustAngle", $"{Wind}|max_wind_str", $"{Rain}|sum_rain_24", $"{Rain}|reachable" }, addresses);
        Assert.IsFalse(addresses.Contains($"{Outdoor}|CO2"), "an outdoor module doesn't measure CO2");
        Assert.IsFalse(addresses.Contains($"{Outdoor}|Noise"));
        Assert.IsFalse(addresses.Contains($"{Main}|battery"), "the main module has no battery");
        Assert.AreEqual(Core.ValueType.Text, template.ReportTemplates.Single(r => r.Address == $"{Outdoor}|temp_trend").Type);
        Assert.AreEqual(Core.ValueType.Number, template.ReportTemplates.Single(r => r.Address == $"{Wind}|WindAngle").Type);
        Assert.AreEqual("Outdoor Temperature", template.ReportTemplates.Single(r => r.Address == $"{Outdoor}|Temperature").Name);
        Assert.AreEqual("0 0/5 * ? * * *", template.RefreshSchedule);
        Assert.AreEqual("Netatmo", template.Authorization!.Provider);
    }

    [TestMethod]
    public async Task MeasurementsCarryTheirTimeAndAnUploadIsReportedOnce()
    {
        _kit.Api.Route("GET api/getstationsdata", Stations(time: 1793800000, outdoorTemperature: 4.2));
        var device = _kit.Weather();
        device.Initialize(Configuration(Parameters(),
            [$"{Outdoor}|Temperature", $"{Outdoor}|max_temp", $"{Wind}|GustStrength", $"{Rain}|reachable", $"{Main}|signal", $"{Outdoor}|battery", $"{Main}|temp_trend"]));
        var reports = _kit.Capture(device);
        await device.StartAsync(CancellationToken.None);

        var outdoor = reports.At($"{Outdoor}|Temperature")!;
        Assert.AreEqual((4.2, 1793799950L), (outdoor.Value.GetValue<double>(), outdoor.TimeStamp));
        Assert.IsNull(reports.At($"{Outdoor}|max_temp"), "a value Netatmo didn't send isn't reported as 0");
        Assert.AreEqual(20, reports.At($"{Wind}|GustStrength")!.Value.GetValue<int>());
        Assert.IsFalse(reports.At($"{Rain}|reachable")!.Value.GetValue<bool>());
        Assert.AreEqual(56, reports.At($"{Main}|signal")!.Value.GetValue<int>());
        Assert.AreEqual(80, reports.At($"{Outdoor}|battery")!.Value.GetValue<int>());
        Assert.AreEqual("stable", reports.At($"{Main}|temp_trend")!.Value.GetValue<string>());

        // The same upload again, even with another value, isn't reported.
        _kit.Api.Route("GET api/getstationsdata", Stations(time: 1793800000, outdoorTemperature: 9.9));
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);
        Assert.HasCount(1, reports.Where(r => r.Id == $"{Outdoor}|Temperature").ToList());

        _kit.Api.Route("GET api/getstationsdata", Stations(time: 1793800600, outdoorTemperature: 5.0));
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);
        Assert.AreEqual((5.0, 1793800550L), (reports.At($"{Outdoor}|Temperature")!.Value.GetValue<double>(), reports.At($"{Outdoor}|Temperature")!.TimeStamp));
    }

    [TestMethod]
    public async Task TheStationIdNarrowsTheCallAndAnEmptyOneReadsEveryStation()
    {
        _kit.Api.Route("GET api/getstationsdata", Stations());
        var one = _kit.Weather();
        one.Initialize(Configuration(Parameters(("stationId", Main))));
        await one.StartAsync(CancellationToken.None);
        var all = _kit.Weather();
        all.Initialize(Configuration(Parameters()));
        await all.StartAsync(CancellationToken.None);

        var calls = _kit.Api.Calls.Where(c => c.Key == "GET api/getstationsdata").ToList();
        Assert.AreEqual((Main, (string?)null), (calls[0].Query("device_id"), calls[1].Query("device_id")));
        Assert.AreEqual("user@example.com", all.GetAuthorizationStatus().Account);
    }

    [TestMethod]
    public async Task HomeCoachesAreReportedWhenIncluded()
    {
        _kit.Api.Route("GET api/getstationsdata", Stations());
        _kit.Api.Route("GET api/gethomecoachsdata", Coaches);
        var device = _kit.Weather();
        device.Initialize(Configuration(Parameters(("includeHomeCoach", "true")), [$"{Coach}|health_idx", $"{Coach}|CO2", $"{Coach}|signal"]));
        var reports = _kit.Capture(device);
        await device.StartAsync(CancellationToken.None);

        Assert.AreEqual(2, reports.At($"{Coach}|health_idx")!.Value.GetValue<int>());
        Assert.AreEqual(900, reports.At($"{Coach}|CO2")!.Value.GetValue<int>());
        Assert.AreEqual(60, reports.At($"{Coach}|signal")!.Value.GetValue<int>());
        CollectionAssert.Contains(DeviceTemplates.Create(device).ReportTemplates.Select(r => r.Address).ToList(), $"{Coach}|health_idx");

        var without = _kit.Weather();
        without.Initialize(Configuration(Parameters()));
        await without.StartAsync(CancellationToken.None);
        Assert.AreEqual(1, _kit.Api.Count("GET api/gethomecoachsdata"));
    }

    [TestMethod]
    public async Task AnUnavailableNetatmoKeepsTheDeviceRunning()
    {
        _kit.Api.Routes["GET api/getstationsdata"] = _ => FakeNetatmo.Json("<html>Bad gateway</html>", System.Net.HttpStatusCode.BadGateway);
        var device = _kit.Weather();
        device.Initialize(Configuration(Parameters()));
        await device.StartAsync(CancellationToken.None);

        Assert.AreEqual(DeviceState.Running, device.State);
        StringAssert.Contains(device.StateMessage, "502");

        _kit.Api.Route("GET api/getstationsdata", Stations());
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);
        Assert.AreEqual((DeviceState.Running, ""), (device.State, device.StateMessage));
    }
}
