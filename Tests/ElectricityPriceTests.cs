using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Core.Models;
using RIoT2.Net.Devices.Catalog;

namespace RIoT2.Net.Devices.Tests;

[TestClass]
public class ElectricityPriceTests
{
    // 4 November 2026: the CET delivery day starts at 23:00 UTC the day before.
    private static readonly DateTimeOffset DayStart = new(2026, 11, 3, 23, 0, 0, TimeSpan.Zero);

    private static TimeZoneInfo Helsinki => TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "FLE Standard Time" : "Europe/Helsinki");

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void ACurveA03RepeatsThePreviousPriceForAMissingPosition()
    {
        var xml = Document(Period(DayStart, DayStart.AddHours(1), "PT15M", "A03", (1, 10m), (3, 30m)));
        var slots = EntsoePrices.Parse(xml);
        CollectionAssert.AreEqual(new[] { 10m, 10m, 30m, 30m }, slots.Select(x => x.Price).ToArray());
        Assert.AreEqual(DayStart.AddMinutes(15), slots[1].Start);
        Assert.AreEqual(DayStart.AddHours(1), slots[3].End);
    }

    [TestMethod]
    public void ACurveA01LeavesAMissingPositionOut()
    {
        var xml = Document(Period(DayStart, DayStart.AddHours(1), "PT15M", "A01", (1, 10m), (3, 30m)));
        CollectionAssert.AreEqual(new[] { 10m, 30m }, EntsoePrices.Parse(xml).Select(x => x.Price).ToArray());
    }

    [TestMethod]
    public void TheFinerResolutionWinsWhereSeriesOverlapAndDaysFollowEachOther()
    {
        var xml = Document(
            Period(DayStart, DayStart.AddHours(2), "PT60M", "A01", (1, 100m), (2, 200m)),
            Period(DayStart, DayStart.AddHours(1), "PT15M", "A01", (1, 1m), (2, 2m), (3, 3m), (4, 4m)),
            Period(DayStart.AddDays(1), DayStart.AddDays(1).AddHours(1), "PT60M", "A01", (1, -5.5m)));
        var slots = EntsoePrices.Parse(xml);
        CollectionAssert.AreEqual(new[] { 1m, 2m, 3m, 4m, 200m, -5.5m }, slots.Select(x => x.Price).ToArray());
        Assert.AreEqual(DayStart.AddDays(1), slots[^1].Start);
    }

    [TestMethod]
    public void AnAcknowledgementIsNoPrices()
    {
        Assert.AreEqual("No matching data found", EntsoePrices.Acknowledgement(
            """<Acknowledgement_MarketDocument xmlns="urn:iec62325.351:tc57wg16:451-1:acknowledgementdocument:7:0"><Reason><code>999</code><text>No matching data found</text></Reason></Acknowledgement_MarketDocument>"""));
        Assert.IsNull(EntsoePrices.Acknowledgement(Document()));
    }

    [TestMethod]
    public async Task TheCurrentPriceHasVatAndTheTemplatePrecision()
    {
        var (device, http, reports) = Create(DayStart.AddHours(13).AddMinutes(30), vat: "25.5");
        http.Enqueue(Document(Hours(DayStart, 24, 123.456m)));
        await device.StartAsync(default);

        var price = reports.Last(x => x.Id == "price-report");
        // 123.456 EUR/MWh * 1.255 / 10 = 15.493728 c/kWh, one decimal.
        Assert.AreEqual(15.5m, price.Value.GetValue<decimal>());
    }

    [TestMethod]
    public async Task TheCurrentSlotIsTheSameAsTheOldPositionFormulaInFinnishWinterTime()
    {
        // Before 2.1.0 the price was position ceil(((hour - 1) * 60 + minute + 1) / resolution) of the day, in local time.
        foreach (var resolution in new[] { 15, 60 })
            foreach (var minutes in new[] { 75, 14 * 60 + 30, 20 * 60 + 59, 23 * 60 + 44 })
            {
                var local = new DateTime(2026, 11, 4, 0, 0, 0).AddMinutes(minutes);
                var now = new DateTimeOffset(local, TimeSpan.FromHours(2)).ToUniversalTime();
                var oldPosition = (int)Math.Ceiling((double)(Math.Max(local.Hour - 1, 0) * 60 + local.Minute + 1) / resolution);

                var (device, http, reports) = Create(now, precision: "-1");
                var count = 24 * 60 / resolution;
                http.Enqueue(Document(Period(DayStart, DayStart.AddDays(1), $"PT{resolution}M", "A01",
                    Enumerable.Range(1, count).Select(p => (p, (decimal)p * 10)).ToArray())));
                await device.StartAsync(default);

                Assert.AreEqual(oldPosition, reports.Last(x => x.Id == "price-report").Value.GetValue<decimal>(), $"{local:HH:mm} PT{resolution}M");
            }
    }

    [TestMethod]
    public async Task ThePricesSeriesStartsAtLocalMidnightAndIsSentWhenItChanges()
    {
        var now = DayStart.AddHours(10);
        var (device, http, reports) = Create(now);
        // Yesterday (CET), today and nothing for tomorrow yet.
        http.Enqueue(Document(Hours(DayStart.AddDays(-1), 24, 50m), Hours(DayStart, 24, 100m)));
        await device.StartAsync(default);

        var series = ForecastSeries.From(reports.Single(x => x.Id == "prices-report").Value)!;
        Assert.IsNull(series.Validate());
        Assert.AreEqual(ForecastSeries.ConfirmedKind, series.Kind);
        Assert.AreEqual("c/kWh", series.Unit);
        Assert.AreEqual("PT1H", series.Resolution);
        // Helsinki's day starts at 22:00 UTC: the last hour of yesterday's CET day is in it.
        Assert.AreEqual(new DateTimeOffset(2026, 11, 3, 22, 0, 0, TimeSpan.Zero), series.Points[0].Start);
        Assert.AreEqual(5.0, series.Points[0].Value);
        Assert.AreEqual(10.0, series.Points[1].Value);
        Assert.HasCount(25, series.Points);

        // Nothing new on the next refresh: no report.
        await device.RefreshReportAsync(device.Id, null!, default);
        Assert.AreEqual(1, reports.Count(x => x.Id == "prices-report"));
        Assert.AreEqual(2, reports.Count(x => x.Id == "price-report"));
        Assert.AreEqual(1, http.Requests.Count, "today's prices last more than 13 hours");
    }

    [TestMethod]
    public async Task TomorrowsPricesAreAskedForWhenDueAtMostEveryHalfHour()
    {
        var time = new ManualTime(DayStart.AddHours(12)); // 11:00 UTC, 12 hours of prices left
        var (device, http, reports) = Create(time);
        http.Enqueue(Document(Hours(DayStart, 24, 100m)));
        await device.StartAsync(default);
        Assert.AreEqual(1, http.Requests.Count);

        // Not published yet: ENTSO-E answers with today only. The device keeps today and asks again later.
        time.Now = time.Now.AddMinutes(30);
        http.Enqueue(Document(Hours(DayStart, 24, 100m)));
        await device.RefreshReportAsync(device.Id, null!, default);
        Assert.AreEqual(2, http.Requests.Count);

        time.Now = time.Now.AddMinutes(15);
        await device.RefreshReportAsync(device.Id, null!, default);
        Assert.AreEqual(2, http.Requests.Count, "asked less than 30 minutes ago");

        time.Now = time.Now.AddMinutes(15);
        http.Enqueue(Document(Hours(DayStart, 24, 100m), Hours(DayStart.AddDays(1), 24, 200m)));
        await device.RefreshReportAsync(device.Id, null!, default);
        Assert.AreEqual(3, http.Requests.Count);
        var series = ForecastSeries.From(reports.Last(x => x.Id == "prices-report").Value)!;
        Assert.AreEqual(2, reports.Count(x => x.Id == "prices-report"));
        Assert.AreEqual(20.0, series.Points[^1].Value);
        StringAssert.Contains(http.Requests.Last().RequestUri!.Query, "periodStart=202611030000&periodEnd=202611060000");

        // A failed download keeps the prices.
        time.Now = DayStart.AddDays(1).AddHours(12);
        http.Fail();
        await device.RefreshReportAsync(device.Id, null!, default);
        Assert.AreEqual(20m, reports.Last(x => x.Id == "price-report").Value.GetValue<decimal>());
    }

    [TestMethod]
    public async Task ThePricesAreKeptBetweenStarts()
    {
        var (device, http, _) = Create(DayStart.AddHours(5));
        http.Enqueue(Document(Hours(DayStart, 24, 100m), Hours(DayStart.AddDays(1), 24, 200m)));
        await device.StartAsync(default);
        await device.StopAsync(default);

        var (again, http2, reports) = Create(DayStart.AddHours(6), dataFile: device.DataFile);
        await again.StartAsync(default);
        Assert.AreEqual(0, http2.Requests.Count);
        Assert.AreEqual(10m, reports.Single(x => x.Id == "price-report").Value.GetValue<decimal>());
    }

    [TestMethod]
    public async Task NoPricesAtAllIsAnError()
    {
        var (device, http, _) = Create(DayStart.AddHours(5));
        http.Enqueue("""<Acknowledgement_MarketDocument><Reason><text>No matching data found</text></Reason></Acknowledgement_MarketDocument>""");
        await Assert.ThrowsAsync<Exception>(() => device.StartAsync(default));
        Assert.AreEqual(Core.DeviceState.Error, device.State);
    }

    [TestMethod]
    public void TheTemplateDeclaresThePricesSeries()
    {
        var template = new ElectricityPrice(NullLogger.Instance).GetConfigurationTemplate();
        var prices = template.ReportTemplates.Single(x => x.Address == "prices");
        Assert.AreEqual(ValueSemantics.Forecast, prices.Schema!.Semantic);
        Assert.AreEqual("c/kWh", prices.Schema.Unit);
        Assert.IsNotNull(template.ReportTemplates.Single(x => x.Address == "price"));
    }

    private (TestElectricityPrice Device, FakeHttp Http, List<Report> Reports) Create(DateTimeOffset now, string vat = "0", string precision = "1", string? dataFile = null) =>
        Create(new ManualTime(now), vat, precision, dataFile);

    private (TestElectricityPrice Device, FakeHttp Http, List<Report> Reports) Create(ManualTime time, string vat = "0", string precision = "1", string? dataFile = null)
    {
        var http = new FakeHttp();
        var device = new TestElectricityPrice(http)
        {
            Time = time,
            Zone = Helsinki,
            DataFile = dataFile ?? Path.Combine(TestContext.TestRunResultsDirectory ?? Path.GetTempPath(), Guid.NewGuid().ToString("N"), "priceData.json")
        };
        device.Initialize(new DeviceConfiguration
        {
            Id = "electricity",
            DeviceParameters = new() { ["securityToken"] = "t", ["endpoint"] = "https://prices.invalid/api", ["domain"] = "zone", ["vat"] = vat },
            ReportTemplates =
            [
                new ReportTemplate { Id = "price-report", Address = "price", Parameters = new() { ["precision"] = precision } },
                new ReportTemplate { Id = "prices-report", Address = "prices", Parameters = new() { ["precision"] = "1", ["unit"] = "c/kWh" } }
            ]
        });
        var reports = new List<Report>();
        device.ReportUpdated += (_, report) => reports.Add((Report)report);
        return (device, http, reports);
    }

    private static string Hours(DateTimeOffset start, int hours, decimal price) =>
        Period(start, start.AddHours(hours), "PT60M", "A01", Enumerable.Range(1, hours).Select(p => (p, price)).ToArray());

    private static string Period(DateTimeOffset start, DateTimeOffset end, string resolution, string curve, params (int Position, decimal Price)[] points) =>
        $"""
        <TimeSeries><mRID>1</mRID><curveType>{curve}</curveType>
          <Period><timeInterval><start>{start.UtcDateTime:yyyy-MM-ddTHH:mmZ}</start><end>{end.UtcDateTime:yyyy-MM-ddTHH:mmZ}</end></timeInterval>
            <resolution>{resolution}</resolution>
            {string.Concat(points.Select(p => $"<Point><position>{p.Position}</position><price.amount>{p.Price.ToString(System.Globalization.CultureInfo.InvariantCulture)}</price.amount></Point>"))}
          </Period>
        </TimeSeries>
        """;

    private static string Document(params string[] series) =>
        $"""<?xml version="1.0" encoding="utf-8"?><Publication_MarketDocument xmlns="urn:iec62325.351:tc57wg16:451-3:publicationdocument:7:3"><mRID>x</mRID><type>A44</type>{string.Concat(series)}</Publication_MarketDocument>""";

    internal sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal sealed class FakeHttp
    {
        private readonly Queue<string?> _answers = new();

        public List<HttpRequestMessage> Requests { get; } = [];

        public void Enqueue(string body) => _answers.Enqueue(body);

        public void Fail() => _answers.Enqueue(null);

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var body = _answers.Count > 0 ? _answers.Dequeue() : null;
            if (body == null)
                throw new HttpRequestException("offline");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    internal sealed class TestElectricityPrice(FakeHttp http) : ElectricityPrice(NullLogger.Instance)
    {
        internal override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            http.SendAsync(request, cancellationToken);
    }
}
