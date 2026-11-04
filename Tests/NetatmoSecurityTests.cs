using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Core;
using RIoT2.Core.Models;
using RIoT2.Core.Plugins;
using RIoT2.Net.Devices.Models;
using RIoT2.Net.Devices.Services.Interfaces;
using static RIoT2.Net.Devices.Tests.NetatmoFixtures;

namespace RIoT2.Net.Devices.Tests;

/// <summary>
/// The Netatmo Security device (3.0.0): its home and state from polling, events from the webhook and from polling
/// reported once, its commands, and connecting it to Netatmo from the UI.
/// </summary>
[TestClass]
public class NetatmoSecurityTests
{
    private static readonly string[] StateReports =
    [
        $"{IndoorCamera}|monitoring", $"{OutdoorCamera}|monitoring", $"{OutdoorCamera}|floodlight", $"{OutdoorCamera}|connection",
        $"{Doorbell}|connection", $"{DoorTag}|battery", $"{SmokeAlarm}|battery", $"person|{Alice}", $"person|{Unnamed}", "home|webhook",
        $"{IndoorCamera}|event", $"{OutdoorCamera}|event", $"{Doorbell}|event", $"{DoorTag}|event", $"{SmokeAlarm}|event"
    ];

    private NetatmoTestKit _kit = null!;

    [TestInitialize]
    public void Initialize() => _kit = new NetatmoTestKit();

    [TestCleanup]
    public void Cleanup() => _kit.Dispose();

    private async Task<(NetatmoTestKit.TestSecurity Device, System.Collections.Concurrent.ConcurrentQueue<Report> Reports)> StartAsync(
        Dictionary<string, string>? parameters = null, IEnumerable<string>? commands = null)
    {
        var device = _kit.Security();
        device.Initialize(Configuration(parameters ?? Parameters(), StateReports, commands));
        var reports = _kit.Capture(device);
        await device.StartAsync(CancellationToken.None);
        return (device, reports);
    }

    private static Dictionary<string, string> WithWebhook(params (string, string)[] extra) =>
        Parameters([("webhookUrl", WebhookBase), ("webhookSecret", Secret), .. extra]);

    [TestMethod]
    public async Task StartPicksTheSecuredHomeTakesItsStateAndOnlyRemembersPastEvents()
    {
        _kit.Api.RouteHome();
        _kit.Api.Route("GET api/getevents", Events(Movement("evt-old", 1793790000, Snapshot)));

        var (device, reports) = await StartAsync();

        Assert.AreEqual((DeviceState.Running, ""), (device.State, device.StateMessage));
        Assert.AreEqual(HomeId, _kit.Api.Calls.Single(c => c.Key == "GET api/homestatus").Query("home_id"), "the home with security modules, not the first one");
        Assert.IsTrue(_kit.Api.Calls.All(c => c.Token == "access"));
        Assert.IsTrue(reports.At($"{IndoorCamera}|monitoring")!.Value.GetValue<bool>());
        Assert.IsFalse(reports.At($"{OutdoorCamera}|monitoring")!.Value.GetValue<bool>());
        Assert.AreEqual("auto", reports.At($"{OutdoorCamera}|floodlight")!.Value.GetValue<string>());
        Assert.IsTrue(reports.At($"{OutdoorCamera}|connection")!.Value.GetValue<bool>(), "monitoring off isn't disconnected");
        Assert.IsTrue(reports.At($"{Doorbell}|connection")!.Value.GetValue<bool>());
        Assert.AreEqual("high", reports.At($"{DoorTag}|battery")!.Value.GetValue<string>());
        Assert.IsTrue(reports.At($"person|{Alice}")!.Value.GetValue<bool>());
        Assert.IsFalse(reports.At($"person|{Unnamed}")!.Value.GetValue<bool>());
        Assert.IsFalse(reports.At("home|webhook")!.Value.GetValue<bool>());
        Assert.IsFalse(reports.Any(r => r.Id.EndsWith("|event")), "past events aren't reported again after a restart");

        var status = device.GetAuthorizationStatus();
        Assert.AreEqual((DeviceAuthorizationState.Connected, "user@example.com"), (status.State, status.Account));
    }

    [TestMethod]
    public async Task TemplateListsTheSecurityModulesPersonsAndCommandsWithoutCallingNetatmo()
    {
        _kit.Api.RouteHome();
        var (device, _) = await StartAsync();
        var calls = _kit.Api.Calls.Count;

        var template = DeviceTemplates.Create(device);

        Assert.AreEqual(calls, _kit.Api.Calls.Count);
        var reports = template.ReportTemplates.Select(r => r.Address).ToList();
        CollectionAssert.IsSubsetOf(new[] { $"{IndoorCamera}|event", $"{IndoorCamera}|monitoring", $"{OutdoorCamera}|floodlight", $"{Doorbell}|connection",
            $"{DoorTag}|event", $"{DoorTag}|battery", $"{SmokeAlarm}|event", $"person|{Alice}", "home|webhook" }, reports);
        Assert.IsFalse(reports.Any(a => a.StartsWith("70:ee:50:00:00:15")), "a relay isn't a security module");
        Assert.IsFalse(reports.Contains($"person|{Unnamed}"), "an unnamed face has no presence report");
        var commands = template.CommandTemplates!.Select(c => c.Address).ToList();
        CollectionAssert.IsSubsetOf(new[] { "refresh", $"{IndoorCamera}|monitoring", $"{OutdoorCamera}|floodlight", $"person|{Alice}", "home|away" }, commands);
        Assert.AreEqual("Netatmo", template.Authorization!.Provider);
        Assert.AreEqual("0 0/5 * ? * * *", template.RefreshSchedule);
        Assert.IsFalse(template.DeviceParameters.Values.Any(v => v.Length > 0 && v != "false" && v != "true"), "no sample secrets");
    }

    [TestMethod]
    public async Task AHomeIdChoosesTheHomeAndAnUnknownOneNamesTheHomes()
    {
        _kit.Api.RouteHome();
        var (device, _) = await StartAsync(Parameters(("homeId", OtherHomeId)));
        Assert.AreEqual(OtherHomeId, _kit.Api.Calls.Last(c => c.Key == "GET api/homestatus").Query("home_id"));

        var wrong = _kit.Security();
        wrong.Initialize(Configuration(Parameters(("homeId", "nope")), StateReports));
        await Assert.ThrowsAsync<Exception>(() => wrong.StartAsync(CancellationToken.None));
        Assert.AreEqual(DeviceState.Error, wrong.State);
        StringAssert.Contains(wrong.StateMessage, $"{HomeId} (Home)");
        Assert.AreEqual(DeviceState.Running, device.State);
    }

    [TestMethod]
    public async Task WebhookIsRegisteredAndAPushIsReportedOnceWithItsSnapshot()
    {
        _kit.Api.RouteHome();
        _kit.Api.RouteWebhooks();
        _kit.Api.Routes[$"GET {Snapshot}"] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        var (device, reports) = await StartAsync(WithWebhook());

        Assert.AreEqual(WebhookUrl, device.WebhookUrl);
        Assert.AreEqual(WebhookUrl, System.Text.Json.JsonDocument.Parse(_kit.Api.Calls.Single(c => c.Key == "POST webhooks/v1").Body).RootElement.GetProperty("url").GetString());
        Assert.IsTrue(reports.At("home|webhook")!.Value.GetValue<bool>());

        var push = $$"""{"user_id":"u1","event_type":"movement","home_id":"{{HomeId}}","camera_id":"{{IndoorCamera}}","device_id":"{{IndoorCamera}}","event_id":"evt-1","message":"Motion in the hall","snapshot_url":"{{Snapshot}}","push_type":"NACamera-movement","time":1793800100}""";
        Assert.IsTrue(_kit.Hub.TryAccept(Secret, push));
        Assert.IsTrue(_kit.Hub.TryAccept(Secret, push), "Netatmo may deliver a push twice");
        await _kit.Hub.DrainAsync();

        var reported = reports.Where(r => r.Id == $"{IndoorCamera}|event").ToList();
        Assert.HasCount(1, reported);
        var security = reported[0].Security();
        Assert.AreEqual((SecurityEventType.motionDetected, "Motion in the hall", "evt-1", IndoorCamera), (security.SecurityEvent, security.Message, security.EventId, security.ModuleId));
        Assert.AreEqual(("image", 1793800100L), (reported[0].Filter, reported[0].TimeStamp));
        Assert.AreEqual($"http://node/api/download/{_kit.Storage.Saved.Single().Document.Filename}", security.ImageUrl);
        Assert.AreEqual($"{IndoorCamera}|event", _kit.Storage.Saved.Single().Address);

        // While the webhook works, refreshes don't poll until the reconcile interval has passed.
        var polls = _kit.Api.Count("GET api/getevents");
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);
        Assert.AreEqual(polls, _kit.Api.Count("GET api/getevents"));

        _kit.Api.Route("GET api/getevents", Events(Movement("evt-1", 1793800100, Snapshot)));
        _kit.Clock += TimeSpan.FromMinutes(16);
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);
        Assert.AreEqual(polls + 1, _kit.Api.Count("GET api/getevents"));
        Assert.HasCount(1, reports.Where(r => r.Id == $"{IndoorCamera}|event").ToList(), "the polled copy of a pushed event isn't reported again");
    }

    [TestMethod]
    public async Task WithoutAWorkingWebhookEveryRefreshPollsAndReportsNewEvents()
    {
        _kit.Api.RouteHome();
        var (device, reports) = await StartAsync();

        _kit.Api.Route("GET api/getevents", Events(Movement("evt-2", 1793800200), Movement("evt-3", 1793800300)));
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);

        var events = reports.Where(r => r.Id == $"{IndoorCamera}|event").Select(r => r.Security().EventId).ToList();
        CollectionAssert.AreEqual(new[] { "evt-2", "evt-3" }, events, "oldest first, each once");
        Assert.AreEqual("no-image", reports.First(r => r.Id == $"{IndoorCamera}|event").Filter);
    }

    [TestMethod]
    [DataRow("""{"event_type":"person","camera_id":"70:ee:50:00:00:10","event_id":"p1","persons":[{"id":"person-alice","is_known":true}]}""", "70:ee:50:00:00:10", SecurityEventType.personName, "Alice")]
    [DataRow("""{"event_type":"person","camera_id":"70:ee:50:00:00:10","event_id":"p2","persons":[{"id":"person-x","is_known":false}]}""", "70:ee:50:00:00:10", SecurityEventType.strangerPersonDetected, null)]
    [DataRow("""{"event_type":"human","camera_id":"70:ee:50:00:00:11","event_id":"h1"}""", "70:ee:50:00:00:11", SecurityEventType.personDetected, null)]
    [DataRow("""{"push_type":"NOC-vehicle","camera_id":"70:ee:50:00:00:11","event_id":"v1"}""", "70:ee:50:00:00:11", SecurityEventType.vehicleDetected, null)]
    [DataRow("""{"event_type":"animal","camera_id":"70:ee:50:00:00:11","event_id":"a1"}""", "70:ee:50:00:00:11", SecurityEventType.animalDetected, null)]
    [DataRow("""{"event_type":"incoming_call","device_id":"70:ee:50:00:00:12","event_id":"d1"}""", "70:ee:50:00:00:12", SecurityEventType.doorbellPressed, null)]
    [DataRow("""{"event_type":"missed_call","device_id":"70:ee:50:00:00:12","event_id":"d2"}""", "70:ee:50:00:00:12", SecurityEventType.doorbellCallMissed, null)]
    [DataRow("""{"event_type":"tag_open","module_id":"12:00:00:00:00:13","device_id":"70:ee:50:00:00:10","event_id":"t1"}""", "12:00:00:00:00:13", SecurityEventType.doorOpened, null)]
    [DataRow("""{"event_type":"tag_big_move","module_id":"12:00:00:00:00:13","event_id":"t2"}""", "12:00:00:00:00:13", SecurityEventType.tagMoved, null)]
    [DataRow("""{"event_type":"smoke","device_id":"70:ee:50:00:00:14","sub_type":"1","event_id":"s1"}""", "70:ee:50:00:00:14", SecurityEventType.smokeDetected, "1")]
    [DataRow("""{"event_type":"alarm_started","camera_id":"70:ee:50:00:00:10","event_id":"al1"}""", "70:ee:50:00:00:10", SecurityEventType.soundDetected, null)]
    public async Task PushesAreReportedOnTheirModuleAsTheirKind(string push, string module, SecurityEventType expected, string? value)
    {
        _kit.Api.RouteHome();
        var (device, reports) = await StartAsync();

        await device.HandleWebhookAsync(NetatmoWebhookEvent.Parse(push)!, CancellationToken.None);

        var report = reports.Single(r => r.Id.EndsWith("|event"));
        Assert.AreEqual($"{module}|event", report.Id);
        Assert.AreEqual(expected, report.Security().SecurityEvent);
        Assert.AreEqual(value, report.Security().EventValue?.ToString());
    }

    [TestMethod]
    public async Task StatePushesUpdateTheStateReports()
    {
        _kit.Api.RouteHome();
        var (device, reports) = await StartAsync();

        async Task Push(string json) => await device.HandleWebhookAsync(NetatmoWebhookEvent.Parse(json)!, CancellationToken.None);
        await Push($$"""{"event_type":"off","camera_id":"{{IndoorCamera}}","home_id":"{{HomeId}}"}""");
        await Push($$"""{"event_type":"light_mode","camera_id":"{{OutdoorCamera}}","mode":"on"}""");
        await Push($$"""{"event_type":"person_away","person_id":"{{Alice}}"}""");
        await Push($$"""{"push_type":"NOC-disconnection","camera_id":"{{OutdoorCamera}}","event_id":"c1"}""");
        await Push($$"""{"event_type":"movement","home_id":"{{OtherHomeId}}","camera_id":"{{IndoorCamera}}","event_id":"other"}""");

        Assert.IsFalse(reports.At($"{IndoorCamera}|monitoring")!.Value.GetValue<bool>());
        Assert.AreEqual("on", reports.At($"{OutdoorCamera}|floodlight")!.Value.GetValue<string>());
        Assert.IsFalse(reports.At($"person|{Alice}")!.Value.GetValue<bool>());
        Assert.IsFalse(reports.At($"{OutdoorCamera}|connection")!.Value.GetValue<bool>());
        Assert.AreEqual(SecurityEventType.connectionLost, reports.At($"{OutdoorCamera}|event")!.Security().SecurityEvent);
        Assert.IsNull(reports.At($"{IndoorCamera}|event"), "a push for another home is ignored");
    }

    [TestMethod]
    public async Task CommandsCallNetatmoAndUpdateTheirState()
    {
        _kit.Api.RouteHome();
        foreach (var route in new[] { "POST api/setstate", "POST api/setpersonshome", "POST api/setpersonsaway" })
            _kit.Api.Route(route, """{"status":"ok","time_server":1793800000}""");
        var (device, reports) = await StartAsync(commands: [$"{IndoorCamera}|monitoring", $"{OutdoorCamera}|floodlight", $"person|{Alice}", "home|away", "refresh"]);

        await device.ExecuteCommandAsync($"{IndoorCamera}|monitoring", "false", CancellationToken.None);
        Assert.AreEqual($$$"""{"home":{"id":"{{{HomeId}}}","modules":[{"id":"{{{IndoorCamera}}}","monitoring":"off"}]}}""", _kit.Api.Calls.Last().Body);
        Assert.IsFalse(reports.At($"{IndoorCamera}|monitoring")!.Value.GetValue<bool>());

        await device.ExecuteCommandAsync($"{OutdoorCamera}|floodlight", "\"ON\"", CancellationToken.None);
        StringAssert.Contains(_kit.Api.Calls.Last().Body, "\"floodlight\":\"on\"");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => device.ExecuteCommandAsync($"{OutdoorCamera}|floodlight", "\"blink\"", CancellationToken.None));

        await device.ExecuteCommandAsync($"person|{Alice}", "true", CancellationToken.None);
        Assert.AreEqual(("POST api/setpersonshome", $"home_id={HomeId}&person_ids%5B%5D={Alice}"), (_kit.Api.Calls.Last().Key, _kit.Api.Calls.Last().Body));
        await device.ExecuteCommandAsync($"person|{Alice}", "false", CancellationToken.None);
        Assert.AreEqual(("POST api/setpersonsaway", $"home_id={HomeId}&person_id={Alice}"), (_kit.Api.Calls.Last().Key, _kit.Api.Calls.Last().Body));
        await device.ExecuteCommandAsync("home|away", "true", CancellationToken.None);
        Assert.AreEqual($"home_id={HomeId}", _kit.Api.Calls.Last().Body);

        _kit.Api.Routes["POST api/setstate"] = _ => FakeNetatmo.Error(HttpStatusCode.Forbidden, 13, "Missing scope");
        var error = await Assert.ThrowsAsync<Services.Netatmo.NetatmoApiException>(() => device.ExecuteCommandAsync($"{IndoorCamera}|monitoring", "true", CancellationToken.None));
        Assert.IsTrue(error.IsMissingScope);
        Assert.AreEqual(DeviceAuthorizationState.Expired, device.GetAuthorizationStatus().State, "the user is asked to connect again");
    }

    [TestMethod]
    public async Task AFailedFirstPollDoesntReportThePastLater()
    {
        _kit.Api.RouteHome();
        _kit.Api.Route("GET api/getevents", Events(Movement("evt-old", 1793790000)));
        _kit.Api.Routes["GET api/homestatus"] = _ => FakeNetatmo.Json("{}", HttpStatusCode.ServiceUnavailable);
        var (device, reports) = await StartAsync();
        Assert.AreEqual(DeviceState.Running, device.State);

        _kit.Api.Route("GET api/homestatus", HomeStatus);
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);
        _kit.Api.Route("GET api/getevents", Events(Movement("evt-old", 1793790000), Movement("evt-new", 1793800000)));
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "evt-new" }, reports.Where(r => r.Id.EndsWith("|event")).Select(r => r.Security().EventId).ToArray());
    }

    [TestMethod]
    public async Task AnUnknownFacePushedAndThenPolledIsReportedOnce()
    {
        _kit.Api.RouteHome();
        var (device, reports) = await StartAsync();

        await device.HandleWebhookAsync(NetatmoWebhookEvent.Parse($$"""{"event_type":"person","camera_id":"{{IndoorCamera}}","event_id":"f1","persons":[{"id":"{{Unnamed}}","is_known":false}]}""")!, CancellationToken.None);
        _kit.Api.Route("GET api/getevents", Events($$"""{"id":"f1","type":"person","time":1793800000,"module_id":"{{IndoorCamera}}","person_id":"{{Unnamed}}"}"""));
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);

        var faces = reports.Where(r => r.Id == $"{IndoorCamera}|event").ToList();
        Assert.HasCount(1, faces);
        Assert.AreEqual(SecurityEventType.strangerPersonDetected, faces[0].Security().SecurityEvent);
    }

    [TestMethod]
    public async Task CommandsWaitForTheHome()
    {
        var device = _kit.Security();
        device.Initialize(Configuration(Parameters(), commands: ["home|away", "refresh"]));

        await Assert.ThrowsExactlyAsync<DeviceNotReadyException>(() => device.ExecuteCommandAsync("home|away", "true", CancellationToken.None));
    }

    [TestMethod]
    public async Task AnUnconnectedDeviceRunsAndIsConnectedFromTheUi()
    {
        _kit.Api.RouteHome();
        var parameters = Parameters();
        parameters.Remove("token");
        parameters.Remove("refresh_token");
        var (device, reports) = await StartAsync(parameters);

        Assert.AreEqual((DeviceState.Running, "Connect the device to Netatmo in its settings."), (device.State, device.StateMessage));
        Assert.AreEqual(0, _kit.Api.Calls.Count);
        Assert.AreEqual(DeviceAuthorizationState.NotConnected, device.GetAuthorizationStatus().State);

        // A weather device of the same app adds its scope: one sign-in serves both.
        var weather = _kit.Weather();
        weather.Initialize(Configuration(Parameters(("includeHomeCoach", "true"))));

        const string redirect = "http://riot.local/api/oauth/callback";
        var url = await DeviceCalls.BeginAuthorizationAsync(device, redirect, "state-1", CancellationToken.None);
        StringAssert.StartsWith(url, "https://api.netatmo.com/oauth2/authorize?client_id=netatmo-app&redirect_uri=http%3A%2F%2Friot.local%2Fapi%2Foauth%2Fcallback");
        foreach (var scope in new[] { "read_camera", "write_presence", "read_smokedetector", "read_station", "read_homecoach" })
            StringAssert.Contains(url, scope);
        StringAssert.Contains(url, "state=state-1");

        _kit.TokenResponse = _ => FakeNetatmo.Json("""{"access_token":"new-access","refresh_token":"new-refresh","expires_in":10800}""");
        await DeviceCalls.CompleteAuthorizationAsync(device, "code-1", redirect, CancellationToken.None);

        var exchange = _kit.TokenPosts.Single().Body;
        foreach (var field in new[] { "grant_type=authorization_code", "code=code-1", "client_secret=app-secret", "redirect_uri=http%3A%2F%2Friot.local" })
            StringAssert.Contains(exchange, field);
        Assert.AreEqual((DeviceAuthorizationState.Connected, "user@example.com"), (device.GetAuthorizationStatus().State, device.GetAuthorizationStatus().Account));
        Assert.AreEqual((DeviceState.Running, ""), (device.State, device.StateMessage));
        Assert.IsTrue(_kit.Api.Calls.All(c => c.Token == "new-access"));
        Assert.IsNotNull(reports.At($"{IndoorCamera}|monitoring"), "the device reads its home once connected");
        Assert.AreEqual(DeviceAuthorizationState.Connected, weather.GetAuthorizationStatus().State, "devices of one app share the connection");
    }

    [TestMethod]
    public async Task ARefusedSignInIsReported()
    {
        var device = _kit.Security();
        device.Initialize(Configuration(Parameters()));

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            device.CompleteAuthorizationAsync("bad-code", "http://riot.local/api/oauth/callback", CancellationToken.None));
        StringAssert.Contains(error.Message, "invalid_grant");
    }

    [TestMethod]
    public async Task WithoutTheAppTheDeviceCantBeConnected()
    {
        var device = _kit.Security();
        Assert.AreEqual(DeviceAuthorizationState.NotConfigured, device.GetAuthorizationStatus().State, "before it is initialized");

        await Assert.ThrowsExactlyAsync<DeviceNotReadyException>(() => device.BeginAuthorizationAsync("http://riot.local/api/oauth/callback", "s", CancellationToken.None));
    }

    [TestMethod]
    public async Task ARateLimitIsWaitedOutWhileTheDeviceKeepsRunning()
    {
        _kit.Api.RouteHome();
        _kit.Api.Routes["GET api/homestatus"] = _ => FakeNetatmo.Error(HttpStatusCode.Forbidden, 26, "User usage reached");
        var (device, _) = await StartAsync();

        Assert.AreEqual(DeviceState.Running, device.State);
        StringAssert.StartsWith(device.StateMessage, "Netatmo limits the calls");
        Assert.AreEqual(0, _kit.TokenPosts.Count, "a rate limit isn't a token problem");

        var calls = _kit.Api.Calls.Count;
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);
        Assert.AreEqual(calls, _kit.Api.Calls.Count);

        _kit.Api.Route("GET api/homestatus", HomeStatus);
        _kit.Clock += TimeSpan.FromMinutes(16);
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);
        Assert.AreEqual((DeviceState.Running, ""), (device.State, device.StateMessage));
    }

    [TestMethod]
    public async Task ARejectedTokenIsRefreshedOnceAndThenAsksForANewConnection()
    {
        _kit.Api.RouteHome();
        _kit.Api.Routes["GET api/homesdata"] = call => call.Token == "access"
            ? FakeNetatmo.Error(HttpStatusCode.Forbidden, 3, "Access token expired")
            : FakeNetatmo.Json(HomesData);
        _kit.TokenResponse = body => body.Contains("grant_type=refresh_token")
            ? FakeNetatmo.Json("""{"access_token":"refreshed","refresh_token":"refresh-2"}""")
            : FakeNetatmo.Json("{}", HttpStatusCode.BadRequest);

        var (device, _) = await StartAsync();
        Assert.AreEqual((DeviceState.Running, ""), (device.State, device.StateMessage));
        Assert.AreEqual("refreshed", _kit.Api.Calls.Last().Token);

        _kit.Api.Routes["GET api/homestatus"] = _ => FakeNetatmo.Error(HttpStatusCode.Forbidden, 2, "Invalid access token");
        _kit.TokenResponse = _ => FakeNetatmo.Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);

        Assert.AreEqual(DeviceState.Running, device.State);
        var status = device.GetAuthorizationStatus();
        Assert.AreEqual(DeviceAuthorizationState.Expired, status.State);
        StringAssert.Contains(status.Message, "Connect the device to Netatmo again");
    }

    [TestMethod]
    public async Task ASnapshotThatCantBeDownloadedKeepsNetatmosUrl()
    {
        _kit.Api.RouteHome();
        _kit.Api.Routes[$"GET {Snapshot}"] = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var (device, reports) = await StartAsync();

        await device.HandleWebhookAsync(NetatmoWebhookEvent.Parse($$"""{"event_type":"movement","camera_id":"{{IndoorCamera}}","event_id":"e9","snapshot_url":"{{Snapshot}}"}""")!, CancellationToken.None);

        var report = reports.At($"{IndoorCamera}|event")!;
        Assert.AreEqual((Snapshot, "image"), (report.Security().ImageUrl, report.Filter));
        Assert.AreEqual(DeviceState.Running, device.State);
    }

    [TestMethod]
    [DataRow("https://netatmo.example.com", "short", "webhookSecret")]
    [DataRow("netatmo.example.com", Secret, "webhookUrl")]
    [DataRow("https://netatmo.example.com/?x=1", Secret, "webhookUrl")]
    public void AnInvalidWebhookConfigurationIsReported(string url, string secret, string parameter)
    {
        var device = _kit.Security();

        Assert.ThrowsExactly<Exception>(() => device.Initialize(Configuration(Parameters(("webhookUrl", url), ("webhookSecret", secret)))));
        Assert.AreEqual(DeviceState.Error, device.State);
        StringAssert.Contains(device.StateMessage, parameter);
    }

    [TestMethod]
    public async Task AnotherAppsWebhookIsLeftAloneAndTheDevicePolls()
    {
        _kit.Api.RouteHome();
        _kit.Api.RouteWebhooks("https://homeassistant.example.com/api/webhook/abc");
        var (device, reports) = await StartAsync(WithWebhook());

        Assert.AreEqual(NetatmoWebhookState.Conflict, device.WebhookStatus.State);
        Assert.AreEqual(0, _kit.Api.Count("POST webhooks/v1") + _kit.Api.Count("DELETE webhooks/v1"));
        StringAssert.Contains(device.StateMessage, "separate Netatmo app");
        Assert.IsFalse(reports.At("home|webhook")!.Value.GetValue<bool>());

        var polls = _kit.Api.Count("GET api/getevents");
        await device.RefreshReportAsync(device.Id, null!, CancellationToken.None);
        Assert.AreEqual(polls + 1, _kit.Api.Count("GET api/getevents"), "without a webhook every refresh polls");
    }
}
