using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Web;
using Microsoft.Extensions.Logging.Abstractions;
using RIoT2.Core;
using RIoT2.Core.Models;
using RIoT2.Net.Devices.Catalog;
using RIoT2.Net.Devices.Services;
using RIoT2.Net.Devices.Services.Interfaces;
using RIoT2.Net.Devices.Services.Netatmo;

namespace RIoT2.Net.Devices.Tests;

/// <summary>Synthetic Netatmo payloads in the shapes of the Connect API; the ids and addresses are made up.</summary>
internal static class NetatmoFixtures
{
    public const string HomeId = "5f0000000000000000000a01";
    public const string OtherHomeId = "5f0000000000000000000b02";
    public const string IndoorCamera = "70:ee:50:00:00:10";
    public const string OutdoorCamera = "70:ee:50:00:00:11";
    public const string Doorbell = "70:ee:50:00:00:12";
    public const string DoorTag = "12:00:00:00:00:13";
    public const string SmokeAlarm = "70:ee:50:00:00:14";
    public const string Alice = "person-alice";
    public const string Unnamed = "person-unnamed";
    public const string Secret = "s3cr3t-0123456789-abcdefghij-KLMNOP";
    public const string WebhookBase = "https://netatmo.example.com/";
    public const string WebhookUrl = "https://netatmo.example.com/api/netatmo/webhook/" + Secret;
    public const string Snapshot = "https://snapshots.example.com/evt-1.jpg";

    public static readonly string HomesData = $$"""
        {"status":"ok","body":{"homes":[
          {"id":"{{OtherHomeId}}","name":"Cottage","modules":[{"id":"70:ee:50:00:00:01","type":"NAMain","name":"Station"}]},
          {"id":"{{HomeId}}","name":"Home","modules":[
            {"id":"{{IndoorCamera}}","type":"NACamera","name":"Hall"},
            {"id":"{{OutdoorCamera}}","type":"NOC","name":"Yard"},
            {"id":"{{Doorbell}}","type":"NDB","name":"Door"},
            {"id":"{{DoorTag}}","type":"NACamDoorTag","name":"Back door","bridge":"{{IndoorCamera}}"},
            {"id":"{{SmokeAlarm}}","type":"NSD","name":"Kitchen"},
            {"id":"70:ee:50:00:00:15","type":"NAPlug","name":"Relay"}],
           "persons":[{"id":"{{Alice}}","pseudo":"Alice"},{"id":"{{Unnamed}}"}]}],
          "user":{"email":"user@example.com"} } }
        """;

    public static readonly string HomeStatus = $$"""
        {"status":"ok","body":{"home":{"id":"{{HomeId}}","modules":[
          {"id":"{{IndoorCamera}}","type":"NACamera","monitoring":"on"},
          {"id":"{{OutdoorCamera}}","type":"NOC","monitoring":"off","floodlight":"auto"},
          {"id":"{{Doorbell}}","type":"NDB","reachable":true},
          {"id":"{{DoorTag}}","type":"NACamDoorTag","reachable":true,"battery_state":"high"},
          {"id":"{{SmokeAlarm}}","type":"NSD","battery_state":"full"}],
          "persons":[{"id":"{{Alice}}","out_of_sight":false},{"id":"{{Unnamed}}","out_of_sight":true}]} } }
        """;

    public static string Events(params string[] events) =>
        $$"""{"status":"ok","body":{"home":{"id":"{{HomeId}}","events":[{{string.Join(",", events)}}]} } }""";

    public static string Movement(string id, long time, string? snapshot = null) =>
        $$"""{"id":"{{id}}","type":"movement","time":{{time}},"module_id":"{{IndoorCamera}}","message":"Motion"{{(snapshot == null ? "" : ",\"snapshot\":{\"url\":\"" + snapshot + "\"}")}} }""";

    public static string Stations(long time = 1793800000, double outdoorTemperature = 4.2) => $$"""
        {"status":"ok","body":{"devices":[{"_id":"70:ee:50:00:00:01","station_name":"Home","module_name":"Indoor","type":"NAMain",
          "wifi_status":56,"reachable":true,"data_type":["Temperature","CO2","Humidity","Noise","Pressure"],
          "dashboard_data":{"time_utc":{{time}},"Temperature":21.5,"min_temp":20.1,"max_temp":22.0,"temp_trend":"stable","CO2":650,
            "Humidity":40,"Noise":35,"Pressure":1012.5,"AbsolutePressure":1009.1,"pressure_trend":"up"},
          "modules":[
            {"_id":"02:00:00:00:00:02","type":"NAModule1","module_name":"Outdoor","data_type":["Temperature","Humidity"],"battery_percent":80,
             "rf_status":70,"reachable":true,"dashboard_data":{"time_utc":{{time - 50}},"Temperature":{{outdoorTemperature.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"Humidity":88,"temp_trend":"down"} },
            {"_id":"03:00:00:00:00:03","type":"NAModule2","module_name":"Wind","data_type":["Wind"],"battery_percent":60,"rf_status":65,
             "reachable":true,"dashboard_data":{"time_utc":{{time - 40}},"WindStrength":12,"WindAngle":200,"GustStrength":20,"GustAngle":210,"max_wind_str":25} },
            {"_id":"05:00:00:00:00:05","type":"NAModule3","module_name":"Rain","data_type":["Rain"],"battery_percent":55,"rf_status":60,
             "reachable":false,"dashboard_data":{"time_utc":{{time - 30}},"Rain":0.3,"sum_rain_1":0.5,"sum_rain_24":2.1} }]}],
          "user":{"mail":"user@example.com"} } }
        """;

    public const string Coaches = """
        {"status":"ok","body":{"devices":[{"_id":"70:ee:50:00:00:99","station_name":"Bedroom","type":"NHC","wifi_status":60,"reachable":true,
          "data_type":["Temperature","CO2","Humidity","Noise","Pressure"],
          "dashboard_data":{"time_utc":1793800001,"Temperature":20.0,"CO2":900,"Humidity":45,"Noise":30,"Pressure":1011,"health_idx":2}}]}}
        """;

    public static Dictionary<string, string> Parameters(params (string Key, string Value)[] extra)
    {
        var parameters = new Dictionary<string, string>
        {
            ["clientId"] = "netatmo-app",
            ["clientSecret"] = "app-secret",
            ["token"] = "access",
            ["refresh_token"] = "refresh"
        };
        foreach (var (key, value) in extra)
            parameters[key] = value;
        return parameters;
    }

    public static DeviceConfiguration Configuration(Dictionary<string, string> parameters, IEnumerable<string>? reports = null, IEnumerable<string>? commands = null) => new()
    {
        Id = "netatmo-device",
        Name = "Netatmo",
        DeviceParameters = parameters,
        ReportTemplates = (reports ?? []).Select(address => new ReportTemplate { Id = address, Address = address, Parameters = [] }).ToList(),
        CommandTemplates = (commands ?? []).Select(address => new CommandTemplate { Id = address, Address = address }).ToList()
    };
}

internal sealed record NetatmoCall(string Method, Uri Uri, string Body, string? Token)
{
    public string Path => Uri.AbsolutePath.TrimStart('/');
    public string Key => Uri.Host == "api.netatmo.com" ? $"{Method} {Path}" : $"{Method} {Uri.GetLeftPart(UriPartial.Path)}";
    public string? Query(string name) => HttpUtility.ParseQueryString(Uri.Query)[name];
}

/// <summary>Netatmo's API as a table of routes ("GET api/homesdata", or a full URL for snapshots); unknown routes answer 404.</summary>
internal sealed class FakeNetatmo
{
    private readonly ConcurrentQueue<NetatmoCall> _calls = new();

    public ConcurrentDictionary<string, Func<NetatmoCall, HttpResponseMessage>> Routes { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<NetatmoCall> Calls => _calls.ToArray();

    public int Count(string key) => _calls.Count(call => call.Key == key);

    public void Route(string key, string json) => Routes[key] = _ => Json(json);

    public void RouteHome()
    {
        Route("GET api/homesdata", NetatmoFixtures.HomesData);
        Route("GET api/homestatus", NetatmoFixtures.HomeStatus);
        Route("GET api/getevents", NetatmoFixtures.Events());
    }

    public void RouteWebhooks(params string[] registered)
    {
        Route("GET webhooks/v1", $$"""{"status":"ok","body":[{{string.Join(",", registered.Select(url => "{\"url\":\"" + url + "\"}"))}}]}""");
        Route("POST webhooks/v1", """{"status":"ok"}""");
        Route("DELETE webhooks/v1", """{"status":"ok"}""");
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var call = new NetatmoCall(request.Method.Method, request.RequestUri!, body, request.Headers.Authorization?.Parameter);
        _calls.Enqueue(call);
        return Routes.TryGetValue(call.Key, out var respond)
            ? respond(call)
            : Json("""{"error":{"code":404,"message":"no such route"}}""", HttpStatusCode.NotFound);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Error(HttpStatusCode status, int code, string message = "refused") =>
        Json($$"""{"error":{"code":{{code}},"message":"{{message}}"} }""", status);
}

internal sealed class FakeMemoryStorage : IMemoryStorageService
{
    public ConcurrentQueue<(Document Document, string Address)> Saved { get; } = new();

    public string Save(Document document, string address)
    {
        Saved.Enqueue((document, address));
        return $"http://node/api/download/{document.Filename}";
    }

    public Document? Get(string filename, string address = "") => null;
    public Document? GetLatest(string address) => null;
    public List<DocumentMetadata>? List(string address) => null;
    public void DeleteAddress(string address) { }
    public List<MemoryStorageAddess> GetAllDocuments() => [];
}

/// <summary>The fakes a Netatmo test needs: the API, the token endpoint, a clock, the hub and the memory storage.</summary>
internal sealed class NetatmoTestKit : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", Guid.NewGuid().ToString("N"));

    public NetatmoTestKit()
    {
        Directory.CreateDirectory(_dataDirectory);
        Factory = new NetatmoAuthClientFactory(NullLogger.Instance, _dataDirectory, (url, body) =>
        {
            TokenPosts.Enqueue((url, body));
            return Task.FromResult(TokenResponse(body));
        });
        Hub = new NetatmoWebhookHub(NullLogger.Instance, () => Clock);
    }

    public DateTimeOffset Clock { get; set; } = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    public FakeNetatmo Api { get; } = new();
    public FakeMemoryStorage Storage { get; } = new();
    public NetatmoAuthClientFactory Factory { get; }
    public NetatmoWebhookHub Hub { get; }
    public ConcurrentQueue<(string Url, string Body)> TokenPosts { get; } = new();
    public Func<string, HttpResponseMessage> TokenResponse { get; set; } = _ => FakeNetatmo.Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);

    public TestSecurity Security() => new(this) { Now = () => Clock };
    public TestWeather Weather() => new(this) { Now = () => Clock };

    public ConcurrentQueue<Report> Capture(Core.Interfaces.IDevice device)
    {
        var reports = new ConcurrentQueue<Report>();
        device.ReportUpdated += (_, report) => reports.Enqueue((Report)report);
        return reports;
    }

    public void Dispose()
    {
        Hub.Dispose();
        Directory.Delete(_dataDirectory, true);
    }

    internal sealed class TestSecurity(NetatmoTestKit kit) : NetatmoSecurity(NullLogger.Instance, kit.Storage, kit.Factory, kit.Hub)
    {
        internal override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            kit.Api.SendAsync(request, cancellationToken);
    }

    internal sealed class TestWeather(NetatmoTestKit kit) : NetatmoWeather(NullLogger.Instance, kit.Factory)
    {
        internal override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            kit.Api.SendAsync(request, cancellationToken);
    }
}

internal static class ReportExtensions
{
    public static Report? At(this IEnumerable<Report> reports, string address) => reports.LastOrDefault(report => report.Id == address);

    public static Models.SecurityReport Security(this Report report) => report.Value.ToJson().ToObj<Models.SecurityReport>()!;
}
