using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Core;
using RIoT2.Core.Models;
using RIoT2.Net.Devices.Catalog;

namespace RIoT2.Net.Devices.Tests;

[TestClass]
public class WeatherForecastTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 9, 20, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Midnight = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task OpenMeteoGivesAnHourlySeriesPerReportFromTheCurrentHour()
    {
        var (device, http, reports) = Create(new() { ["provider"] = "openMeteo", ["latitude"] = "60.17", ["longitude"] = "24.94", ["forecastHours"] = "3" },
            "temperature", "precipitation", "humidity");
        http.Answers.Enqueue(OpenMeteo(12));
        await device.StartAsync(default);

        Assert.AreEqual(DeviceState.Running, device.State);
        var url = http.Requests.Single().RequestUri!.ToString();
        StringAssert.StartsWith(url, "https://api.open-meteo.com/v1/forecast?latitude=60.17&longitude=24.94&hourly=temperature_2m,precipitation,relative_humidity_2m");
        StringAssert.Contains(url, "timeformat=unixtime");
        StringAssert.Contains(url, "wind_speed_unit=ms");

        var temperature = Series(reports, "temperature");
        Assert.IsNull(temperature.Validate());
        Assert.AreEqual(ForecastSeries.ForecastKind, temperature.Kind);
        Assert.AreEqual("°C", temperature.Unit);
        Assert.AreEqual(Now, temperature.IssuedAt);
        // From 09:00 until 12:20: 09, 10, 11 and 12.
        CollectionAssert.AreEqual(new double?[] { 9, 10, 11, 12 }, temperature.Points.Select(x => x.Value).ToList());
        Assert.AreEqual(Midnight.AddHours(9), temperature.Points[0].Start);

        // Precipitation at 10:00 is the hour before it.
        var precipitation = Series(reports, "precipitation");
        Assert.AreEqual(Midnight.AddHours(9), precipitation.Points.Single(x => x.Value == 1.0).Start);
        Assert.IsNull(Series(reports, "humidity").Points[0].Value, "a null in the response is a point without a value");
    }

    [TestMethod]
    public async Task AnUnchangedForecastIsNotSentAgain()
    {
        var (device, http, reports) = Create(new() { ["latitude"] = "60.17", ["longitude"] = "24.94", ["forecastHours"] = "5" }, "temperature");
        http.Answers.Enqueue(OpenMeteo(12));
        http.Answers.Enqueue(OpenMeteo(12));
        http.Answers.Enqueue(OpenMeteo(13));
        await device.StartAsync(default);
        await device.RefreshReportAsync(device.Id, null!, default);
        Assert.HasCount(1, reports);
        await device.RefreshReportAsync(device.Id, null!, default);
        Assert.HasCount(2, reports);
    }

    [TestMethod]
    public async Task FmiReadsTheEditedForecastAndHarmonieForRadiation()
    {
        var (device, http, reports) = Create(new() { ["provider"] = "fmi", ["fmiPlace"] = "helsinki", ["forecastHours"] = "2" },
            "temperature", "precipitation", "solarRadiation");
        http.Answers.Enqueue(Fmi(("Temperature", 9, "4.02"), ("Precipitation1h", 10, "0.5"), ("Temperature", 10, "NaN"), ("Temperature", 11, "6.28")));
        http.Answers.Enqueue(Fmi(("RadiationGlobal", 9, "9.8"), ("RadiationGlobal", 10, "22")));
        await device.StartAsync(default);

        var urls = http.Requests.Select(x => x.RequestUri!.ToString()).ToList();
        Assert.HasCount(2, urls);
        StringAssert.Contains(urls[0], "storedquery_id=fmi::forecast::edited::weather::scandinavia::point::simple&place=helsinki&parameters=Temperature,Precipitation1h");
        StringAssert.Contains(urls[0], "starttime=2026-11-04T09:00:00Z&endtime=2026-11-04T11:20:00Z");
        StringAssert.Contains(urls[1], "storedquery_id=fmi::forecast::harmonie::surface::point::simple&place=helsinki&parameters=RadiationGlobal");

        var temperature = Series(reports, "temperature");
        CollectionAssert.AreEqual(new double?[] { 4.02, null, 6.28 }, temperature.Points.Select(x => x.Value).ToList());
        Assert.AreEqual(Midnight.AddHours(9), Series(reports, "precipitation").Points.Single().Start);
        Assert.AreEqual("W/m²", Series(reports, "solarRadiation").Unit);
    }

    [TestMethod]
    public async Task FmiUsesTheCoordinatesWithoutAPlace()
    {
        var (device, http, _) = Create(new() { ["provider"] = "fmi", ["latitude"] = "60.17", ["longitude"] = "24.94" }, "temperature");
        http.Answers.Enqueue(Fmi(("Temperature", 9, "4")));
        await device.StartAsync(default);
        StringAssert.Contains(http.Requests.Single().RequestUri!.ToString(), "&latlon=60.17,24.94&");
    }

    [TestMethod]
    [DataRow("openMeteo", "", "", "", "48", "latitude and longitude")]
    [DataRow("fmi", "", "", "", "48", "fmiPlace")]
    [DataRow("yr", "60", "24", "", "48", "provider")]
    [DataRow("openMeteo", "91", "24", "", "48", "latitude")]
    [DataRow("openMeteo", "60", "east", "", "48", "longitude")]
    [DataRow("openMeteo", "60", "24", "", "241", "hours")]
    public async Task BadParametersStopTheStart(string provider, string latitude, string longitude, string place, string hours, string problem)
    {
        var (device, http, _) = Create(new() { ["provider"] = provider, ["latitude"] = latitude, ["longitude"] = longitude, ["fmiPlace"] = place, ["forecastHours"] = hours }, "temperature");
        await Assert.ThrowsAsync<Exception>(() => device.StartAsync(default));
        Assert.AreEqual(DeviceState.Error, device.State);
        StringAssert.Contains(device.StateMessage, problem);
        Assert.IsEmpty(http.Requests);
    }

    [TestMethod]
    public async Task AFailedRequestIsAnError()
    {
        var (device, http, _) = Create(new() { ["latitude"] = "60", ["longitude"] = "24" }, "temperature");
        http.Answers.Enqueue("""{"error":true,"reason":"Latitude must be in range"}""");
        http.Status = HttpStatusCode.BadRequest;
        await Assert.ThrowsAsync<Exception>(() => device.StartAsync(default));
        Assert.AreEqual(DeviceState.Error, device.State);
    }

    [TestMethod]
    public void TheTemplateDeclaresEveryForecast()
    {
        var template = new WeatherForecast(NullLogger.Instance).GetConfigurationTemplate();
        CollectionAssert.AreEqual(new[] { "temperature", "precipitation", "windSpeed", "cloudCover", "solarRadiation", "humidity" },
            template.ReportTemplates.Select(x => x.Address).ToArray());
        foreach (var report in template.ReportTemplates)
            Assert.AreEqual(ValueSemantics.Forecast, report.Schema!.Semantic, report.Address);
        Assert.AreEqual(ValueSemantics.Temperature, template.ReportTemplates.First().Schema!.Properties!["points"].Items!.Properties!["value"].Semantic);
    }

    private static ForecastSeries Series(List<Report> reports, string id) => ForecastSeries.From(reports.Last(x => x.Id == id).Value)!;

    private static (TestWeatherForecast Device, FakeHttp Http, List<Report> Reports) Create(Dictionary<string, string> parameters, params string[] addresses)
    {
        var http = new FakeHttp();
        var device = new TestWeatherForecast(http) { Time = new ElectricityPriceTests.ManualTime(Now) };
        device.Initialize(new DeviceConfiguration
        {
            Id = "weather",
            DeviceParameters = parameters,
            ReportTemplates = addresses.Select(x => new ReportTemplate { Id = x, Address = x }).ToList()
        });
        var reports = new List<Report>();
        device.ReportUpdated += (_, report) => reports.Add((Report)report);
        return (device, http, reports);
    }

    // Values: the hour of day for temperature, 1.0 at 10:00 for precipitation, null humidity at 09:00.
    private static string OpenMeteo(int lastHour)
    {
        var hours = Enumerable.Range(0, lastHour + 1).ToList();
        var times = string.Join(",", hours.Select(h => Midnight.AddHours(h).ToUnixTimeSeconds()));
        var temperature = string.Join(",", hours.Select(h => h.ToString()));
        var precipitation = string.Join(",", hours.Select(h => h == 10 ? "1.0" : "0.0"));
        var humidity = string.Join(",", hours.Select(h => h == 9 ? "null" : "80"));
        return $$$"""{"hourly_units":{"time":"unixtime"},"hourly":{"time":[{{{times}}}],"temperature_2m":[{{{temperature}}}],"precipitation":[{{{precipitation}}}],"relative_humidity_2m":[{{{humidity}}}]}}""";
    }

    private static string Fmi(params (string Parameter, int Hour, string Value)[] values)
    {
        var members = values.Select(v =>
            "<wfs:member><BsWfs:BsWfsElement><BsWfs:Time>" + Midnight.AddHours(v.Hour).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ") + "</BsWfs:Time>" +
            "<BsWfs:ParameterName>" + v.Parameter + "</BsWfs:ParameterName><BsWfs:ParameterValue>" + v.Value + "</BsWfs:ParameterValue>" +
            "</BsWfs:BsWfsElement></wfs:member>");
        return "<wfs:FeatureCollection xmlns:wfs=\"http://www.opengis.net/wfs/2.0\" xmlns:BsWfs=\"http://xml.fmi.fi/schema/wfs/2.0\">" +
            string.Concat(members) + "</wfs:FeatureCollection>";
    }

    private sealed class FakeHttp
    {
        public Queue<string> Answers { get; } = new();
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public List<HttpRequestMessage> Requests { get; } = [];

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Answers.Dequeue()) });
        }
    }

    private sealed class TestWeatherForecast(FakeHttp http) : WeatherForecast(NullLogger.Instance)
    {
        internal override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            http.SendAsync(request, cancellationToken);
    }
}
