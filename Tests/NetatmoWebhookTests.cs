using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Net.Devices.Controllers;
using RIoT2.Net.Devices.Models;
using RIoT2.Net.Devices.Services.Interfaces;
using RIoT2.Net.Devices.Services.Netatmo;
using static RIoT2.Net.Devices.Tests.NetatmoFixtures;

namespace RIoT2.Net.Devices.Tests;

/// <summary>
/// Netatmo's webhook (3.0.0): the hub registers one URL per app through <c>webhooks/v1</c>, leaves another
/// integration's alone, backs off, and passes pushes to the devices of their home; the controller answers at once.
/// </summary>
[TestClass]
public class NetatmoWebhookTests
{
    private NetatmoTestKit _kit = null!;

    [TestInitialize]
    public void Initialize() => _kit = new NetatmoTestKit();

    [TestCleanup]
    public void Cleanup() => _kit.Dispose();

    private NetatmoApiClient Api(string clientId = "netatmo-app", string accessToken = "access")
    {
        var auth = _kit.Factory.Get(clientId);
        auth.Configure(accessToken, "refresh", "app-secret");
        return new NetatmoApiClient(auth, _kit.Api.SendAsync, NullLogger.Instance);
    }

    private Task<IDisposable> SubscribeAsync(bool takeover = false, string? homeId = HomeId, Func<NetatmoWebhookEvent, CancellationToken, Task>? handler = null,
        string clientId = "netatmo-app", CancellationToken cancellationToken = default) =>
        _kit.Hub.SubscribeAsync(new NetatmoWebhookSubscription(Api(clientId), Secret, WebhookUrl, takeover, homeId, handler ?? ((_, _) => Task.CompletedTask)), cancellationToken);

    [TestMethod]
    public void PushesAreReadWhereNetatmoPutsTheirFields()
    {
        var person = NetatmoWebhookEvent.Parse("""
            {"user_id":"u","event_type":"person","home":{"id":"h1"},"camera_id":"cam","event_id":"e1","snapshot_url":"https://s/1.jpg",
             "persons":[{"id":"p1","is_known":true,"face_url":"https://s/f.jpg"},{"id":"p2","is_known":false}],"push_type":"NACamera-person"}
            """)!;
        Assert.AreEqual(("person", "h1", "cam", "e1", "https://s/1.jpg"), (person.EventType, person.HomeId, person.CameraId, person.EventId, person.SnapshotUrl));
        Assert.AreEqual(2, person.Persons.Count);
        Assert.AreEqual(("p2", false), (person.Persons[1].Id, person.Persons[1].IsKnown));

        var events = NetatmoSecurityEvent.From(person, "cam").ToList();
        Assert.AreEqual(2, events.Count, "one event per person seen");
        Assert.AreNotEqual(events[0].DeduplicationKey("x"), events[1].DeduplicationKey("x"));

        var nested = NetatmoWebhookEvent.Parse("""{"push_type":"NOC-human","extra_params":{"module_id":"m1"},"home_id":"h1"}""")!;
        Assert.AreEqual(("human", "m1"), (nested.EventType, nested.ModuleId), "the type comes from push_type when event_type is missing");
        Assert.IsTrue(NetatmoWebhookEvent.Parse("""{"push_type":"webhook_activation"}""")!.IsActivation);
        Assert.IsNull(NetatmoWebhookEvent.Parse("""{"push_type":"webhook_activation"}""")!.EventType);
        Assert.IsNull(NetatmoWebhookEvent.Parse("not json"));
        Assert.IsNull(NetatmoWebhookEvent.Parse("[1]"));
    }

    [TestMethod]
    public async Task TheWebhookIsRegisteredWhenNoneIsAndKeptWhenItIsOurs()
    {
        _kit.Api.RouteWebhooks();
        using (await SubscribeAsync())
        {
            Assert.AreEqual(NetatmoWebhookState.Active, _kit.Hub.GetStatus("netatmo-app").State);
            Assert.AreEqual($$"""{"url":"{{WebhookUrl}}"}""", _kit.Api.Calls.Single(c => c.Key == "POST webhooks/v1").Body);
        }

        // After a restart Netatmo still has it: nothing is registered again.
        _kit.Api.RouteWebhooks(WebhookUrl);
        using (await SubscribeAsync())
            Assert.AreEqual((NetatmoWebhookState.Active, 1), (_kit.Hub.GetStatus("netatmo-app").State, _kit.Api.Count("POST webhooks/v1")));
        Assert.AreEqual(0, _kit.Api.Count("DELETE webhooks/v1"), "stopping a device leaves the registration");
    }

    [TestMethod]
    public async Task AnotherWebhookIsReplacedOnlyWithTakeover()
    {
        _kit.Api.RouteWebhooks("https://homeassistant.example.com/api/webhook/x");
        using (await SubscribeAsync())
        {
            var status = _kit.Hub.GetStatus("netatmo-app");
            Assert.AreEqual(NetatmoWebhookState.Conflict, status.State);
            StringAssert.Contains(status.Message, "webhookTakeover");
            Assert.AreEqual(0, _kit.Api.Count("POST webhooks/v1") + _kit.Api.Count("DELETE webhooks/v1"));
        }

        using (await SubscribeAsync(takeover: true))
        {
            Assert.AreEqual(NetatmoWebhookState.Active, _kit.Hub.GetStatus("netatmo-app").State);
            CollectionAssert.AreEqual(new[] { "GET webhooks/v1", "DELETE webhooks/v1", "POST webhooks/v1" },
                _kit.Api.Calls.Skip(1).Select(c => c.Key).ToArray());
        }
    }

    [TestMethod]
    public async Task ANewSecretReplacesThisInstallsEarlierUrl()
    {
        _kit.Api.RouteWebhooks(WebhookBase + "api/netatmo/webhook/the-earlier-secret-0123456789abcdef");
        using (await SubscribeAsync())
        {
            Assert.AreEqual(NetatmoWebhookState.Active, _kit.Hub.GetStatus("netatmo-app").State);
            Assert.AreEqual((1, 1), (_kit.Api.Count("DELETE webhooks/v1"), _kit.Api.Count("POST webhooks/v1")));
        }
    }

    [TestMethod]
    public async Task ARefusedRegistrationIsReportedAndTriedAgainLater()
    {
        _kit.Api.RouteWebhooks();
        _kit.Api.Routes["POST webhooks/v1"] = _ => FakeNetatmo.Json("""{"code":"WH009","message":"webhook limit reached for this application"}""", HttpStatusCode.Conflict);
        using (await SubscribeAsync(clientId: "app-conflict"))
            Assert.AreEqual(NetatmoWebhookState.Conflict, _kit.Hub.GetStatus("app-conflict").State);

        _kit.Api.Routes["POST webhooks/v1"] = _ => FakeNetatmo.Error(HttpStatusCode.BadRequest, 21, "Invalid url parameter");
        using (await SubscribeAsync(clientId: "app-invalid"))
        {
            var refused = _kit.Hub.GetStatus("app-invalid");
            Assert.AreEqual(NetatmoWebhookState.Failed, refused.State);
            StringAssert.Contains(refused.Message, "public HTTPS address");
        }

        _kit.Api.Routes["POST webhooks/v1"] = _ => FakeNetatmo.Error(HttpStatusCode.Forbidden, 27, "Service temporarily unavailable");
        using (await SubscribeAsync(clientId: "app-busy"))
        {
            Assert.AreEqual(NetatmoWebhookState.Failed, _kit.Hub.GetStatus("app-busy").State);
            var gets = _kit.Api.Count("GET webhooks/v1");
            await _kit.Hub.RecheckAsync("app-busy", CancellationToken.None);
            Assert.AreEqual(gets, _kit.Api.Count("GET webhooks/v1"), "backs off");

            _kit.Api.Route("POST webhooks/v1", """{"status":"ok"}""");
            _kit.Clock += TimeSpan.FromMinutes(2);
            await _kit.Hub.RecheckAsync("app-busy", CancellationToken.None);
            Assert.AreEqual(NetatmoWebhookState.Active, _kit.Hub.GetStatus("app-busy").State);
        }
    }

    [TestMethod]
    public async Task ADeactivationOutlivesARestartAndAFailedSubscribeLeavesNoListener()
    {
        _kit.Api.RouteWebhooks();
        var calls = 0;
        using (await SubscribeAsync())
        {
            Assert.IsTrue(_kit.Hub.TryAccept(Secret, """{"push_type":"webhook_deactivation"}"""));
            await _kit.Hub.DrainAsync();
        }

        // The device restarts; Netatmo still lists the URL, but it doesn't push to it.
        _kit.Api.RouteWebhooks(WebhookUrl);
        using (await SubscribeAsync())
            Assert.AreEqual(NetatmoWebhookState.Deactivated, _kit.Hub.GetStatus("netatmo-app").State);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            SubscribeAsync(handler: (_, _) => { Interlocked.Increment(ref calls); return Task.CompletedTask; }, cancellationToken: cancelled.Token));

        Assert.IsTrue(_kit.Hub.TryAccept(Secret, $$"""{"event_type":"movement","home_id":"{{HomeId}}","event_id":"e1"}"""), "accepted while no device listens");
        await _kit.Hub.DrainAsync();
        Assert.AreEqual(0, calls, "the failed subscription was removed");
    }

    [TestMethod]
    public async Task PushesReachTheDevicesOfTheirHomeAndDeactivationIsNoticed()
    {
        _kit.Api.RouteWebhooks();
        var received = new List<string?>();
        using var subscription = await SubscribeAsync(handler: (pushed, _) => { lock (received) received.Add(pushed.EventId); return Task.CompletedTask; });

        Assert.IsFalse(_kit.Hub.TryAccept("wrong-secret", "{}"));
        Assert.IsFalse(_kit.Hub.TryAccept("", "{}"));
        Assert.IsTrue(_kit.Hub.TryAccept(Secret, $$"""{"event_type":"movement","home_id":"{{HomeId}}","event_id":"mine"}"""));
        Assert.IsTrue(_kit.Hub.TryAccept(Secret, $$"""{"event_type":"movement","home_id":"{{OtherHomeId}}","event_id":"theirs"}"""));
        Assert.IsTrue(_kit.Hub.TryAccept(Secret, """{"push_type":"webhook_deactivation"}"""));
        await _kit.Hub.DrainAsync();

        CollectionAssert.AreEqual(new[] { "mine", null }, received.ToArray(), "the deactivation is passed on too");
        Assert.AreEqual(NetatmoWebhookState.Deactivated, _kit.Hub.GetStatus("netatmo-app").State);

        // Registered again after the wait, even though Netatmo still lists the URL.
        _kit.Api.RouteWebhooks(WebhookUrl);
        _kit.Clock += NetatmoWebhookHub.DeactivatedWait;
        await _kit.Hub.RecheckAsync("netatmo-app", CancellationToken.None);
        Assert.AreEqual(NetatmoWebhookState.Active, _kit.Hub.GetStatus("netatmo-app").State);
        Assert.AreEqual(1, _kit.Api.Count("DELETE webhooks/v1"));

        subscription.Dispose();
        Assert.IsTrue(_kit.Hub.TryAccept(Secret, "{}"), "pushes during a restart are still accepted, so Netatmo doesn't count failures");
    }

    [TestMethod]
    public async Task TheControllerQueuesThePushAndAnswersAtOnce()
    {
        _kit.Api.RouteWebhooks();
        var release = new TaskCompletionSource();
        var handled = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = await SubscribeAsync(handler: async (pushed, _) => { await release.Task; handled.TrySetResult(pushed.EventId); });
        var controller = new NetatmoWebhookController(_kit.Hub, NullLogger.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        async Task<IResult> Post(string secret, string body)
        {
            controller.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            return await controller.ReceiveAsync(secret, CancellationToken.None);
        }

        Assert.IsInstanceOfType<NotFound>(await Post("unknown", "{}"));
        Assert.IsInstanceOfType<Ok>(await Post(Secret, $$"""{"event_type":"movement","home_id":"{{HomeId}}","event_id":"e1"}"""));
        Assert.IsFalse(handled.Task.IsCompleted, "the answer doesn't wait for the device");
        release.SetResult();
        Assert.AreEqual("e1", await handled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task AConfiguredRefreshTokenAloneGetsAnAccessTokenFirst()
    {
        _kit.TokenResponse = _ => FakeNetatmo.Json("""{"access_token":"fresh","refresh_token":"next"}""");
        _kit.Api.Route("GET api/homesdata", HomesData);
        var api = Api(clientId: "refresh-only", accessToken: "");

        await api.GetAsync<NetatmoHomesData>("api/homesdata", null, CancellationToken.None);

        Assert.AreEqual("fresh", _kit.Api.Calls.Single().Token);
        StringAssert.Contains(_kit.TokenPosts.Single().Body, "grant_type=refresh_token");
    }

    [TestMethod]
    public void ErrorsOfBothNetatmoApisAreDecoded()
    {
        var api = NetatmoApiClient.ParseError(HttpStatusCode.Forbidden, """{"error":{"code":26,"message":"User usage reached"}}""", "api/homestatus?home_id=x");
        Assert.AreEqual((26, true), (api.Code, api.IsThrottled));
        Assert.AreEqual("Netatmo answered 403 to api/homestatus: User usage reached (code 26).", api.Message, "ids in the query aren't in the message");

        var webhook = NetatmoApiClient.ParseError(HttpStatusCode.Conflict, """{"code":"WH009","message":"webhook limit reached"}""", "webhooks/v1");
        Assert.AreEqual(("WH009", (int?)null), (webhook.TextCode, webhook.Code));

        var html = NetatmoApiClient.ParseError(HttpStatusCode.BadGateway, "<html/>", "api/homesdata");
        Assert.AreEqual("Netatmo answered 502 to api/homesdata: BadGateway.", html.Message);
    }
}
