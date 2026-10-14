using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Net.Devices.Abstracts;
using RIoT2.Net.Devices.Services;

namespace RIoT2.Net.Devices.Tests;

[TestClass]
public class NetatmoAuthenticationTests
{
    private string _dataDirectory;
    private ConcurrentQueue<string> _posts;
    private Func<string, HttpResponseMessage> _respond;

    [TestInitialize]
    public void Initialize()
    {
        _dataDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);
        _posts = new ConcurrentQueue<string>();
        _respond = body => Token(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}");
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_dataDirectory, true);

    private NetatmoAuthClientFactory Factory() => new(NullLogger.Instance, _dataDirectory, (url, body) =>
    {
        Assert.AreEqual(NetatmoAuthClient.TokenUrl, url);
        _posts.Enqueue(body);
        return Task.FromResult(_respond(body));
    });

    private string Legacy => Path.Combine(_dataDirectory, "netatmoAuth.json");
    private string Own(string clientId) => Path.Combine(_dataDirectory, $"netatmoAuth-{clientId}.json");

    private static HttpResponseMessage Token(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json) };

    [TestMethod]
    public void StoredRotatedTokensWinOverConfiguredOnes()
    {
        File.WriteAllText(Legacy, """{"access_token":"synthetic-current-access","refresh_token":"synthetic-current-refresh"}""");

        var client = Factory().Get("client-a");
        client.Configure("synthetic-old-access", "synthetic-old-refresh", "secret");

        Assert.AreEqual(Legacy, client.TokenFile, "The first account keeps the file of earlier versions.");
        Assert.AreEqual("synthetic-current-access", client.AccessToken);
        Assert.AreEqual("synthetic-current-refresh", client.RefreshToken);
        StringAssert.Contains(File.ReadAllText(Legacy), "synthetic-current-refresh");
        StringAssert.Contains(File.ReadAllText(Legacy), "\"client_id\":\"client-a\"");

        client.Configure("synthetic-other-access", "synthetic-other-refresh", "secret");
        Assert.AreEqual("synthetic-current-access", client.AccessToken, "A later configuration doesn't replace loaded tokens.");
    }

    [TestMethod]
    public void LegacyFileStaysWithItsAccountAfterRestart()
    {
        File.WriteAllText(Legacy, """{"access_token":"a-access","refresh_token":"a-refresh"}""");
        Factory().Get("client-a");

        // Next start: the other account configures first and must not take the file.
        var factory = Factory();
        var other = factory.Get("client-b");
        var owner = factory.Get("client-a");

        Assert.AreEqual(Own("client-b"), other.TokenFile);
        Assert.AreEqual(Legacy, owner.TokenFile);
    }

    [TestMethod]
    public void MissingFileUsesConfiguredTokensAndWritesNothing()
    {
        var client = Factory().Get("client-a");
        client.Configure("synthetic-initial-access", "synthetic-initial-refresh", "secret");

        Assert.AreEqual("synthetic-initial-access", client.AccessToken);
        Assert.AreEqual("synthetic-initial-refresh", client.RefreshToken);
        Assert.AreEqual(0, Directory.GetFiles(_dataDirectory).Length);
    }

    [TestMethod]
    public void MalformedFileIsReportedAndKept()
    {
        const string malformed = "{invalid-json";
        File.WriteAllText(Legacy, malformed);

        var client = Factory().Get("client-a");
        Assert.ThrowsExactly<InvalidOperationException>(() => client.Configure("synthetic-access", "synthetic-refresh", "secret"));

        Assert.AreEqual(malformed, File.ReadAllText(Legacy));
    }

    [TestMethod]
    public async Task TwoAccountsKeepSeparateTokens()
    {
        _respond = body => body.Contains("client_id=client-a", StringComparison.Ordinal)
            ? Token(HttpStatusCode.OK, """{"access_token":"a-new-access","refresh_token":"a-new-refresh"}""")
            : Token(HttpStatusCode.OK, """{"access_token":"b-new-access","refresh_token":"b-new-refresh"}""");
        var factory = Factory();
        var a = factory.Get("client-a");
        var b = factory.Get("client-b");
        a.Configure("a-access", "a-refresh", "a-secret");
        b.Configure("b-access", "b-refresh", "b-secret");

        Assert.IsTrue(await a.RefreshAsync("a-access"));

        Assert.AreEqual("a-new-access", a.AccessToken);
        Assert.AreEqual("b-access", b.AccessToken);
        Assert.AreNotEqual(a.TokenFile, b.TokenFile);
        Assert.IsTrue(File.Exists(a.TokenFile));
        Assert.IsFalse(File.Exists(b.TokenFile));
        StringAssert.Contains(_posts.Single(), "client_secret=a-secret");
        Assert.AreSame(a, factory.Get("client-a"));
    }

    [TestMethod]
    public async Task DevicesSharingAnAccountRefreshOnce()
    {
        var release = new TaskCompletionSource();
        var factory = new NetatmoAuthClientFactory(NullLogger.Instance, _dataDirectory, async (url, body) =>
        {
            _posts.Enqueue(body);
            await release.Task;
            return Token(HttpStatusCode.OK, """{"access_token":"new-access","refresh_token":"new-refresh"}""");
        });
        var client = factory.Get("client-a");
        client.Configure("old-access", "old-refresh", "secret");

        var first = client.RefreshAsync("old-access");
        var second = client.RefreshAsync("old-access");
        release.SetResult();

        Assert.IsTrue(await first);
        Assert.IsTrue(await second);
        Assert.AreEqual(1, _posts.Count, "The second device sees the new token instead of spending the rotated refresh token.");
        Assert.AreEqual("new-refresh", client.RefreshToken);
    }

    [TestMethod]
    public async Task RefusedRefreshFallsBackToConfiguredTokens()
    {
        File.WriteAllText(Own("client-a"), """{"access_token":"stored-access","refresh_token":"stored-refresh","client_id":"client-a"}""");
        var client = Factory().Get("client-a");
        client.Configure("configured-access", "configured-refresh", "secret");

        Assert.IsTrue(await client.RefreshAsync("stored-access"));

        Assert.AreEqual("configured-access", client.AccessToken);
        Assert.IsFalse(File.Exists(client.TokenFile));
        Assert.IsFalse(await client.RefreshAsync("configured-access"));
    }

    [TestMethod]
    public async Task NetworkFailureKeepsStoredTokens()
    {
        File.WriteAllText(Own("client-a"), """{"access_token":"stored-access","refresh_token":"stored-refresh","client_id":"client-a"}""");
        _respond = _ => throw new HttpRequestException("offline");
        var client = Factory().Get("client-a");
        client.Configure("configured-access", "configured-refresh", "secret");

        Assert.IsFalse(await client.RefreshAsync("stored-access"));

        Assert.AreEqual("stored-access", client.AccessToken);
        Assert.IsTrue(File.Exists(client.TokenFile));
    }

    [TestMethod]
    public void NetatmoDevicesKeepNoStaticState()
    {
        foreach (var type in new[] { typeof(NetatmoBase), typeof(Catalog.NetatmoWeather), typeof(Catalog.NetatmoSecurity) })
        {
            var fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(field => !field.IsLiteral && !field.IsInitOnly)
                .Select(field => field.Name)
                .ToArray();
            CollectionAssert.AreEqual(Array.Empty<string>(), fields, type.Name);
        }
    }
}
