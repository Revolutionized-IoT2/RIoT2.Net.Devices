using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Core.Models;
using RIoT2.Net.Devices.Catalog;
using RIoT2.Net.Devices.Models;

namespace RIoT2.Net.Devices.Tests;

[TestClass]
public class HueEventTests
{
    [TestMethod]
    public async Task BrightnessOnlyEventDoesNotInventAnOnState()
    {
        var (device, reports) = await CreateDeviceAsync();

        Update(device, """{"id":"light-1","type":"light","dimming":{"brightness":42}}""");

        Assert.AreEqual(1, reports.Count);
        Assert.IsNull(reports[0].State);
        Assert.AreEqual(42d, reports[0].Dimming);
    }

    [TestMethod]
    public async Task PartialUpdatesPreserveKnownFieldsAndUseLatestBrightnessForColor()
    {
        var (device, reports) = await CreateDeviceAsync();
        Update(device, """{"id":"light-1","type":"light","on":{"on":true},"dimming":{"brightness":60},"color":{"xy":{"x":0.4,"y":0.3}}}""");
        Update(device, """{"id":"light-1","type":"light","dimming":{"brightness":42}}""");
        Update(device, """{"id":"light-1","type":"light","color":{"xy":{"x":0.3,"y":0.4}}}""");
        Update(device, """{"id":"light-1","type":"light","color_temperature":{"mirek":300}}""");
        Update(device, """{"id":"light-1","type":"light","on":{"on":false}}""");

        Assert.AreEqual(5, reports.Count);
        Assert.AreEqual(true, reports[1].State);
        Assert.AreEqual(42d, reports[1].Color!.Brightness);
        Assert.AreEqual(true, reports[2].State);
        Assert.AreEqual(42d, reports[2].Dimming);
        Assert.IsNotNull(reports[2].Hue);
        Assert.IsNotNull(reports[2].Saturation);
        Assert.AreEqual(300, reports[3].ColorTemperature);
        Assert.AreEqual(false, reports[4].State);
        Assert.AreEqual(42d, reports[4].Dimming);
        Assert.AreEqual(300, reports[4].ColorTemperature);
    }

    [TestMethod]
    public async Task ColorBeforeBrightnessIsRetainedWithoutInventingBrightness()
    {
        var (device, reports) = await CreateDeviceAsync();
        Update(device, """{"id":"light-1","type":"light","color":{"xy":{"x":0.3,"y":0.4}}}""");
        Assert.IsNull(reports[0].Color);
        Assert.IsNull(reports[0].Dimming);

        Update(device, """{"id":"light-1","type":"light","dimming":{"brightness":20}}""");

        Assert.IsNotNull(reports[1].Color);
        Assert.AreEqual(20d, reports[1].Color!.Brightness);
    }

    [TestMethod]
    public async Task ExplicitInvalidTemperatureDoesNotReportStaleMirek()
    {
        var (device, reports) = await CreateDeviceAsync();
        Update(device, """{"id":"light-1","type":"light","color_temperature":{"mirek":300,"mirek_valid":true}}""");
        Update(device, """{"id":"light-1","type":"light","color_temperature":{"mirek":300,"mirek_valid":false}}""");

        Assert.AreEqual(300, reports[0].ColorTemperature);
        Assert.IsNull(reports[1].ColorTemperature);
    }

    [TestMethod]
    public async Task MixedAndEmptyEventBatchesStillProcessEveryLight()
    {
        var (device, reports) = await CreateDeviceAsync();

        device.Hue_HueEventReceived("""
            data: [{"type":"update","data":[]},{"type":"update","data":null},{"type":"update","data":[{"id":"bridge","type":"bridge"},{"id":"light-1","type":"light","on":{"on":true}},null]}]
            """);

        Assert.AreEqual(1, reports.Count);
        Assert.AreEqual(true, reports[0].State);
    }

    [TestMethod]
    public async Task SnapshotsAreIsolatedByLightAndClearedOnReconfiguration()
    {
        var (device, reports) = await CreateDeviceAsync();
        Update(device, """{"id":"light-1","type":"light","on":{"on":true}}""");
        Update(device, """{"id":"light-2","type":"light","dimming":{"brightness":10}}""");
        Assert.IsNull(reports[1].State);

        await device.StopAsync(default);
        device.Initialize(Configuration());
        await device.StartAsync(default);
        Update(device, """{"id":"light-1","type":"light","dimming":{"brightness":30}}""");

        Assert.IsNull(reports[2].State);
        Assert.AreEqual(30d, reports[2].Dimming);
    }

    private static void Update(Hue device, string light) =>
        device.Hue_HueEventReceived("data: [{\"type\":\"update\",\"data\":[" + light + "]}]");

    private static async Task<(TestHue, List<ReportedLight>)> CreateDeviceAsync()
    {
        var device = new TestHue();
        var reports = new List<ReportedLight>();
        device.Initialize(Configuration());
        device.ReportUpdated += (_, report) => reports.Add(((Report)report).Value.GetValue<ReportedLight>()!);
        await device.StartAsync(default);
        return (device, reports);
    }

    private static DeviceConfiguration Configuration() => new()
    {
        Id = "hue",
        DeviceParameters = new() { ["bridgeIpAddress"] = "hue.invalid", ["apiKey"] = "key" },
        ReportTemplates =
        [
            new ReportTemplate { Id = "report-1", Address = "light-1" },
            new ReportTemplate { Id = "report-2", Address = "light-2" }
        ]
    };

    private sealed class TestHue() : Hue(NullLogger.Instance)
    {
        protected override Task StartDeviceAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        protected override Task StopDeviceAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class ReportedLight
    {
        public bool? State { get; set; }
        public double? Dimming { get; set; }
        public RGB? Color { get; set; }
        public double? Hue { get; set; }
        public double? Saturation { get; set; }
        public int? ColorTemperature { get; set; }
    }
}
