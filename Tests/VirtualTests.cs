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
    public void CommandIsReportedWithTheCommandedValueAndType(string json)
    {
        var device = new Virtual(NullLogger.Instance);
        device.Initialize(new DeviceConfiguration
        {
            Id = "virtual",
            DeviceParameters = [],
            CommandTemplates = [new CommandTemplate { Id = "set", Address = "value" }],
            ReportTemplates = [new ReportTemplate { Id = "state", Address = "value" }]
        });
        device.Start();
        Report published = null;
        device.ReportUpdated += (_, report) => published = (Report)report;

        // CommandService passes the command value to the device serialized as JSON.
        var commanded = new ValueModel(json, enforceCamelCase: false);
        device.ExecuteCommand("set", commanded.ToJson());

        Assert.IsNotNull(published);
        Assert.AreEqual("state", published.Id);
        Assert.AreEqual(commanded.Type, published.Value.Type);
        Assert.AreEqual(commanded.ToJson(), published.Value.ToJson());
    }
}
