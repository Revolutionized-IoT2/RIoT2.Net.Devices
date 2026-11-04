// Ported from eufy-sdk src/core/store.ts behaviour and src/client/device-registry.ts getDevices() @ 5f92bea5 (Apache-2.0), modified for RIoT2.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Net.Devices.Services.Eufy;
using RIoT2.Net.Devices.Services.Eufy.Cloud;

namespace RIoT2.Net.Devices.Tests.Eufy.Cloud;

[TestClass]
public class EufySessionStoreTests
{
    private static string TestDirectory() => Path.Combine(AppContext.BaseDirectory, "eufy-store-tests", Guid.NewGuid().ToString("N"));

    private static PersistedSession Session() => new()
    {
        UserId = "user-1",
        AccountUserId = "acct-1",
        AuthToken = "token-1",
        Region = "eu-pr",
        CountryCode = "DE",
        Openudid = "0123456789abcdef",
        PhoneModel = "Pixel 8",
        MediaUserAgent = "Dalvik/2.1.0",
        ShareKey = "00112233445566778899aabbccddeeff",
        KeyIdent = "ffeeddccbbaa99887766554433221100",
        TokenExpiresAt = 1_800_000_000,
        SavedAt = 1_700_000_000_000,
    };

    [TestMethod]
    public void FileNameHashesTheNormalisedEmail()
    {
        var name = FileEufySessionStore.FileName("  Someone@Example.COM ");
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("someone@example.com")))[..16];

        Assert.AreEqual($"eufy-session-{expected}.json", name);
        Assert.AreEqual(name, FileEufySessionStore.FileName("someone@example.com"));
        Assert.DoesNotContain("someone", name);
        Assert.DoesNotContain("example", name);
    }

    [TestMethod]
    public void RoundTripsAndClears()
    {
        var directory = TestDirectory();
        try
        {
            var store = FileEufySessionStore.ForAccount(directory, "someone@example.com");
            Assert.IsNull(store.Load());

            store.Save(Session());
            store.Save(Session() with { AuthToken = "token-2" });

            Assert.AreEqual(Session() with { AuthToken = "token-2" }, store.Load());
            Assert.AreEqual(Session() with { AuthToken = "token-2" }, FileEufySessionStore.ForAccount(directory, "SOMEONE@example.com").Load());
            CollectionAssert.AreEqual(new[] { Path.GetFileName(store.Path) }, Directory.GetFiles(directory).Select(Path.GetFileName).ToArray());
            using (var json = JsonDocument.Parse(File.ReadAllText(store.Path)))
            {
                Assert.AreEqual("token-2", json.RootElement.GetProperty("authToken").GetString());
                Assert.AreEqual("acct-1", json.RootElement.GetProperty("accountUserId").GetString());
            }
            if (!OperatingSystem.IsWindows())
                Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.Path));

            store.Clear();
            Assert.IsNull(store.Load());
            store.Clear();
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void ACorruptFileLoadsAsNoSession()
    {
        var directory = TestDirectory();
        try
        {
            var store = FileEufySessionStore.ForAccount(directory, "someone@example.com");
            Directory.CreateDirectory(directory);
            File.WriteAllText(store.Path, "{ not json");
            Assert.IsNull(store.Load());
            File.WriteAllText(store.Path, "{\"userId\":\"u\"}");
            Assert.IsNull(store.Load());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void ValidityNeedsACompleteCredentialAndAnUnexpiredToken()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        Assert.IsTrue(EufySessionValidity.IsSessionValid(Session(), now));
        Assert.IsTrue(EufySessionValidity.IsSessionValid(Session() with { TokenExpiresAt = 0 }, now));
        Assert.IsFalse(EufySessionValidity.IsSessionValid(null, now));
        Assert.IsFalse(EufySessionValidity.IsSessionValid(Session() with { AccountUserId = null }, now));
        Assert.IsFalse(EufySessionValidity.IsSessionValid(Session() with { AuthToken = "" }, now));
        Assert.IsFalse(EufySessionValidity.IsSessionValid(Session() with { ShareKey = "" }, now));
        Assert.IsFalse(EufySessionValidity.IsSessionValid(Session() with { KeyIdent = "" }, now));
        Assert.IsFalse(EufySessionValidity.IsSessionValid(Session() with { TokenExpiresAt = 1_700_000_300 }, now));
        Assert.IsTrue(EufySessionValidity.IsSessionValid(Session() with { TokenExpiresAt = 1_700_000_301 }, now));
    }
}

[TestClass]
public class EufyDeviceListTests
{
    private static FakeReply Devices(params object[] devices) => FakeReply.Ok(new { devices });

    private static readonly object Station = new
    {
        device_sn = "T8010P0000000001",
        device_name = "HomeBase",
        device_model = "T8010",
        device_type = 0,
        station_sn = "T8010P0000000001",
        p2p_did = "ABCDEF-123456-ABCDE",
        @params = new object[]
        {
            new { param_type = 1224, param_value = "1", update_time = 1_700_000_000 },
            new { param_type = 1176, param_value = 63, update_time = 1_700_000_000 },
            new { param_type = 99, param_value = (string?)null, update_time = 0 },
        },
    };

    private static readonly object AttachedCamera = new
    {
        device_sn = "T8113P0000000002",
        device_alias_name = "Garage",
        device_model = "T8113",
        device_type = 1,
        device_channel = 2,
        parent_sn = "T8010P0000000001",
        station_sn = "T8113P0000000002",
    };

    private static readonly object Standalone = new
    {
        device_sn = "T8410P0000000003",
        device_model = "T8410",
        device_type = 31,
        station_sn = "",
    };

    private static (EufyCloudClient Client, FakeEufyCloud Cloud) Create()
    {
        var cloud = new FakeEufyCloud();
        var client = new EufyCloudClient(
            new EufyAccountOptions { Email = "someone@example.invalid", Password = "synthetic", CountryCode = "US" },
            NullLogger.Instance, cloud, new MemoryEufySessionStore(), null);
        return (client, cloud);
    }

    [TestMethod]
    public async Task MergesTheBareAndPerHouseListsBySerialAndMapsRecords()
    {
        var (client, cloud) = Create();
        using var clientScope = client;
        using var cloudScope = cloud;
        cloud.EstimatedDomain = "mega-us-pr.eufy.com";
        cloud.Route = r => r.Path switch
        {
            "/app/house/get_house_list" => FakeReply.Ok(new { house_infos = new[] { new { house_id = "h1" }, new { house_id = "h2" } } }),
            "/app/house/get_devs_list" => r.BodyString("house_id") switch
            {
                null => Devices(Station, AttachedCamera),
                "h1" => Devices(AttachedCamera, new { device_name = "no serial" }),
                _ => Devices(Standalone),
            },
            _ => null,
        };
        Assert.AreEqual(EufyLoginStatus.Ok, (await client.LoginAsync(CancellationToken.None)).Status);

        var devices = await client.GetDevicesAsync(CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "T8010P0000000001", "T8113P0000000002", "T8410P0000000003" }, devices.Select(d => d.Serial).ToArray());
        var bodies = cloud.To("/app/house/get_devs_list").Select(r => r.Json!.Value.GetRawText()).ToList();
        CollectionAssert.AreEqual(new[] { "{}", "{\"house_id\":\"h1\"}", "{\"house_id\":\"h2\"}" }, bodies);
        Assert.AreEqual("app-house-us-pr.eufy.com", cloud.To("/app/house/get_house_list").Single().Host);
        Assert.IsFalse(client.LastRefreshPartial);

        var station = devices[0];
        Assert.AreEqual("HomeBase", station.Name);
        Assert.AreEqual("T8010", station.Model);
        Assert.AreEqual(0, station.DeviceType);
        Assert.AreEqual("ABCDEF-123456-ABCDE", station.P2pDid);
        Assert.IsTrue(station.IsStation);
        Assert.IsNull(station.Channel);
        Assert.AreEqual("1", station.Params[1224]);
        Assert.AreEqual("63", station.Params[1176]);
        Assert.IsFalse(station.Params.ContainsKey(99));
        Assert.AreEqual(JsonValueKind.Object, station.Raw.ValueKind);
        Assert.AreEqual("T8010P0000000001", station.Raw.GetProperty("device_sn").GetString());

        var camera = devices[1];
        Assert.AreEqual("Garage", camera.Name);
        Assert.AreEqual("T8010P0000000001", camera.StationSerial);
        Assert.AreEqual(2, camera.Channel);
        Assert.IsFalse(camera.IsStation);
        Assert.IsEmpty(camera.Params);

        var standalone = devices[2];
        Assert.AreEqual("T8410P0000000003", standalone.Name);
        Assert.AreEqual("T8410P0000000003", standalone.StationSerial);
        Assert.IsTrue(standalone.IsStation);
    }

    [TestMethod]
    public async Task APartialRefreshKeepsThePreviouslyKnownDevices()
    {
        var (client, cloud) = Create();
        using var clientScope = client;
        using var cloudScope = cloud;
        var failing = false;
        cloud.Route = r => r.Path switch
        {
            "/app/house/get_house_list" => FakeReply.Ok(new { house_infos = new[] { new { house_id = "h1" } } }),
            "/app/house/get_devs_list" when r.BodyString("house_id") is null => Devices(Station),
            "/app/house/get_devs_list" => failing ? FakeReply.Error(500, 1234, "synthetic outage") : Devices(Standalone),
            _ => null,
        };
        await client.LoginAsync(CancellationToken.None);
        Assert.HasCount(2, await client.GetDevicesAsync(CancellationToken.None));

        failing = true;
        var partial = await client.GetDevicesAsync(CancellationToken.None);

        Assert.IsTrue(client.LastRefreshPartial);
        CollectionAssert.AreEquivalent(new[] { "T8010P0000000001", "T8410P0000000003" }, partial.Select(d => d.Serial).ToArray());
    }

    [TestMethod]
    public async Task AFailingHouseListStillQueriesTheBareList()
    {
        var (client, cloud) = Create();
        using var clientScope = client;
        using var cloudScope = cloud;
        cloud.Route = r => r.Path switch
        {
            "/app/house/get_house_list" => FakeReply.Error(500, 1234, "synthetic outage"),
            "/app/house/get_devs_list" => Devices(Station),
            _ => null,
        };
        await client.LoginAsync(CancellationToken.None);

        var devices = await client.GetDevicesAsync(CancellationToken.None);

        Assert.HasCount(1, devices);
        Assert.IsTrue(client.LastRefreshPartial);
    }

    [TestMethod]
    public async Task ARejectedSessionIsRethrown()
    {
        var (client, cloud) = Create();
        using var clientScope = client;
        using var cloudScope = cloud;
        await client.LoginAsync(CancellationToken.None);
        cloud.Route = r => r.Path switch
        {
            "/app/house/get_house_list" => FakeReply.Ok(new { house_infos = Array.Empty<object>() }),
            "/app/house/get_devs_list" => FakeReply.Error(401, EufyCloudClient.SessionKicked, "kicked out"),
            "/passport/login" => FakeReply.Error(400, 26006, "synthetic: wrong password"),
            _ => null,
        };

        await Assert.ThrowsExactlyAsync<EufySessionExpiredException>(() => client.GetDevicesAsync(CancellationToken.None));
    }
}
