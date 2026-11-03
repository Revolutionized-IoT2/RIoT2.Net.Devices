using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Core.Models;
using RIoT2.Net.Devices.Catalog;

namespace RIoT2.Net.Devices.Tests;

[TestClass]
public class VirtualTests
{
    [TestMethod]
    [DataRow("\"Living room\"")]
    [DataRow("42.5")]
    [DataRow("true")]
    [DataRow("{\"Temperature\":21}")]
    public async Task CommandIsReportedWithTheCommandedValueAndType(string json)
    {
        var device = new Virtual(NullLogger.Instance);
        device.Initialize(new DeviceConfiguration
        {
            Id = "virtual",
            DeviceParameters = [],
            CommandTemplates = [new CommandTemplate { Id = "set", Address = "value" }],
            ReportTemplates = [new ReportTemplate { Id = "state", Address = "value" }]
        });
        await device.StartAsync(CancellationToken.None);
        Report? published = null;
        device.ReportUpdated += (_, report) => published = (Report)report;

        // CommandService passes the command value to the device serialized as JSON.
        var commanded = new ValueModel(json, enforceCamelCase: false);
        await device.ExecuteCommandAsync("set", commanded.ToJson(), CancellationToken.None);

        Assert.IsNotNull(published);
        Assert.AreEqual("state", published.Id);
        Assert.AreEqual(commanded.Type, published.Value.Type);
        Assert.AreEqual(commanded.ToJson(), published.Value.ToJson());
    }

    [TestMethod]
    public async Task TimerReportsTheTemplatesAddressWhenItsScheduleFires()
    {
        var device = new Catalog.Timer(NullLogger.Instance);
        device.Initialize(new DeviceConfiguration
        {
            Id = "timer",
            DeviceParameters = [],
            RefreshSchedule = "0 * * ? * *",
            ReportTemplates = [new ReportTemplate { Id = "morning", Address = "wake up" }]
        });
        Assert.IsNull(device.Configuration.RefreshSchedule, "the report templates' schedules drive the timer");
        await device.StartAsync(CancellationToken.None);
        Report? published = null;
        device.ReportUpdated += (_, report) => published = (Report)report;

        await device.RefreshReportAsync("timer", "morning", CancellationToken.None);

        Assert.IsNotNull(published);
        Assert.AreEqual(("morning", "wake up"), (published.Id, published.Value.GetValue<string>()));
        await device.StopAsync(CancellationToken.None);
    }
}
