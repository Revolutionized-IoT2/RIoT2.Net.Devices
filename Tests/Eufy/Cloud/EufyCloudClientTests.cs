// Ported from eufy-sdk src/transport/http/__tests__/session-expiry.spec.ts, session-key-single-flight.spec.ts,
// session-country.spec.ts, gtoken-header.spec.ts, device-identity.spec.ts, push-registration.spec.ts,
// download-media.spec.ts @ 5f92bea5 (Apache-2.0), modified for RIoT2.

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Net.Devices.Services.Eufy;
using RIoT2.Net.Devices.Services.Eufy.Cloud;

namespace RIoT2.Net.Devices.Tests.Eufy.Cloud;

[TestClass]
public class EufyCloudClientTests
{
    private const string Email = "someone+tag@example.invalid";
    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private sealed class Harness : IDisposable
    {
        public FakeEufyCloud Cloud { get; } = new();
        public MemoryEufySessionStore Store { get; } = new();
        public ManualTimeProvider Clock { get; } = new(Start);
        public EufyCloudClient Client { get; private set; } = null!;

        public static Harness Create(string country = "DE", string email = Email, string password = "synthetic-password", Action<Harness>? before = null)
        {
            var h = new Harness();
            before?.Invoke(h);
            h.Client = new EufyCloudClient(
                new EufyAccountOptions { Email = email, Password = password, CountryCode = country, DataDirectory = "unused" },
                NullLogger.Instance, h.Cloud, h.Store, h.Clock)
            {
                HostResolver = (_, _) => Task.FromResult(new[] { IPAddress.Parse("52.1.2.3") }),
            };
            return h;
        }

        public async Task LoginAsync()
        {
            var result = await Client.LoginAsync(CancellationToken.None);
            Assert.AreEqual(EufyLoginStatus.Ok, result.Status);
        }

        public void Dispose()
        {
            Client.Dispose();
            Cloud.Dispose();
        }
    }

    private static PersistedSession StoredSession(string token = "stored-token", string? country = null, string? accountUserId = "user-1") => new()
    {
        UserId = "user-1",
        AccountUserId = accountUserId,
        AuthToken = token,
        Region = "eu-pr",
        CountryCode = country,
        Openudid = "STORED-UDID",
        PhoneModel = "STORED-Model",
        MediaUserAgent = "STORED-UA",
        ShareKey = "00112233445566778899aabbccddeeff",
        KeyIdent = "ffeeddccbbaa99887766554433221100",
        TokenExpiresAt = 0,
        SavedAt = Start.ToUnixTimeMilliseconds(),
    };

    private static FakeReply Kicked => FakeReply.Error(401, EufyCloudClient.SessionKicked, "synthetic wording that carries no keywords");

    [TestMethod]
    public async Task LoginRunsEstimateExchangeAndPasswordLogin()
    {
        using var h = Harness.Create();

        await h.LoginAsync();

        Assert.IsTrue(h.Client.IsAuthenticated);
        Assert.AreEqual("user-1", h.Client.UserId);
        Assert.AreEqual("eu-pr", h.Client.RegionShard);
        var paths = h.Cloud.Requests.Select(r => $"{r.Host}{r.Path}").ToList();
        CollectionAssert.AreEqual(new[]
        {
            "mega-us-pr.eufy.com/passport/estimate_domain",
            "app-openapi-eu-pr.eufy.com/openapi/oauth/key/exchange",
            "app-passport-eu-pr.eufy.com/passport/login",
            "app-openapi-eu-pr.eufy.com/openapi/oauth/key/exchange",
        }, paths);

        var estimate = h.Cloud.Requests[0];
        Assert.AreEqual("de", estimate.BodyString("ab"));
        Assert.AreEqual("application/json", estimate.Header("content-type"));

        var login = h.Cloud.Requests[2];
        Assert.AreEqual("text/plain", login.Header("content-type"));
        Assert.AreEqual("algo_ecdh", login.Header("x-encryption-info"));
        Assert.AreEqual("replay", login.Header("x-replay-info"));
        Assert.IsNull(login.Header("x-auth-token"));
        Assert.AreEqual(Email, login.BodyString("email"));
        Assert.AreEqual("DE", login.BodyString("ab"));
        Assert.AreNotEqual("synthetic-password", login.BodyString("password"));
        Assert.AreEqual("", login.BodyString("captcha_id"));
        Assert.AreEqual(130, login.Json!.Value.GetProperty("client_secret_info").GetProperty("public_key").GetString()!.Length);
        Assert.AreEqual(h.Client.Openudid, login.Header("openudid"));
        Assert.AreEqual(h.Client.PhoneModel, login.Header("phone-model"));
        Assert.AreEqual(h.Client.PhoneModel, login.Header("phone_model"));
        Assert.AreEqual("eufy_mega", login.Header("app-name"));
        Assert.AreEqual("DE", login.Header("country"));

        // The post-login exchange carries the token, which binds the key-ident to the user.
        Assert.AreEqual("token-1", h.Cloud.Requests[3].Header("x-auth-token"));
        var saved = h.Store.Load();
        Assert.IsNotNull(saved);
        Assert.AreEqual("token-1", saved.AuthToken);
        Assert.AreEqual("eu-pr", saved.Region);
        Assert.AreEqual("DE", saved.CountryCode);
        Assert.AreEqual(h.Cloud.Requests[3].Header("x-key-ident"), saved.KeyIdent);
        Assert.AreEqual(h.Client.Openudid, saved.Openudid);
    }

    [TestMethod]
    public async Task AuthenticatedCallsCarryTheTokenAndTheAccountGtoken()
    {
        using var h = Harness.Create();
        h.Cloud.Route = r => r.Path.EndsWith("/passport/login", StringComparison.Ordinal)
            ? h.Cloud.LoginOk(userId: "acct-1", apCloudUserId: "ap-cloud-1")
            : FakeReply.Ok(new { answer = 42 });
        await h.LoginAsync();

        var data = await h.Client.PostAsync("mega", "/synthetic", new { a = 1 }, CancellationToken.None);

        Assert.AreEqual(42, data.GetProperty("answer").GetInt32());
        Assert.AreEqual("ap-cloud-1", h.Client.UserId);
        var call = h.Cloud.To("/synthetic").Single();
        Assert.AreEqual("app-mega-eu-pr.eufy.com", call.Host);
        Assert.AreEqual("application/json", call.Header("content-type"));
        Assert.AreEqual("token-1", call.Header("x-auth-token"));
        Assert.AreEqual("token-1", call.Header("authorization"));
        Assert.AreEqual(EufyCrypto.Gtoken("acct-1"), call.Header("gtoken"));
        Assert.AreEqual(1, call.Json!.Value.GetProperty("a").GetInt32());
        Assert.AreEqual("acct-1", h.Store.Load()!.AccountUserId);
    }

    [TestMethod]
    public async Task RestoresAPersistedSessionWithoutNetwork()
    {
        using var h = Harness.Create(before: x => x.Store.Save(StoredSession(country: "de")));

        Assert.IsTrue(h.Client.IsAuthenticated);
        var result = await h.Client.LoginAsync(CancellationToken.None);

        Assert.AreEqual(EufyLoginStatus.Ok, result.Status);
        Assert.IsEmpty(h.Cloud.Requests);
        Assert.AreEqual("STORED-UDID", h.Client.Openudid);
        Assert.AreEqual("STORED-Model", h.Client.PhoneModel);
        Assert.AreEqual("STORED-UA", h.Client.MediaUserAgent);
        Assert.AreEqual("eu-pr", h.Client.RegionShard);

        // The restored key signs the next call.
        h.Cloud.KnowKey("ffeeddccbbaa99887766554433221100", "00112233445566778899aabbccddeeff");
        await h.Client.PostAsync("mega", "/synthetic", null, CancellationToken.None);
        Assert.AreEqual("stored-token", h.Cloud.To("/synthetic").Single().Header("x-auth-token"));
        Assert.AreEqual(0, h.Cloud.KeyExchanges);
    }

    [TestMethod]
    public void DoesNotRestoreASessionOfAnotherCountryOrWithoutTheAccountId()
    {
        using var other = Harness.Create(country: "GB", before: x => x.Store.Save(StoredSession(country: "US")));
        Assert.IsFalse(other.Client.IsAuthenticated);

        using var noCountry = Harness.Create(country: "GB", before: x => x.Store.Save(StoredSession()));
        Assert.IsTrue(noCountry.Client.IsAuthenticated);

        using var legacy = Harness.Create(before: x => x.Store.Save(StoredSession(accountUserId: null)));
        Assert.IsFalse(legacy.Client.IsAuthenticated);
        Assert.IsEmpty(legacy.Client.AuthTokenHeaders());
        Assert.AreEqual("STORED-UDID", legacy.Client.Openudid);

        using var expired = Harness.Create(before: x => x.Store.Save(StoredSession() with { TokenExpiresAt = Start.ToUnixTimeSeconds() + 299 }));
        Assert.IsFalse(expired.Client.IsAuthenticated);
    }

    [TestMethod]
    public void IdentityIsStableAndAccountNameIsTheEmailLocalPart()
    {
        using var a = Harness.Create();
        using var b = Harness.Create();
        Assert.AreEqual(a.Client.Openudid, b.Client.Openudid);
        Assert.AreEqual(EufyCrypto.Md5Hex($"eufy-mega:{Email}")[..16], a.Client.Openudid);
        Assert.AreEqual(EufyPhoneModel.RandomPhoneModel(a.Client.Openudid), a.Client.PhoneModel);
        Assert.AreEqual(a.Client.PhoneModel, b.Client.PhoneModel);
        Assert.AreEqual("someone+tag", a.Client.AccountName);

        using var bare = Harness.Create(email: " someone ");
        Assert.AreEqual("someone", bare.Client.AccountName);
    }

    [TestMethod]
    public async Task CaptchaThenWrongAnswerThenOk()
    {
        using var h = Harness.Create();
        var captchas = 0;
        h.Cloud.Route = r =>
        {
            if (r.Path.EndsWith("/passport/generate/captcha", StringComparison.Ordinal))
                return FakeReply.Ok(new { captcha_id = $"cap-{++captchas}", item = "data:image/png;base64,AAAA" });
            if (!r.Path.EndsWith("/passport/login", StringComparison.Ordinal))
                return null;
            var answer = r.BodyString("answer");
            if (string.IsNullOrEmpty(r.BodyString("captcha_id")))
                return FakeReply.Error(200, EufyCloudClient.CaptchaRequired, "need captcha");
            return answer == "ABCD" ? h.Cloud.LoginOk() : FakeReply.Error(200, EufyCloudClient.CaptchaWrong, "wrong captcha");
        };

        var first = await h.Client.LoginAsync(CancellationToken.None);
        Assert.AreEqual(EufyLoginStatus.Captcha, first.Status);
        Assert.AreEqual("data:image/png;base64,AAAA", first.CaptchaImage);
        Assert.IsFalse(first.Retry);
        Assert.IsFalse(h.Client.IsAuthenticated);
        Assert.IsNull(h.Cloud.To("/passport/generate/captcha").Single().Header("x-auth-token"));

        var wrong = await h.Client.SolveCaptchaAsync("WXYZ", CancellationToken.None);
        Assert.AreEqual(EufyLoginStatus.Captcha, wrong.Status);
        Assert.IsTrue(wrong.Retry);
        var wrongLogin = h.Cloud.To("/passport/login").ElementAt(1);
        Assert.AreEqual("cap-1", wrongLogin.BodyString("captcha_id"));
        Assert.AreEqual("WXYZ", wrongLogin.BodyString("answer"));

        var ok = await h.Client.SolveCaptchaAsync("ABCD", CancellationToken.None);
        Assert.AreEqual(EufyLoginStatus.Ok, ok.Status);
        Assert.AreEqual("cap-2", h.Cloud.To("/passport/login").Last().BodyString("captcha_id"));
        Assert.IsTrue(h.Client.IsAuthenticated);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => h.Client.SolveCaptchaAsync("ABCD", CancellationToken.None));
    }

    [TestMethod]
    public async Task TwoFactorSendsTheCodeWithTheLimitedTokenThenCompletes()
    {
        using var h = Harness.Create();
        h.Cloud.Route = r =>
        {
            if (!r.Path.EndsWith("/passport/login", StringComparison.Ordinal))
                return null;
            return string.IsNullOrEmpty(r.BodyString("verify_code"))
                ? h.Cloud.LoginOk(token: "limited-token", faInfo: new { info = "verify" })
                : h.Cloud.LoginOk(token: "full-token");
        };

        var first = await h.Client.LoginAsync(CancellationToken.None);
        Assert.AreEqual(EufyLoginStatus.TwoFactor, first.Status);
        Assert.IsNotNull(first.TwoFactorMethod);
        Assert.IsFalse(h.Client.IsAuthenticated);
        Assert.IsNull(h.Store.Load());
        var send = h.Cloud.To("/app/sendmsg/verify_code").Single();
        Assert.AreEqual("app-push-eu-pr.eufy.com", send.Host);
        Assert.AreEqual("limited-token", send.Header("x-auth-token"));
        Assert.AreEqual(1004, send.Json!.Value.GetProperty("biz_type").GetInt32());
        Assert.AreEqual(2, send.Json!.Value.GetProperty("message_type").GetInt32());

        // Not a restorable session: login() goes to the network again rather than claiming ok.
        var ok = await h.Client.SubmitVerifyCodeAsync("123456", CancellationToken.None);

        Assert.AreEqual(EufyLoginStatus.Ok, ok.Status);
        var verify = h.Cloud.To("/passport/login").Last();
        Assert.AreEqual("123456", verify.BodyString("verify_code"));
        Assert.AreEqual("limited-token", verify.Header("x-auth-token"));
        Assert.IsTrue(h.Client.IsAuthenticated);
        Assert.AreEqual("full-token", h.Store.Load()!.AuthToken);
    }

    [TestMethod]
    public async Task ProbesTheOtherContentTypeOn4416AndRemembersIt()
    {
        using var h = Harness.Create();
        await h.LoginAsync();
        h.Cloud.Route = r => r.Path.EndsWith("/probe", StringComparison.Ordinal) && r.Header("content-type") == "application/json"
            ? FakeReply.Error(403, EufyCloudClient.SignatureInvalid, "signature invalid")
            : FakeReply.Ok(new { ok = true });

        Assert.IsTrue((await h.Client.PostAsync("mega", "/probe", null, CancellationToken.None)).GetProperty("ok").GetBoolean());
        Assert.IsTrue((await h.Client.PostAsync("mega", "/probe", null, CancellationToken.None)).GetProperty("ok").GetBoolean());

        var types = h.Cloud.To("/probe").Select(r => r.Header("content-type")).ToList();
        CollectionAssert.AreEqual(new[] { "application/json", "text/plain", "text/plain" }, types);
    }

    [TestMethod]
    public async Task ReExchangesTheKeyOnceOnAnIdentityError()
    {
        using var h = Harness.Create();
        await h.LoginAsync();
        var exchanges = h.Cloud.KeyExchanges;
        h.Cloud.ForgetKeys();

        var data = await h.Client.PostAsync("mega", "/synthetic", null, CancellationToken.None);

        Assert.AreEqual(JsonValueKind.Object, data.ValueKind);
        Assert.AreEqual(exchanges + 1, h.Cloud.KeyExchanges);
        Assert.AreEqual("token-1", h.Cloud.To("/oauth/key/exchange").Last().Header("x-auth-token"));
        Assert.AreEqual(h.Cloud.To("/oauth/key/exchange").Last().Header("x-key-ident"), h.Store.Load()!.KeyIdent);
        Assert.IsTrue(h.Client.IsAuthenticated);
    }

    [TestMethod]
    public async Task ConcurrentIdentityErrorsShareOneExchange()
    {
        using var h = Harness.Create();
        await h.LoginAsync();
        var exchanges = h.Cloud.KeyExchanges;
        h.Cloud.ForgetKeys();
        h.Cloud.ExchangeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var calls = Enumerable.Range(0, 8).Select(i => h.Client.PostAsync("mega", $"/synthetic/{i}", null, CancellationToken.None)).ToArray();
        await WaitUntilAsync(() => h.Cloud.KeyExchanges > exchanges);
        await Task.Delay(100);
        h.Cloud.ExchangeGate.SetResult();
        await Task.WhenAll(calls);

        Assert.AreEqual(exchanges + 1, h.Cloud.KeyExchanges);
    }

    [TestMethod]
    public async Task ARejectedTokenIsReplacedByOneLoginAndTheCallCompletes()
    {
        using var h = Harness.Create();
        await h.LoginAsync();
        var rejected = 0;
        h.Cloud.Route = r => r.Path.EndsWith("/synthetic", StringComparison.Ordinal) && Interlocked.Increment(ref rejected) == 1
            ? FakeReply.Error(401, 401, "token not exist, token = 0123456789abcdef0123")
            : null;
        h.Cloud.LoginToken = "token-2";

        await h.Client.PostAsync("mega", "/synthetic", null, CancellationToken.None);

        Assert.AreEqual(2, h.Cloud.Logins);
        Assert.AreEqual("token-2", h.Cloud.To("/synthetic").Last().Header("x-auth-token"));
        Assert.AreEqual("token-2", h.Store.Load()!.AuthToken);
    }

    [TestMethod]
    public async Task ConcurrentRejectionsCollapseIntoOneLogin()
    {
        using var h = Harness.Create();
        await h.LoginAsync();
        h.Cloud.LoginToken = "token-2";
        h.Cloud.Route = r => r.Path.StartsWith("/synthetic", StringComparison.Ordinal) && r.Header("x-auth-token") == "token-1" ? Kicked : null;

        await Task.WhenAll(h.Client.PostAsync("mega", "/synthetic/a", null, CancellationToken.None), h.Client.PostAsync("mega", "/synthetic/b", null, CancellationToken.None));

        Assert.AreEqual(2, h.Cloud.Logins);
    }

    [TestMethod]
    public async Task ASessionThatStaysRejectedThrowsSessionExpiredAndHoldsOff()
    {
        using var h = Harness.Create();
        await h.LoginAsync();
        h.Cloud.Route = r => r.Path.EndsWith("/synthetic", StringComparison.Ordinal) ? Kicked : null;

        var first = await Assert.ThrowsExactlyAsync<EufySessionExpiredException>(() => h.Client.PostAsync("mega", "/synthetic", null, CancellationToken.None));
        Assert.AreEqual(2, h.Cloud.Logins); // one re-login, not a loop
        Assert.AreEqual(TimeSpan.FromSeconds(60), first.RetryAfter);
        Assert.IsFalse(h.Client.IsAuthenticated);
        Assert.IsNull(h.Store.Load());

        h.Clock.Advance(TimeSpan.FromSeconds(10));
        var held = await Assert.ThrowsExactlyAsync<EufySessionExpiredException>(() => h.Client.PostAsync("mega", "/synthetic", null, CancellationToken.None));
        Assert.AreEqual(2, h.Cloud.Logins);
        Assert.Contains("possible cause", held.Message);
        Assert.AreEqual(TimeSpan.FromSeconds(50), held.RetryAfter);
        Assert.AreEqual(401, held.Status);
    }

    [TestMethod]
    public async Task ARejectionWhoseReLoginNeedsAHumanIsSurfaced()
    {
        using var h = Harness.Create();
        await h.LoginAsync();
        h.Cloud.Route = r =>
        {
            if (r.Path.EndsWith("/synthetic", StringComparison.Ordinal))
                return Kicked;
            if (r.Path.EndsWith("/passport/login", StringComparison.Ordinal))
                return FakeReply.Error(200, EufyCloudClient.CaptchaRequired, "need captcha");
            if (r.Path.EndsWith("/passport/generate/captcha", StringComparison.Ordinal))
                return FakeReply.Ok(new { captcha_id = "cap-1", item = "data:," });
            return null;
        };

        await Assert.ThrowsExactlyAsync<EufySessionExpiredException>(() => h.Client.PostAsync("mega", "/synthetic", null, CancellationToken.None));

        Assert.HasCount(1, h.Cloud.To("/synthetic"));
        h.Clock.Advance(TimeSpan.FromHours(1));
        await Assert.ThrowsExactlyAsync<EufySessionExpiredException>(() => h.Client.PostAsync("mega", "/synthetic", null, CancellationToken.None));
        Assert.HasCount(2, h.Cloud.To("/passport/login")); // the first login and the one challenged re-login only
    }

    [TestMethod]
    public async Task AGtokenMismatchKeepsTheSessionAndDoesNotLeakTheToken()
    {
        using var h = Harness.Create();
        await h.LoginAsync();
        h.Cloud.Route = r => r.Path.EndsWith("/synthetic", StringComparison.Ordinal)
            ? FakeReply.Error(401, 401, "token = 0123456789abcdef0123, gtoken not equal userid error")
            : null;

        var error = await Assert.ThrowsAsync<EufyCloudException>(() => h.Client.PostAsync("mega", "/synthetic", null, CancellationToken.None));

        Assert.IsNotInstanceOfType<EufySessionExpiredException>(error);
        Assert.AreEqual(401, error.Code);
        Assert.AreEqual(401, error.Status);
        Assert.DoesNotContain("0123456789abcdef0123", error.Message);
        Assert.AreEqual(1, h.Cloud.Logins);
        Assert.IsTrue(h.Client.IsAuthenticated);
    }

    [TestMethod]
    [DataRow("token not exist, token = 0123456789abcdef0123", 401, true)]
    [DataRow("token = 0123456789abcdef0123 expired", 401, true)]
    [DataRow("token = 0123456789abcdef0123, gtoken not equal userid error", 401, false)]
    [DataRow("synthetic", 26084, true)]
    [DataRow("rate limited", 401, false)]
    public void ClassifiesTokenRejections(string message, int code, bool rejected)
    {
        Assert.AreEqual(rejected, EufyCloudClient.TokenRejected(code, message));
    }

    [TestMethod]
    public async Task RegistersThePushTokenWithMegaAndSecurity()
    {
        using var h = Harness.Create();
        await h.LoginAsync();

        await h.Client.RegisterPushTokenAsync("synthetic-fcm-token", CancellationToken.None);

        var mega = h.Cloud.To("/app/push/register_push_token").Single();
        Assert.AreEqual("app-push-eu-pr.eufy.com", mega.Host);
        Assert.AreEqual("{\"is_notification_enable\":true,\"token\":\"synthetic-fcm-token\",\"voip_token\":\"\"}", mega.Json!.Value.GetRawText());
        var security = h.Cloud.To("/v1/apppush/register_push_token").Single();
        Assert.AreEqual("security-app-eu.eufylife.com", security.Host);
        Assert.AreEqual("text/plain", security.Header("content-type"));
        Assert.AreEqual("synthetic-fcm-token", security.BodyString("token"));
        Assert.AreEqual("1700000000000", security.BodyString("transaction"));
        var check = h.Cloud.To("/v1/app/review/app_push_check").Single();
        Assert.AreEqual("eufySecurity", check.BodyString("app_type"));
        Assert.HasCount(1, h.Cloud.Requests.Where(r => r.Path == "/v3/openapi/oauth/key/exchange" && r.Host == "security-app-eu.eufylife.com"));
        Assert.IsLessThan(h.Cloud.Requests.ToList().IndexOf(security), h.Cloud.Requests.ToList().IndexOf(mega));
    }

    [TestMethod]
    public async Task ReadsDskKeysAndTheDevicePublicKey()
    {
        using var h = Harness.Create();
        await h.LoginAsync();
        h.Cloud.Route = r => r.Path.EndsWith("/get_dsk_keys", StringComparison.Ordinal)
            ? FakeReply.Ok(new { device_dsks = new[] { new { device_sn = "T8010P1", dsk_key = "dsk-1", expiration = 1_800_000_000L } } })
            : null;
        h.Cloud.Raw = r => r.Path == "/v1/app/public_key/query"
            ? FakeEufyCloud.JsonResponse(200, new { code = 0, data = new { public_key = "ab".PadRight(128, 'c') } })
            : null;

        var keys = await h.Client.GetDskKeysAsync(["T8010P1"], CancellationToken.None);
        var key = await h.Client.GetDevicePublicKeyAsync("T8010P1", CancellationToken.None);
        await h.Client.GetDevicePublicKeyAsync("T8010P1", CancellationToken.None);

        Assert.AreEqual(("dsk-1", 1_800_000_000L), keys["T8010P1"]);
        var request = h.Cloud.To("/get_dsk_keys").Single();
        Assert.AreEqual("app-devicerelation-eu-pr.eufy.com", request.Host);
        Assert.AreEqual("eufy_security", request.Json!.Value.GetProperty("device_dsks")[0].GetProperty("category").GetString());
        Assert.AreEqual("", request.Json!.Value.GetProperty("invalid_dsks").GetProperty("T8010P1").GetString());
        Assert.AreEqual("T8010P1", request.Json!.Value.GetProperty("station_sns")[0].GetString());
        Assert.AreEqual(128, key.Length);
        var query = h.Cloud.To("/v1/app/public_key/query").Single();
        Assert.AreEqual("security-app-eu.eufylife.com", query.Host);
        Assert.AreEqual("?device_sn=T8010P1&type=2", query.Uri.Query);
        Assert.AreEqual("token-1", query.Header("x-auth-token"));
    }

    [TestMethod]
    public async Task DownloadsAndDecodesAPushImageWithTheSessionHeaders()
    {
        using var h = Harness.Create();
        await h.LoginAsync();
        var jpeg = new byte[] { 0xff, 0xd8, 0xff, 0xe0, 1, 2, 3, 0xff, 0xd9 };
        h.Cloud.Raw = r => r.Host == "security-app.eufylife.com"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(jpeg) }
            : null;

        var image = await h.Client.DownloadImageAsync("https://security-app.eufylife.com/media/1", "ABCDEF-123456-ABCDE", CancellationToken.None);

        CollectionAssert.AreEqual(jpeg, image);
        var request = h.Cloud.Requests.Last();
        Assert.AreEqual("token-1", request.Header("x-auth-token"));
        Assert.AreEqual(EufyCrypto.Gtoken("user-1"), request.Header("gtoken"));
        Assert.AreEqual(h.Client.MediaUserAgent, request.Header("user-agent"));
        Assert.AreEqual("eufy_mega", request.Header("app-name"));
    }

    [TestMethod]
    public async Task FollowsOneAllowlistedRedirectWithoutTheCredentials()
    {
        using var h = Harness.Create();
        await h.LoginAsync();
        h.Cloud.Raw = r => r.Host switch
        {
            "security-app-eu.eufylife.com" when r.Method == "GET" => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://zhixin-security-eu.s3.eu-central-1.amazonaws.com/object?signature=secret") } },
            "zhixin-security-eu.s3.eu-central-1.amazonaws.com" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([7]) },
            _ => null,
        };

        var bytes = await h.Client.DownloadImageAsync("https://security-app-eu.eufylife.com/media", null, CancellationToken.None);

        CollectionAssert.AreEqual(new byte[] { 7 }, bytes);
        var redirected = h.Cloud.Requests.Last();
        Assert.IsNull(redirected.Header("x-auth-token"));
        Assert.IsNull(redirected.Header("gtoken"));
    }

    [TestMethod]
    [DataRow("https://security-app.eufylife.com.evil.example/media")]
    [DataRow("https://security-app-1.eufylife.com/media")]
    [DataRow("http://security-app.eufylife.com/media")]
    [DataRow("https://user:pass@security-app.eufylife.com/media")]
    [DataRow("https://security-app.eufylife.com:8443/media")]
    [DataRow("https://127.0.0.1/media")]
    [DataRow("https://[::1]/media")]
    [DataRow("https://localhost/media")]
    public async Task RejectsMediaUrlsOutsideTheAllowlist(string url)
    {
        using var h = Harness.Create();
        await h.LoginAsync();
        var before = h.Cloud.Requests.Count;

        var error = await Assert.ThrowsExactlyAsync<EufyMediaException>(() => h.Client.DownloadImageAsync(url, null, CancellationToken.None));

        Assert.AreEqual("url-not-allowed", error.Reason);
        Assert.DoesNotContain(url, error.Message);
        Assert.AreEqual(before, h.Cloud.Requests.Count);
    }

    [TestMethod]
    public async Task RejectsAMediaHostResolvingPrivatelyAndMapsAuthFailures()
    {
        using var h = Harness.Create();
        await h.LoginAsync();
        h.Client.HostResolver = (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.0.0.5") });
        var privateHost = await Assert.ThrowsExactlyAsync<EufyMediaException>(() => h.Client.DownloadImageAsync("https://security-app.eufylife.com/media", null, CancellationToken.None));
        Assert.AreEqual("address-not-public", privateHost.Reason);

        h.Client.HostResolver = (_, _) => Task.FromResult(new[] { IPAddress.Parse("52.1.2.3") });
        h.Cloud.Raw = r => r.Method == "GET" ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : null;
        await Assert.ThrowsExactlyAsync<EufySessionExpiredException>(() => h.Client.DownloadImageAsync("https://security-app.eufylife.com/media", null, CancellationToken.None));

        h.Cloud.Raw = r => r.Method == "GET" ? new HttpResponseMessage(HttpStatusCode.NotFound) : null;
        var missing = await Assert.ThrowsExactlyAsync<EufyMediaException>(() => h.Client.DownloadImageAsync("https://security-app.eufylife.com/media", null, CancellationToken.None));
        Assert.AreEqual("http-status", missing.Reason);
        Assert.AreEqual(404, missing.Status);
    }

    [TestMethod]
    public void ClearSessionForgetsThePersistedSession()
    {
        using var h = Harness.Create(before: x => x.Store.Save(StoredSession()));
        Assert.IsTrue(h.Client.IsAuthenticated);

        h.Client.ClearSession();

        Assert.IsFalse(h.Client.IsAuthenticated);
        Assert.IsNull(h.Store.Load());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.IsTrue(condition());
    }
}
