using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Core.Models;
using RIoT2.Net.Devices.Catalog;
using RIoT2.Net.Devices.Models;

namespace RIoT2.Net.Devices.Tests;

[TestClass]
public class ElectricityPriceTests
{
    [TestMethod]
    public void RefreshUsesConfiguredReportTemplatePrecision()
    {
        var device = new TestElectricityPrice();
        var reportTemplate = new ReportTemplate
        {
            Id = "price-report",
            Address = "price",
            Parameters = new Dictionary<string, string> { ["precision"] = "1" }
        };
        device.Initialize(new DeviceConfiguration
        {
            Id = "electricity",
            DeviceParameters = [],
            ReportTemplates = [reportTemplate]
        });
        device.Start();
        typeof(ElectricityPrice)
            .GetField("_priceData", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(device, ValidPriceData(123.456m));

        Report published = null;
        device.ReportUpdated += (_, report) => published = (Report)report;

        device.Refresh(null);

        Assert.IsNotNull(published);
        Assert.AreEqual(12.3m, published.Value.GetValue<decimal>());
    }

    private static Publication_MarketDocument ValidPriceData(decimal price) => new()
    {
        TimeSeries = new Publication_MarketDocumentTimeSeries
        {
            Period = new Publication_MarketDocumentTimeSeriesPeriod
            {
                resolution = "PT60M",
                timeInterval = new Publication_MarketDocumentTimeSeriesPeriodTimeInterval
                {
                    start = DateTime.Now.Date.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    end = DateTime.Now.Date.AddDays(1).ToString("yyyy-MM-ddTHH:mm:ssZ")
                },
                Point = Enumerable.Range(1, 24)
                    .Select(position => new Publication_MarketDocumentTimeSeriesPeriodPoint
                    {
                        position = position,
                        priceamount = price
                    })
                    .ToArray()
            }
        }
    };

    private sealed class TestElectricityPrice() : ElectricityPrice(NullLogger.Instance)
    {
        public override void StartDevice() { }
        public override void StopDevice() { }
    }
}
