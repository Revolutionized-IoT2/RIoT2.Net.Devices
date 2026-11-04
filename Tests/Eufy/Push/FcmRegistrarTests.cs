using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Net.Devices.Services.Eufy.Push;

namespace RIoT2.Net.Devices.Tests.Eufy.Push;

/// <summary>FCM registration (fcm.ts): FID install, GServices check-in, GCM register3 — requests pinned to upstream.</summary>
[TestClass]
public class FcmRegistrarTests
{
    internal const string CheckinResponseHex = "08011880d095ffbc31394ef324ebcd2c1a474102000000000000f05a0178";

    /// <summary>Answers the three registration endpoints; register3 fails <paramref name="register3Failures"/> times first.</summary>
    internal sealed class FakeFcmHandler(int register3Failures = 0) : HttpMessageHandler
    {
        private int _register3Calls;

        public List<(HttpRequestMessage Request, byte[] Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add((request, body));
            var url = request.RequestUri!.ToString();
            if (url == FcmConstants.InstallationsUrl)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"name\":\"n\",\"fid\":\"x\",\"refreshToken\":\"refresh-1\",\"authToken\":{\"token\":\"fis-auth\",\"expiresIn\":\"604800s\"}}") };
            if (url == FcmConstants.CheckinUrl)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Convert.FromHexString(CheckinResponseHex)) };
            if (url == FcmConstants.RegisterUrl)
            {
                return ++_register3Calls <= register3Failures
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Error=PHONE_REGISTRATION_ERROR") }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("token=fcm-token-1\n") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    [TestMethod]
    public async Task RegistersAsUpstreamDoes()
    {
        var handler = new FakeFcmHandler(register3Failures: 1);
        var delays = new List<TimeSpan>();
        using var http = new HttpClient(handler);
        var registrar = new FcmRegistrar(http, NullLogger.Instance, (d, _) => { delays.Add(d); return Task.CompletedTask; });

        var creds = await registrar.RegisterAsync(CancellationToken.None);

        Assert.AreEqual("5123456789012345678", creds.AndroidId);
        Assert.AreEqual("17293822569102704642", creds.SecurityToken);
        Assert.AreEqual("fcm-token-1", creds.FcmToken);
        Assert.AreEqual("refresh-1", creds.RefreshToken);
        Assert.HasCount(22, creds.Fid);
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(1) }, delays);
        Assert.HasCount(4, handler.Requests);

        var (install, installBody) = handler.Requests[0];
        Assert.AreEqual(HttpMethod.Post, install.Method);
        Assert.AreEqual("https://firebaseinstallations.googleapis.com/v1/projects/batterycam-3250a/installations", install.RequestUri!.ToString());
        Assert.AreEqual("com.oceanwing.battery.cam", install.Headers.GetValues("X-Android-Package").Single());
        Assert.AreEqual("F051262F9F99B638F3C76DE349830638555B4A0A", install.Headers.GetValues("X-Android-Cert").Single());
        Assert.AreEqual("AIzaSyCSz1uxGrHXsEktm7O3_wv-uLGpC9BvXR8", install.Headers.GetValues("x-goog-api-key").Single());
        Assert.AreEqual("application/json", install.Content!.Headers.ContentType!.ToString());
        var json = JsonNode.Parse(installBody)!;
        Assert.AreEqual(creds.Fid, json["fid"]!.GetValue<string>());
        Assert.AreEqual("1:348804314802:android:440a6773b3620da7", json["appId"]!.GetValue<string>());
        Assert.AreEqual("FIS_v2", json["authVersion"]!.GetValue<string>());
        Assert.AreEqual("a:16.3.1", json["sdkVersion"]!.GetValue<string>());

        var (checkin, checkinBody) = handler.Requests[1];
        Assert.AreEqual("https://android.clients.google.com/checkin", checkin.RequestUri!.ToString());
        Assert.AreEqual("application/x-protobuf", checkin.Content!.Headers.ContentType!.ToString());
        CollectionAssert.AreEqual(FcmRegistrar.BuildCheckinRequest(), checkinBody);

        var (register, registerBody) = handler.Requests[3];
        Assert.AreEqual("https://android.clients.google.com/c2dm/register3", register.RequestUri!.ToString());
        Assert.AreEqual("AidLogin 5123456789012345678:17293822569102704642", register.Headers.GetValues("Authorization").Single());
        Assert.AreEqual("com.oceanwing.battery.cam", register.Headers.GetValues("app").Single());
        Assert.AreEqual("201216023", register.Headers.GetValues("gcm_ver").Single());
        Assert.AreEqual("Android-GCM/1.5", register.Headers.GetValues("User-Agent").Single());
        Assert.AreEqual("application/x-www-form-urlencoded", register.Content!.Headers.ContentType!.ToString());
        Assert.AreEqual(
            "X-subtype=348804314802&sender=348804314802&X-app_ver=741&X-osv=25&X-cliv=fiid-20.2.0&X-gmsv=201216023"
            + $"&X-appid={creds.Fid}&X-scope=*&X-Goog-Firebase-Installations-Auth=fis-auth"
            + "&X-gmp_app_id=1%3A348804314802%3Aandroid%3A440a6773b3620da7&X-firebase-app-name-hash=R1dAH9Ui7M-ynoznwBdw01tLxhI"
            + "&X-Firebase-Client-Log-Type=1&app=com.oceanwing.battery.cam&device=5123456789012345678&app_ver=741&gcm_ver=201216023"
            + "&plat=0&cert=F051262F9F99B638F3C76DE349830638555B4A0A&target_ver=28",
            System.Text.Encoding.UTF8.GetString(registerBody));
    }

    [TestMethod]
    public async Task GivesUpAfterFiveRegister3Attempts()
    {
        using var http = new HttpClient(new FakeFcmHandler(register3Failures: 5));
        var registrar = new FcmRegistrar(http, NullLogger.Instance, (_, _) => Task.CompletedTask);
        var ex = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => registrar.RegisterAsync(CancellationToken.None));
        StringAssert.StartsWith(ex.Message, "GCM register failed: Error=PHONE_REGISTRATION_ERROR");
    }

    [TestMethod]
    public void GeneratesValidFirebaseInstallationIds()
    {
        for (var i = 0; i < 200; i++)
        {
            var fid = FcmRegistrar.GenerateFid();
            StringAssert.Matches(fid, new System.Text.RegularExpressions.Regex("^[cdef][A-Za-z0-9_-]{21}$"));
        }
    }

    [TestMethod]
    public void FormEncodesLikeUrlSearchParams()
    {
        Assert.AreEqual("a+b=*-._%7E%2F%3A%C3%A4", FcmRegistrar.FormEncode([new("a b", "*-._~/:ä")]));
    }
}
